using IISManager.Core;

namespace IISManager.Agent;

public sealed class OperationWorker(OperationCoordinator coordinator) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await coordinator.InitializeAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await coordinator.RunAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
