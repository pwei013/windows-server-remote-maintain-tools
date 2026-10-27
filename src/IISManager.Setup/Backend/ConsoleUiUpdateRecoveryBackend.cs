using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using IISManager.Setup.Core;

namespace IISManager.Setup.Backend;

internal sealed record VerifiedPartialConsoleUiReceipt(Guid Id, string ReceiptPath, string DirectoryPath,
    string InstallDirectory, ServiceEvidence BeforeService, FileEvidence[] Originals, AccessEvidence[] Permissions,
    FileEvidence Snapshot, InstallationMarker BackupMarker, FileEvidence BackupMarkerSnapshot,
    PayloadFile[] ObservedRuntime, FileEvidence[] TemporaryFiles)
{
    internal const string GuardName = "console-ui-update-completion-guard.json";
    internal string GuardPath => Path.Combine(DirectoryPath, GuardName);
    internal string StageDirectory => Path.Combine(DirectoryPath, "stage");
    internal string BackupDirectory => Path.Combine(DirectoryPath, "runtime-backup");

    internal void RequireUnclaimed()
    {
        SetupFileSecurity.RejectReparseChain(GuardPath, true);
        if (File.Exists(GuardPath) || Directory.Exists(GuardPath))
            throw new SetupException("UI_RECOVERY_ALREADY_CLAIMED", "该部分更新已经提交过一次收尾；请只读核对关联回执，不能再次提交。");
    }

    internal void RequireMarker(InstallationMarker marker)
    {
        var expected = BackupMarker with { State = "Updating", Generation = Id };
        if (!JsonSerializer.SerializeToUtf8Bytes(expected, PayloadArchive.JsonOptions)
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(marker, PayloadArchive.JsonOptions)))
            throw new SetupException("UI_RECOVERY_MARKER_DRIFT", "Updating 归属记录与原回执及受限旧归属备份不符，拒绝收尾。");
    }
}

public sealed record ConsoleUiUpdateRecoveryInspection(bool CanRecover, SetupPlan? Plan, Guid OriginalReceiptId,
    string ReceiptPath, int PreservedRuntime, string[] PendingFiles, string[] TemporaryFiles, bool RestoreRunning,
    SetupIssue[] Issues, Guid? InterruptedReceiptId = null, string[]? RetainedFiles = null);

/// <summary>A separately reviewed, single-attempt completion of a stopped Console UI-only partial update.</summary>
public sealed partial class ConsoleUiUpdateRecoveryBackend(ISetupPayloadSource source, bool finishInterrupted = false)
{
    private sealed record Evidence(VerifiedPartialConsoleUiReceipt Original, InstallationEvidence Existing,
        FileEvidence[] FrozenFiles, FileEvidence[] Targets, AccessEvidence[] Directories, PayloadFile[] Pending,
        InterruptedUiCompletion? Interrupted = null);
    private readonly object gate = new();
    private Evidence? inspected;

    public ConsoleUiUpdateRecoveryInspection Inspect(string receiptPath)
    {
        lock (gate) inspected = null;
        try
        {
            RequireWindowsAdministrator();
            using var mutex = SetupGlobalMutex.Acquire();
            using var payload = source.Open(SetupComponent.Console);
            var proof = ReadEvidence(receiptPath, payload.Manifest);
            lock (gate) inspected = proof;
            // Tokens never enter the public inspection contract or the review form.
            var plan = proof.Existing.Plan with { Servers = proof.Existing.Plan.Servers.Select(s => s with { AccessToken = "" }).ToArray() };
            return new(true, plan, proof.Original.Id, proof.Interrupted?.ReceiptPath ?? proof.Original.ReceiptPath,
                payload.Manifest.Files.Count(f => f.Kind == PayloadFileKind.Runtime) - proof.Pending.Length,
                proof.Pending.Select(f => f.Path).ToArray(), proof.Original.TemporaryFiles.Select(f =>
                    Path.GetRelativePath(proof.Original.InstallDirectory, f.Path) + " · SHA256 " + f.Sha256).ToArray(),
                proof.Original.BeforeService.State == 4, [], proof.Interrupted?.Id,
                proof.Interrupted is null ? [] : ["已归档临时文件 · SHA256 " + proof.Interrupted.ArchivedTemporary!.Sha256]);
        }
        catch (Exception exception)
        {
            return new(false, null, Guid.Empty, receiptPath, 0, [], [], false,
                [new(exception is SetupException safe ? safe.Code : "UI_RECOVERY_INSPECTION_UNVERIFIED", SafeFailure(exception))]);
        }
    }

    public Task<SetupOutcome> ExecuteAsync(string receiptPath, IProgress<SetupProgress> progress, CancellationToken token) =>
        Task.Run(() => Execute(receiptPath, progress, token), CancellationToken.None);

    private SetupOutcome Execute(string receiptPath, IProgress<SetupProgress> progress, CancellationToken token)
    {
        SetupReceipt? receipt = null;
        Evidence? proof = null;
        var claimed = false;
        try
        {
            RequireWindowsAdministrator(); token.ThrowIfCancellationRequested();
            Evidence? approved;
            lock (gate) { approved = inspected; inspected = null; }
            if (approved is null || !(approved.Interrupted?.ReceiptPath ?? approved.Original.ReceiptPath).Equals(receiptPath, StringComparison.OrdinalIgnoreCase))
                throw new SetupException("UI_RECOVERY_INSPECTION_REQUIRED", "须先在本窗口完成只读核对，再确认执行一次收尾。");
            using var mutex = SetupGlobalMutex.Acquire();
            using var payload = source.Open(SetupComponent.Console);
            Report(progress, "RecoveryPreflight", "重新核对受限原回执、完整备份、载荷、停止状态及现场漂移。");
            proof = ReadEvidence(receiptPath, payload.Manifest);
            RequireSameInspection(approved, proof);
            using var originalPin = PathLease.Acquire(proof.Original.ReceiptPath, includeFile: true);
            using var interruptedPin = proof.Interrupted is null ? null : PathLease.Acquire(proof.Interrupted.ReceiptPath, includeFile: true);
            using var rootPin = PathLease.Acquire(proof.Existing.Plan.InstallDirectory);
            using var dataPin = PathLease.Acquire(proof.Existing.Plan.DataDirectory);
            using var ledgerPin = PathLease.Acquire(proof.Existing.LedgerDirectory);
            VerifyFrozen(proof);
            RequireUnclaimed(proof); RequireStopped(proof);
            token.ThrowIfCancellationRequested();
            receipt = new SetupReceipt(proof.Existing.Plan,
                proof.Interrupted is null ? "CompleteConsoleUiUpdate" : "FinishConsoleUiUpdate", proof.Interrupted?.Id ?? proof.Original.Id);
            receipt.RecordService(proof.Original.BeforeService);
            receipt.RecordPreservedFiles(proof.Existing.ConfigurationFiles.Concat([proof.Original.Snapshot,
                proof.Original.BackupMarkerSnapshot, proof.Existing.MarkerEvidence!]));
            if (proof.Interrupted is not null)
                receipt.RecordPreservedFiles([proof.Interrupted.Snapshot, proof.Interrupted.OriginalClaim!, proof.Interrupted.ArchivedTemporary!]);
            foreach (var directory in proof.Directories) receipt.RecordAccess(directory);
            receipt.Advance("ClaimRecovery", "Applying");
            claimed = true; // A claim write may itself have an unknown outcome; never retry it.
            var claimPath = proof.Interrupted?.GuardPath ?? proof.Original.GuardPath;
            WriteNewVerified(claimPath, JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1, procedure = proof.Interrupted is null ? "CompleteConsoleUiUpdate" : "FinishConsoleUiUpdate",
                originalReceiptId = proof.Interrupted?.Id ?? proof.Original.Id,
                recoveryReceiptId = receipt.Id, receiptPath = receipt.ReceiptPath
            }, PayloadArchive.JsonOptions));
            var guard = SetupInventory.Snapshot(claimPath);
            using var guardPin = PathLease.Acquire(claimPath, includeFile: true);
            receipt.Advance("Extract", "Applying");
            payload.ExtractTo(receipt.StageDirectory);
            VerifyFrozen(proof); RequireStopped(proof);
            var identity = WindowsServiceManager.ResolveIdentity(proof.Existing.Plan.ServiceAccount);
            FileEvidence? completedMarker = null;
            var newArchives = new List<FileEvidence>();
            using (var ledgerLock = SetupInventory.LockStoppedLedger(SetupComponent.Console, proof.Existing.LedgerDirectory))
            {
                VerifyFrozen(proof); RequireStopped(proof); SetupInventory.CheckLedger(proof.Existing.LedgerDirectory);
                receipt.Advance("BackupRuntime", "Applying");
                Report(progress, "BackupRuntime", "另存本次已审阅现场备份；原更新回执和完整旧备份保留。");
                receipt.RecordFiles(proof.Targets);
                foreach (var file in proof.Original.ObservedRuntime)
                    WindowsSetupBackend.BackupRuntime(proof.Existing.Plan.InstallDirectory, receipt.BackupDirectory, file);
                WindowsSetupBackend.BackupMarker(proof.Existing.Plan.InstallDirectory, receipt.BackupDirectory);
                VerifyFrozen(proof); RequireStopped(proof);
                receipt.Advance("PreserveTemporary", "Applying");
                Report(progress, "PreserveTemporary", proof.Interrupted is null
                    ? "核验并受限复制已审阅临时产物，读回并记录后移除原临时文件。"
                    : "核验并保留上次已归档临时产物的当前字节和权限，不再次移动或改写它。");
                foreach (var temporary in proof.Original.TemporaryFiles)
                {
                    // The archive has its own restricted ACL; it is not an existing runtime/configuration replacement.
                    SetupInventory.VerifySnapshots([temporary]);
                    var destination = Path.Combine(receipt.BackupDirectory, "preserved-" + Path.GetFileName(temporary.Path));
                    SetupFileSecurity.RejectReparseChain(destination, true);
                    receipt.RecordTemporaryPreservation(temporary, destination);
                    using (var read = new FileStream(temporary.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var write = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
                    { read.CopyTo(write); write.Flush(flushToDisk: true); }
                    SetupFileSecurity.SetNewProgramFileOwner(destination);
                    var archived = SetupInventory.Snapshot(destination);
                    if (archived.Size != temporary.Size || archived.Sha256 != temporary.Sha256)
                        throw new SetupException("UI_RECOVERY_TEMP_COPY_UNVERIFIED", "已审阅临时文件的受限复制未能核验，停止后续操作。");
                    SetupFileSecurity.RequireSameDescriptor(SetupFileSecurity.RestrictedSddl, archived.ParentSddl);
                    SetupFileSecurity.RequireSameDescriptor("O:BAG:BAD:(A;ID;FA;;;SY)(A;ID;FA;;;BA)", archived.Sddl);
                    receipt.RecordTemporaryPreservation(temporary, destination, archived);
                    receipt.RecordPreservedFiles([archived]);
                    newArchives.Add(archived);
                    SetupInventory.VerifySnapshots([temporary, archived]);
                    RequireStopped(proof);
                    File.Delete(temporary.Path);
                    if (File.Exists(temporary.Path) || Directory.Exists(temporary.Path))
                        throw new SetupException("UI_RECOVERY_TEMP_REMOVAL_UNVERIFIED", "原临时文件移除未能核验，请保留两份证据并先只读核对。");
                    SetupFileSecurity.RequireSameDescriptor(temporary.ParentSddl, SetupFileSecurity.ReadSddl(Path.GetDirectoryName(temporary.Path)!));
                    SetupInventory.VerifySnapshots([archived]);
                    receipt.RecordTemporaryPreservation(temporary, destination, archived, originalAbsent: true);
                }
                VerifyPreserved(proof, guard, newArchives: newArchives);
                receipt.Advance("ReplaceRuntime", "Applying");
                Report(progress, "ReplaceRuntime", $"保留已符合新载荷的程序，只补齐 {proof.Pending.Length} 个已审阅界面文件。");
                // The same replacement contract checks old content, full descriptor and actual identity access.
                foreach (var file in proof.Pending)
                    WindowsSetupBackend.ReplaceRuntime(receipt.StageDirectory, proof.Existing.Plan.InstallDirectory,
                        file, proof.Targets, identity, receipt);
                VerifyPreserved(proof, guard, newArchives: newArchives); RequireStopped(proof);
                VerifyCompleteRuntime(proof, payload.Manifest, identity);
                SetupInventory.CheckLedger(proof.Existing.LedgerDirectory);
                receipt.Advance("WriteCompleteMarker", "Applying");
                WindowsSetupBackend.WriteMarker(proof.Existing.Plan,
                    payload.Manifest.Files.Where(f => f.Kind == PayloadFileKind.Runtime).OrderBy(f => f.Path, StringComparer.Ordinal).ToArray(),
                    receipt.Id, "Complete");
                SetupFileSecurity.RequireSameDescriptor(proof.Existing.MarkerEvidence!.Sddl,
                    SetupFileSecurity.ReadSddl(proof.Existing.MarkerEvidence.Path));
                completedMarker = SetupInventory.Snapshot(proof.Existing.MarkerEvidence.Path);
                var completed = SetupInventory.Read(SetupComponent.Console, proof.Existing.Plan.InstallDirectory, payload.Manifest);
                WindowsSetupBackend.RequireSameService(proof.Original.BeforeService, completed.Service, 1);
                SetupInventory.VerifySnapshots(proof.Existing.ConfigurationFiles);
                VerifyCompleteRuntime(proof, payload.Manifest, identity);
                VerifyPreserved(proof, guard, completedMarker, newArchives);
            }
            VerifyPreserved(proof, guard, completedMarker!, newArchives); RequireStopped(proof);
            if (proof.Original.BeforeService.State == 4)
            {
                receipt.Advance("StartService", "Applying");
                Report(progress, "StartService", "完整界面及原权限已读回，按审阅方案恢复原先运行的 Console。");
                WindowsServiceManager.StartAsync(proof.Existing.Plan.ServiceName).GetAwaiter().GetResult();
            }
            receipt.Advance("VerifyCompletion", "Applying");
            var finalService = WindowsServiceManager.Read(proof.Existing.Plan.ServiceName);
            WindowsSetupBackend.RequireSameService(proof.Original.BeforeService, finalService, proof.Original.BeforeService.State);
            SetupInventory.VerifySnapshots(proof.Existing.ConfigurationFiles);
            VerifyCompleteRuntime(proof, payload.Manifest, identity);
            VerifyPreserved(proof, guard, completedMarker!, newArchives);
            receipt.RecordService(finalService, after: true); receipt.Finish("Complete", "OK");
            Report(progress, "Complete", "界面运行文件、归属记录和原服务状态已核验；请继续浏览器及真实 IIS 只读验收。");
            return new(true, "Complete", "OK", "Console 界面更新收尾完成，原配置及账本保留，原服务状态已恢复。请继续浏览器和 Agent 连接验收。", receipt.ReceiptPath);
        }
        catch (Exception exception)
        {
            var state = claimed ? "PartialOrUnknown" : "Failed";
            var code = exception is SetupException safe ? safe.Code : exception is OperationCanceledException ? "CANCELLED" : "UI_RECOVERY_OPERATION_UNVERIFIED";
            if (receipt is not null)
            {
                try
                {
                    if (proof is not null) receipt.RecordService(WindowsServiceManager.Read(proof.Existing.Plan.ServiceName), after: true);
                    receipt.Finish(state, code, exception);
                }
                catch { state = "Unknown"; code = "UI_RECOVERY_RECEIPT_UNVERIFIED"; }
            }
            return new(false, state, code, SafeFailure(exception) + (claimed
                ? " 已停止后续操作，保留原回执、关联回执及一次性标记，先只读核对，不重放或自动回滚。" : " 未提交收尾，请保留现场并核对提示。"), receipt?.ReceiptPath ?? "");
        }
        finally { receipt?.Dispose(); }
    }

    private Evidence ReadEvidence(string receiptPath, PayloadManifest manifest)
    {
        PayloadArchive.ValidateManifest(manifest, SetupComponent.Console);
        var interrupted = finishInterrupted ? ReadInterruptedCompletion(receiptPath) : null;
        var original = ReadOriginal(interrupted?.OriginalReceiptPath ?? receiptPath, manifest, interrupted);
        var existing = SetupInventory.ReadPartialConsoleUi(original, manifest);
        var plan = existing.Plan with { ConfigureFirewall = false, GrantCertificateKeyRead = false };
        if (plan.TransportMode != "HttpToken" || plan.SourceAccessMode != "PrivateNetwork")
            throw new SetupException("UI_RECOVERY_CONNECTION_MODE_UNSUPPORTED", "此入口仅支持当前已确认的内网 HTTP Console 部分界面更新。");
        var issues = SetupPlanValidator.Validate(plan);
        if (issues.Length != 0) throw new SetupException(issues[0].Code, "当前 Console 配置不满足收尾条件，拒绝修改配置。");
        var dataDirectory = SetupFileSecurity.CanonicalPath(plan.DataDirectory);
        if (SetupFileSecurity.Under(path: dataDirectory, root: plan.InstallDirectory) ||
            SetupFileSecurity.Under(path: plan.InstallDirectory, root: dataDirectory))
            throw new SetupException("UI_RECOVERY_DIRECTORY_OVERLAP", "界面收尾须使用与程序目录分离的原数据目录，拒绝处理重叠边界。");
        WindowsSetupBackend.RequireSameService(original.BeforeService, existing.Service, 1);
        if (existing.Service.ProcessId != 0) throw new SetupException("UI_RECOVERY_SERVICE_NOT_STOPPED", "Console 必须已稳定停止且没有运行进程。");
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == plan.Port))
            throw new SetupException("UI_RECOVERY_PORT_IN_USE", "原 Console 监听端口已被使用，拒绝收尾。");
        SetupFileSecurity.RequireTrustedAncestors(plan.DataDirectory, includeExistingDirectory: false);
        var identity = WindowsServiceManager.ResolveIdentity(plan.ServiceAccount);
        var targets = original.ObservedRuntime.Select(f => SetupInventory.Snapshot(SetupFileSecurity.ResolveRelative(plan.InstallDirectory, f.Path))).ToArray();
        foreach (var target in targets) SetupFileSecurity.ProveAccess(target.Path, identity, FileSystemRights.ReadAndExecute);
        foreach (var config in existing.ConfigurationFiles) SetupFileSecurity.ProveAccess(config.Path, identity, FileSystemRights.Read);
        SetupFileSecurity.ProveAccess(Path.Combine(plan.InstallDirectory, "logs"), identity, FileSystemRights.Modify);
        SetupFileSecurity.ProveAccess(existing.LedgerDirectory, identity, FileSystemRights.Modify);
        SetupInventory.CheckLedger(existing.LedgerDirectory);
        var ledgerFiles = ReadLedgerFiles(existing.LedgerDirectory);
        SetupFileSecurity.RequireTrustedAncestors(original.StageDirectory);
        SetupFileSecurity.RequireRestricted(original.StageDirectory);
        SetupFileSecurity.RequireTrustedAncestors(original.BackupDirectory);
        SetupFileSecurity.RequireRestricted(original.BackupDirectory);
        var stageFiles = WalkFiles(original.StageDirectory);
        var expectedStage = manifest.Files.ToDictionary(f => SetupFileSecurity.ResolveRelative(original.StageDirectory, f.Path), StringComparer.OrdinalIgnoreCase);
        if (!expectedStage.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(stageFiles))
            throw new SetupException("UI_RECOVERY_STAGE_SET_MISMATCH", "原暂存目录与当前完整载荷的精确文件集合不符。");
        foreach (var file in manifest.Files)
        {
            var path = SetupFileSecurity.ResolveRelative(original.StageDirectory, file.Path);
            // Old extraction did not set a program-file owner. These bytes are only corroborating evidence;
            // execution extracts a fresh, hash-verified embedded payload into the new restricted receipt.
            SetupInventory.VerifyFile(path, file);
        }
        var backupFiles = WalkFiles(original.BackupDirectory);
        var expectedBackup = original.Originals.Select(f => SetupFileSecurity.ResolveRelative(original.BackupDirectory,
            Relative(plan.InstallDirectory, f.Path))).Append(original.BackupMarkerSnapshot.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expectedBackup.SetEquals(backupFiles)) throw new SetupException("UI_RECOVERY_BACKUP_SET_MISMATCH", "完整旧备份含缺失或额外文件，拒绝收尾。");
        foreach (var file in original.Originals)
        {
            var backup = SetupFileSecurity.ResolveRelative(original.BackupDirectory, Relative(plan.InstallDirectory, file.Path));
            SetupFileSecurity.RequireTrustedProgramFile(backup);
            SetupInventory.VerifyFile(backup, new(Relative(plan.InstallDirectory, file.Path), file.Size, file.Sha256, PayloadFileKind.Runtime));
        }
        var pending = manifest.Files.Where(f => f.Kind == PayloadFileKind.Runtime && !original.ObservedRuntime.Any(o =>
            o.Path == f.Path && o.Size == f.Size && o.Sha256.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))).ToArray();
        // Freeze all observed bytes and descriptors, including optional publish metadata and exact leftover temp.
        var publish = Path.Combine(plan.InstallDirectory, "PUBLISH-FILES.json");
        var optional = File.Exists(publish) ? new[] { publish } : [];
        foreach (var path in optional) SetupFileSecurity.RequireTrustedProgramFile(path);
        var frozen = stageFiles.Concat(backupFiles).Concat(targets.Select(f => f.Path)).Concat(ledgerFiles)
            .Concat(existing.ConfigurationFiles.Select(f => f.Path)).Concat(original.TemporaryFiles.Select(f => f.Path))
            .Concat(optional).Append(original.ReceiptPath).Append(existing.MarkerEvidence!.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).Select(SetupInventory.Snapshot).ToArray();
        var directories = WalkDirectories(plan.InstallDirectory, skipLogs: true)
            .Concat(WalkDirectories(original.StageDirectory)).Concat(WalkDirectories(original.BackupDirectory))
            .Concat([original.DirectoryPath, plan.DataDirectory, existing.LedgerDirectory, Path.Combine(plan.InstallDirectory, "logs")])
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)
            .Select(path => new AccessEvidence(path, SetupFileSecurity.ReadSddl(path), SetupFileSecurity.ReadSddl(path), true)).ToArray();
        var proof = new Evidence(original, existing with { Plan = plan }, frozen, targets, directories, pending, interrupted);
        if (interrupted is not null) proof = VerifyInterruptedCompletion(proof, manifest);
        VerifyFrozen(proof); RequireStopped(proof); RequireUnclaimed(proof);
        return proof;
    }

    private static VerifiedPartialConsoleUiReceipt ReadOriginal(string receiptPath, PayloadManifest manifest,
        InterruptedUiCompletion? interrupted = null)
    {
        var full = Path.GetFullPath(receiptPath);
        var directory = SetupFileSecurity.CanonicalPath(Path.GetDirectoryName(full)!);
        var receipts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IISManager.Setup", "receipts");
        var name = Path.GetFileName(directory);
        if (receiptPath != full || Path.GetFileName(full) != "receipt.json" ||
            !Path.GetDirectoryName(directory)!.Equals(receipts, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(name, "N", out var id) || id == Guid.Empty || name != id.ToString("N"))
            throw new SetupException("UI_RECOVERY_RECEIPT_PATH_INVALID", "收尾只接受 ProgramData 中规范的原安装回执路径。");
        SetupFileSecurity.RejectReparseChain(full, false); SetupFileSecurity.RequireTrustedAncestors(directory);
        SetupFileSecurity.RequireRestricted(directory); SetupFileSecurity.RequireTrustedProgramFile(full);
        var snapshot = SetupInventory.Snapshot(full);
        using var json = ReadJson(full);
        var value = json.RootElement;
        if (value.GetProperty("version").GetInt32() != 1 || value.GetProperty("id").GetGuid() != id ||
            Text(value, "component") != "Console" || Text(value, "mode") != "Update" ||
            Text(value, "transportMode") != "HttpToken" || Text(value, "sourceAccessMode") != "PrivateNetwork" ||
            Text(value, "state") != "PartialOrUnknown" || Text(value, "code") != "WINDOWS_OPERATION_UNVERIFIED" ||
            Text(value, "failureStage") != "ReplaceRuntime" || Text(value, "exceptionType") != "UnauthorizedAccessException" ||
            value.GetProperty("hresult").GetInt32() != unchecked((int)0x80070005) ||
            value.TryGetProperty("recoveryOf", out var recoveryOf) && recoveryOf.ValueKind != JsonValueKind.Null)
            throw new SetupException("UI_RECOVERY_RECEIPT_UNSUPPORTED", "回执不属于本次支持的 Console 运行文件替换拒绝访问，不能使用此收尾入口。");
        var service = value.GetProperty("beforeService").Deserialize<ServiceEvidence>(PayloadArchive.JsonOptions);
        var after = value.GetProperty("afterService").Deserialize<ServiceEvidence>(PayloadArchive.JsonOptions);
        if (service is null || service.Name != "IISManager.Console" || service.State is not (1 or 4) ||
            service.StartType != 2 || service.ServiceType != 16 || WindowsServiceManager.SameAccount(service.Account, "LocalSystem") ||
            service.BinaryPath is null || service.BinaryPath.Length > 600 || string.IsNullOrWhiteSpace(service.Account))
            throw new SetupException("UI_RECOVERY_SERVICE_RECORD_INVALID", "原回执缺少固定 Console 服务的受支持身份和稳定状态。");
        WindowsSetupBackend.RequireSameService(service, after, 1);
        if (after!.ProcessId != 0) throw new SetupException("UI_RECOVERY_SERVICE_RECORD_INVALID", "原回执未能证明 Console 已停止。");
        var match = Regex.Match(service.BinaryPath,
            """^"(?<exe>[A-Za-z]:\\[^\"]+)" --contentRoot "(?<root>[A-Za-z]:\\[^\"]+)" --environment Production$""",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success) throw new SetupException("UI_RECOVERY_SERVICE_PATH_INVALID", "原 Console 入口不是固定生产入口。");
        var root = SetupFileSecurity.CanonicalPath(match.Groups["root"].Value);
        if (!match.Groups["exe"].Value.Equals(Path.Combine(root, "IISManager.Console.exe"), StringComparison.OrdinalIgnoreCase))
            throw new SetupException("UI_RECOVERY_SERVICE_PATH_INVALID", "原 Console 程序与内容根不符。");
        var originals = ReadArray<FileEvidence>(value, "runtimeBackups", 4096);
        var permissions = ReadArray<AccessEvidence>(value, "permissions", 16384);
        var old = new List<PayloadFile>();
        foreach (var file in originals)
        {
            var relative = Relative(root, file.Path);
            if (string.IsNullOrWhiteSpace(file.Sddl) || string.IsNullOrWhiteSpace(file.ParentSddl))
                throw new SetupException("UI_RECOVERY_PERMISSION_RECORD_INVALID", "原运行文件权限记录不完整。");
            old.Add(new(relative, file.Size, file.Sha256, PayloadFileKind.Runtime));
        }
        var changedScope = ConsoleUiUpdatePolicy.GetPendingScope(old.ToArray(), manifest);
        if (changedScope.Length == 0) throw new SetupException("UI_RECOVERY_NO_UI_CHANGE", "载荷没有界面更新，拒绝收尾。");
        var markerPath = Path.Combine(directory, "runtime-backup", SetupInventory.MarkerName);
        SetupFileSecurity.RequireTrustedProgramFile(markerPath);
        using var markerJson = ReadJson(markerPath);
        var backupMarker = markerJson.RootElement.Deserialize<InstallationMarker>(PayloadArchive.JsonOptions)
            ?? throw new SetupException("UI_RECOVERY_BACKUP_MARKER_INVALID", "旧归属备份无效。");
        if (backupMarker.Version != 1 || backupMarker.Component != "Console" || backupMarker.State != "Complete" ||
            backupMarker.Generation == Guid.Empty || !backupMarker.InstallDirectory.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            !WindowsServiceManager.SameAccount(backupMarker.Account, service.Account) ||
            backupMarker.TransportMode != "HttpToken" || backupMarker.SourceAccessMode != "PrivateNetwork" ||
            backupMarker.RuntimeFiles is null || !SameRuntime(old, backupMarker.RuntimeFiles))
            throw new SetupException("UI_RECOVERY_BACKUP_MARKER_INVALID", "受限旧归属备份与原运行记录、账号或连接模式不符。");
        var expected = manifest.Files.Where(f => f.Kind == PayloadFileKind.Runtime).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var observed = new List<PayloadFile>();
        foreach (var file in expected.Values)
        {
            var path = SetupFileSecurity.ResolveRelative(root, file.Path);
            SetupFileSecurity.RejectReparseChain(path, true);
            var before = originals.SingleOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (!File.Exists(path))
            {
                if (before is not null || Directory.Exists(path)) throw new SetupException("UI_RECOVERY_RUNTIME_MISSING", "原运行文件缺失或资源路径被目录占用，拒绝收尾。");
                continue;
            }
            SetupFileSecurity.RequireTrustedProgramFile(path);
            var actual = SetupInventory.Snapshot(path);
            var equalsNew = actual.Size == file.Size && actual.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
            var equalsOld = before is not null && actual.Size == before.Size && actual.Sha256.Equals(before.Sha256, StringComparison.OrdinalIgnoreCase);
            if (!equalsNew && !equalsOld) throw new SetupException("UI_RECOVERY_RUNTIME_UNKNOWN", "现场运行文件内容既不属于旧备份也不属于完整新载荷。");
            if (before is not null)
            {
                SetupFileSecurity.RequireSameDescriptor(before.Sddl, actual.Sddl);
                SetupFileSecurity.RequireSameDescriptor(before.ParentSddl, actual.ParentSddl);
            }
            observed.Add(new(file.Path, actual.Size, actual.Sha256, PayloadFileKind.Runtime));
        }
        var temporaryFiles = Directory.EnumerateFiles(root, ".*.setup-tmp", SearchOption.TopDirectoryOnly).ToArray();
        if (temporaryFiles.Length > 1) throw new SetupException("UI_RECOVERY_TEMP_UNVERIFIED", "组件根目录存在多个临时产物，请先只读核对。");
        var temporaries = new List<FileEvidence>();
        foreach (var path in temporaryFiles)
        {
            if (!Regex.IsMatch(Path.GetFileName(path), @"^\.[0-9a-f]{32}\.setup-tmp$", RegexOptions.CultureInvariant))
                throw new SetupException("UI_RECOVERY_TEMP_UNVERIFIED", "遗留临时文件名称无法核实。");
            var unfinished = permissions.LastOrDefault();
            var before = unfinished is null ? null : originals.SingleOrDefault(f => f.Path.Equals(unfinished.Path, StringComparison.OrdinalIgnoreCase));
            if (unfinished is null || unfinished.ReadBackVerified || before is null ||
                !Path.GetDirectoryName(before.Path)!.Equals(root, StringComparison.OrdinalIgnoreCase))
                throw new SetupException("UI_RECOVERY_TEMP_UNVERIFIED", "遗留临时文件缺少最后未完成替换记录的归属依据。");
            var file = expected[Relative(root, before.Path)];
            SetupFileSecurity.RequireTrustedProgramFile(path); SetupInventory.VerifyFile(path, file);
            var current = SetupInventory.Snapshot(path);
            SetupFileSecurity.RequireSameDescriptor(before.Sddl, current.Sddl);
            SetupFileSecurity.RequireSameDescriptor(before.ParentSddl, current.ParentSddl);
            temporaries.Add(current);
        }
        var proof = new VerifiedPartialConsoleUiReceipt(id, full, directory, root, service, originals, permissions,
            snapshot, backupMarker, SetupInventory.Snapshot(markerPath), observed.ToArray(), temporaries.ToArray());
        if (interrupted is null) proof.RequireUnclaimed();
        else VerifyOriginalClaim(proof, interrupted);
        SetupInventory.VerifySnapshots([snapshot]);
        return proof;
    }

    private static void VerifyFrozen(Evidence proof)
    {
        SetupInventory.VerifySnapshots(proof.FrozenFiles);
        VerifyDirectories(proof.Directories);
        VerifyOriginalEvidenceSets(proof);
        SetupInventory.CheckKnownTree(proof.Existing.Plan.InstallDirectory, proof.Existing.RuntimeFiles,
            proof.Existing.ConfigurationFiles.Select(f => f.Path).ToArray(), proof.Existing.LedgerDirectory,
            proof.Original.TemporaryFiles.Select(f => f.Path));
        RequireFileSet(proof.Existing.LedgerDirectory, ReadLedgerFiles(proof.Existing.LedgerDirectory),
            proof.FrozenFiles.Where(f => SetupFileSecurity.Under(path: f.Path, root: proof.Existing.LedgerDirectory)).Select(f => f.Path));
        foreach (var file in proof.Existing.RuntimeFiles.Where(f => !proof.Original.ObservedRuntime.Any(o => o.Path == f.Path)))
        {
            var path = SetupFileSecurity.ResolveRelative(proof.Existing.Plan.InstallDirectory, file.Path);
            SetupFileSecurity.RejectReparseChain(path, true);
            if (File.Exists(path) || Directory.Exists(path)) throw new SetupException("UI_RECOVERY_RUNTIME_DRIFT", "已核验缺失的新文件路径发生漂移。");
        }
    }

    private static void VerifyPreserved(Evidence proof, FileEvidence guard, FileEvidence? completedMarker = null,
        IEnumerable<FileEvidence>? newArchives = null)
    {
        if (newArchives is not null) SetupInventory.VerifySnapshots(newArchives);
        SetupInventory.VerifySnapshots(proof.Existing.ConfigurationFiles);
        var mutable = proof.Pending.Select(f => SetupFileSecurity.ResolveRelative(proof.Existing.Plan.InstallDirectory, f.Path))
            .Concat(proof.Original.TemporaryFiles.Select(f => f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (completedMarker is not null) mutable.Add(completedMarker.Path);
        SetupInventory.VerifySnapshots(proof.FrozenFiles.Where(f => !mutable.Contains(f.Path)));
        if (completedMarker is not null) SetupInventory.VerifySnapshots([completedMarker]);
        SetupInventory.VerifySnapshots([guard]); VerifyDirectories(proof.Directories);
        VerifyOriginalEvidenceSets(proof);
        SetupInventory.CheckKnownTree(proof.Existing.Plan.InstallDirectory, proof.Existing.RuntimeFiles,
            proof.Existing.ConfigurationFiles.Select(f => f.Path).ToArray(), proof.Existing.LedgerDirectory);
        if (proof.Original.TemporaryFiles.Any(f => File.Exists(f.Path) || Directory.Exists(f.Path)))
            throw new SetupException("UI_RECOVERY_TEMP_DRIFT", "已保留的临时文件原路径被再次占用。");
        RequireFileSet(proof.Existing.LedgerDirectory, ReadLedgerFiles(proof.Existing.LedgerDirectory),
            proof.FrozenFiles.Where(f => SetupFileSecurity.Under(path: f.Path, root: proof.Existing.LedgerDirectory)).Select(f => f.Path));
    }

    private static void VerifyCompleteRuntime(Evidence proof, PayloadManifest manifest, SecurityIdentifier identity)
    {
        foreach (var file in manifest.Files.Where(f => f.Kind == PayloadFileKind.Runtime))
        {
            var path = SetupFileSecurity.ResolveRelative(proof.Existing.Plan.InstallDirectory, file.Path);
            SetupFileSecurity.RequireTrustedProgramFile(path); SetupInventory.VerifyFile(path, file);
            SetupFileSecurity.ProveAccess(path, identity, FileSystemRights.ReadAndExecute);
        }
        foreach (var original in proof.Original.Originals)
        {
            var actual = SetupInventory.Snapshot(original.Path);
            SetupFileSecurity.RequireSameDescriptor(original.Sddl, actual.Sddl);
            SetupFileSecurity.RequireSameDescriptor(original.ParentSddl, actual.ParentSddl);
        }
    }

    private static void VerifyOriginalEvidenceSets(Evidence proof)
    {
        foreach (var directory in new[] { proof.Original.StageDirectory, proof.Original.BackupDirectory })
        {
            var actual = Walk(directory);
            RequireFileSet(directory, actual.Files, proof.FrozenFiles.Where(f => SetupFileSecurity.Under(path: f.Path, root: directory)).Select(f => f.Path));
            var expectedDirectories = proof.Directories.Where(d => d.Path.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
                SetupFileSecurity.Under(path: d.Path, root: directory)).Select(d => d.Path);
            RequireFileSet(directory, actual.Directories, expectedDirectories);
        }
        VerifyInterruptedEvidenceSets(proof);
    }

    private static void RequireStopped(Evidence proof)
    {
        var actual = WindowsServiceManager.Read(proof.Existing.Plan.ServiceName);
        WindowsSetupBackend.RequireSameService(proof.Original.BeforeService, actual, 1);
        if (actual!.ProcessId != 0) throw new SetupException("UI_RECOVERY_SERVICE_NOT_STOPPED", "Console 必须已稳定停止且没有运行进程。");
    }

    private static void RequireSameInspection(Evidence approved, Evidence current)
    {
        if (approved.Original.Id != current.Original.Id || !SameRuntime(approved.Pending, current.Pending) ||
            approved.Interrupted?.Id != current.Interrupted?.Id ||
            !approved.FrozenFiles.Select(f => f.Path).SequenceEqual(current.FrozenFiles.Select(f => f.Path), StringComparer.Ordinal) ||
            !approved.Directories.Select(d => d.Path).SequenceEqual(current.Directories.Select(d => d.Path), StringComparer.Ordinal))
            throw new SetupException("UI_RECOVERY_INSPECTION_DRIFT", "只读审阅后文件集合或执行范围变化，拒绝收尾。");
        SetupInventory.VerifySnapshots(approved.FrozenFiles); VerifyDirectories(approved.Directories);
    }

    private static bool SameRuntime(IEnumerable<PayloadFile> before, IEnumerable<PayloadFile> after) =>
        before.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => $"{f.Path}|{f.Size}|{f.Sha256.ToUpperInvariant()}|{f.Kind}")
            .SequenceEqual(after.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => $"{f.Path}|{f.Size}|{f.Sha256.ToUpperInvariant()}|{f.Kind}"));

    private static string Relative(string root, string path) => SetupPathBoundary.GetRuntimeRelativePath(root: root, path: path);

    private static T[] ReadArray<T>(JsonElement value, string key, int maximum)
    {
        var array = value.GetProperty(key);
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 || array.GetArrayLength() > maximum)
            throw new SetupException("UI_RECOVERY_RECORD_COUNT_INVALID", "原回执记录集合超出支持范围。");
        return array.EnumerateArray().Select(item => item.Deserialize<T>(PayloadArchive.JsonOptions)
            ?? throw new SetupException("UI_RECOVERY_RECORD_INVALID", "原回执记录无效。")).ToArray();
    }

    private static JsonDocument ReadJson(string path)
    {
        SetupFileSecurity.RejectReparseChain(path, false);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 4 * 1024 * 1024) throw new SetupException("UI_RECOVERY_JSON_TOO_LARGE", "收尾证据 JSON 超出支持范围。");
        var json = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
        try { RequireUniqueKeys(json.RootElement); return json; } catch { json.Dispose(); throw; }
    }

    private static void RequireUniqueKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new SetupException("UI_RECOVERY_DUPLICATED_FIELD", "收尾证据存在重复字段。");
                RequireUniqueKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RequireUniqueKeys(item);
    }

    private static string? Text(JsonElement element, string name) => element.GetProperty(name).GetString();

    private static string[] WalkFiles(string root) => Walk(root).Files;
    private static string[] WalkDirectories(string root, bool skipLogs = false) => Walk(root, skipLogs).Directories;
    private static (string[] Files, string[] Directories) Walk(string root, bool skipLogs = false)
    {
        var files = new List<string>(); var directories = new List<string>(); var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            SetupFileSecurity.RejectReparseChain(directory, false); directories.Add(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                SetupFileSecurity.RejectReparseChain(path, false);
                if (Directory.Exists(path))
                {
                    if (skipLogs && path.Equals(Path.Combine(root, "logs"), StringComparison.OrdinalIgnoreCase)) continue;
                    pending.Push(path);
                }
                else files.Add(path);
                if (files.Count + directories.Count + pending.Count > 8192)
                    throw new SetupException("UI_RECOVERY_TREE_TOO_LARGE", "收尾证据目录超出支持范围。");
            }
        }
        return (files.Order(StringComparer.Ordinal).ToArray(), directories.Order(StringComparer.Ordinal).ToArray());
    }

    private static string[] ReadLedgerFiles(string ledger)
    {
        var files = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(ledger))
        {
            SetupFileSecurity.RejectReparseChain(path, false);
            if (Directory.Exists(path)) throw new SetupException("UI_RECOVERY_LEDGER_UNVERIFIED", "账本含未知目录，拒绝启动服务。");
            if (Path.GetFileName(path) == ".console.lock")
            {
                if (new FileInfo(path).Length != 0) throw new SetupException("UI_RECOVERY_LEDGER_UNVERIFIED", "账本锁记录异常。");
                continue;
            }
            if (Path.GetExtension(path) != ".json") throw new SetupException("UI_RECOVERY_LEDGER_UNVERIFIED", "账本含未知文件，拒绝启动服务。");
            files.Add(path);
            if (files.Count > 4096) throw new SetupException("UI_RECOVERY_LEDGER_UNVERIFIED", "账本记录数量超出支持范围。");
        }
        return files.Order(StringComparer.Ordinal).ToArray();
    }

    private static void RequireFileSet(string directory, IEnumerable<string> actual, IEnumerable<string> expected)
    {
        if (!actual.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expected))
            throw new SetupException("UI_RECOVERY_FILE_SET_DRIFT", "已审阅目录的文件集合发生漂移，拒绝收尾。");
    }

    private static void VerifyDirectories(IEnumerable<AccessEvidence> directories)
    {
        foreach (var record in directories)
        {
            SetupFileSecurity.RejectReparseChain(record.Path, false);
            if (!Directory.Exists(record.Path)) throw new SetupException("UI_RECOVERY_DIRECTORY_DRIFT", "已审阅目录不再存在。");
            SetupFileSecurity.RequireSameDescriptor(record.AfterSddl, SetupFileSecurity.ReadSddl(record.Path));
        }
    }

    private static void WriteNewVerified(string path, byte[] bytes)
    {
        SetupFileSecurity.RejectReparseChain(path, true);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
        { stream.Write(bytes); stream.Flush(flushToDisk: true); }
        SetupFileSecurity.SetNewProgramFileOwner(path);
        using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (read.Length != bytes.Length || !SHA256.HashData(read).SequenceEqual(SHA256.HashData(bytes)))
            throw new SetupException("UI_RECOVERY_CLAIM_UNVERIFIED", "一次性收尾标记未能读回核验。");
    }

    private static void RequireWindowsAdministrator()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new SetupException("WINDOWS_X64_REQUIRED", "收尾只支持 Windows x64。");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new SetupException("ADMINISTRATOR_REQUIRED", "请使用已提升的管理员进程打开收尾入口。");
    }

    private static string SafeFailure(Exception exception)
    {
        var failure = SafeFailureSummary.From(exception);
        return (exception is SetupException safe ? safe.Message + " " : "收尾条件或操作未能核实。 ") +
            $"({failure.LeafExceptionType} / 0x{failure.LeafHResult:X8})";
    }

    private static void Report(IProgress<SetupProgress> progress, string stage, string message)
    { try { progress.Report(new(stage, message)); } catch { } }
}
