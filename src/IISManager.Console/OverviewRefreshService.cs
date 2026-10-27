namespace IISManager.Console;

public sealed record OverviewRefreshState(DateTimeOffset? CollectedAt, OverviewServer[] Servers,
    bool Refreshing, int CompletedServers, int TotalServers);

/// <summary>Shared, bounded read collection continues independently of individual browser requests.</summary>
public sealed class OverviewRefreshService(ConsoleOptions options, OverviewService reader, TimeProvider? time = null) : IDisposable
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly Dictionary<string, OverviewServer> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<OverviewServer>> _active = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private DateTimeOffset? _completedAt;
    private bool _batchRunning;
    private bool _disposed;

    public OverviewRefreshState GetProgress(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var stale = options.Servers.Where(server => !_results.TryGetValue(server.Id, out var result) ||
                result.CollectedAt is null || _time.GetUtcNow() - result.CollectedAt >= TimeSpan.FromSeconds(options.Monitoring.OverviewCacheSeconds)).ToArray();
            if (!_batchRunning && stale.Length > 0)
            {
                _batchRunning = true;
                var tasks = stale.Select(server => StartServerLocked(server.Id)).ToArray();
                _ = CompleteBatchAsync(tasks);
            }
            else if (!_batchRunning && _active.Count == 0 && _completedAt is null) _completedAt = _time.GetUtcNow();
            var rows = options.Servers.Select(server =>
            {
                var refreshing = _active.ContainsKey(server.Id);
                if (_results.TryGetValue(server.Id, out var result))
                    return result with { Refreshing = refreshing, IsStale = refreshing };
                return new OverviewServer(server.Id, server.Name, new Uri(server.AgentUrl).IdnHost,
                    server.Simulation ? "Simulation" : "Windows", "pending", null, null, null, null, null, [], [])
                    { Group = server.Group, Environment = server.Environment, Refreshing = refreshing };
            }).ToArray();
            return new(_completedAt, rows, _batchRunning || _active.Count > 0,
                rows.Count(row => !row.Refreshing && row.ConnectionState != "pending"), rows.Length);
        }
    }

    public async Task<OverviewServer> RefreshServerAsync(string id, CancellationToken cancellation)
    {
        Task<OverviewServer> work;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!options.Servers.Any(server => server.Id == id)) throw new AgentCallException("SERVER_NOT_FOUND", 404);
            work = StartServerLocked(id);
        }
        // A disconnected browser must not cancel the collection shared with other viewers.
        return await work.WaitAsync(cancellation);
    }

    private Task<OverviewServer> StartServerLocked(string id)
    {
        if (_active.TryGetValue(id, out var existing)) return existing;
        var task = Task.Run(async () =>
        {
            try
            {
                OverviewServer result;
                try { result = await reader.ReadServerAsync(id, _stopping.Token); }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    var server = options.Servers.Single(item => item.Id == id);
                    result = new(server.Id, server.Name, new Uri(server.AgentUrl).IdnHost,
                        server.Simulation ? "Simulation" : "Windows", "offline", _time.GetUtcNow(), null, null, null, null,
                        [], [new("overview", "OVERVIEW_READ_FAILED")]) { Group = server.Group, Environment = server.Environment };
                }
                lock (_sync) _results[id] = result;
                return result;
            }
            finally { lock (_sync) _active.Remove(id); }
        });
        _active[id] = task;
        return task;
    }

    private async Task CompleteBatchAsync(Task<OverviewServer>[] tasks)
    {
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        finally
        {
            lock (_sync) { _batchRunning = false; _completedAt = _time.GetUtcNow(); }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _stopping.Cancel();
        }
    }
}
