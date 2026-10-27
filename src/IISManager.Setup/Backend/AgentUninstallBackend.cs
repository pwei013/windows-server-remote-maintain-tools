using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using IISManager.Setup.Core;

namespace IISManager.Setup.Backend;

/// <summary>Local Agent service removal only. Program/data, IIS, certificates, ACLs and firewall remain unchanged.</summary>
public sealed class AgentUninstallBackend : IAgentUninstallBackend
{
    public AgentUninstallInspection Inspect(string installDirectory)
    {
        try
        {
            RequireWindows();
            return ReadEvidence(installDirectory).Review;
        }
        catch (Exception exception)
        {
            var code = exception is SetupException setup ? setup.Code : "UNINSTALL_INSPECTION_UNVERIFIED";
            return new(false, false, "Unverified", "", installDirectory, "", "",
                [new(code, SafeMessage(exception))]);
        }
    }

    public Task<SetupOutcome> UninstallAsync(AgentUninstallRequest request, IProgress<SetupProgress> progress,
        CancellationToken cancellationToken) => Task.Run(() => Execute(request, progress, cancellationToken), CancellationToken.None);

    private static SetupOutcome Execute(AgentUninstallRequest request, IProgress<SetupProgress> progress, CancellationToken cancellationToken)
    {
        SetupReceipt? receipt = null;
        var targetMutation = false;
        var pins = new List<IDisposable>();
        try
        {
            RequireWindows();
            using var executingIdentity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(executingIdentity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new SetupException("ADMINISTRATOR_REQUIRED", "卸载 Agent 服务需要由操作员以管理员身份运行。");
            cancellationToken.ThrowIfCancellationRequested();
            using var installationMutex = SetupGlobalMutex.Acquire();
            var before = ReadEvidence(request.InstallDirectory);
            AgentUninstallPolicy.RequireReview(request, before.Review);
            pins.Add(PathLease.Acquire(before.Root, guardDirectories: true));
            pins.Add(PathLease.Acquire(before.Data, guardDirectories: true));
            pins.Add(PathLease.Acquire(before.Ledger, guardDirectories: true));
            pins.Add(PathLease.Acquire(Path.Combine(before.Ledger, ".owner.lock"), includeFile: true, guardDirectories: true));
            foreach (var file in before.Files)
            {
                pins.Add(PathLease.Acquire(file.Path, includeFile: true, guardDirectories: true));
                // Keep reviewed configuration and marker bytes fixed while SCM may be mutated.
                pins.Add(new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
            before = ReadEvidence(request.InstallDirectory);
            AgentUninstallPolicy.RequireReview(request, before.Review);
            var plan = new SetupPlan { Component = SetupComponent.Agent, InstallDirectory = before.Root, DataDirectory = before.Data };
            receipt = new SetupReceipt(plan, procedure: "UninstallAgent", operationMode: "Uninstall");
            receipt.RecordService(before.Service);
            receipt.RecordPreservedFiles(before.Files);
            receipt.Advance("ReviewAgentService");
            Report(progress, "Preflight", "只读核对固定 Agent 服务、配置来源、安装目录和任务账本；保留全部文件、防火墙、证书与 IIS 资源。");
            var operations = new UninstallOperations(before, receipt, progress, () => targetMutation = true);
            AgentUninstallWorkflow.Execute(request, operations, cancellationToken);
            receipt.RecordService(null, after: true);
            receipt.Finish("Complete", "AGENT_SERVICE_REMOVED");
            Report(progress, "Complete", "已独立核对 Agent 服务不存在；程序、配置、日志、账本、防火墙、证书和 IIS 资源均保留。");
            return new(true, "Complete", "AGENT_SERVICE_REMOVED", "Agent 服务已移除，全部程序和数据文件保留。防火墙规则、证书、权限及受管 IIS 资源未修改。", receipt.ReceiptPath);
        }
        catch (Exception exception)
        {
            var state = targetMutation ? "PartialOrUnknown" : "Failed";
            var code = exception is OperationCanceledException ? "CANCELLED" : exception is SetupException setup ? setup.Code : "UNINSTALL_RESULT_UNVERIFIED";
            if (receipt is not null)
            {
                try
                {
                    receipt.RecordService(WindowsServiceManager.Read(AgentUninstallPolicy.ServiceName), after: true);
                    receipt.Finish(state, code, exception);
                }
                catch
                {
                    // Even when an independent read is denied/marked-for-deletion, persist the original failure.
                    try { receipt.Finish(state, code, exception); }
                    catch { state = "Unknown"; code = "RECEIPT_OR_STATE_UNVERIFIED"; }
                }
            }
            var message = SafeMessage(exception);
            if (targetMutation) message += " 已停止后续写入，请保留回执并先只读核实服务实际状态；不自动重放、回滚或重新启动。";
            return new(false, state, code, message, receipt?.ReceiptPath ?? "");
        }
        finally
        {
            receipt?.Dispose();
            for (var index = pins.Count - 1; index >= 0; index--) pins[index].Dispose();
        }
    }

    private sealed record Evidence(string Root, string Data, string Ledger, ServiceEvidence Service, FileEvidence[] Files,
        string RootSddl, string DataSddl, string LedgerSddl, string RootIdentity, string DataIdentity, string LedgerIdentity, string OwnerLockIdentity)
    {
        internal string Fingerprint(ServiceEvidence? service = null) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { Root, Data, Ledger, Service = service ?? Service, Files, RootSddl, DataSddl, LedgerSddl,
                RootIdentity, DataIdentity, LedgerIdentity, OwnerLockIdentity }, PayloadArchive.JsonOptions)));
        internal AgentUninstallInspection Review => new(true, true, Service.StateName, Service.Account, Root, Data, Fingerprint(), []);
    }

    private static Evidence ReadEvidence(string installDirectory)
    {
        var root = SetupFileSecurity.CanonicalPath(installDirectory);
        var service = WindowsServiceManager.Read(AgentUninstallPolicy.ServiceName) ??
            throw new SetupException("AGENT_SERVICE_NOT_FOUND", "未找到固定 Agent 服务；没有执行卸载，请先核对是否已被移除。");
        AgentUninstallPolicy.RequireService(new(service.Name, service.BinaryPath, service.Account, service.StartType, service.ServiceType, service.State, service.ProcessId), root);
        SetupFileSecurity.RequireTrustedAncestors(root);
        using var rootPin = PathLease.Acquire(root, guardDirectories: true);
        RejectConfigurationOverrides(new SetupPlan { Component = SetupComponent.Agent, InstallDirectory = root });
        var executable = Path.Combine(root, "IISManager.Agent.exe");
        SetupFileSecurity.RequireTrustedProgramFile(executable);
        var paths = new List<string> { executable };
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "appsettings.json", "appsettings.Production.json", "appsettings.Local.json" })
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path))
            {
                if (name == "appsettings.json") throw new SetupException("CONFIGURATION_UNVERIFIED", "Agent 配置缺失，无法核实账本来源，停止卸载。");
                continue;
            }
            SetupFileSecurity.RequireTrustedProgramFile(path);
            paths.Add(path);
            using var stream = SetupInventory.ReadLimited(path);
            using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 64 });
            SetupInventory.Flatten(json.RootElement, "", values);
        }
        if (!values.TryGetValue("Agent:Mode", out var mode) || mode != "Windows" ||
            !values.TryGetValue("Agent:StateDirectory", out var ledgerRaw) || string.IsNullOrWhiteSpace(ledgerRaw))
            throw new SetupException("UNINSTALL_LEDGER_SOURCE_UNVERIFIED", "Agent 模式或任务账本来源无法核实，停止卸载。");
        var ledger = SetupFileSecurity.CanonicalPath(Path.IsPathFullyQualified(ledgerRaw) ? ledgerRaw : Path.GetFullPath(Path.Combine(root, ledgerRaw)));
        if (ledger.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new SetupException("UNINSTALL_LEDGER_SOURCE_UNVERIFIED", "任务账本不能使用程序根目录。");
        var data = Path.GetDirectoryName(ledger)!;
        var markerPath = Path.Combine(root, SetupInventory.MarkerName);
        if (File.Exists(markerPath))
        {
            SetupFileSecurity.RequireTrustedProgramFile(markerPath);
            using var markerStream = SetupInventory.ReadLimited(markerPath);
            var marker = JsonSerializer.Deserialize<InstallationMarker>(markerStream, PayloadArchive.JsonOptions);
            if (marker is null || marker.Version != 1 || marker.Component != "Agent" || marker.State != "Complete" || marker.Generation == Guid.Empty ||
                !string.Equals(marker.InstallDirectory, root, StringComparison.OrdinalIgnoreCase) ||
                !WindowsServiceManager.SameAccount(marker.Account, service.Account) ||
                !string.Equals(marker.DataDirectory, data, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ledger, Path.Combine(data, "operations"), StringComparison.OrdinalIgnoreCase))
                throw new SetupException("UNINSTALL_MARKER_UNVERIFIED", "现有安装归属、服务身份或账本目录不符；部分安装须先只读核实，停止卸载。");
            // RuntimeFiles intentionally is not checked: a correctly managed Agent may have received manual incremental binaries.
            paths.Add(markerPath);
        }
        SetupFileSecurity.RejectReparseChain(data, false);
        SetupFileSecurity.RejectReparseChain(ledger, false);
        SetupFileSecurity.RequireTrustedAncestors(data, includeExistingDirectory: false);
        var identity = WindowsServiceManager.ResolveIdentity(service.Account);
        SetupFileSecurity.RequireRestricted(data, identity);
        SetupFileSecurity.RequireRestricted(ledger, identity);
        using var dataPin = PathLease.Acquire(data, guardDirectories: true);
        using var ledgerPin = PathLease.Acquire(ledger, guardDirectories: true);
        CheckLedger(ledger);
        var ownerLock = Path.Combine(ledger, ".owner.lock");
        SetupFileSecurity.RejectReparseChain(ownerLock, false);
        if (!File.Exists(ownerLock) || Directory.Exists(ownerLock))
            throw new SetupException("UNINSTALL_OWNER_LOCK_UNVERIFIED", "Agent 原有账本锁文件不存在或类型不符，停止卸载；不创建替代锁。");
        using var ownerLockPin = PathLease.Acquire(ownerLock, includeFile: true, guardDirectories: true);
        return new(root, data, ledger, service, paths.Select(SetupInventory.Snapshot).ToArray(),
            SetupFileSecurity.ReadSddl(root), SetupFileSecurity.ReadSddl(data), SetupFileSecurity.ReadSddl(ledger),
            rootPin.LeafIdentity, dataPin.LeafIdentity, ledgerPin.LeafIdentity, ownerLockPin.LeafIdentity);
    }

    private static void RejectConfigurationOverrides(SetupPlan plan)
    {
        SetupInventory.RejectEnvironmentOverrides(plan);
        static bool Relevant(string name) => name.StartsWith("Agent__", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Agent:", StringComparison.OrdinalIgnoreCase) ||
            new[] { "DOTNET_", "ASPNETCORE_" }.Any(prefix => name.StartsWith(prefix + "Agent__", StringComparison.OrdinalIgnoreCase) || name.StartsWith(prefix + "Agent:", StringComparison.OrdinalIgnoreCase));
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.Machine })
        {
            var environment = Environment.GetEnvironmentVariables(target);
            if (environment.Keys.Cast<string>().Any(Relevant) ||
                new[] { "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT" }.Any(name => environment[name] is string value && !string.IsNullOrWhiteSpace(value) && value != "Production"))
                throw new SetupException("UNINSTALL_CONFIGURATION_ENVIRONMENT_UNVERIFIED", "进程或机器环境存在 Agent 配置覆盖，不能根据 JSON 推断实际账本；停止卸载。");
        }
    }

    private static void CheckLedger(string ledger)
    {
        SetupFileSecurity.RejectReparseChain(ledger, false);
        foreach (var path in Directory.EnumerateFiles(ledger, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id) || id == Guid.Empty)
                throw new SetupException("LEDGER_UNVERIFIED", "任务账本文件名无效，停止卸载。");
            using var stream = SetupInventory.ReadLimited(path);
            using var json = JsonDocument.Parse(stream);
            AgentUninstallPolicy.RequireTerminalLedgerStates([json.RootElement.GetProperty("state").GetString() ?? ""]);
            if (json.RootElement.GetProperty("request").GetProperty("operationId").GetGuid() != id)
                throw new SetupException("LEDGER_UNVERIFIED", "账本编号与文件名不符，停止卸载。");
        }
    }

    private sealed class UninstallOperations(Evidence before, SetupReceipt receipt, IProgress<SetupProgress> progress, Action mutating) : IAgentUninstallOperations
    {
        private ServiceEvidence? stopped;
        public AgentUninstallInspection Inspect() => ReadEvidence(before.Root).Review;
        public void StopService()
        {
            receipt.Advance("StopAgentService", "Applying");
            Report(progress, "StopService", "停止固定 Agent 服务；若后续核验失败，保留停止状态及回执，不自动恢复或重放。");
            mutating();
            WindowsServiceManager.StopReviewedAgentAsync(before.Service).GetAwaiter().GetResult();
        }
        public IDisposable LockStoppedLedger()
        {
            receipt.Advance("LockAgentLedger", "Applying");
            var path = Path.Combine(before.Ledger, ".owner.lock");
            SetupFileSecurity.RejectReparseChain(path, false);
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        public void VerifyStoppedState()
        {
            receipt.Advance("VerifyStoppedAgent", "Applying");
            var after = ReadEvidence(before.Root);
            if (after.Service.State != 1 || after.Service.ProcessId != 0 ||
                before.Fingerprint() != after.Fingerprint(after.Service with { State = before.Service.State, ProcessId = before.Service.ProcessId }))
                throw new SetupException("UNINSTALL_STOPPED_STATE_DRIFT", "停止后服务入口、身份、配置或账本来源发生漂移，停止后续删除。");
            // ReadEvidence checks the terminal ledger while its owner lock is held.
            stopped = after.Service;
            receipt.RecordService(stopped, after: true);
        }
        public void DeleteServiceAndVerifyAbsent()
        {
            if (stopped is null) throw new SetupException("UNINSTALL_STOPPED_STATE_UNVERIFIED", "尚未核实停止状态，拒绝移除服务。");
            receipt.Advance("DeleteAgentService", "Applying");
            Report(progress, "DeleteService", "仅移除固定 Agent 服务注册，关闭本进程服务句柄后有时限地只读核对服务已不存在。");
            WindowsServiceManager.DeleteStoppedAgentAsync(stopped).GetAwaiter().GetResult();
            if (WindowsServiceManager.Read(AgentUninstallPolicy.ServiceName) is not null)
                throw new SetupException("SERVICE_DELETE_UNVERIFIED", "Agent 服务删除后仍能读回，结果未核实。");
            SetupInventory.VerifySnapshots(before.Files);
        }
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new SetupException("WINDOWS_REQUIRED", "Agent 服务卸载仅在目标 Windows 服务器执行。");
    }
    private static void Report(IProgress<SetupProgress> progress, string stage, string message)
    {
        try { progress.Report(new(stage, message)); }
        catch { /* UI notification failure must never interrupt an in-flight SCM result observation. */ }
    }
    private static string SafeMessage(Exception exception)
    {
        var detail = SafeFailureSummary.From(exception);
        var prefix = exception is SetupException setup ? setup.Message : exception is OperationCanceledException ? "卸载已取消，未继续修改服务。" : "卸载结果无法核实，请根据回执先只读检查。";
        return $"{prefix} {detail.LeafExceptionType} / 0x{detail.LeafHResult:X8}";
    }
}
