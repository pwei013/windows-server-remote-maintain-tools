using System.Globalization;
using System.Runtime.Versioning;
using IISManager.Contracts;
using Microsoft.Web.Administration;

namespace IISManager.Agent;

// Directory remains server-internal: this source is never serialized into an HTTP result.
public sealed record FrebSiteConfiguration(long SiteId, string SiteName, bool Enabled, string Directory, string? ErrorCode = null);
public sealed record FrebSiteConfigurationSnapshot(bool TracingInstalled, FrebSiteConfiguration[] Sites, bool Truncated = false);
public interface IFrebSiteConfigurationSource
{
    Task<FrebSiteConfigurationSnapshot> ReadAsync(CancellationToken cancellation);
}

public sealed class FrebDiagnosticsReadService
{
    private const int CacheSeconds = 10;
    private const int MaximumSites = 32;
    private readonly string _mode;
    private readonly IFrebSiteConfigurationSource? _source;
    private readonly FrebTraceReader _reader = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _deadline;
    private readonly object _gate = new();
    private Task<FrebDiagnosticsSnapshot>? _inflight;
    private FrebDiagnosticsSnapshot? _cached;
    private long _cachedAt;

    public FrebDiagnosticsReadService(AgentOptions options)
        : this(options, OperatingSystem.IsWindows() ? new WindowsFrebSiteConfigurationSource() : null) { }
    public FrebDiagnosticsReadService(AgentOptions options, IFrebSiteConfigurationSource? source, TimeProvider? time = null, TimeSpan? deadline = null)
    {
        _mode = options.Mode == "Simulation" ? "Simulation" : "Windows";
        _source = source; _time = time ?? TimeProvider.System; _deadline = deadline ?? TimeSpan.FromSeconds(8);
        if (_deadline <= TimeSpan.Zero || _deadline > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(deadline));
    }

    public async Task<FrebDiagnosticsSnapshot> GetAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (_mode == "Simulation") return Empty("simulation", "FREB_SIMULATION");
        if (_source is null) return Empty("unavailable", "FREB_WINDOWS_REQUIRED");
        Task<FrebDiagnosticsSnapshot> collection;
        lock (_gate)
        {
            if (_cached is not null && _time.GetElapsedTime(_cachedAt) < TimeSpan.FromSeconds(CacheSeconds)) return _cached;
            // A stalled native call retains this one task; refreshes cannot accumulate collectors.
            collection = _inflight ??= Task.Run(CollectAsync);
        }
        FrebDiagnosticsSnapshot result;
        try { result = await collection.WaitAsync(_deadline, cancellation); }
        catch (TimeoutException) { return Empty("unavailable", "FREB_TIMEOUT"); }
        lock (_gate)
        {
            if (ReferenceEquals(_inflight, collection)) { _cached = result; _cachedAt = _time.GetTimestamp(); _inflight = null; }
        }
        return result;
    }

    private async Task<FrebDiagnosticsSnapshot> CollectAsync()
    {
        using var timeout = new CancellationTokenSource(_deadline);
        var now = _time.GetUtcNow(); var rows = new List<FrebSiteDiagnostics>();
        try
        {
            var settings = await _source!.ReadAsync(timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            if (settings?.Sites is null) return Empty("unavailable", "FREB_CONFIGURATION_UNAVAILABLE");
            var sites = settings.Sites.Take(MaximumSites + 1).ToArray();
            var limited = settings.Truncated || sites.Length > MaximumSites;
            if (!settings.TracingInstalled)
                return new(now, _mode, CacheSeconds, "unavailable", "FREB_ROLE_MISSING",
                    sites.Take(MaximumSites).Select(site => new FrebSiteDiagnostics(site?.SiteId ?? 0, SafeSiteName(site?.SiteName), "unavailable", "FREB_ROLE_MISSING", limited, [])).ToArray());
            var budget = new FrebReadBudget();
            foreach (var site in sites.Take(MaximumSites))
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (site is null || site.SiteId <= 0 || site.SiteName is not { Length: > 0 and <= 256 } || site.SiteName.Any(char.IsControl))
                { rows.Add(new(site?.SiteId ?? 0, SafeSiteName(site?.SiteName), "unavailable", "FREB_CONFIGURATION_UNAVAILABLE", false, [])); continue; }
                if (site.ErrorCode is not null)
                { rows.Add(new(site.SiteId, site.SiteName, "unavailable", site.ErrorCode == "FREB_ACCESS_DENIED" ? "FREB_ACCESS_DENIED" : "FREB_CONFIGURATION_UNAVAILABLE", false, [])); continue; }
                rows.Add(_reader.ReadSite(site, now, budget, timeout.Token));
            }
            var unavailable = rows.Count(row => row.State is "unavailable" or "partial");
            var state = limited || unavailable > 0 ? rows.Any(row => row.State is "available" or "disabled" or "partial") ? "partial" : "unavailable" :
                rows.Count > 0 && rows.All(row => row.State == "disabled") ? "disabled" : "available";
            return new(now, _mode, CacheSeconds, state, limited ? "FREB_SITE_LIMIT" : null, rows.ToArray());
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        { return new(now, _mode, CacheSeconds, rows.Count > 0 ? "partial" : "unavailable", "FREB_TIMEOUT", rows.ToArray()); }
        catch (Exception exception)
        {
            return new(now, _mode, CacheSeconds, rows.Count > 0 ? "partial" : "unavailable",
                exception is UnauthorizedAccessException ? "FREB_ACCESS_DENIED" : "FREB_CONFIGURATION_UNAVAILABLE", rows.ToArray());
        }
    }
    private FrebDiagnosticsSnapshot Empty(string state, string code) => new(_time.GetUtcNow(), _mode, CacheSeconds, state, code, []);
    private static string SafeSiteName(string? name) => name is { Length: > 0 and <= 256 } && !name.Any(char.IsControl) ? name : "站点";
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsFrebSiteConfigurationSource : IFrebSiteConfigurationSource
{
    public Task<FrebSiteConfigurationSnapshot> ReadAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var manager = new ServerManager();
        var modules = manager.GetApplicationHostConfiguration().GetSection("system.webServer/globalModules").GetCollection();
        var installed = modules.Any(module => string.Equals(Convert.ToString(module["name"], CultureInfo.InvariantCulture),
            "FailedRequestsTracingModule", StringComparison.OrdinalIgnoreCase));
        var rows = new List<FrebSiteConfiguration>(); var truncated = false;
        foreach (var site in manager.Sites.Take(33))
        {
            cancellation.ThrowIfCancellationRequested();
            if (rows.Count == 32) { truncated = true; break; }
            try
            {
                var settings = site.TraceFailedRequestsLogging;
                rows.Add(new(site.Id, site.Name, settings.Enabled,
                    settings.Enabled ? Environment.ExpandEnvironmentVariables(settings.Directory) : ""));
            }
            catch (Exception exception)
            { rows.Add(new(site.Id, site.Name, false, "", exception is UnauthorizedAccessException ? "FREB_ACCESS_DENIED" : "FREB_CONFIGURATION_UNAVAILABLE")); }
        }
        return Task.FromResult(new FrebSiteConfigurationSnapshot(installed, rows.ToArray(), truncated));
    }
}
