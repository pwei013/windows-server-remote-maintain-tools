using System.Text.Json;
using IISManager.Contracts;
using IISManager.Core;

namespace IISManager.Agent.Backends;

/// <summary>In-memory Development demo. The host must reject this adapter outside Development.</summary>
public sealed class SimulationIisBackend : IIisBackend, IIisTopologyBackend
{
    private readonly AgentOptions _options;
    private readonly object _gate = new();
    private readonly List<PoolConfig> _pools = [];
    private readonly List<SiteConfig> _sites = [];
    private readonly Dictionary<string, ApplicationTopology[]> _applications = new(StringComparer.OrdinalIgnoreCase);
    private long _recycleGeneration;
    private long _siteRestartGeneration;
    private const string DemoCertificate = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly CertificateInfo _certificate = new(DemoCertificate, "CN=demo.simulation.test (SIMULATION)",
        "SIMULATION — NOT A REAL CERTIFICATE", DateTimeOffset.Parse("2020-01-01T00:00:00Z"),
        DateTimeOffset.Parse("2099-01-01T00:00:00Z"), true);

    public SimulationIisBackend(AgentOptions options)
    {
        _options = options;
        _pools.Add(new PoolConfig { Name = "SIMULATION-DefaultPool", State = "Stopped", AutoStart = false });
    }

    public Task<AgentInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AgentInfo(Protocol.Version, "1.0.0", "SIMULATION", "Simulation",
            Environment.OSVersion.VersionString, "SIMULATION — no Windows/IIS access", false,
            _options.ReservedPorts.Append(_options.Port).Distinct().Order().ToArray()));
    }
    public Task<IisSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(Snapshot());
    }
    public Task<SiteTopologySnapshot> GetSiteTopologyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(Topology());
    }
    public Task<PoolConfig?> GetPoolAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(Snapshot().ApplicationPools.FirstOrDefault(x => BackendRules.Equal(x.Name, name)));
    }
    public Task<CertificateInfo[]> GetCertificatesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new[] { _certificate });
    }
    public Task<ValidationResult> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(Validate(request));
    }
    public Task<ExecutionResult> ExecuteAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (request.ExpiresAt <= DateTimeOffset.UtcNow)
                return Task.FromResult(new ExecutionResult(OperationStates.Expired, null, new("operation.expired", "操作已过期。")));
            if (string.IsNullOrEmpty(request.ExpectedFingerprint))
                return Task.FromResult(new ExecutionResult(OperationStates.Failed, null, new("configuration.fingerprintRequired", "执行必须携带最新预检指纹。")));
            var validation = Validate(request);
            if (!validation.Valid) return Task.FromResult(new ExecutionResult(OperationStates.Failed, null, validation.Errors[0]));
            var proposed = validation.Proposed!.Value;
            if (OperationTypes.IsSiteMaintenance(request.Type))
            {
                var expected = proposed.Deserialize<SiteTopology>(Protocol.Json)!;
                var before = validation.Before!.Value.Deserialize<SiteTopology>(Protocol.Json)!;
                var index = _sites.FindIndex(site => BackendRules.Equal(site.Name, request.Target.SiteName));
                var root = expected.Applications.Single(application => application.Path == "/");
                _sites[index] = _sites[index] with { PhysicalPath = root.PhysicalPath, ApplicationPoolName = root.ApplicationPoolName, Bindings = expected.Bindings };
                _applications[_sites[index].Name] = expected.Applications;
                var actual = Topology().Sites.Single(site => BackendRules.Equal(site.Name, request.Target.SiteName));
                var matches = BackendRules.MaintenanceMatches(actual, expected, before, Snapshot());
                return Task.FromResult(new ExecutionResult(matches ? OperationStates.Succeeded : OperationStates.Partial,
                    JsonSerializer.SerializeToElement(actual, Protocol.Json), matches ? null : new("iis.readbackMismatch", "模拟维护后的独立配置读回不一致。")));
            }
            if (request.Type == OperationTypes.CreateSite)
                _sites.Add(proposed.Deserialize<SiteConfig>(Protocol.Json)!);
            else if (OperationTypes.IsSiteLifecycle(request.Type))
            {
                var index = _sites.FindIndex(x => BackendRules.Equal(x.Name, request.Target.SiteName));
                _sites[index] = proposed.Deserialize<SiteConfig>(Protocol.Json)!;
                if (request.Type == OperationTypes.SiteRestart) _siteRestartGeneration++;
            }
            else if (request.Type == OperationTypes.CreatePool)
                _pools.Add(proposed.Deserialize<PoolConfig>(Protocol.Json)!);
            else
            {
                var index = _pools.FindIndex(x => BackendRules.Equal(x.Name, request.Target.ApplicationPoolName));
                _pools[index] = proposed.Deserialize<PoolConfig>(Protocol.Json)!;
                if (request.Type == OperationTypes.Recycle) _recycleGeneration++;
            }
            object? after = OperationTypes.IsSite(request.Type)
                ? Snapshot().Sites.FirstOrDefault(x => BackendRules.Equal(x.Name, request.Target.SiteName))
                : Snapshot().ApplicationPools.FirstOrDefault(x => BackendRules.Equal(x.Name, request.Target.ApplicationPoolName));
            return Task.FromResult(new ExecutionResult(OperationStates.Succeeded, JsonSerializer.SerializeToElement(after, Protocol.Json)));
        }
    }
    private IisSnapshot Snapshot()
    {
        var pools = _pools.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(pool =>
        {
            var applications = _sites.SelectMany(site => ApplicationsFor(site).Where(application => BackendRules.Equal(application.ApplicationPoolName, pool.Name))
                .Select(application => site.Name + application.Path)).Order(StringComparer.Ordinal).ToArray();
            var value = pool with { Applications = applications, Fingerprint = "" };
            return value with { Fingerprint = BackendRules.Hash(new { value, recycleGeneration = _recycleGeneration }) };
        }).ToArray();
        return new(DateTimeOffset.UtcNow, pools, _sites.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }
    private ValidationResult Validate(OperationRequest request)
    {
        var evidence = new { mode = "SIMULATION", recycleGeneration = _recycleGeneration, siteRestartGeneration = _siteRestartGeneration, certificate = _certificate };
        var value = OperationTypes.IsSiteMaintenance(request.Type)
            ? BackendRules.ValidateMaintenance(request, Snapshot(), Topology(), _options, false, evidence)
            : BackendRules.Validate(request, Snapshot(), _options, false, evidence, CheckSite);
        if (value.Valid && request.Type == OperationTypes.UpdateSite)
        {
            var patch = request.Parameters.Deserialize<SiteUpdateParameters>(Protocol.Json)!;
            if (patch.Bindings is not null)
            {
                var errors = new List<ApiError>();
                foreach (var binding in patch.Bindings.Where(binding => binding.Protocol == "https"))
                    if (!BackendRules.Equal(binding.CertificateThumbprint, DemoCertificate) || !BackendRules.Equal(binding.HostName, "demo.simulation.test"))
                        errors.Add(new("certificate.notFound", "模拟 HTTPS 仅支持演示证书和 demo.simulation.test 主机名。"));
                if (errors.Count != 0) value = value with { Valid = false, Errors = errors.ToArray() };
            }
        }
        return value with { Warnings = value.Warnings.Append("SIMULATION：全部操作仅修改内存演示数据；未校验 Windows 身份、HTTP.sys 或真实证书私钥。").ToArray() };
    }
    private ApplicationTopology[] ApplicationsFor(SiteConfig site) => _applications.TryGetValue(site.Name, out var applications) ? applications :
        [new ApplicationTopology { Path = "/", ApplicationPoolName = site.ApplicationPoolName, PhysicalPath = site.PhysicalPath, EnabledProtocols = "http",
            VirtualDirectories = [new VirtualDirectoryTopology { Path = "/", PhysicalPath = site.PhysicalPath }] }];
    private SiteTopologySnapshot Topology()
    {
        var snapshot = Snapshot();
        return new(snapshot.CollectedAt, snapshot.Sites.Select(site => new SiteTopology { Id = site.Id, Name = site.Name,
            State = site.State, Bindings = site.Bindings, Applications = ApplicationsFor(site) }).ToArray(),
            snapshot.ApplicationPools.Select(pool => pool with { Fingerprint = "" }).ToArray());
    }
    private void CheckSite(SiteParameters p, PoolConfig pool, List<ApiError> errors)
    {
        try { SitePathGuard.Validate(p.PhysicalPath, _options.AllowedSiteRoots); }
        catch (OperationException ex) { errors.Add(new(ex.Code, ex.Message)); }
        foreach (var binding in p.Bindings.Where(x => x.Protocol == "https"))
            if (!BackendRules.Equal(binding.CertificateThumbprint, DemoCertificate) || !BackendRules.Equal(binding.HostName, "demo.simulation.test"))
                errors.Add(new("certificate.notFound", "模拟 HTTPS 仅支持演示证书和 demo.simulation.test 主机名。"));
    }
}
