using IISManager.Setup.Core;

internal static class AgentUninstallTests
{
    private const string Root = @"C:\Tools\Agent";
    private const string Data = @"C:\ProgramData\IISManager.Agent";
    private static readonly string Fingerprint = new('A', 64);

    internal static readonly (string Name, Action Run)[] All =
    [
        ("Agent 卸载只接受固定服务及精确入口", ServiceOwnership),
        ("Agent 卸载拒绝非稳定服务状态", ServiceStates),
        ("Agent 卸载审阅指纹及目录漂移阻止执行", ReviewDrift),
        ("Agent 卸载拒绝未核实和缺失服务", UnverifiedReview),
        ("Agent 卸载账本只接受终态", LedgerStates),
        ("Agent 卸载依次复核停止锁账本再删除且释放锁", WorkflowOrder),
        ("Agent 卸载审阅变化不停止服务", WorkflowReviewFailure),
        ("Agent 卸载停止后核对失败不删除或重放", WorkflowStoppedFailure),
        ("Agent 卸载删除结果未知仅调用一次且释放锁", WorkflowDeleteUnknown),
        ("Agent 卸载停止失败不锁账本或删除", WorkflowStopFailure),
        ("Agent 卸载执行前取消不修改服务", WorkflowCancellation),
        ("Agent 卸载开始停止后不被取消打断核对", WorkflowCancellationAfterStop),
        ("Agent 卸载账本锁失败不删除", WorkflowLedgerLockFailure)
    ];

    private static AgentUninstallService Service() => new("IISManager.Agent",
        "\"" + Root + "\\IISManager.Agent.exe\" --contentRoot \"" + Root + "\" --environment Production",
        "LocalSystem", 2, 16, 4, 123);

    private static AgentUninstallInspection Review() => new(true, true, "Running", "LocalSystem", Root, Data, Fingerprint, []);
    private static AgentUninstallRequest Request() => new(Root, Fingerprint);

    private static void ServiceOwnership()
    {
        AgentUninstallPolicy.RequireService(Service(), Root);
        foreach (var invalid in new[]
        {
            Service() with { Name = "IISManager.Console" },
            Service() with { BinaryPath = Service().BinaryPath.Replace("IISManager.Agent.exe", "other.exe") },
            Service() with { BinaryPath = Service().BinaryPath.Replace("Production", "Development") },
            Service() with { BinaryPath = Service().BinaryPath + " --Agent:StateDirectory C:\\elsewhere" },
            Service() with { BinaryPath = Service().BinaryPath.Replace(Root, Root + "Other") },
            Service() with { ServiceType = 32 }
        }) Reject(() => AgentUninstallPolicy.RequireService(invalid, Root));
        Reject(() => AgentUninstallPolicy.RequireService(Service(), @"C:\"));
        Reject(() => AgentUninstallPolicy.RequireService(Service(), @"\\server\Agent"));
    }

    private static void ServiceStates()
    {
        AgentUninstallPolicy.RequireService(Service() with { State = 1, ProcessId = 0 }, Root);
        foreach (uint state in new uint[] { 0, 2, 3, 5, 6, 7, 99 })
            Reject(() => AgentUninstallPolicy.RequireService(Service() with { State = state }, Root));
        Reject(() => AgentUninstallPolicy.RequireService(Service() with { State = 4, ProcessId = 0 }, Root));
        Reject(() => AgentUninstallPolicy.RequireService(Service() with { State = 1, ProcessId = 123 }, Root));
    }

    private static void ReviewDrift()
    {
        AgentUninstallPolicy.RequireReview(Request(), Review());
        Reject(() => AgentUninstallPolicy.RequireReview(Request() with { Fingerprint = new('B', 64) }, Review()));
        Reject(() => AgentUninstallPolicy.RequireReview(Request() with { InstallDirectory = Root + "Other" }, Review()));
        Reject(() => AgentUninstallPolicy.RequireReview(Request() with { Fingerprint = "" }, Review()));
    }

    private static void UnverifiedReview()
    {
        Reject(() => AgentUninstallPolicy.RequireReview(Request(), Review() with { CanUninstall = false }));
        Reject(() => AgentUninstallPolicy.RequireReview(Request(), Review() with { ServiceExists = false }));
        Reject(() => AgentUninstallPolicy.RequireReview(Request(), Review() with { Issues = [new("UNVERIFIED", "未核实")] }));
    }

    private static void LedgerStates()
    {
        AgentUninstallPolicy.RequireTerminalLedgerStates([]);
        AgentUninstallPolicy.RequireTerminalLedgerStates(["succeeded", "failed", "partial", "expired"]);
        foreach (var state in new[] { "queued", "running", "unknown", "", "invalid", "Succeeded" })
            Reject(() => AgentUninstallPolicy.RequireTerminalLedgerStates(["succeeded", state]));
    }

    private static void WorkflowOrder()
    {
        var operations = new FakeOperations();
        AgentUninstallWorkflow.Execute(Request(), operations);
        Equal(operations.Calls, "Inspect", "Stop", "LockLedger", "VerifyStopped", "DeleteAndVerify", "DisposeLedger");
    }

    private static void WorkflowReviewFailure()
    {
        var operations = new FakeOperations { Inspection = Review() with { Fingerprint = new('B', 64) } };
        Reject(() => AgentUninstallWorkflow.Execute(Request(), operations));
        Equal(operations.Calls, "Inspect");
    }

    private static void WorkflowStoppedFailure()
    {
        var operations = new FakeOperations { Failure = "VerifyStopped" };
        Reject(() => AgentUninstallWorkflow.Execute(Request(), operations));
        Equal(operations.Calls, "Inspect", "Stop", "LockLedger", "VerifyStopped", "DisposeLedger");
    }

    private static void WorkflowDeleteUnknown()
    {
        var operations = new FakeOperations { Failure = "DeleteAndVerify" };
        Reject(() => AgentUninstallWorkflow.Execute(Request(), operations));
        Equal(operations.Calls, "Inspect", "Stop", "LockLedger", "VerifyStopped", "DeleteAndVerify", "DisposeLedger");
    }

    private static void WorkflowStopFailure()
    {
        var operations = new FakeOperations { Failure = "Stop" };
        Reject(() => AgentUninstallWorkflow.Execute(Request(), operations));
        Equal(operations.Calls, "Inspect", "Stop");
    }

    private static void WorkflowCancellation()
    {
        var operations = new FakeOperations();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { AgentUninstallWorkflow.Execute(Request(), operations, cancellation.Token); throw new InvalidOperationException("Cancellation was ignored."); }
        catch (OperationCanceledException) { }
        if (operations.Calls.Any(call => call != "Inspect")) throw new InvalidOperationException("Cancelled uninstall mutated the service.");
    }

    private static void WorkflowLedgerLockFailure()
    {
        var operations = new FakeOperations { Failure = "LockLedger" };
        Reject(() => AgentUninstallWorkflow.Execute(Request(), operations));
        Equal(operations.Calls, "Inspect", "Stop", "LockLedger");
    }

    private static void WorkflowCancellationAfterStop()
    {
        using var cancellation = new CancellationTokenSource();
        var operations = new FakeOperations { AfterStop = cancellation.Cancel };
        AgentUninstallWorkflow.Execute(Request(), operations, cancellation.Token);
        Equal(operations.Calls, "Inspect", "Stop", "LockLedger", "VerifyStopped", "DeleteAndVerify", "DisposeLedger");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (SetupException) { return; }
        throw new InvalidOperationException("Unsafe uninstall input or state was accepted.");
    }

    private static void Equal(List<string> actual, params string[] expected)
    {
        if (!actual.SequenceEqual(expected)) throw new InvalidOperationException("Unexpected uninstall call order: " + string.Join(",", actual));
    }

    private sealed class FakeOperations : IAgentUninstallOperations
    {
        internal List<string> Calls { get; } = [];
        internal AgentUninstallInspection Inspection { get; init; } = Review();
        internal string Failure { get; init; } = "";
        internal Action? AfterStop { get; init; }
        private void Call(string name)
        {
            Calls.Add(name);
            if (Failure == name) throw new SetupException("INJECTED_FAILURE", "合成卸载失败。");
        }
        public AgentUninstallInspection Inspect() { Call("Inspect"); return Inspection; }
        public void StopService() { Call("Stop"); AfterStop?.Invoke(); }
        public IDisposable LockStoppedLedger() { Call("LockLedger"); return new LedgerLease(Calls); }
        public void VerifyStoppedState() => Call("VerifyStopped");
        public void DeleteServiceAndVerifyAbsent() => Call("DeleteAndVerify");
    }

    private sealed class LedgerLease(List<string> calls) : IDisposable
    {
        public void Dispose() => calls.Add("DisposeLedger");
    }
}
