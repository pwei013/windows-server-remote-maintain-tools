using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using IISManager.Setup.Core;

namespace IISManager.Setup.Backend;

// This proof is available only after the original restricted receipt has passed the narrow legacy checks.
internal sealed record VerifiedPartialConsoleReceipt(Guid Id, string ReceiptPath, string DirectoryPath,
    string InstallDirectory, ServiceEvidence Service, AccessEvidence[] Permissions, FileEvidence Snapshot)
{
    internal const string GuardName = "console-firewall-completion-guard.json";
    internal string GuardPath => Path.Combine(DirectoryPath, GuardName);

    internal static VerifiedPartialConsoleReceipt Read(string receiptPath)
    {
        var full = Path.GetFullPath(receiptPath);
        var directory = SetupFileSecurity.CanonicalPath(Path.GetDirectoryName(full)!);
        var receipts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IISManager.Setup", "receipts");
        var name = Path.GetFileName(directory);
        if (!full.Equals(receiptPath, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).Equals("receipt.json", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetDirectoryName(directory)!.Equals(receipts, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(name, "N", out var id) || id == Guid.Empty || name != id.ToString("N"))
            throw Failure("RECOVERY_RECEIPT_PATH_INVALID", "收尾仅接受 ProgramData 安装回执目录中的规范原回执路径。");
        SetupFileSecurity.RejectReparseChain(full, false);
        SetupFileSecurity.RequireTrustedAncestors(directory);
        SetupFileSecurity.RequireRestricted(directory);
        SetupFileSecurity.RequireTrustedProgramFile(full);
        var snapshot = SetupInventory.Snapshot(full);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 2 * 1024 * 1024) throw Failure("RECOVERY_RECEIPT_TOO_LARGE", "原安装回执超出支持范围。");
        using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
        RequireUniqueKeys(json.RootElement);
        var value = json.RootElement;
        if (value.ValueKind != JsonValueKind.Object || value.GetProperty("version").GetInt32() != 1 ||
            value.GetProperty("id").GetGuid() != id || Text(value, "component") != "Console" || Text(value, "mode") != "Install" ||
            Text(value, "transportMode") != "HttpToken" || Text(value, "sourceAccessMode") != "PrivateNetwork" ||
            Text(value, "state") != "PartialOrUnknown" || Text(value, "code") != "WINDOWS_OPERATION_UNVERIFIED" ||
            Text(value, "exceptionType") != "TargetInvocationException" || value.GetProperty("hresult").GetInt32() != unchecked((int)0x80131604) ||
            value.GetProperty("firewall").ValueKind != JsonValueKind.Null || value.GetProperty("beforeService").ValueKind != JsonValueKind.Null)
            throw Failure("RECOVERY_RECEIPT_NOT_SUPPORTED", "该回执不属于本次已确认的控制台防火墙失败，不能自动收尾。");
        var service = value.GetProperty("afterService").Deserialize<ServiceEvidence>(PayloadArchive.JsonOptions);
        if (service is null || service.Name != "IISManager.Console" || service.State != 1 || service.ProcessId != 0 ||
            service.StartType != 2 || service.ServiceType != 16 || string.IsNullOrWhiteSpace(service.Account) ||
            WindowsServiceManager.SameAccount(service.Account, "LocalSystem") || service.BinaryPath is null || service.BinaryPath.Length > 600)
            throw Failure("RECOVERY_SERVICE_RECORD_INVALID", "原回执未能证明固定控制台服务处于已停止的受支持状态。");
        var match = Regex.Match(service.BinaryPath,
            """^"(?<exe>[A-Za-z]:\\[^\"]+)" --contentRoot "(?<root>[A-Za-z]:\\[^\"]+)" --environment Production$""",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success) throw Failure("RECOVERY_SERVICE_PATH_INVALID", "原服务入口不符合固定控制台生产入口。");
        var root = SetupFileSecurity.CanonicalPath(match.Groups["root"].Value);
        if (!match.Groups["exe"].Value.Equals(Path.Combine(root, "IISManager.Console.exe"), StringComparison.OrdinalIgnoreCase))
            throw Failure("RECOVERY_SERVICE_PATH_INVALID", "原服务程序与内容根不符。");
        var records = value.GetProperty("permissions");
        if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() is < 1 or > 256)
            throw Failure("RECOVERY_PERMISSION_RECORDS_INVALID", "原回执缺少受支持的权限读回证据。");
        var permissions = records.EnumerateArray().Select(item => item.Deserialize<AccessEvidence>(PayloadArchive.JsonOptions)
            ?? throw Failure("RECOVERY_PERMISSION_RECORDS_INVALID", "原权限证据无效。")).ToArray();
        if (permissions.Any(p => string.IsNullOrWhiteSpace(p.Path) || string.IsNullOrWhiteSpace(p.BeforeSddl) || string.IsNullOrWhiteSpace(p.AfterSddl)))
            throw Failure("RECOVERY_PERMISSION_RECORDS_INVALID", "原权限证据字段不完整。");
        var proof = new VerifiedPartialConsoleReceipt(id, full, directory, root, service, permissions, snapshot);
        proof.RequireUnclaimed();
        SetupInventory.VerifySnapshots([snapshot]);
        return proof;
    }

    internal void RequireUnclaimed()
    {
        SetupFileSecurity.RejectReparseChain(GuardPath, true);
        if (File.Exists(GuardPath) || Directory.Exists(GuardPath))
            throw Failure("RECOVERY_ALREADY_CLAIMED", "本次原安装已经执行过收尾尝试；须先只读核实关联回执，不能重放。");
    }

    private static string? Text(JsonElement element, string key) => element.GetProperty(key).GetString();
    private static void RequireUniqueKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in value.EnumerateObject())
            {
                if (!names.Add(item.Name)) throw Failure("RECOVERY_RECEIPT_DUPLICATED", "原回执存在重复字段，拒绝解释歧义。");
                RequireUniqueKeys(item.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RequireUniqueKeys(item);
    }
    private static SetupException Failure(string code, string message) => new(code, message);
}

/// <summary>One guarded completion of the proven legacy Console firewall failure. Never rewrites existing configuration or permissions.</summary>
public sealed class ConsoleFirewallRecoveryBackend(ISetupPayloadSource source)
{
    private sealed record RecoveryEvidence(VerifiedPartialConsoleReceipt Original, InstallationEvidence Existing, SetupPlan Plan,
        FileEvidence[] Files, AccessEvidence[] Directories);
    private readonly object inspectionGate = new();
    private RecoveryEvidence? inspected;

    public ConsoleFirewallRecoveryInspection Inspect(string receiptPath)
    {
        Guid id = Guid.Empty;
        lock (inspectionGate) inspected = null;
        try
        {
            RequireWindowsAdministrator();
            using var payload = source.Open(SetupComponent.Console);
            PayloadArchive.ValidateManifest(payload.Manifest, SetupComponent.Console);
            var proof = ReadEvidence(receiptPath, payload.Manifest);
            id = proof.Original.Id;
            lock (inspectionGate) inspected = proof;
            var displayPlan = proof.Plan with { AllowedHosts = (string[])proof.Plan.AllowedHosts.Clone(),
                AllowedNetworks = (string[])proof.Plan.AllowedNetworks.Clone(), Servers = (SetupServer[])proof.Plan.Servers.Clone() };
            return new(true, displayPlan, id, proof.Original.ReceiptPath, []);
        }
        catch (Exception exception)
        {
            return new(false, null, id, receiptPath,
                [new(exception is SetupException safe ? safe.Code : "RECOVERY_INSPECTION_UNVERIFIED", SafeFailure(exception))]);
        }
    }

    public Task<SetupOutcome> ExecuteAsync(string receiptPath, IProgress<SetupProgress> progress, CancellationToken token) =>
        // Native mutex ownership and its release stay on the same worker throughout the completion attempt.
        Task.Run(() => Execute(receiptPath, progress, token), CancellationToken.None);

    private SetupOutcome Execute(string receiptPath, IProgress<SetupProgress> progress, CancellationToken token)
    {
        SetupReceipt? receipt = null;
        RecoveryEvidence? proof = null;
        bool claimAttempted = false;
        try
        {
            RequireWindowsAdministrator(); token.ThrowIfCancellationRequested();
            RecoveryEvidence? approved;
            lock (inspectionGate) { approved = inspected; inspected = null; }
            if (approved is null || !approved.Original.ReceiptPath.Equals(receiptPath, StringComparison.OrdinalIgnoreCase))
                throw new SetupException("RECOVERY_INSPECTION_REQUIRED", "须先在本窗口完成只读审查，再明确执行一次收尾。");
            using var mutex = SetupGlobalMutex.Acquire();
            using var payload = source.Open(SetupComponent.Console);
            PayloadArchive.ValidateManifest(payload.Manifest, SetupComponent.Console);
            Report(progress, "RecoveryPreflight", "独立核验原受限回执、已停止服务、完整程序、当前配置和原权限证据。");
            VerifyUnchanged(approved);
            proof = ReadEvidence(receiptPath, payload.Manifest);
            using var originalPin = PathLease.Acquire(proof.Original.ReceiptPath, includeFile: true);
            using var rootPin = PathLease.Acquire(proof.Plan.InstallDirectory);
            using var dataPin = PathLease.Acquire(proof.Plan.DataDirectory);
            using var ledgerPin = PathLease.Acquire(proof.Existing.LedgerDirectory);
            RequireSameInspection(approved, proof);
            VerifyUnchanged(approved);
            VerifyUnchanged(proof); proof.Original.RequireUnclaimed(); RequireMarkerAbsent(proof.Plan);
            SetupFirewall.RequireNoRulesWithPrefix(SetupFirewall.RuleName(proof.Plan));
            token.ThrowIfCancellationRequested();
            receipt = new SetupReceipt(proof.Plan, "CompleteConsoleFirewall", proof.Original.Id);
            receipt.RecordService(proof.Existing.Service);
            receipt.RecordPreservedFiles(proof.Files);
            foreach (var directory in proof.Directories) receipt.RecordAccess(directory);
            receipt.Advance("ClaimRecovery", "Applying");
            Report(progress, "ClaimRecovery", "记录一次性收尾标记和关联回执；未知结果保留证据，不能重复执行。");
            claimAttempted = true;
            CreateNewVerified(proof.Original.GuardPath, JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1, procedure = "CompleteConsoleFirewall", originalReceiptId = proof.Original.Id,
                recoveryReceiptId = receipt.Id, receiptPath = receipt.ReceiptPath
            }, PayloadArchive.JsonOptions));
            using var guardPin = PathLease.Acquire(proof.Original.GuardPath, includeFile: true);
            var guard = SetupInventory.Snapshot(proof.Original.GuardPath);
            VerifyUnchanged(proof); RequireMarkerAbsent(proof.Plan);
            SetupFirewall.RequireNoRulesWithPrefix(SetupFirewall.RuleName(proof.Plan));
            receipt.Advance("Firewall", "Applying");
            Report(progress, "Firewall", "添加修复后的八项内网来源规则，并独立核验程序、服务、端口与来源。");
            SetupFirewall.Add(proof.Plan, receipt.Id, receipt.RecordFirewall);
            SetupFirewall.VerifyRecoveryRule(proof.Plan, receipt.Id);
            VerifyUnchanged(proof); SetupInventory.VerifySnapshots([guard]); RequireMarkerAbsent(proof.Plan);
            receipt.Advance("WriteInstallingMarker", "Applying");
            var markerPath = Path.Combine(proof.Plan.InstallDirectory, SetupInventory.MarkerName);
            CreateNewVerified(markerPath, MarkerBytes(proof.Plan, proof.Existing.RuntimeFiles, receipt.Id, "Installing"));
            var marker = SetupInventory.Snapshot(markerPath);
            VerifyUnchanged(proof); SetupInventory.VerifySnapshots([guard, marker]);
            SetupFirewall.VerifyRecoveryRule(proof.Plan, receipt.Id);
            receipt.Advance("StartService", "Applying");
            Report(progress, "StartService", "程序、配置、权限和已停止服务已再次核验，启动现有控制台服务。");
            WindowsServiceManager.StartAsync(proof.Plan.ServiceName).GetAwaiter().GetResult();
            receipt.Advance("VerifyCompletion", "Applying");
            RequireSameService(proof.Existing.Service, WindowsServiceManager.Read(proof.Plan.ServiceName), 4);
            SetupFirewall.VerifyRecoveryRule(proof.Plan, receipt.Id);
            SetupInventory.VerifySnapshots(proof.Files);
            SetupInventory.VerifyRecoveryTree(proof.Existing);
            VerifyDirectories(proof.Directories);
            SetupInventory.VerifySnapshots([proof.Original.Snapshot, guard, marker]);
            WindowsSetupBackend.WriteMarker(proof.Plan, proof.Existing.RuntimeFiles, receipt.Id, "Complete");
            SetupFileSecurity.RequireSameDescriptor(marker.Sddl, SetupFileSecurity.ReadSddl(markerPath));
            var completedMarker = SetupInventory.Snapshot(markerPath);
            var completed = SetupInventory.Read(SetupComponent.Console, proof.Plan.InstallDirectory, payload.Manifest);
            RequireSameService(proof.Existing.Service, completed.Service, 4);
            SetupInventory.VerifySnapshots(proof.Files);
            SetupInventory.VerifyRecoveryTree(proof.Existing);
            VerifyDirectories(proof.Directories);
            SetupFirewall.VerifyRecoveryRule(proof.Plan, receipt.Id);
            SetupInventory.VerifySnapshots([proof.Original.Snapshot, guard, completedMarker]);
            receipt.RecordService(completed.Service, after: true); receipt.Finish("Complete", "OK");
            Report(progress, "Complete", "防火墙、归属记录和现有服务 Running 状态已核验，原配置与 Token 保留。");
            return new(true, "Complete", "OK", "控制台防火墙收尾完成，现有服务已运行；请继续内网连接及只读业务验收。", receipt.ReceiptPath);
        }
        catch (Exception exception)
        {
            var code = exception is OperationCanceledException ? "CANCELLED" : exception is SetupException safe ? safe.Code : "RECOVERY_OPERATION_UNVERIFIED";
            var state = claimAttempted ? "PartialOrUnknown" : "Failed";
            if (receipt is not null)
            {
                try
                {
                    if (proof is not null) receipt.RecordService(WindowsServiceManager.Read(proof.Plan.ServiceName), after: true);
                    receipt.Finish(state, code, exception);
                }
                catch { state = "Unknown"; code = "RECOVERY_RECEIPT_OR_STATE_UNVERIFIED"; }
            }
            var message = SafeFailure(exception);
            if (claimAttempted) message += " 保留新回执和已生成的一次性标记，先只读核对；停止后续操作，不能自动重放、回滚或覆盖现有配置。";
            return new(false, state, code, message, receipt?.ReceiptPath ?? "");
        }
        finally { receipt?.Dispose(); }
    }

    private static RecoveryEvidence ReadEvidence(string receiptPath, PayloadManifest manifest)
    {
        var original = VerifiedPartialConsoleReceipt.Read(receiptPath);
        var existing = SetupInventory.ReadRecoverableConsole(original, manifest);
        RequireSameService(original.Service, existing.Service, 1);
        var plan = existing.Plan with { Mode = SetupMode.Install, ConfigureFirewall = true, GrantCertificateKeyRead = false };
        var issues = SetupPlanValidator.Validate(plan);
        if (issues.Length != 0) throw new SetupException(issues[0].Code, "现有控制台生产配置不满足收尾条件，保留配置并拒绝写入。");
        SetupFileSecurity.CanonicalPath(plan.DataDirectory);
        SetupFileSecurity.RequireTrustedAncestors(plan.DataDirectory, includeExistingDirectory: false);
        var identity = WindowsServiceManager.ResolveIdentity(plan.ServiceAccount);
        var lastPermissions = original.Permissions.GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToArray();
        if (lastPermissions.Any(p => !p.ReadBackVerified))
            throw new SetupException("RECOVERY_PERMISSION_READBACK_MISSING", "原回执存在未完成权限读回，不能收尾。");
        var directories = new List<AccessEvidence>();
        foreach (var record in lastPermissions)
        {
            var path = SetupFileSecurity.CanonicalPath(record.Path);
            if (!(SetupFileSecurity.Under(plan.InstallDirectory, path) || SetupFileSecurity.Under(plan.DataDirectory, path) ||
                path.Equals(Path.Combine(plan.InstallDirectory, "logs"), StringComparison.OrdinalIgnoreCase) ||
                path.Equals(existing.LedgerDirectory, StringComparison.OrdinalIgnoreCase)))
                throw new SetupException("RECOVERY_PERMISSION_SCOPE_INVALID", "原权限证据包含组件边界之外的路径，拒绝收尾。");
            SetupFileSecurity.RequireRestricted(path, identity);
            var current = SetupFileSecurity.ReadSddl(path);
            SetupFileSecurity.RequireSameDescriptor(record.AfterSddl, current);
            directories.Add(new(path, current, current, true));
        }
        foreach (var required in new[] { plan.InstallDirectory, plan.DataDirectory, Path.Combine(plan.InstallDirectory, "logs"), existing.LedgerDirectory })
            if (!directories.Any(p => p.Path.Equals(required, StringComparison.OrdinalIgnoreCase)))
                throw new SetupException("RECOVERY_PERMISSION_READBACK_MISSING", "原回执未能证明程序、数据、日志和账本的完整权限读回。");
        foreach (var file in manifest.Files)
        {
            var path = SetupFileSecurity.ResolveRelative(plan.InstallDirectory, file.Path);
            SetupFileSecurity.RequireTrustedProgramFile(path);
            SetupInventory.VerifyFile(path, file);
            SetupFileSecurity.ProveAccess(path, identity, file.Kind == PayloadFileKind.Runtime ? FileSystemRights.ReadAndExecute : FileSystemRights.Read);
        }
        foreach (var file in existing.ConfigurationFiles) SetupFileSecurity.ProveAccess(file.Path, identity, FileSystemRights.Read);
        SetupFileSecurity.ProveAccess(Path.Combine(plan.InstallDirectory, "logs"), identity, FileSystemRights.Modify);
        SetupFileSecurity.ProveAccess(existing.LedgerDirectory, identity, FileSystemRights.Modify);
        var local = Path.Combine(plan.InstallDirectory, "appsettings.Local.json");
        if (!existing.ConfigurationFiles.Any(f => f.Path.Equals(local, StringComparison.OrdinalIgnoreCase)))
            throw new SetupException("RECOVERY_LOCAL_CONFIGURATION_MISSING", "原安装的现场配置缺失，不能猜测或重新生成 Token。");
        var stage = Path.Combine(original.DirectoryPath, "stage");
        if (File.Exists(stage)) throw new SetupException("RECOVERY_STAGE_INVALID", "原暂存路径被文件占用。");
        if (Directory.Exists(stage))
        {
            // The old extractor may have used the administrator's personal default file owner.
            // Stage hashes are auxiliary evidence only: no stage file is executed or copied during completion.
            SetupFileSecurity.RequireTrustedAncestors(stage);
            SetupFileSecurity.RequireRestricted(stage);
            foreach (var file in manifest.Files) SetupInventory.VerifyFile(SetupFileSecurity.ResolveRelative(stage, file.Path), file);
        }
        RequireEmptyLedger(existing.LedgerDirectory);
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == plan.Port))
            throw new SetupException("RECOVERY_LISTEN_PORT_OCCUPIED", "原控制台端口已被其他监听使用，拒绝停止其他进程或更改端口。");
        SetupFirewall.RequireNoRulesWithPrefix(SetupFirewall.RuleName(plan));
        SetupFirewall.Preflight(plan);
        var files = existing.RuntimeFiles.Select(f => SetupInventory.Snapshot(SetupFileSecurity.ResolveRelative(plan.InstallDirectory, f.Path)))
            .Concat(existing.ConfigurationFiles).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var proof = new RecoveryEvidence(original, existing, plan, files, directories.ToArray());
        VerifyUnchanged(proof);
        return proof;
    }

    private static void VerifyUnchanged(RecoveryEvidence proof)
    {
        SetupInventory.VerifySnapshots([proof.Original.Snapshot]);
        SetupInventory.VerifySnapshots(proof.Files);
        SetupInventory.VerifyRecoveryTree(proof.Existing);
        VerifyDirectories(proof.Directories);
        SetupFileSecurity.RequireTrustedAncestors(proof.Original.DirectoryPath);
        SetupFileSecurity.RequireTrustedAncestors(proof.Plan.InstallDirectory);
        SetupFileSecurity.RequireTrustedAncestors(proof.Plan.DataDirectory, includeExistingDirectory: false);
        RequireSameService(proof.Existing.Service, WindowsServiceManager.Read(proof.Plan.ServiceName), 1);
        RequireEmptyLedger(proof.Existing.LedgerDirectory);
        SetupFileSecurity.RejectReparseChain(Path.Combine(proof.Plan.InstallDirectory, SetupInventory.MarkerName), true);
    }
    private static void RequireSameInspection(RecoveryEvidence approved, RecoveryEvidence current)
    {
        var before = approved.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        if (before.Count != current.Files.Length)
            throw new SetupException("RECOVERY_INSPECTION_DRIFT", "只读审查后程序或配置文件集合已变化，拒绝收尾。");
        foreach (var file in current.Files)
        {
            if (!before.TryGetValue(file.Path, out var expected) || file.Size != expected.Size || file.Sha256 != expected.Sha256)
                throw new SetupException("RECOVERY_INSPECTION_DRIFT", "只读审查后程序或配置内容已变化，拒绝收尾。");
            SetupFileSecurity.RequireSameDescriptor(expected.Sddl, file.Sddl);
            SetupFileSecurity.RequireSameDescriptor(expected.ParentSddl, file.ParentSddl);
        }
    }
    private static void VerifyDirectories(IEnumerable<AccessEvidence> directories)
    {
        foreach (var record in directories)
        {
            SetupFileSecurity.RejectReparseChain(record.Path, false);
            if (!Directory.Exists(record.Path)) throw new SetupException("RECOVERY_DIRECTORY_CHANGED", "已核验的组件目录身份发生变化。");
            SetupFileSecurity.RequireSameDescriptor(record.AfterSddl, SetupFileSecurity.ReadSddl(record.Path));
        }
    }
    private static void RequireMarkerAbsent(SetupPlan plan)
    {
        var marker = Path.Combine(plan.InstallDirectory, SetupInventory.MarkerName);
        SetupFileSecurity.RejectReparseChain(marker, true);
        if (File.Exists(marker) || Directory.Exists(marker))
            throw new SetupException("RECOVERY_MARKER_ALREADY_EXISTS", "现有归属记录表明安装已进入后续阶段，不能重放这次防火墙收尾。");
    }
    private static void RequireEmptyLedger(string ledger)
    {
        SetupInventory.CheckLedger(ledger);
        foreach (var entry in Directory.EnumerateFileSystemEntries(ledger))
        {
            SetupFileSecurity.RejectReparseChain(entry, false);
            if (!Path.GetFileName(entry).Equals(".console.lock", StringComparison.Ordinal) || !File.Exists(entry) || new FileInfo(entry).Length != 0)
                throw new SetupException("RECOVERY_LEDGER_NOT_EMPTY", "账本存在任务或未知条目，收尾不能启动可能重放任务的服务。");
            using var existingLock = new FileStream(entry, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }
    private static void RequireSameService(ServiceEvidence expected, ServiceEvidence? actual, uint state)
    {
        if (actual is null || actual.Name != "IISManager.Console" || actual.State != state ||
            expected.BinaryPath != actual.BinaryPath || !WindowsServiceManager.SameAccount(expected.Account, actual.Account) ||
            expected.StartType != actual.StartType || expected.ServiceType != actual.ServiceType || state == 1 && actual.ProcessId != 0)
            throw new SetupException("RECOVERY_SERVICE_DRIFT", "控制台服务入口、身份、启动类型或稳定状态已漂移，拒绝收尾。");
    }
    private static byte[] MarkerBytes(SetupPlan plan, PayloadFile[] runtime, Guid generation, string state) =>
        JsonSerializer.SerializeToUtf8Bytes(new InstallationMarker(1, plan.Component.ToString(), plan.InstallDirectory, plan.DataDirectory,
            plan.HostName, SetupPlanValidator.GetEffectiveAllowedNetworks(plan), plan.ServiceAccount, state, generation, runtime,
            plan.TransportMode, plan.SourceAccessMode), PayloadArchive.JsonOptions);
    private static void CreateNewVerified(string path, byte[] bytes)
    {
        SetupFileSecurity.RejectReparseChain(path, true);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
        { stream.Write(bytes); stream.Flush(flushToDisk: true); }
        SetupFileSecurity.SetNewProgramFileOwner(path);
        using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (read.Length != bytes.Length || !SHA256.HashData(read).SequenceEqual(SHA256.HashData(bytes)))
            throw new SetupException("RECOVERY_NEW_FILE_READBACK_MISMATCH", "新增收尾标记未能持久化核验，停止后续操作。");
    }
    private static void RequireWindowsAdministrator()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new SetupException("WINDOWS_X64_REQUIRED", "收尾后端仅支持 Windows x64。");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new SetupException("ADMINISTRATOR_REQUIRED", "收尾须由已提升的管理员进程执行。");
    }
    private static string SafeFailure(Exception exception)
    {
        var summary = SafeFailureSummary.From(exception);
        var reason = exception is SetupException safe ? safe.Message + " " : "";
        return reason + "收尾条件或操作未能核实（" + summary.LeafExceptionType + ", HResult=0x" + summary.LeafHResult.ToString("X8") + "）。";
    }
    private static void Report(IProgress<SetupProgress> progress, string stage, string message)
    { try { progress.Report(new(stage, message)); } catch { /* UI reporting cannot invalidate durable evidence. */ } }
}
