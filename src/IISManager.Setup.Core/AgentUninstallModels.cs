namespace IISManager.Setup.Core;

public sealed record AgentUninstallInspection(bool CanUninstall, bool ServiceExists, string ServiceState,
    string ServiceAccount, string InstallDirectory, string DataDirectory, string Fingerprint, SetupIssue[] Issues);

public sealed record AgentUninstallRequest(string InstallDirectory, string Fingerprint);

public interface IAgentUninstallBackend
{
    AgentUninstallInspection Inspect(string installDirectory);
    Task<SetupOutcome> UninstallAsync(AgentUninstallRequest request, IProgress<SetupProgress> progress,
        CancellationToken cancellationToken);
}

public sealed record AgentUninstallService(string Name, string BinaryPath, string Account, uint StartType,
    uint ServiceType, uint State, uint ProcessId);

// The Windows adapter journals each operation. This sequence is also exercised without SCM on macOS.
public interface IAgentUninstallOperations
{
    AgentUninstallInspection Inspect();
    void StopService();
    IDisposable LockStoppedLedger();
    void VerifyStoppedState();
    void DeleteServiceAndVerifyAbsent();
}

public static class AgentUninstallWorkflow
{
    public static void Execute(AgentUninstallRequest request, IAgentUninstallOperations operations,
        CancellationToken cancellationToken = default)
    {
        AgentUninstallPolicy.RequireReview(request, operations.Inspect());
        cancellationToken.ThrowIfCancellationRequested();
        // Once SCM may have received Stop/Delete, observe its result instead of cancellation or replay.
        operations.StopService();
        using var ledger = operations.LockStoppedLedger();
        operations.VerifyStoppedState();
        operations.DeleteServiceAndVerifyAbsent();
    }
}
