using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using IISManager.Contracts;

namespace IISManager.Console;

public sealed record OperationHealthFollowUp
{
    public int Version { get; init; } = 1;
    public string State { get; init; } = "pending";
    public DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? ScopeSource { get; init; }
    public string? ErrorCode { get; init; }
    public int MatchedChecks { get; init; }
    public OperationHealthCheckResult[] Results { get; init; } = [];
}

public sealed record OperationHealthCheckResult
{
    public string CheckId { get; init; } = "";
    public string CheckRevision { get; init; } = "";
    public string SiteName { get; init; } = "";
    public string ApplicationPath { get; init; } = "/";
    public string State { get; init; } = "pending";
    public int? StatusCode { get; init; }
    public long? ElapsedMilliseconds { get; init; }
    public DateTimeOffset? CheckedAt { get; init; }
    public string? ErrorCode { get; init; }
}

public static class OperationHealthFollowUpRules
{
    public const int MaximumChecks = 20;
    public const int BatchTimeoutSeconds = 60;
    public const int MaximumWaitMinutes = 10;
    public static bool Eligible(string type) => type is OperationTypes.Start or OperationTypes.Stop or OperationTypes.Recycle or
        OperationTypes.SiteStart or OperationTypes.SiteStop or OperationTypes.SiteRestart;
    private static bool Text(string? value, int maximum) => value is not null && value.Length is > 0 && value.Length <= maximum && !value.Any(char.IsControl);
    private static bool Code(string? value) => value is null || Regex.IsMatch(value, "^[A-Z][A-Z0-9_]{0,80}$");
    public static bool Valid(OperationHealthFollowUp? value, string type) => value is null ||
        Eligible(type) && value.Version == 1 && value.RequestedAt != default &&
        value.State is "pending" or "running" or "completed" or "skipped" or "unknown" &&
        value.ScopeSource is null or "site" or "agentRecord" or "topology" && Code(value.ErrorCode) &&
        value.MatchedChecks is >= 0 and <= 200 && value.Results is not null && value.Results.Length <= MaximumChecks &&
        value.Results.Length <= value.MatchedChecks && value.Results.Select(result => result?.CheckId).Distinct(StringComparer.Ordinal).Count() == value.Results.Length &&
        (value.State != "running" || value.StartedAt is not null) &&
        (value.State is "pending" or "running" || value.CompletedAt is not null) &&
        value.Results.All(result => result is not null && Guid.TryParseExact(result.CheckId, "D", out _) &&
            Guid.TryParseExact(result.CheckRevision, "N", out _) && Text(result.SiteName, 64) && Text(result.ApplicationPath, 512) &&
            result.State is "pending" or "healthy" or "unhealthy" or "unavailable" or "simulation" or "unsupported" or "skipped" or "unknown" &&
            result.StatusCode is null or >= 100 and <= 599 && result.ElapsedMilliseconds is null or >= 0 && Code(result.ErrorCode));

    private static readonly HashSet<string> ProbeCodes = new(StringComparer.Ordinal)
    {
        "HEALTH_APPLICATION_NOT_FOUND", "HEALTH_APPLICATION_PATH_MISMATCH", "HEALTH_BINDING_AMBIGUOUS", "HEALTH_BINDING_MISMATCH",
        "HEALTH_BUSY", "HEALTH_CONNECTION_FAILED", "HEALTH_LOCAL_ADDRESS_UNAVAILABLE", "HEALTH_MANAGEMENT_PORT_DENIED",
        "HEALTH_PATH_INVALID", "HEALTH_REQUEST_INVALID", "HEALTH_RESPONSE_INVALID", "HEALTH_SIMULATION", "HEALTH_SITE_NOT_FOUND",
        "HEALTH_SITE_STOPPED", "HEALTH_STATUS_MISMATCH", "HEALTH_TARGET_NOT_RUNNING", "HEALTH_TIMEOUT", "HEALTH_TLS_ERROR",
        "HEALTH_TOPOLOGY_UNAVAILABLE", "HEALTH_URL_INVALID", "HEALTH_AGENT_UPGRADE_REQUIRED", "AGENT_NOT_FOUND",
        "AGENT_REQUEST_REJECTED", "AGENT_ACCESS_DENIED", "AGENT_AUTHENTICATION_REJECTED", "AGENT_HTTP_ERROR", "AGENT_TIMEOUT",
        "AGENT_DNS_FAILED", "AGENT_TLS_FAILED", "AGENT_UNREACHABLE", "AGENT_RESPONSE_INVALID"
    };
    internal static string? SafeProbeCode(string? code) => code is null ? null : ProbeCodes.Contains(code) ? code : "HEALTH_CHECK_UNAVAILABLE";
}

/// <summary>Observes durable intents and performs only status/topology reads and existing safe health probes.</summary>
public sealed class OperationHealthFollowUpService(ConsoleTaskStore store, HealthMonitoringService health,
    IMonitoringAgentClient agent, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _pending = new();
    private readonly SemaphoreSlim _pass = new(1, 1);
    private bool _initialized;

    public void Observe(ConsoleTaskRecord task)
    {
        if (task.HealthFollowUp?.State == "pending" && OperationHealthFollowUpRules.Eligible(task.Request.Type))
            _pending.TryAdd(task.Request.OperationId, _time.GetUtcNow());
        else if (task.HealthFollowUp?.State is not "running") _pending.TryRemove(task.Request.OperationId, out _);
    }

    public async Task RunPendingAsync(CancellationToken cancellation = default)
    {
        if (!await _pass.WaitAsync(0, cancellation)) return;
        try
        {
            if (!store.Healthy) return;
            var indexed = await store.PendingHealthFollowUpIdsAsync(cancellation);
            if (!_initialized)
            {
                foreach (var id in indexed)
                {
                    var gate = store.Lock(id); await gate.WaitAsync(cancellation);
                    try
                    {
                        var task = await store.GetAsync(id, cancellation);
                        if (task?.HealthFollowUp?.State == "running")
                            await store.SaveAsync(task with { HealthFollowUp = Interrupted(task.HealthFollowUp) }, CancellationToken.None);
                    }
                    finally { gate.Release(); }
                }
                _initialized = true;
            }
            foreach (var id in indexed) _pending.TryAdd(id, _time.GetUtcNow());
            using var limit = new SemaphoreSlim(2, 2);
            await Task.WhenAll(_pending.Where(pair => pair.Value <= _time.GetUtcNow()).OrderBy(pair => pair.Value).Take(32).Select(async pair =>
            {
                await limit.WaitAsync(cancellation);
                try { await ProcessAsync(pair.Key, cancellation); }
                finally { limit.Release(); }
            }));
        }
        finally { _pass.Release(); }
    }

    private async Task ProcessAsync(Guid id, CancellationToken cancellation)
    {
        _pending[id] = _time.GetUtcNow().AddSeconds(5);
        var task = await ReadAsync(id, cancellation);
        if (task?.HealthFollowUp?.State != "pending") { _pending.TryRemove(id, out _); return; }
        if (_time.GetUtcNow() - task.HealthFollowUp.RequestedAt >= TimeSpan.FromMinutes(OperationHealthFollowUpRules.MaximumWaitMinutes))
        { await FinishAsync(id, "unknown", "HEALTH_OPERATION_WAIT_TIMEOUT"); return; }
        if (task.State is OperationStates.Failed or OperationStates.Partial or OperationStates.Expired)
        { await FinishAsync(id, "skipped", "HEALTH_OPERATION_NOT_SUCCEEDED"); return; }
        if (task.State != OperationStates.Succeeded || task.AgentRecord is null)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var record = await agent.GetAsync<OperationRecord>(task.ServerId, "operations/" + id.ToString("D"), deadline.Token);
                task = await MergeStatusAsync(id, record, cancellation);
            }
            catch (AgentCallException exception) when (exception.Code == "AGENT_RESPONSE_MISMATCH")
            { await FinishAsync(id, "unknown", "HEALTH_OPERATION_STATUS_INVALID"); return; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is AgentCallException or OperationCanceledException or JsonException)
            {
                await UpdateAsync(id, current => current.State == "pending" ? current with { ErrorCode = "HEALTH_OPERATION_STATUS_UNAVAILABLE" } : current);
                return;
            }
            if (task?.HealthFollowUp?.State != "pending") return;
            if (task.State is OperationStates.Queued or OperationStates.Running) return;
            if (task.State != OperationStates.Succeeded)
            { await FinishAsync(id, task.State == OperationStates.Unknown ? "unknown" : "skipped", task.State == OperationStates.Unknown ? "HEALTH_OPERATION_UNCONFIRMED" : "HEALTH_OPERATION_NOT_SUCCEEDED"); return; }
        }
        try
        {
            var confirmed = global::TaskStatus.WithAgent(task, task.AgentRecord!);
            if (confirmed.State != OperationStates.Succeeded)
            { await FinishAsync(id, "unknown", "HEALTH_OPERATION_UNCONFIRMED"); return; }
        }
        catch (AgentCallException) { await FinishAsync(id, "unknown", "HEALTH_OPERATION_STATUS_INVALID"); return; }
        await RunBatchAsync(task, cancellation);
    }

    private async Task RunBatchAsync(ConsoleTaskRecord task, CancellationToken cancellation)
    {
        var id = task.Request.OperationId;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(OperationHealthFollowUpRules.BatchTimeoutSeconds));
        try
        {
            HealthCheckView[] checks;
            try { checks = await health.GetViewsAsync(deadline.Token); }
            catch (HealthCheckException) { await FinishAsync(id, "unknown", "HEALTH_CONFIGURATION_UNAVAILABLE"); return; }
            var scope = await ResolveScopeAsync(task, deadline.Token);
            if (scope is null) { await FinishAsync(id, "skipped", "HEALTH_SCOPE_UNRESOLVED"); return; }
            var matched = checks.Where(check => check.Enabled && check.ServerId == task.ServerId && scope.Matches(check))
                .OrderBy(check => check.SiteName, StringComparer.OrdinalIgnoreCase).ThenBy(check => check.ApplicationPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(check => check.Id, StringComparer.Ordinal).ToArray();
            if (matched.Length == 0) { await FinishAsync(id, "skipped", "HEALTH_NO_MATCHING_ENABLED_CHECKS", scope.Source); return; }
            var planned = matched.Take(OperationHealthFollowUpRules.MaximumChecks).Select(check => new OperationHealthCheckResult
            { CheckId = check.Id, CheckRevision = check.Revision, SiteName = check.SiteName, ApplicationPath = check.ApplicationPath }).ToArray();
            var started = await UpdateAsync(id, current => current.State == "pending" ? current with
            {
                State = "running", StartedAt = _time.GetUtcNow(), ScopeSource = scope.Source, ErrorCode = null,
                MatchedChecks = matched.Length, Results = planned
            } : current);
            if (started?.State != "running") return;
            // The running receipt is durable and independently read back before the first probe.
            foreach (var check in matched.Take(OperationHealthFollowUpRules.MaximumChecks))
            {
                deadline.Token.ThrowIfCancellationRequested();
                var result = await ProbeAsync(check, deadline.Token);
                var updated = await UpdateAsync(id, current => current.State == "running" ? current with
                { Results = current.Results.Select(item => item.CheckId == check.Id ? result : item).ToArray() } : current);
                if (updated?.State != "running") return;
            }
            await UpdateAsync(id, current => current.State == "running" ? current with { State = "completed", CompletedAt = _time.GetUtcNow(),
                ErrorCode = matched.Length > OperationHealthFollowUpRules.MaximumChecks ? "HEALTH_CHECK_LIMIT_REACHED" : null } : current);
            _pending.TryRemove(id, out _);
        }
        catch (AgentCallException) { await FinishAsync(id, "unknown", "HEALTH_SCOPE_UNAVAILABLE"); }
        catch (OperationCanceledException)
        { await FinishAsync(id, "unknown", cancellation.IsCancellationRequested ? "HEALTH_BATCH_INTERRUPTED" : "HEALTH_BATCH_TIMEOUT"); }
    }

    private async Task<OperationHealthCheckResult> ProbeAsync(HealthCheckView expected, CancellationToken cancellation)
    {
        var result = new OperationHealthCheckResult { CheckId = expected.Id, CheckRevision = expected.Revision,
            SiteName = expected.SiteName, ApplicationPath = expected.ApplicationPath };
        try
        {
            var view = await health.RunAsync(expected.Id, cancellation, expectedRevision: expected.Revision);
            if (view.Revision != expected.Revision || view.Result is null)
                return result with { State = "unknown", ErrorCode = "HEALTH_CHECK_RESULT_CHANGED" };
            return result with { State = view.Result.State, StatusCode = view.Result.StatusCode, ElapsedMilliseconds = view.Result.ElapsedMilliseconds,
                CheckedAt = view.Result.CheckedAt, ErrorCode = OperationHealthFollowUpRules.SafeProbeCode(view.Result.ErrorCode) };
        }
        catch (HealthCheckException exception)
        {
            var code = exception.Code switch
            {
                "HEALTH_CONFIG_CONFLICT" => "HEALTH_CHECK_CHANGED", "HEALTH_CHECK_DISABLED" => "HEALTH_CHECK_DISABLED",
                "HEALTH_CHECK_NOT_FOUND" => "HEALTH_CHECK_NOT_FOUND", "HEALTH_CHECK_BUSY" => "HEALTH_CHECK_BUSY", _ => "HEALTH_CHECK_UNAVAILABLE"
            };
            return result with { State = exception.StatusCode is 404 or 409 ? "skipped" : "unknown", ErrorCode = code };
        }
    }

    private sealed record Scope(string Source, string? Site, HashSet<string>? Applications)
    {
        public bool Matches(HealthCheckView check) => Site is not null ? string.Equals(Site, check.SiteName, StringComparison.OrdinalIgnoreCase) :
            Applications!.Contains(check.SiteName + check.ApplicationPath);
    }
    private async Task<Scope?> ResolveScopeAsync(ConsoleTaskRecord task, CancellationToken cancellation)
    {
        if (OperationTypes.IsSiteLifecycle(task.Request.Type)) return string.IsNullOrWhiteSpace(task.Request.Target.SiteName) ? null : new("site", task.Request.Target.SiteName, null);
        var name = task.Request.Target.ApplicationPoolName;
        if (string.IsNullOrWhiteSpace(name)) return null;
        var applications = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var found = false;
        foreach (var snapshot in new[] { task.AgentRecord?.Before, task.AgentRecord?.After })
        {
            if (!snapshot.HasValue || snapshot.Value.ValueKind != JsonValueKind.Object) continue;
            var value = snapshot.Value;
            if (!value.TryGetProperty("applications", out var entries) || entries.ValueKind != JsonValueKind.Array) continue;
            var texts = entries.EnumerateArray().Select(entry => entry.ValueKind == JsonValueKind.String ? entry.GetString() : null).ToArray();
            if (texts.Any(text => text is null || text.Length > 577 || text.Any(char.IsControl) || !text.Contains('/'))) continue;
            found = true; foreach (var text in texts) applications.Add(text!);
        }
        if (found) return new("agentRecord", null, applications);
        var topology = await agent.GetAsync<SiteTopologySnapshot>(task.ServerId, "site-topology", cancellation);
        if (topology?.Sites is null || topology.Sites.Any(site => site is null || site.Name is null || site.Applications is null ||
                site.Applications.Any(application => application is null || application.Path is null))) throw new AgentCallException("AGENT_RESPONSE_INVALID");
        foreach (var site in topology.Sites)
            foreach (var application in site.Applications.Where(application => string.Equals(name, application.ApplicationPoolName, StringComparison.OrdinalIgnoreCase)))
                applications.Add(site.Name + application.Path);
        return applications.Count == 0 ? null : new("topology", null, applications);
    }

    private async Task<ConsoleTaskRecord?> ReadAsync(Guid id, CancellationToken cancellation)
    {
        var gate = store.Lock(id); await gate.WaitAsync(cancellation);
        try { return await store.GetAsync(id, cancellation); }
        finally { gate.Release(); }
    }
    private async Task<ConsoleTaskRecord?> MergeStatusAsync(Guid id, OperationRecord record, CancellationToken cancellation)
    {
        var gate = store.Lock(id); await gate.WaitAsync(cancellation);
        try
        {
            var current = await store.GetAsync(id, cancellation);
            if (current?.HealthFollowUp?.State != "pending") return current;
            if (current.State is OperationStates.Succeeded or OperationStates.Failed or OperationStates.Partial or OperationStates.Expired) return current;
            var refreshed = global::TaskStatus.WithAgent(current, record);
            if (!ReferenceEquals(refreshed, current)) await store.SaveAsync(refreshed, CancellationToken.None);
            return refreshed;
        }
        finally { gate.Release(); }
    }
    private async Task<OperationHealthFollowUp?> UpdateAsync(Guid id, Func<OperationHealthFollowUp, OperationHealthFollowUp> update)
    {
        var gate = store.Lock(id); await gate.WaitAsync();
        try
        {
            if (!store.Healthy) throw new ConsoleLedgerUnavailableException();
            var current = await store.GetAsync(id);
            if (current?.HealthFollowUp is null) return null;
            var next = update(current.HealthFollowUp);
            if (!ReferenceEquals(next, current.HealthFollowUp)) await store.SaveAsync(current with { HealthFollowUp = next }, CancellationToken.None);
            return next;
        }
        finally { gate.Release(); }
    }
    private async Task FinishAsync(Guid id, string state, string code, string? source = null)
    {
        await UpdateAsync(id, current => current.State is "pending" or "running" ? current with { State = state, ErrorCode = code,
            ScopeSource = source ?? current.ScopeSource, CompletedAt = _time.GetUtcNow(), Results = current.Results.Select(result =>
                result.State == "pending" ? result with { State = "unknown", ErrorCode = "HEALTH_CHECK_CANCELLED" } : result).ToArray() } : current);
        _pending.TryRemove(id, out _);
    }
    private OperationHealthFollowUp Interrupted(OperationHealthFollowUp current) => current with { State = "unknown", ErrorCode = "HEALTH_BATCH_INTERRUPTED",
        CompletedAt = _time.GetUtcNow(), Results = current.Results.Select(result => result.State == "pending" ? result with
            { State = "unknown", ErrorCode = "HEALTH_CHECK_CANCELLED" } : result).ToArray() };
}

public sealed class OperationHealthFollowUpWorker(OperationHealthFollowUpService service,
    ILogger<OperationHealthFollowUpWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try { await service.RunPendingAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogWarning("event=operation_health_follow_up_unavailable exceptionType={ExceptionType}", exception.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
