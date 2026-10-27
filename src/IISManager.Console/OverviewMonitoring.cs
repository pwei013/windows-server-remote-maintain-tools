using IISManager.Contracts;

namespace IISManager.Console;

public sealed class MonitoringOptions
{
    public string DataDirectory { get; set; } = "data/monitoring";
    public int OverviewCacheSeconds { get; set; } = 15;
    public int OverviewConcurrency { get; set; } = 4;
    public int OverviewTimeoutSeconds { get; set; } = 5;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DataDirectory) || OverviewCacheSeconds is < 5 or > 120 ||
            OverviewConcurrency is < 1 or > 16 || OverviewTimeoutSeconds is < 1 or > 30)
            throw new InvalidOperationException("MONITORING_OPTIONS_INVALID");
    }
}

public interface IMonitoringAgentClient
{
    Task<T> GetAsync<T>(string id, string route, CancellationToken cancellation);
    Task ValidateAsync(string id, HealthProbeRequest request, CancellationToken cancellation);
    Task<HealthProbeResult> ProbeAsync(string id, HealthProbeRequest request, CancellationToken cancellation);
}

public sealed class MonitoringAgentClient(AgentClient client) : IMonitoringAgentClient
{
    public Task<T> GetAsync<T>(string id, string route, CancellationToken cancellation) => client.GetAsync<T>(id, route, cancellation);
    public async Task ValidateAsync(string id, HealthProbeRequest request, CancellationToken cancellation)
    {
        var result = await client.PostHealthAsync<System.Text.Json.JsonElement>(id, "health-check/validate", request, cancellation);
        if (!result.TryGetProperty("valid", out var valid) || valid.ValueKind != System.Text.Json.JsonValueKind.True)
            throw new AgentCallException("AGENT_RESPONSE_INVALID");
    }
    public Task<HealthProbeResult> ProbeAsync(string id, HealthProbeRequest request, CancellationToken cancellation) =>
        client.PostHealthAsync<HealthProbeResult>(id, "health-check", request, cancellation);
}

public sealed record OverviewModuleError(string Module, string Code);
public sealed record CertificateBinding(string SiteName, string HostName, int Port, string Protocol);
public sealed record CertificateAlert(string Thumbprint, string Subject, string FriendlyName, DateTimeOffset? NotBefore,
    DateTimeOffset? NotAfter, bool HasPrivateKey, int? DaysRemaining, string Severity, CertificateBinding[] Bindings);
public sealed record OverviewServer(string Id, string Name, string HostName, string Mode, string ConnectionState,
    DateTimeOffset? CollectedAt, AgentInfo? Info, IisSnapshot? Snapshot, SiteTopologySnapshot? Topology,
    WindowsServiceSnapshot? Services, CertificateAlert[] Certificates, OverviewModuleError[] Errors)
{
    public string Group { get; init; } = "";
    public string Environment { get; init; } = "";
    public bool Refreshing { get; init; }
    public bool IsStale { get; init; }
    public long? ElapsedMilliseconds { get; init; }
}
public sealed record MonitoringOverview(DateTimeOffset? CollectedAt, OverviewServer[] Servers, HealthCheckView[] HealthChecks)
{
    public ApiError? HealthError { get; init; }
    public bool IsRefreshing { get; init; }
    public int CompletedServers { get; init; }
    public int TotalServers { get; init; }
}

public sealed class OverviewService(ConsoleOptions options, IMonitoringAgentClient agent, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _collectionGate = new(options.Monitoring.OverviewConcurrency, options.Monitoring.OverviewConcurrency);
    private OverviewServer[]? _cached;
    private DateTimeOffset _collectedAt;

    public async Task<(DateTimeOffset CollectedAt, OverviewServer[] Servers)> GetAsync(CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            if (_cached is not null && _time.GetUtcNow() - _collectedAt < TimeSpan.FromSeconds(options.Monitoring.OverviewCacheSeconds))
                return (_collectedAt, _cached);
            var collected = await Task.WhenAll(options.Servers.Select(server => ReadServerAsync(server.Id, cancellation)));
            _cached = collected;
            _collectedAt = _time.GetUtcNow();
            return (_collectedAt, collected);
        }
        finally { _gate.Release(); }
    }

    public async Task<OverviewServer> ReadServerAsync(string id, CancellationToken cancellation)
    {
        var server = options.Servers.FirstOrDefault(item => item.Id == id) ?? throw new AgentCallException("SERVER_NOT_FOUND", 404);
        await _collectionGate.WaitAsync(cancellation);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.Monitoring.OverviewTimeoutSeconds));
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            return (await CollectAsync(server, deadline.Token, cancellation)) with { ElapsedMilliseconds = elapsed.ElapsedMilliseconds };
        }
        finally { _collectionGate.Release(); }
    }

    private async Task<OverviewServer> CollectAsync(ServerOptions server, CancellationToken cancellation, CancellationToken callerCancellation)
    {
        var errors = new System.Collections.Concurrent.ConcurrentBag<OverviewModuleError>();
        async Task<T?> Read<T>(string route) where T : class
        {
            try
            {
                var data = await agent.GetAsync<T>(server.Id, route, cancellation);
                if (!ValidPayload(data)) throw new AgentCallException("AGENT_RESPONSE_INVALID");
                return data;
            }
            catch (AgentCallException exception)
            {
                callerCancellation.ThrowIfCancellationRequested();
                errors.Add(new(route, exception.Code));
                return null;
            }
            catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested)
            {
                errors.Add(new(route, "AGENT_TIMEOUT"));
                return null;
            }
        }
        var infoTask = Read<AgentInfo>("info");
        var snapshotTask = Read<IisSnapshot>("snapshot");
        var topologyTask = Read<SiteTopologySnapshot>("site-topology");
        var certificatesTask = Read<CertificateInfo[]>("certificates");
        var servicesTask = Read<WindowsServiceSnapshot>("windows-services");
        await Task.WhenAll(infoTask, snapshotTask, topologyTask, certificatesTask, servicesTask);
        var info = await infoTask;
        var snapshot = await snapshotTask;
        var topology = await topologyTask;
        var services = await servicesTask;
        var certificates = await certificatesTask;
        if (info is not null && info.ProtocolVersion != Protocol.Version)
        {
            errors.Add(new("info", "AGENT_PROTOCOL_UNSUPPORTED"));
            snapshot = null; topology = null; services = null; certificates = null;
        }
        if (services is not null) services = services with { Services = services.Services.Where(service => service.Managed).ToArray() };
        // A failed certificate read is unavailable data, never an empty certificate store.
        var alerts = certificates is null ? [] : BuildCertificateAlerts(certificates,
            snapshot?.Sites ?? topology?.Sites.Select(site => new SiteConfig { Name = site.Name, Bindings = site.Bindings }).ToArray() ?? [], _time.GetUtcNow());
        var state = info is null && snapshot is null && topology is null && services is null && certificates is null
            ? "offline" : errors.IsEmpty ? "online" : "partial";
        return new(server.Id, server.Name, new Uri(server.AgentUrl).IdnHost, server.Simulation ? "Simulation" : "Windows",
            state, _time.GetUtcNow(), info, snapshot, topology, services, alerts, errors.OrderBy(error => error.Module).ToArray())
            { Group = server.Group, Environment = server.Environment };
    }

    private static bool ValidPayload(object? value) => value switch
    {
        AgentInfo info => !string.IsNullOrEmpty(info.ProtocolVersion) && info.MachineName is not null && info.ReservedPorts is not null,
        IisSnapshot snapshot => snapshot.ApplicationPools is not null && snapshot.Sites is not null &&
            snapshot.ApplicationPools.All(pool => pool is not null && pool.Name is not null) &&
            snapshot.Sites.All(site => site is not null && site.Name is not null && site.Bindings is not null &&
                site.Bindings.All(binding => binding is not null && !string.IsNullOrEmpty(binding.Protocol))),
        SiteTopologySnapshot topology => topology.Sites is not null && topology.ApplicationPools is not null && topology.Sites.All(site =>
            site is not null && site.Name is not null && site.Bindings is not null &&
            site.Bindings.All(binding => binding is not null && !string.IsNullOrEmpty(binding.Protocol)) &&
            site.Applications is not null && site.Applications.All(application => application is not null && application.Path is not null)),
        WindowsServiceSnapshot services => services.Services is not null && services.Services.All(service => service is not null && service.Name is not null),
        CertificateInfo[] certificates => certificates.All(certificate => certificate is not null && !string.IsNullOrEmpty(certificate.Thumbprint)),
        _ => false
    };

    public static CertificateAlert[] BuildCertificateAlerts(CertificateInfo[] certificates, SiteConfig[] sites, DateTimeOffset now)
    {
        static string Key(string value) => value.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        var bindings = sites.SelectMany(site => site.Bindings.Where(binding =>
                string.Equals(binding.Protocol, "https", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(binding.CertificateThumbprint))
            .Select(binding => (Key: Key(binding.CertificateThumbprint!), Binding: new CertificateBinding(site.Name, binding.HostName, binding.Port, binding.Protocol))))
            .GroupBy(item => item.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Select(item => item.Binding).Distinct().ToArray());
        var known = certificates.GroupBy(certificate => Key(certificate.Thumbprint), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        return known.Keys.Union(bindings.Keys, StringComparer.Ordinal).Select(key =>
        {
            var linked = bindings.GetValueOrDefault(key) ?? [];
            if (!known.TryGetValue(key, out var certificate)) return new CertificateAlert(key, "", "绑定证书未找到", null, null, false, null, "missing", linked);
            var remaining = certificate.NotAfter - now;
            var severity = certificate.NotBefore > now ? "notYetValid" : remaining <= TimeSpan.Zero ? "expired" :
                remaining <= TimeSpan.FromDays(7) ? "critical" : remaining <= TimeSpan.FromDays(15) ? "warning" :
                remaining <= TimeSpan.FromDays(30) ? "notice" : "normal";
            return new CertificateAlert(key, certificate.Subject, certificate.FriendlyName, certificate.NotBefore, certificate.NotAfter,
                certificate.HasPrivateKey, (int)Math.Ceiling(remaining.TotalDays), severity, linked);
        }).OrderBy(certificate => certificate.Severity == "normal" ? 1 : 0).ThenBy(certificate => certificate.NotAfter).ThenBy(certificate => certificate.Thumbprint).ToArray();
    }
}
