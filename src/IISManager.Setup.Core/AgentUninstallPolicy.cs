namespace IISManager.Setup.Core;

public static class AgentUninstallPolicy
{
    public const string ServiceName = "IISManager.Agent";

    public static void RequireService(AgentUninstallService service, string installDirectory)
    {
        if (!SetupPlanValidator.IsWindowsDirectory(installDirectory) || service.Name != ServiceName ||
            service.ServiceType != 16 || service.StartType is not (2 or 3 or 4) ||
            string.IsNullOrWhiteSpace(service.Account) || service.Account.Any(char.IsControl))
            throw new SetupException("UNINSTALL_SERVICE_OWNERSHIP_UNVERIFIED", "只能卸载固定 Agent 服务，服务类型、身份与安装目录须可核实。");
        var root = installDirectory.Replace('/', '\\').TrimEnd('\\');
        var expected = $"\"{root}\\IISManager.Agent.exe\" --contentRoot \"{root}\" --environment Production";
        if (!service.BinaryPath.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new SetupException("UNINSTALL_SERVICE_OWNERSHIP_UNVERIFIED", "服务程序、内容根或 Production 参数与所选 Agent 目录不符，停止卸载。");
        if (service.State is not (1 or 4) || service.State == 1 && service.ProcessId != 0 || service.State == 4 && service.ProcessId == 0)
            throw new SetupException("UNINSTALL_SERVICE_STATE_UNSTABLE", "Agent 服务状态不稳定，先只读核实，不自动停止或删除。");
    }

    public static void RequireReview(AgentUninstallRequest request, AgentUninstallInspection inspection)
    {
        if (!inspection.CanUninstall || !inspection.ServiceExists || inspection.Issues is not { Length: 0 })
            throw new SetupException("UNINSTALL_INSPECTION_UNVERIFIED", "卸载目标未通过只读核对，请重新检查服务与任务状态。");
        if (!SetupPlanValidator.IsWindowsDirectory(request.InstallDirectory) ||
            !request.InstallDirectory.Equals(inspection.InstallDirectory, StringComparison.OrdinalIgnoreCase) ||
            !ValidFingerprint(request.Fingerprint) || !ValidFingerprint(inspection.Fingerprint) ||
            !request.Fingerprint.Equals(inspection.Fingerprint, StringComparison.Ordinal))
            throw new SetupException("UNINSTALL_REVIEW_DRIFT", "服务、目录或配置在审阅后变化，请重新只读检查；未执行卸载。");
    }

    public static void RequireTerminalLedgerStates(IEnumerable<string> states)
    {
        foreach (var state in states)
        {
            if (state is "queued" or "running" or "unknown")
                throw new SetupException("TASKS_UNFINISHED", "账本存在排队、运行或未知任务，请先核实；不删除账本或自动重放。");
            if (state is not ("succeeded" or "failed" or "partial" or "expired"))
                throw new SetupException("LEDGER_UNVERIFIED", "任务账本状态无法核实，停止卸载。");
        }
    }

    private static bool ValidFingerprint(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
