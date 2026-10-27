using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using IISManager.Setup.Core;

namespace IISManager.Setup.Backend;

/// <summary>Local Windows installer executor. No remote deployment, rollback, secret export or task replay.</summary>
public sealed class WindowsSetupBackend(ISetupPayloadSource payloadSource) : ISetupBackend
{
    public SetupCertificate[] GetCertificates()
    {
        RequireWindows();
        try { return SetupCertificates.List(); }
        catch (SetupException) { throw; }
        catch (Exception exception) { throw new SetupException("CERTIFICATE_LIST_FAILED", SafeFailure(exception)); }
    }
    public SetupInspection Inspect(SetupComponent component, string installDirectory)
    {
        try
        {
            RequireWindows();
            if (!Enum.IsDefined(component)) throw new SetupException("COMPONENT_INVALID", "组件无效。");
            var root = SetupFileSecurity.CanonicalPath(installDirectory);
            SetupFileSecurity.RejectReparseChain(root, true);
            var service = WindowsServiceManager.Read("IISManager." + component);
            if (service is null)
            {
                var issue = Directory.Exists(root) || File.Exists(root) ? new[] { new SetupIssue("DIRECTORY_ALREADY_EXISTS", "新装路径已存在，不能证明属于本组件；请选择新目录。") } : [];
                return new(false, "Absent", "", root, null, issue);
            }
            using var payload = payloadSource.Open(component);
            PayloadArchive.ValidateManifest(payload.Manifest, component);
            var existing = SetupInventory.Read(component, root, payload.Manifest);
            return new(true, existing.Service.StateName, existing.Service.Account, root, existing.Plan, []);
        }
        catch (Exception exception)
        {
            var code = exception is SetupException safe ? safe.Code : "INSPECTION_UNVERIFIED";
            var message = exception is SetupException known ? known.Message : SafeFailure(exception);
            return new(false, "Unverified", "", installDirectory, null, [new(code, message)]);
        }
    }

    public Task<SetupOutcome> ExecuteAsync(SetupPlan plan, SecureString? password, IProgress<SetupProgress> progress, CancellationToken cancellationToken) =>
        // The native mutex is thread-owned. A dedicated pool invocation keeps every target mutation and release on this thread.
        Task.Run(() => Execute(plan, password, progress, cancellationToken), CancellationToken.None);

    private SetupOutcome Execute(SetupPlan request, SecureString? password, IProgress<SetupProgress> progress, CancellationToken cancellationToken)
    {
        SetupReceipt? receipt = null; SetupPlan? effective = null; bool targetMutation = false;
        try
        {
            RequireWindows(); RequireAdministrator();
            cancellationToken.ThrowIfCancellationRequested();
            var issues = SetupPlanValidator.Validate(request);
            if (issues.Length != 0) throw new SetupException(issues[0].Code, issues[0].Message);
            using var installationMutex = SetupGlobalMutex.Acquire();
            using var payload = payloadSource.Open(request.Component);
            PayloadArchive.ValidateManifest(payload.Manifest, request.Component);
            Report(progress, "Preflight", "只读核对组件归属、服务、任务账本、连接模式与运行权限。");
            InstallationEvidence? original = null;
            if (request.Mode == SetupMode.Update)
            {
                if (request.GrantCertificateKeyRead || request.ConfigureFirewall)
                    throw new SetupException("UPDATE_PERMISSION_CHANGE_FORBIDDEN", "更新保留原身份、私钥权限和防火墙；权限变更须由操作员单独准备。");
                original = SetupInventory.Read(request.Component, request.InstallDirectory, payload.Manifest, request.HostName);
                if (request.TransportMode != original.Plan.TransportMode)
                    throw new SetupException("UPDATE_TRANSPORT_CHANGE_FORBIDDEN", "更新须保持原连接模式，不能自动迁移或降低已有连接保护。");
                if (request.SourceAccessMode != original.Plan.SourceAccessMode)
                    throw new SetupException("UPDATE_SOURCE_POLICY_CHANGE_FORBIDDEN", "更新须保持原访问来源策略，不能自动改为允许全部内网。");
                effective = original.Plan;
                SetupInventory.CheckLedger(original.LedgerDirectory);
            }
            else
            {
                effective = request with { InstallDirectory = SetupFileSecurity.CanonicalPath(request.InstallDirectory), DataDirectory = SetupFileSecurity.CanonicalPath(request.DataDirectory) };
                PreflightNew(effective, password);
            }
            var networks = SetupPlanValidator.GetEffectiveAllowedNetworks(effective);
            if (effective.SourceAccessMode is not ("Allowlist" or "PrivateNetwork") || networks.Any(n => !SetupPlanValidator.IsNetwork(n)) ||
                (effective.SourceAccessMode == "PrivateNetwork" || effective.Component == SetupComponent.Console || effective.TransportMode == "HttpToken") && networks.Length == 0)
                throw new SetupException("SOURCE_ACCESS_POLICY_INVALID", "实际访问来源策略无法核实，停止安装或更新。");
            if (effective.TransportMode == "Https" && !SetupPlanValidator.IsHost(effective.HostName))
                throw new SetupException("UPDATE_HOST_NAME_REQUIRED", "旧包更新须填写现有访问主机名或 IP，仅用于服务器证书 SAN 核验。");
            var root = effective.InstallDirectory;
            var ledger = original?.LedgerDirectory ?? Path.Combine(effective.DataDirectory, effective.Component == SetupComponent.Agent ? "operations" : "console-tasks");
            var logs = Path.Combine(root, "logs");
            SetupFileSecurity.RequireTrustedAncestors(root);
            SetupFileSecurity.RequireTrustedAncestors(effective.DataDirectory, includeExistingDirectory: false);
            using var rootPin = PathLease.Acquire(root);
            using var dataPin = PathLease.Acquire(effective.DataDirectory);
            CertificateEvidence[] certificates = [];
            if (effective.TransportMode == "Https")
            {
                Report(progress, "Certificates", "核验选定本机证书的 SAN、EKU、私钥、信任链和在线撤销状态。");
                certificates = SetupCertificates.Validate(effective);
            }
            else Report(progress, "ConnectionPolicy", "HTTP 模式核验访问 Token 及实际连接来源限制，无需枚举、校验证书或授权私钥。");
            if (effective.GrantCertificateKeyRead) foreach (var certificate in certificates) SetupFileSecurity.PreflightKeyGrant(certificate.KeyPath);
            SecurityIdentifier? identity = null;
            try { identity = WindowsServiceManager.ResolveIdentity(effective.ServiceAccount); }
            catch (SetupException) when (effective.Mode == SetupMode.Install && effective.IdentityKind == ServiceIdentityKind.VirtualAccount &&
                (effective.GrantCertificateKeyRead || effective.TransportMode == "HttpToken")) { }
            if (!effective.GrantCertificateKeyRead && certificates.Length != 0)
            {
                if (identity is null) throw new SetupException("SERVICE_IDENTITY_UNVERIFIED", "须先证明实际服务 SID 和已有私钥权限，不能在未勾选授权时猜测。");
                foreach (var certificate in certificates) SetupFileSecurity.ProveAccess(certificate.KeyPath, identity, SetupFileSecurity.KeyRead);
            }
            if (original is not null)
            {
                if (identity is null) throw new SetupException("SERVICE_IDENTITY_UNVERIFIED", "原服务身份无法证明。");
                ProveExistingAccess(original, identity, logs);
            }
            SetupFirewall.Preflight(effective);
            CheckDiskSpace(root, payload.Manifest.Files.Where(f => effective.Mode == SetupMode.Install || f.Kind == PayloadFileKind.Runtime).Sum(f => f.Size), original?.RuntimeFiles.Sum(f => f.Size) ?? 0);
            cancellationToken.ThrowIfCancellationRequested();
            receipt = new SetupReceipt(effective);
            receipt.RecordService(original?.Service);
            if (original is not null)
            {
                receipt.RecordPreservedFiles(original.ConfigurationFiles);
                if (original.MarkerEvidence is not null) receipt.RecordPreservedFiles([original.MarkerEvidence]);
            }
            receipt.Advance("Extract");
            Report(progress, "Extract", "将完整自包含载荷提取到管理员和 SYSTEM 专属暂存目录，逐文件核验。");
            payload.ExtractTo(receipt.StageDirectory);
            foreach (var file in payload.Manifest.Files) SetupInventory.VerifyFile(SetupFileSecurity.ResolveRelative(receipt.StageDirectory, file.Path), file);
            cancellationToken.ThrowIfCancellationRequested();
            if (original is null)
            {
                PreflightNew(effective, password); // Recheck the external boundaries immediately before the first target mutation.
                receipt.Advance("CreateDirectories", "Applying");
                Report(progress, "CreateDirectories", "创建本次安装的受限程序、数据、日志和账本目录。"); targetMutation = true;
                var programCreated = SetupFileSecurity.CreateRestrictedTree(root);
                var dataCreated = SetupFileSecurity.CreateRestrictedTree(effective.DataDirectory);
                SetupFileSecurity.CreateDirectoryWithDescriptor(logs, SetupFileSecurity.RestrictedSddl);
                SetupFileSecurity.CreateDirectoryWithDescriptor(ledger, SetupFileSecurity.RestrictedSddl);
                using var newRootPin = PathLease.Acquire(root);
                using var newDataPin = PathLease.Acquire(ledger);
                receipt.Advance("InstallFiles", "Applying");
                Report(progress, "InstallFiles", "写入并核验完整程序载荷与本次新增配置。");
                foreach (var file in payload.Manifest.Files) InstallNewFile(receipt.StageDirectory, root, file);
                var local = Path.Combine(root, "appsettings.Local.json");
                WriteNew(local, Encoding.UTF8.GetBytes(SetupConfiguration.CreateLocalConfiguration(effective)));
                receipt.Advance("RegisterService", "Applying");
                Report(progress, "RegisterService", "注册固定组件服务，并独立读回服务入口和实际身份。");
                WindowsServiceManager.Create(effective, password);
                identity = WindowsServiceManager.ResolveIdentity(effective.ServiceAccount);
                receipt.RecordService(WindowsServiceManager.Read(effective.ServiceName), after: true);
                receipt.Advance("GrantNewDirectories", "Applying");
                Report(progress, "GrantNewDirectories", "仅向实际服务身份授予本次新建目录的必要权限，并核验读回。");
                foreach (var directory in programCreated.Concat(dataCreated).Distinct(StringComparer.OrdinalIgnoreCase))
                    receipt.RecordAccess(SetupFileSecurity.GrantNewDirectory(directory, identity,
                        directory.Equals(effective.DataDirectory, StringComparison.OrdinalIgnoreCase) ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute, receipt.RecordAccess));
                receipt.RecordAccess(SetupFileSecurity.GrantNewDirectory(logs, identity, FileSystemRights.Modify, receipt.RecordAccess));
                receipt.RecordAccess(SetupFileSecurity.GrantNewDirectory(ledger, identity, FileSystemRights.Modify, receipt.RecordAccess));
                foreach (var certificate in certificates)
                {
                    if (effective.GrantCertificateKeyRead)
                    {
                        receipt.Advance("GrantSelectedKey", "Applying");
                        Report(progress, "GrantSelectedKey", "核验并授予所选证书私钥的精确读取权限。");
                        // No ACL write if another installer/operator changed the original evidence.
                        SetupFileSecurity.RequireSameDescriptor(certificate.KeySddl, SetupFileSecurity.ReadSddl(certificate.KeyPath));
                        receipt.RecordAccess(SetupFileSecurity.GrantExactKeyRead(certificate.KeyPath, identity, receipt.RecordAccess));
                    }
                    else SetupFileSecurity.ProveAccess(certificate.KeyPath, identity, SetupFileSecurity.KeyRead);
                }
                receipt.Advance("VerifyIdentityAccess", "Applying");
                Report(progress, "VerifyIdentityAccess", "核验程序、配置、日志和账本对实际服务身份的权限。");
                VerifyReadablePayload(root, payload.Manifest, identity);
                SetupFileSecurity.ProveAccess(local, identity, FileSystemRights.Read);
                SetupFileSecurity.ProveAccess(logs, identity, FileSystemRights.Modify);
                SetupFileSecurity.ProveAccess(ledger, identity, FileSystemRights.Modify);
                if (effective.ConfigureFirewall)
                {
                    receipt.Advance("Firewall", "Applying");
                    Report(progress, "Firewall", "创建精确限定程序、服务、端口和内网来源的防火墙规则，并独立读回。");
                    SetupFirewall.Add(effective, receipt.Id, receipt.RecordFirewall);
                }
                receipt.Advance("WriteInstallingMarker", "Applying");
                Report(progress, "WriteInstallingMarker", "记录本次安装的程序归属，保留既有配置和访问策略。");
                WriteMarker(effective, payload.Manifest.Files.Where(f => f.Kind == PayloadFileKind.Runtime).ToArray(), receipt.Id, "Installing");
                receipt.Advance("StartService", "Applying");
                Report(progress, "StartService", "服务配置和权限已核验，启动后独立读回 SCM 状态。");
                WindowsServiceManager.StartAsync(effective.ServiceName).GetAwaiter().GetResult();
            }
            else
            {
                SetupInventory.VerifySnapshots(original.ConfigurationFiles);
                if (original.MarkerEvidence is not null) SetupInventory.VerifySnapshots([original.MarkerEvidence]);
                foreach (var file in original.RuntimeFiles) SetupInventory.VerifyFile(SetupFileSecurity.ResolveRelative(root, file.Path), file);
                SetupInventory.CheckLedger(ledger);
                RequireSameService(original.Service, WindowsServiceManager.Read(effective.ServiceName));
                receipt.Advance("StopService", "Applying"); targetMutation = true;
                Report(progress, "StopService", "未发现未完成任务，停止目标服务并核对账本独占锁。");
                WindowsServiceManager.StopAsync(effective.ServiceName).GetAwaiter().GetResult();
                using (var ledgerLock = SetupInventory.LockStoppedLedger(effective.Component, ledger))
                {
                    using var ledgerPin = PathLease.Acquire(ledger);
                    SetupInventory.CheckLedger(ledger);
                    SetupInventory.VerifySnapshots(original.ConfigurationFiles);
                    if (original.MarkerEvidence is not null) SetupInventory.VerifySnapshots([original.MarkerEvidence]);
                    foreach (var file in original.RuntimeFiles) SetupInventory.VerifyFile(SetupFileSecurity.ResolveRelative(root, file.Path), file);
                    RequireSameService(original.Service, WindowsServiceManager.Read(effective.ServiceName), state: 1);
                    receipt.Advance("BackupRuntime", "Applying");
                    Report(progress, "BackupRuntime", "受限备份原有程序和完整文件权限记录。");
                    var snapshots = original.RuntimeFiles.Select(f => SetupInventory.Snapshot(SetupFileSecurity.ResolveRelative(root, f.Path))).ToArray();
                    receipt.RecordFiles(snapshots);
                    foreach (var file in original.RuntimeFiles) BackupRuntime(root, receipt.BackupDirectory, file);
                    if (original.Marker is not null) BackupMarker(root, receipt.BackupDirectory);
                    WriteMarker(effective, original.RuntimeFiles, receipt.Id, "Updating");
                    receipt.Advance("ReplaceRuntime", "Applying");
                    Report(progress, "ReplaceRuntime", "旧运行文件与权限记录已受限备份，只替换运行载荷，保留全部现场配置和账本。");
                    foreach (var file in payload.Manifest.Files.Where(f => f.Kind == PayloadFileKind.Runtime))
                        ReplaceRuntime(receipt.StageDirectory, root, file, snapshots, identity!, receipt);
                    // Known retired runtime files are retained and still tracked. No unregistered file is deleted.
                    SetupInventory.VerifySnapshots(original.ConfigurationFiles);
                    SetupInventory.CheckLedger(ledger);
                    VerifyReadablePayload(root, payload.Manifest, identity!, runtimeOnly: true);
                    foreach (var certificate in certificates)
                    {
                        SetupFileSecurity.RequireSameDescriptor(certificate.KeySddl, SetupFileSecurity.ReadSddl(certificate.KeyPath));
                        SetupFileSecurity.ProveAccess(certificate.KeyPath, identity!, SetupFileSecurity.KeyRead);
                    }
                }
                // Release the ledger lock only after every file is verified; the service needs the same lock during start.
                if (original.Service.State == 4)
                {
                    receipt.Advance("StartService", "Applying");
                    Report(progress, "StartService", "运行文件与权限已核验，恢复更新前运行状态并独立读回服务状态。");
                    WindowsServiceManager.StartAsync(effective.ServiceName).GetAwaiter().GetResult();
                }
            }
            receipt.Advance("VerifyCompletion", "Applying");
            Report(progress, "VerifyCompletion", "独立核验服务运行状态、程序基线及完成归属记录。");
            var finalService = WindowsServiceManager.Read(effective.ServiceName) ?? throw new SetupException("SERVICE_READBACK_MISSING", "完成读回时服务不存在。");
            var expectedState = original?.Service.State ?? 4;
            if (finalService.State != expectedState || !finalService.BinaryPath.Equals(WindowsServiceManager.BinaryPath(effective), StringComparison.OrdinalIgnoreCase) ||
                !WindowsServiceManager.SameAccount(finalService.Account, effective.ServiceAccount)) throw new SetupException("SERVICE_READBACK_MISMATCH", "服务运行状态、入口或身份与计划不符。");
            if (original is not null) RequireSameService(original.Service, finalService, expectedState);
            else if (finalService.StartType != 2 || finalService.ServiceType != 16) throw new SetupException("SERVICE_READBACK_MISMATCH", "新服务启动类型或进程类型与计划不符。");
            if (original is not null) SetupInventory.VerifySnapshots(original.ConfigurationFiles);
            foreach (var file in payload.Manifest.Files.Where(f => f.Kind == PayloadFileKind.Runtime)) SetupInventory.VerifyFile(SetupFileSecurity.ResolveRelative(root, file.Path), file);
            var currentRuntime = payload.Manifest.Files.Where(f => f.Kind == PayloadFileKind.Runtime)
                .Concat(original?.RuntimeFiles.Where(old => !payload.Manifest.Files.Any(n => n.Kind == PayloadFileKind.Runtime && n.Path.Equals(old.Path, StringComparison.OrdinalIgnoreCase))) ?? [])
                .OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
            WriteMarker(effective, currentRuntime, receipt.Id, "Complete");
            receipt.RecordService(finalService, after: true); receipt.Finish("Complete", "OK");
            Report(progress, "Complete", "文件、权限及服务运行状态已核验；仍需 Windows 上的连接认证、来源限制与只读业务验收。");
            return new(true, "Complete", "OK", "本地安装执行完成，独立读回服务 " + finalService.StateName + "；请继续连接认证、来源限制、实际身份和只读业务验收。", receipt.ReceiptPath);
        }
        catch (Exception exception)
        {
            var code = exception is OperationCanceledException ? "CANCELLED" : exception is SetupException safe ? safe.Code : "WINDOWS_OPERATION_UNVERIFIED";
            var state = targetMutation ? "PartialOrUnknown" : "Failed";
            // Read-only observation after an uncertain write. Never restart, restore permissions or retry the operation here.
            if (receipt is not null)
            {
                try { if (effective is not null) receipt.RecordService(WindowsServiceManager.Read(effective.ServiceName), after: true); receipt.Finish(state, code, exception); }
                catch { state = "Unknown"; code = "RECEIPT_OR_STATE_UNVERIFIED"; }
            }
            var message = exception is SetupException known ? known.Message + " " + SafeFailure(exception) : exception is OperationCanceledException ? "安装已取消。" : SafeFailure(exception);
            if (targetMutation) message += " 已停止后续写入；保留受限备份和回执，请先只读核实，不自动回滚或重放。";
            return new(false, state, code, message, receipt?.ReceiptPath ?? "");
        }
        finally { receipt?.Dispose(); }
    }

    private static void PreflightNew(SetupPlan plan, SecureString? password)
    {
        SetupFileSecurity.RejectReparseChain(plan.InstallDirectory, true); SetupFileSecurity.RejectReparseChain(plan.DataDirectory, true);
        SetupFileSecurity.RequireTrustedAncestors(plan.InstallDirectory);
        SetupFileSecurity.RequireTrustedAncestors(plan.DataDirectory, includeExistingDirectory: false);
        if (Directory.Exists(plan.InstallDirectory) || File.Exists(plan.InstallDirectory) || Directory.Exists(plan.DataDirectory) || File.Exists(plan.DataDirectory))
            throw new SetupException("DIRECTORY_ALREADY_EXISTS", "新装程序或数据路径已存在，不能证明属于本组件；不重置或覆盖既有目录。");
        if (WindowsServiceManager.Read(plan.ServiceName) is not null) throw new SetupException("SERVICE_ALREADY_EXISTS", "固定服务名已存在，请使用经过归属核验的更新模式。");
        if (plan.Component == SetupComponent.Console && plan.IdentityKind == ServiceIdentityKind.LocalSystem)
            throw new SetupException("CONSOLE_IDENTITY_TOO_PRIVILEGED", "控制台请选择虚拟服务账号或已有最小权限账号。");
        WindowsServiceManager.ValidateExistingAccount(plan, password);
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == plan.Port))
            throw new SetupException("LISTEN_PORT_OCCUPIED", "计划端口已被本机监听使用，拒绝覆盖或停止其他进程。");
        if (plan.Component == SetupComponent.Agent)
        {
            var iis = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv");
            if (!new[] { "iisutil.dll", "nativerd.dll", "config/applicationHost.config" }.All(relative => File.Exists(Path.Combine(iis, relative.Replace('/', Path.DirectorySeparatorChar)))))
                throw new SetupException("IIS_COMPONENTS_REQUIRED", "Agent 需要已准备好的 IIS/WAS 管理组件与本机配置；安装器不自动安装 OS 角色。");
            foreach (var allowed in plan.AllowedSiteRoots)
            {
                SetupFileSecurity.RejectReparseChain(allowed, false);
                if (!Directory.Exists(allowed)) throw new SetupException("SITE_ROOT_NOT_DIRECTORY", "允许的站点根必须是已有目录，不能使用文件。");
            }
        }
    }
    private static void ProveExistingAccess(InstallationEvidence existing, SecurityIdentifier identity, string logs)
    {
        SetupFileSecurity.ProveAccess(existing.Plan.InstallDirectory, identity, FileSystemRights.ReadAndExecute);
        SetupFileSecurity.ProveAccess(logs, identity, FileSystemRights.Modify);
        SetupFileSecurity.ProveAccess(existing.LedgerDirectory, identity, FileSystemRights.Modify);
        foreach (var file in existing.ConfigurationFiles) SetupFileSecurity.ProveAccess(file.Path, identity, FileSystemRights.Read);
        foreach (var file in existing.RuntimeFiles) SetupFileSecurity.ProveAccess(SetupFileSecurity.ResolveRelative(existing.Plan.InstallDirectory, file.Path), identity, FileSystemRights.ReadAndExecute);
    }
    private static void VerifyReadablePayload(string root, PayloadManifest manifest, SecurityIdentifier identity, bool runtimeOnly = false)
    {
        foreach (var file in manifest.Files.Where(f => !runtimeOnly || f.Kind == PayloadFileKind.Runtime))
        {
            var path = SetupFileSecurity.ResolveRelative(root, file.Path); SetupInventory.VerifyFile(path, file);
            SetupFileSecurity.RequireTrustedProgramFile(path);
            SetupFileSecurity.ProveAccess(path, identity, file.Kind == PayloadFileKind.Runtime ? FileSystemRights.ReadAndExecute : FileSystemRights.Read);
        }
    }
    private static void CheckDiskSpace(string root, long payloadSize, long backupSize)
    {
        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        var receiptDrive = new DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))!);
        var sharedVolume = drive.Name.Equals(receiptDrive.Name, StringComparison.OrdinalIgnoreCase);
        if (!drive.IsReady || !receiptDrive.IsReady || drive.AvailableFreeSpace < payloadSize + 256L * 1024 * 1024 ||
            receiptDrive.AvailableFreeSpace < (sharedVolume ? 2 * payloadSize : payloadSize) + backupSize + 256L * 1024 * 1024)
            throw new SetupException("DISK_SPACE_INSUFFICIENT", "程序卷或受限暂存/备份卷可用空间不足。");
    }
    private static void InstallNewFile(string stage, string root, PayloadFile file)
    {
        var destination = SetupFileSecurity.ResolveRelative(root, file.Path); var parent = Path.GetDirectoryName(destination)!;
        SetupFileSecurity.RejectReparseChain(destination, true); Directory.CreateDirectory(parent);
        using var parentPin = PathLease.Acquire(parent);
        CopyNew(SetupFileSecurity.ResolveRelative(stage, file.Path), destination);
        SetupFileSecurity.SetNewProgramFileOwner(destination); SetupInventory.VerifyFile(destination, file);
    }
    internal static void BackupRuntime(string root, string backup, PayloadFile file)
    {
        var target = SetupFileSecurity.ResolveRelative(backup, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        CopyNew(SetupFileSecurity.ResolveRelative(root, file.Path), target);
        SetupFileSecurity.SetNewProgramFileOwner(target); SetupInventory.VerifyFile(target, file);
    }
    internal static void BackupMarker(string root, string backup)
    {
        var from = Path.Combine(root, SetupInventory.MarkerName); var to = Path.Combine(backup, SetupInventory.MarkerName);
        CopyNew(from, to);
        SetupFileSecurity.SetNewProgramFileOwner(to);
        var expected = SetupInventory.Snapshot(from); var actual = SetupInventory.Snapshot(to);
        if (expected.Size != actual.Size || expected.Sha256 != actual.Sha256) throw new SetupException("BACKUP_READBACK_MISMATCH", "归属记录备份未能核验。");
    }
    internal static void ReplaceRuntime(string stage, string root, PayloadFile file, FileEvidence[] originals, SecurityIdentifier identity, SetupReceipt receipt)
    {
        if (file.Kind != PayloadFileKind.Runtime) throw new SetupException("RUNTIME_FILE_KIND_INVALID", "更新替换仅接受运行载荷，不能覆盖现场配置。");
        var target = SetupFileSecurity.ResolveRelative(root, file.Path); var parent = Path.GetDirectoryName(target)!;
        receipt.RecordRuntimeStep(file.Path, "VerifyExistingTarget");
        SetupFileSecurity.RejectReparseChain(target, true);
        var before = originals.SingleOrDefault(f => f.Path.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (!Directory.Exists(parent))
        {
            if (before is not null) throw new SetupException("RUNTIME_CHANGED_DURING_UPDATE", "已登记运行文件的父目录在备份后缺失，停止覆盖。");
            receipt.RecordRuntimeStep(file.Path, "PrepareParent");
            CreateRuntimeParent(parent, identity);
        }
        using var parentPin = PathLease.Acquire(parent);
        SetupFileSecurity.RequireTrustedAncestors(parent);
        var parentSddl = SetupFileSecurity.ReadSddl(parent);
        if ((File.Exists(target) || Directory.Exists(target)) && before is null)
            throw new SetupException("RUNTIME_FILE_UNREGISTERED", "新载荷目标已被未登记文件或目录占用。");
        if (before is not null)
        {
            var current = SetupInventory.Snapshot(target);
            if (current.Size != before.Size || !current.Sha256.Equals(before.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new SetupException("RUNTIME_CHANGED_DURING_UPDATE", "运行文件在备份后漂移，停止覆盖。");
            SetupFileSecurity.RequireSameDescriptor(before.Sddl, current.Sddl); SetupFileSecurity.RequireSameDescriptor(before.ParentSddl, current.ParentSddl);
            SetupFileSecurity.RequireSameDescriptor(before.ParentSddl, parentSddl);
            SetupFileSecurity.RequireTrustedProgramFile(target);
            SetupFileSecurity.ProveAccess(target, identity, FileSystemRights.ReadAndExecute);
            if (current.Size == file.Size && current.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                SetupFileSecurity.RequireSameDescriptor(parentSddl, SetupFileSecurity.ReadSddl(parent));
                receipt.RecordRuntimeStep(file.Path, "PreservedVerified");
                return;
            }
        }
        var temporary = Path.Combine(parent, "." + Guid.NewGuid().ToString("N") + ".setup-tmp");
        receipt.RecordRuntimeStep(file.Path, "PrepareTemporary");
        CopyNew(SetupFileSecurity.ResolveRelative(stage, file.Path), temporary);
        receipt.RecordRuntimeStep(file.Path, "SetTemporaryOwner");
        SetupFileSecurity.SetNewProgramFileOwner(temporary);
        if (before is not null)
        {
            receipt.RecordAccess(new(target, before.Sddl, before.Sddl));
            receipt.RecordRuntimeStep(file.Path, "ApplyDescriptor");
            if (!SameDescriptor(before.Sddl, SetupFileSecurity.ReadSddl(temporary)))
            {
                var security = new FileSecurity(); security.SetSecurityDescriptorSddlForm(before.Sddl, AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
                new FileInfo(temporary).SetAccessControl(security);
            }
            receipt.RecordRuntimeStep(file.Path, "VerifyDescriptor");
            SetupFileSecurity.RequireSameDescriptor(before.Sddl, SetupFileSecurity.ReadSddl(temporary));
        }
        receipt.RecordRuntimeStep(file.Path, "VerifyTemporary");
        SetupInventory.VerifyFile(temporary, file); SetupFileSecurity.ProveAccess(temporary, identity, FileSystemRights.ReadAndExecute);
        var temporarySddl = SetupFileSecurity.ReadSddl(temporary);
        if (before is not null) SetupInventory.VerifySnapshots([before]);
        SetupFileSecurity.RequireSameDescriptor(parentSddl, SetupFileSecurity.ReadSddl(parent));
        receipt.RecordRuntimeStep(file.Path, "ReplaceTarget");
        File.Move(temporary, target, overwrite: before is not null);
        receipt.RecordRuntimeStep(file.Path, "VerifyTarget");
        SetupInventory.VerifyFile(target, file);
        var afterSddl = SetupFileSecurity.ReadSddl(target);
        SetupFileSecurity.RequireSameDescriptor(temporarySddl, afterSddl);
        SetupFileSecurity.RequireSameDescriptor(parentSddl, SetupFileSecurity.ReadSddl(parent));
        SetupFileSecurity.RequireTrustedProgramFile(target);
        SetupFileSecurity.ProveAccess(target, identity, FileSystemRights.ReadAndExecute);
        if (before is not null)
        {
            SetupFileSecurity.RequireSameDescriptor(before.Sddl, afterSddl);
            receipt.RecordAccess(new(target, before.Sddl, afterSddl, true));
        }
        receipt.RecordRuntimeStep(file.Path, "TargetVerified");
    }
    private static void CreateRuntimeParent(string requested, SecurityIdentifier identity)
    {
        SetupFileSecurity.RejectReparseChain(requested, true);
        var absent = new Stack<string>();
        var existing = requested;
        while (!Directory.Exists(existing))
        {
            if (File.Exists(existing)) throw new SetupException("RUNTIME_PARENT_OCCUPIED", "运行资源父目录路径被文件占用，停止创建。");
            absent.Push(existing);
            existing = Path.GetDirectoryName(existing) ?? throw new SetupException("RUNTIME_PARENT_MISSING", "运行资源父目录没有已存在祖先。");
        }
        using var ancestorPin = PathLease.Acquire(existing);
        SetupFileSecurity.RequireTrustedAncestors(existing);
        SetupFileSecurity.ProveAccess(existing, identity, FileSystemRights.ReadAndExecute);
        while (absent.Count > 0)
        {
            var next = absent.Pop(); var parent = Path.GetDirectoryName(next)!;
            using var parentPin = PathLease.Acquire(parent);
            var parentSddl = SetupFileSecurity.ReadSddl(parent);
            // Specify only Owner/Group. Windows inherits the verified parent's DACL;
            // no existing directory is reset, and CreateDirectory failure stops this attempt.
            SetupFileSecurity.CreateDirectoryWithDescriptor(next, "O:BAG:BA");
            using var newPin = PathLease.Acquire(next);
            SetupFileSecurity.RequireSameDescriptor(parentSddl, SetupFileSecurity.ReadSddl(parent));
            var descriptor = new RawSecurityDescriptor(SetupFileSecurity.ReadSddl(next));
            if (!Equals(descriptor.Owner, SetupFileSecurity.Administrators) || !Equals(descriptor.Group, SetupFileSecurity.Administrators))
                throw new SetupException("RUNTIME_DIRECTORY_OWNER_UNVERIFIED", "新运行资源目录的 Owner/Group 与明确指定值不符，停止写入。");
            SetupFileSecurity.RequireTrustedAncestors(next);
            SetupFileSecurity.ProveAccess(next, identity, FileSystemRights.ReadAndExecute);
        }
    }
    private static bool SameDescriptor(string expected, string actual)
    {
        try { SetupFileSecurity.RequireSameDescriptor(expected, actual); return true; }
        catch (SetupException exception) when (exception.Code == "ACL_READBACK_MISMATCH") { return false; }
    }
    private static void CopyNew(string source, string target)
    {
        SetupFileSecurity.RejectReparseChain(source, false); SetupFileSecurity.RejectReparseChain(target, true);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
        input.CopyTo(output); output.Flush(flushToDisk: true);
    }
    private static void WriteNew(string target, byte[] bytes)
    {
        SetupFileSecurity.RejectReparseChain(target, true);
        using (var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(flushToDisk: true); }
        SetupFileSecurity.SetNewProgramFileOwner(target);
        using var read = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (read.Length != bytes.Length || !SHA256.HashData(read).SequenceEqual(SHA256.HashData(bytes))) throw new SetupException("CONFIGURATION_READBACK_MISMATCH", "新增配置持久化读回不符，停止写入。");
    }
    internal static void WriteMarker(SetupPlan plan, PayloadFile[] runtime, Guid generation, string state)
    {
        var marker = new InstallationMarker(1, plan.Component.ToString(), plan.InstallDirectory, plan.DataDirectory, plan.HostName,
            SetupPlanValidator.GetEffectiveAllowedNetworks(plan), plan.ServiceAccount, state, generation, runtime, plan.TransportMode, plan.SourceAccessMode);
        SetupReceipt.DurableJson(Path.Combine(plan.InstallDirectory, SetupInventory.MarkerName), JsonSerializer.SerializeToUtf8Bytes(marker, PayloadArchive.JsonOptions));
    }
    internal static void RequireSameService(ServiceEvidence before, ServiceEvidence? after, uint? state = null)
    {
        if (after is null || before.BinaryPath != after.BinaryPath || !WindowsServiceManager.SameAccount(before.Account, after.Account) ||
            before.StartType != after.StartType || before.ServiceType != after.ServiceType || after.State != (state ?? before.State))
            throw new SetupException("SERVICE_CHANGED_DURING_UPDATE", "服务入口、账号、启动类型或状态已漂移，停止更新。");
    }
    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new SetupException("WINDOWS_X64_REQUIRED", "安装执行后端只支持 Windows x64；当前机器不能执行安装。");
    }
    private static void RequireAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new SetupException("ADMINISTRATOR_REQUIRED", "安装需要已提升的管理员进程。");
    }
    private static string SafeFailure(Exception exception)
    {
        var summary = SafeFailureSummary.From(exception);
        return "Windows 操作未能核实（" + summary.LeafExceptionType + ", HResult=0x" + summary.LeafHResult.ToString("X8") + "）；请先只读检查现场。";
    }
    private static void Report(IProgress<SetupProgress> progress, string stage, string message)
    {
        try { progress.Report(new(stage, message)); } catch { /* UI reporting must not interrupt a persisted operation. */ }
    }
}
