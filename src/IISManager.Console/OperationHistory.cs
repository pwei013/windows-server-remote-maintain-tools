using System.Security.Cryptography;
using System.Text.Json;
using IISManager.Contracts;

namespace IISManager.Console;

public sealed class OperationHistoryOptions
{
    public bool Enabled { get; set; } = true;
    public int SyncIntervalSeconds { get; set; } = 10;
    public void Validate()
    {
        if (SyncIntervalSeconds is < 2 or > 120)
            throw new InvalidOperationException("OPERATION_HISTORY_CONFIGURATION_INVALID");
    }
}

// Only audit metadata is mirrored: no request parameters, Agent credentials or session tokens.
public sealed record OperationHistorySnapshot(Guid OperationId, string ServerId, string Type, string Target,
    string State, string? OperatorUserId, string? OperatorName, string? SourceIp, DateTimeOffset AcceptedAt,
    DateTimeOffset UpdatedAt, string RequestHash, string? ErrorCode)
{
    public static OperationHistorySnapshot FromTask(ConsoleTaskRecord task) => new(task.Request.OperationId,
        task.ServerId, task.Request.Type,
        task.Request.Target.ServiceName ?? (task.Request.Target.SiteName is not null ? task.Request.Target.SiteName + task.Request.Target.ApplicationPath + task.Request.Target.VirtualDirectoryPath : task.Request.Target.ApplicationPoolName) ?? "",
        task.State, task.OperatorUserId, task.OperatorName, task.SourceIp, task.AcceptedAt, task.UpdatedAt,
        task.RequestHash, task.Error?.Code);
    public string Hash() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this, Protocol.Json)));
    public bool SameIdentity(OperationHistorySnapshot other) =>
        (this with { State = other.State, UpdatedAt = other.UpdatedAt, ErrorCode = other.ErrorCode }) == other;
}

public sealed record StoredOperationHistory(string PrimaryId, OperationHistorySnapshot Snapshot);
public interface IOperationHistoryRepository
{
    bool Enabled { get; }
    Task<StoredOperationHistory?> FindAsync(Guid operationId, string token, CancellationToken cancellation);
    Task CreateAsync(OperationHistorySnapshot snapshot, string token, CancellationToken cancellation);
    Task UpdateAsync(string primaryId, OperationHistorySnapshot snapshot, string token, CancellationToken cancellation);
}

public sealed class OperationHistoryException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class OperationHistorySynchronizer(IOperationHistoryRepository repository, ConsoleTaskStore store)
{
    public bool Enabled => repository.Enabled;

    // Caller holds the operation lock for the entire read / attempt receipt / write / readback sequence.
    public async Task<ConsoleTaskRecord> SyncAsync(ConsoleTaskRecord task, string token, CancellationToken cancellation)
    {
        if (!Enabled || task.PersistenceState is null or "notConfigured") return task;
        var desired = OperationHistorySnapshot.FromTask(task);
        var hash = desired.Hash();
        if (task.PersistenceState == "synced" && task.PersistenceSyncedHash == hash && task.PersistenceAttemptHash is null)
            return task;
        try
        {
            var stored = await repository.FindAsync(desired.OperationId, token, cancellation);
            if (stored is not null && (!desired.SameIdentity(stored.Snapshot) ||
                task.PersistencePrimaryId is not null && task.PersistencePrimaryId != stored.PrimaryId))
                return await SaveAsync(task with { PersistenceState = "unknown", PersistenceErrorCode = "OPERATION_HISTORY_CONFLICT" });
            if (task.PersistenceAttemptHash is not null)
            {
                // Absence after a timeout is not proof of non-execution. Never automatically replay an uncertain write.
                if (stored is null || stored.Snapshot.Hash() != task.PersistenceAttemptHash)
                    return await SaveAsync(task with { PersistenceState = "unknown", PersistenceErrorCode = "OPERATION_HISTORY_WRITE_UNKNOWN" });
                task = await SaveAsync(task with
                {
                    PersistencePrimaryId = stored.PrimaryId, PersistenceSyncedHash = task.PersistenceAttemptHash,
                    PersistenceAttemptHash = null, PersistenceErrorCode = null, PersistenceState = "synced"
                });
            }
            if (stored?.Snapshot.Hash() == hash)
                return await SaveAsync(task with { PersistencePrimaryId = stored.PrimaryId, PersistenceSyncedHash = hash,
                    PersistenceAttemptHash = null, PersistenceState = "synced", PersistenceErrorCode = null });
            if (stored is not null && task.PersistenceSyncedHash != stored.Snapshot.Hash())
                return await SaveAsync(task with { PersistenceState = "unknown", PersistenceErrorCode = "OPERATION_HISTORY_CONFLICT" });
            if (stored is null && task.PersistenceSyncedHash is not null)
                return await SaveAsync(task with { PersistenceState = "unknown", PersistenceErrorCode = "OPERATION_HISTORY_RECORD_MISSING" });

            var mutationPrimaryId = stored?.PrimaryId;
            task = await SaveAsync(task with { PersistenceAttemptHash = hash, PersistenceState = "unknown",
                PersistenceErrorCode = "OPERATION_HISTORY_WRITE_UNKNOWN" });
            try
            {
                if (stored is null) await repository.CreateAsync(desired, token, cancellation);
                else await repository.UpdateAsync(stored.PrimaryId, desired, token, cancellation);
            }
            catch (OperationHistoryException) { } // Always independently read back even after a successful response.
            stored = await repository.FindAsync(desired.OperationId, token, cancellation);
            if (stored is not null && mutationPrimaryId is not null && stored.PrimaryId != mutationPrimaryId)
                return await SaveAsync(task with { PersistenceState = "unknown", PersistenceErrorCode = "OPERATION_HISTORY_CONFLICT" });
            if (stored is not null && stored.Snapshot == desired)
                task = task with { PersistencePrimaryId = stored.PrimaryId, PersistenceSyncedHash = hash,
                    PersistenceAttemptHash = null, PersistenceState = "synced", PersistenceErrorCode = null };
            return await SaveAsync(task);
        }
        catch (OperationHistoryException exception)
        {
            return await SaveAsync(task with { PersistenceState = task.PersistenceAttemptHash is null ? "pending" : "unknown",
                PersistenceErrorCode = exception.Code });
        }
    }

    private async Task<ConsoleTaskRecord> SaveAsync(ConsoleTaskRecord task)
    {
        // Do not let browser disconnects discard an attempt receipt or a verified readback.
        await store.SaveAsync(task, CancellationToken.None);
        return task;
    }
}

public sealed class OperationHistoryWorker(ConsoleTaskStore store, AgentClient agent,
    OperationHistorySynchronizer history, AppUserAuthenticationService authentication, ConsoleOptions options,
    ILogger<OperationHistoryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!history.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.OperationHistory.SyncIntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!store.Healthy) continue;
                foreach (var id in await store.PendingHistoryIdsAsync(stoppingToken))
                {
                    var gate = store.Lock(id);
                    await gate.WaitAsync(stoppingToken);
                    try
                    {
                        var task = await store.GetAsync(id, stoppingToken);
                        var token = task?.OperatorUserId is { } userId ? authentication.BackgroundAccessToken(userId) : null;
                        if (task is null || token is null) continue;
                        if (task.State is OperationStates.Queued or OperationStates.Running or OperationStates.Unknown)
                        {
                            var refreshed = await TaskStatus.QueryAsync(task, agent);
                            // A failed read must not manufacture a new cloud snapshot on every timer tick.
                            if (!ReferenceEquals(refreshed, task))
                            {
                                task = refreshed with { PersistenceState = "pending" };
                                await store.SaveAsync(task, CancellationToken.None);
                            }
                        }
                        task = await history.SyncAsync(task, token, stoppingToken);
                        if (task.PersistenceErrorCode == "APP_USER_SESSION_EXPIRED") authentication.InvalidateBackgroundToken(token);
                    }
                    finally { gate.Release(); }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            // Exception messages can include URLs or response contents. Log only the local exception type.
            logger.LogError("Operation history worker stopped; exceptionType={ExceptionType}", exception.GetType().Name);
        }
    }
}
