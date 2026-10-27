using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using IISManager.Contracts;
using IISManager.Core;
using Microsoft.Web.Administration;
using Microsoft.Win32;

namespace IISManager.Agent.Backends;

[SupportedOSPlatform("windows")]
public sealed class WindowsIisBackend : IIisBackend, IIisTopologyBackend
{
    private readonly AgentOptions _options;
    public WindowsIisBackend(AgentOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new OperationException("iis.windowsRequired", "Windows IIS 模式只能在 Windows Server 上使用。", 503);
        _options = options;
    }

    public Task<AgentInfo> GetInfoAsync(CancellationToken cancellationToken = default) => Read(() =>
    {
        using var manager = new ServerManager();
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\InetStp");
        return new AgentInfo(Protocol.Version, "1.0.0", Environment.MachineName, "Windows", Environment.OSVersion.VersionString,
            Convert.ToString(key?.GetValue("VersionString"), CultureInfo.InvariantCulture) ?? "Unknown",
            IsShared(manager), _options.ReservedPorts.Append(_options.Port).Distinct().Order().ToArray());
    }, cancellationToken);

    public Task<IisSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Read(() =>
    {
        using var manager = new ServerManager();
        return Snapshot(manager);
    }, cancellationToken);

    public Task<SiteTopologySnapshot> GetSiteTopologyAsync(CancellationToken cancellationToken = default) => Read(() =>
    {
        using var manager = new ServerManager();
        return Topology(manager);
    }, cancellationToken);

    private static SiteTopologySnapshot Topology(ServerManager manager)
    {
        var sites = manager.Sites.OrderBy(site => site.Name, StringComparer.OrdinalIgnoreCase).Select(site => new SiteTopology
        {
            Id = site.Id, Name = site.Name, State = site.State.ToString(),
            Bindings = site.Bindings.Select(binding => new BindingConfig
            {
                Protocol = binding.Protocol,
                IpAddress = binding.IsIPPortHostBinding ? BackendRules.NormalizeIp(binding.EndPoint.Address.ToString()) : "*",
                Port = binding.IsIPPortHostBinding ? binding.EndPoint.Port : 0,
                HostName = binding.IsIPPortHostBinding ? binding.Host ?? "" : "",
                UseSni = binding.Protocol == "https" && (binding.SslFlags & SslFlags.Sni) != 0,
                CertificateThumbprint = binding.Protocol == "https" ? Convert.ToHexString(binding.CertificateHash ?? []) : null
            }).OrderBy(BackendRules.BindingKey, StringComparer.Ordinal).ToArray(),
            Applications = site.Applications.OrderBy(application => application.Path, StringComparer.Ordinal).Select(application => new ApplicationTopology
            {
                Path = application.Path, ApplicationPoolName = application.ApplicationPoolName,
                PhysicalPath = application.VirtualDirectories.FirstOrDefault(directory => directory.Path == "/")?.PhysicalPath ?? "",
                EnabledProtocols = application.EnabledProtocols,
                VirtualDirectories = application.VirtualDirectories.OrderBy(directory => directory.Path, StringComparer.Ordinal).Select(directory => new VirtualDirectoryTopology
                {
                    Path = directory.Path, PhysicalPath = directory.PhysicalPath
                }).ToArray()
            }).ToArray()
        }).ToArray();
        var pools = manager.ApplicationPools.OrderBy(pool => pool.Name, StringComparer.OrdinalIgnoreCase).Select(pool => new PoolConfig
        {
            Name = pool.Name, State = pool.State.ToString(), ManagedRuntimeVersion = pool.ManagedRuntimeVersion,
            ManagedPipelineMode = pool.ManagedPipelineMode.ToString(), Enable32BitAppOnWin64 = pool.Enable32BitAppOnWin64,
            AutoStart = pool.AutoStart, StartMode = pool.StartMode.ToString(),
            IdleTimeoutMinutes = checked((int)pool.ProcessModel.IdleTimeout.TotalMinutes),
            IdleTimeoutAction = ReadIdleAction(pool), IdentityType = pool.ProcessModel.IdentityType.ToString(),
            Applications = sites.SelectMany(site => site.Applications
                .Where(application => BackendRules.Equal(application.ApplicationPoolName, pool.Name))
                .Select(application => site.Name + application.Path)).Order(StringComparer.Ordinal).ToArray()
        }).ToArray();
        // Display-only data uses public properties; legacy snapshots and fingerprints keep their own projection.
        return new SiteTopologySnapshot(DateTimeOffset.UtcNow, sites, pools);
    }

    public Task<PoolConfig?> GetPoolAsync(string name, CancellationToken cancellationToken = default) => Read(() =>
    {
        using var manager = new ServerManager();
        return Snapshot(manager).ApplicationPools.FirstOrDefault(x => BackendRules.Equal(x.Name, name));
    }, cancellationToken);

    public Task<CertificateInfo[]> GetCertificatesAsync(CancellationToken cancellationToken = default) => Read(() =>
    {
        using var store = OpenCertificates();
        return store.Certificates.Cast<X509Certificate2>().OrderBy(x => x.Thumbprint, StringComparer.Ordinal)
            .Select(ToCertificate).ToArray();
    }, cancellationToken);

    public Task<ValidationResult> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default) => Read(() =>
    {
        using var manager = new ServerManager();
        using var validation = Validate(manager, request);
        return validation.Result;
    }, cancellationToken);

    public async Task<ExecutionResult> ExecuteAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        if (OperationTypes.IsSiteMaintenance(request.Type)) return ExecuteMaintenance(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ExpiresAt <= DateTimeOffset.UtcNow) return new(OperationStates.Expired, null, new("operation.expired", "操作已过期。"));
        if (string.IsNullOrEmpty(request.ExpectedFingerprint))
            return new(OperationStates.Failed, null, new("configuration.fingerprintRequired", "执行必须携带最新预检指纹。"));
        var mayHaveMutated = false;
        var stage = "执行前检查";
        string? siteConfigurationFingerprint = null;
        ValidationLease? validation = null;
        try
        {
            using (var manager = new ServerManager())
            {
                validation = Validate(manager, request);
                if (!validation.Result.Valid) return new(OperationStates.Failed, null, validation.Result.Errors[0]);
                cancellationToken.ThrowIfCancellationRequested();
                if (request.ExpiresAt <= DateTimeOffset.UtcNow) return new(OperationStates.Expired, null, new("operation.expired", "操作在执行前已过期。"));
                var pool = manager.ApplicationPools.FirstOrDefault(x => BackendRules.Equal(x.Name, request.Target.ApplicationPoolName));
                var managedSite = manager.Sites.FirstOrDefault(x => BackendRules.Equal(x.Name, request.Target.SiteName));
                if (OperationTypes.IsSiteLifecycle(request.Type))
                    siteConfigurationFingerprint = SiteEvidence(managedSite!).ConfigurationFingerprint;
                stage = "执行 IIS 动作";
                switch (request.Type)
                {
                    case OperationTypes.Start:
                        mayHaveMutated = true;
                        if (pool!.State != ObjectState.Started) pool.Start();
                        break;
                    case OperationTypes.Stop:
                        mayHaveMutated = true;
                        if (pool!.State != ObjectState.Stopped) pool.Stop();
                        break;
                    case OperationTypes.Recycle:
                        mayHaveMutated = true;
                        pool!.Recycle();
                        break;
                    case OperationTypes.SiteStart:
                        mayHaveMutated = true;
                        if (managedSite!.State != ObjectState.Started) managedSite.Start();
                        break;
                    case OperationTypes.SiteStop:
                        mayHaveMutated = true;
                        if (managedSite!.State != ObjectState.Stopped) managedSite.Stop();
                        break;
                    case OperationTypes.SiteRestart:
                        var beforeRestart = SiteEvidence(managedSite!);
                        mayHaveMutated = true;
                        await SiteRestartSequence.ExecuteAsync(beforeRestart, () => managedSite!.Stop(), () =>
                        {
                            using var readManager = new ServerManager();
                            var current = readManager.Sites.FirstOrDefault(x => BackendRules.Equal(x.Name, request.Target.SiteName));
                            return current is null ? null : SiteEvidence(current);
                        }, stopped =>
                        {
                            using var startManager = new ServerManager();
                            var current = startManager.Sites.FirstOrDefault(x => BackendRules.Equal(x.Name, request.Target.SiteName));
                            if (current is null || SiteEvidence(current) != stopped)
                                throw new OperationException("site.configurationChanged", "启动前站点状态或配置发生变化，已暂停启动；请按原编号只读核对。");
                            current.Start();
                        });
                        break;
                    case OperationTypes.CreatePool:
                        stage = "准备应用池配置";
                        var newPool = manager.ApplicationPools.Add(request.Target.ApplicationPoolName!);
                        // Default to manual start when omitted; ApplyPool honors explicit AutoStart.
                        newPool.AutoStart = false;
                        newPool.ManagedRuntimeVersion = "";
                        newPool.ManagedPipelineMode = ManagedPipelineMode.Integrated;
                        newPool.Enable32BitAppOnWin64 = false;
                        newPool.StartMode = StartMode.OnDemand;
                        newPool.ProcessModel.IdentityType = ProcessModelIdentityType.ApplicationPoolIdentity;
                        newPool.ProcessModel.IdleTimeout = TimeSpan.FromMinutes(20);
                        newPool.ProcessModel.SetAttributeValue("idleTimeoutAction", 0);
                        ApplyPool(newPool, request.Parameters.Deserialize<PoolParameters>(Protocol.Json)!);
                        stage = "提交应用池配置";
                        mayHaveMutated = true;
                        manager.CommitChanges();
                        break;
                    case OperationTypes.UpdatePool:
                        ApplyPool(pool!, request.Parameters.Deserialize<PoolParameters>(Protocol.Json)!);
                        mayHaveMutated = true;
                        manager.CommitChanges();
                        break;
                    case OperationTypes.CreateSite:
                        var p = request.Parameters.Deserialize<SiteParameters>(Protocol.Json)!;
                        // Certificate binding APIs can affect HTTP.sys separately from applicationHost.config.
                        // Mark the boundary before invoking any binding API, rather than only at CommitChanges.
                        mayHaveMutated = true;
                        var first = BackendRules.Normalize(p.Bindings[0]);
                        var site = manager.Sites.Add(request.Target.SiteName!, first.Protocol, BindingInformation(first), Path.GetFullPath(p.PhysicalPath));
                        site.ServerAutoStart = false;
                        site.Applications["/"].ApplicationPoolName = p.ApplicationPoolName;
                        site.Bindings.Clear();
                        foreach (var requested in p.Bindings)
                        {
                            var binding = BackendRules.Normalize(requested);
                            if (binding.Protocol == "https")
                                site.Bindings.Add(BindingInformation(binding), Convert.FromHexString(binding.CertificateThumbprint!), "My", binding.UseSni ? SslFlags.Sni : SslFlags.None);
                            else site.Bindings.Add(BindingInformation(binding), "http");
                        }
                        manager.CommitChanges();
                        break;
                }
            }

            stage = "独立读回 IIS 结果";
            return await ReadBack(request, validation.Result.Proposed, siteConfigurationFingerprint);
        }
        catch (OperationCanceledException) when (!mayHaveMutated) { throw; }
        catch (Exception ex)
        {
            var error = SafeError(ex, stage);
            if (!mayHaveMutated) return new(OperationStates.Failed, null, error);
            // Read back with a completely new ServerManager. Never retry a mutation or auto-roll back.
            try
            {
                using var manager = new ServerManager();
                var after = FindAfter(Snapshot(manager), request);
                var state = after is null ? OperationStates.Unknown : OperationStates.Partial;
                return new(state, after is null ? null : JsonSerializer.SerializeToElement(after, Protocol.Json), error);
            }
            catch { return new(OperationStates.Unknown, null, new("iis.resultUnknown", $"动作可能已发生，但独立读回失败；请只读核实后再处理，禁止自动重放。原始错误：{error.Code}；{error.Message}")); }
        }
        finally { validation?.Dispose(); }
    }

    private static bool PoolRuntimeNotReady(Exception exception) => exception is COMException &&
        // HRESULT_FROM_WIN32(ERROR_OBJECT_NOT_FOUND / ERROR_NOT_FOUND). Only creation
        // read paths use this filter; permission, schema and mutation errors are not retried.
        exception.HResult is unchecked((int)0x800710D8) or unchecked((int)0x80070490);

    private async Task<ExecutionResult> ReadBack(OperationRequest request, JsonElement? proposed, string? siteConfigurationFingerprint)
    {
        var expectedState = OperationTypes.IsSite(request.Type)
            ? proposed?.Deserialize<SiteConfig>(Protocol.Json)?.State
            : proposed?.Deserialize<PoolConfig>(Protocol.Json)?.State;
        var observation = await IisStatePolling.ReadUntilAsync(() =>
        {
            using var manager = new ServerManager();
            var current = FindAfter(Snapshot(manager), request);
            var configurationMatches = true;
            if (siteConfigurationFingerprint is not null)
            {
                var site = manager.Sites.FirstOrDefault(x => BackendRules.Equal(x.Name, request.Target.SiteName));
                configurationMatches = site is not null && SiteEvidence(site).ConfigurationFingerprint == siteConfigurationFingerprint;
            }
            return (After: current, ConfigurationMatches: configurationMatches);
        }, current => current.After is null || !current.ConfigurationMatches || expectedState is null ||
            (current.After is PoolConfig pool ? pool.State : ((SiteConfig)current.After).State) == expectedState,
            timeout: null, pollInterval: null,
            retryRead: request.Type == OperationTypes.CreatePool ? PoolRuntimeNotReady : null);
        var after = observation.After;
        if (after is null) return new(OperationStates.Unknown, null, new("iis.resultUnknown", "动作后目标未能读回，禁止自动重放。"));
        var serialized = JsonSerializer.SerializeToElement(after, Protocol.Json);
        var readbackError = ReadbackError(request, after, proposed, observation.ConfigurationMatches);
        if (readbackError is not null)
            return new(OperationStates.Partial, serialized, readbackError);
        if (request.Type == OperationTypes.CreateSite)
        {
            var p = request.Parameters.Deserialize<SiteParameters>(Protocol.Json)!;
            var claims = HttpSysCertificateReader.Read();
            foreach (var b in p.Bindings.Where(x => x.Protocol == "https").Select(BackendRules.Normalize))
                if (!claims.Any(x => ClaimIsExact(x, b) && BackendRules.Equal(x.Thumbprint, b.CertificateThumbprint) && BackendRules.Equal(x.StoreName, "My")))
                    return new(OperationStates.Partial, serialized, new("binding.httpSysReadbackMismatch", "站点配置已读回，但 HTTP.sys 证书/SNI 记录未匹配，禁止自动重放。"));
        }
        return new(OperationStates.Succeeded, serialized);
    }

    private ValidationLease Validate(ServerManager manager, OperationRequest request)
    {
        if (OperationTypes.IsSiteMaintenance(request.Type)) return ValidateMaintenance(manager, request);
        var snapshot = Snapshot(manager);
        var shared = IsShared(manager);
        var errors = new List<ApiError>();
        var directory = (WindowsDirectoryLease?)null;
        HttpSysCertificateRecord[] claims = [];
        TcpListenerRecord[] listeners = [];
        CertificateInfo[] certificates = [];
        var siteApplications = OperationTypes.IsSiteLifecycle(request.Type)
            ? manager.Sites.FirstOrDefault(x => BackendRules.Equal(x.Name, request.Target.SiteName))?.Applications
                .Select(application => request.Target.SiteName + application.Path).Order(StringComparer.Ordinal).ToArray()
            : null;
        var preliminary = BackendRules.Validate(request with { ExpectedFingerprint = null }, snapshot, _options, shared, siteApplications: siteApplications);
        try
        {
            if (preliminary.Valid && request.Type == OperationTypes.CreateSite)
            {
                var p = request.Parameters.Deserialize<SiteParameters>(Protocol.Json)!;
                var pool = manager.ApplicationPools.First(x => BackendRules.Equal(x.Name, p.ApplicationPoolName));
                directory = WindowsDirectoryLease.Acquire(p.PhysicalPath, _options.AllowedSiteRoots, IdentityName(pool));
                claims = HttpSysCertificateReader.Read();
                listeners = TcpListenerGuard.Read();
                using var store = OpenCertificates();
                certificates = store.Certificates.Cast<X509Certificate2>().OrderBy(x => x.Thumbprint, StringComparer.Ordinal).Select(ToCertificate).ToArray();
                foreach (var b in p.Bindings.Select(BackendRules.Normalize))
                {
                    CheckHttpSysClaims(b, claims, snapshot, errors);
                    TcpListenerGuard.Check(b, listeners, snapshot, errors);
                    if (b.Protocol == "https") CheckCertificate(store, b, errors);
                }
            }
            if (preliminary.Valid && request.Type is OperationTypes.CreatePool or OperationTypes.UpdatePool)
            {
                if (!manager.ApplicationPoolDefaults.ProcessModel.Attributes.Any(x => x.Name == "idleTimeoutAction"))
                    errors.Add(new("iis.versionUnsupported", "此操作需要支持 idleTimeoutAction 的 IIS 8.5 或更新版本。"));
                var p = request.Parameters.Deserialize<PoolParameters>(Protocol.Json)!;
                if (p.Enable32BitAppOnWin64 == true && !Environment.Is64BitOperatingSystem)
                    errors.Add(new("pool.architectureInvalid", "32 位工作进程选项要求 64 位 Windows。"));
            }
        }
        catch (Exception ex) { errors.Add(SafeError(ex)); }
        try
        {
            var evidence = new
            {
                hidden = ConfigurationEvidence(manager),
                directories = directory?.Evidence ?? [], claims, listeners, certificates
            };
            var result = BackendRules.Validate(request, snapshot, _options, shared, evidence, siteApplications: siteApplications);
            if (errors.Count > 0) result = result with { Valid = false, Errors = result.Errors.Concat(errors).ToArray() };
            if (request.Type == OperationTypes.CreateSite)
                result = result with { Warnings = result.Warnings.Append("目录权限使用保守的身份 SID/通用身份 DACL 证明；仅组授权、条件 ACL 或存在相关 Deny 时拒绝。未验证全部内容文件或应用业务健康。").ToArray() };
            return new(result, directory);
        }
        catch { directory?.Dispose(); throw; }
    }

    private ValidationLease ValidateMaintenance(ServerManager manager, OperationRequest request)
    {
        var snapshot = Snapshot(manager);
        var topology = Topology(manager);
        var shared = IsShared(manager);
        var preliminary = BackendRules.ValidateMaintenance(request with { ExpectedFingerprint = null }, snapshot, topology, _options, shared);
        WindowsDirectoryLease? directory = null;
        var ownerDirectories = new List<WindowsDirectoryLease>();
        var errors = new List<ApiError>();
        HttpSysCertificateRecord[] claims = [];
        TcpListenerRecord[] listeners = [];
        CertificateInfo[] certificates = [];
        try
        {
            if (preliminary.Valid)
            {
                var proposed = preliminary.Proposed!.Value.Deserialize<SiteTopology>(Protocol.Json)!;
                var applicationPath = request.Type == OperationTypes.UpdateSite ? "/" : request.Target.ApplicationPath!;
                var application = proposed.Applications.Single(item => BackendRules.Equal(item.Path, applicationPath));
                var directoryPath = request.Type is OperationTypes.CreateVirtualDirectory or OperationTypes.UpdateVirtualDirectory
                    ? request.Target.VirtualDirectoryPath! : "/";
                var targetDirectory = application.VirtualDirectories.Single(item => BackendRules.Equal(item.Path, directoryPath));
                var actualSite = manager.Sites.Single(item => BackendRules.Equal(item.Name, request.Target.SiteName));
                var existing = actualSite.Applications.FirstOrDefault(item => BackendRules.Equal(item.Path, applicationPath))?
                    .VirtualDirectories.FirstOrDefault(item => BackendRules.Equal(item.Path, directoryPath));
                if (existing is not null && !string.IsNullOrEmpty(existing.UserName))
                    errors.Add(new("site.directoryCredentialsUnsupported", "目标目录使用独立连接身份，不能用应用池身份安全验证；请由操作员处理，不在本工具修改凭据。"));
                var pool = manager.ApplicationPools.Single(item => BackendRules.Equal(item.Name, application.ApplicationPoolName));
                directory = WindowsDirectoryLease.Acquire(targetDirectory.PhysicalPath, _options.AllowedSiteRoots, IdentityName(pool));
                if (request.Type == OperationTypes.UpdateVirtualDirectory &&
                    request.Parameters.Deserialize<VirtualDirectoryParameters>(Protocol.Json)!.OwnerApplicationPoolName is not null)
                {
                    // The requested pool belongs to the whole application. Hold and verify every
                    // owned mapping against the new identity until the combined change is read back.
                    var owner = actualSite.Applications.Single(item => BackendRules.Equal(item.Path, applicationPath));
                    foreach (var mapping in application.VirtualDirectories.Where(item => !BackendRules.Equal(item.Path, directoryPath)))
                    {
                        if (!string.IsNullOrEmpty(owner.VirtualDirectories.Single(item => BackendRules.Equal(item.Path, mapping.Path)).UserName))
                            errors.Add(new("site.directoryCredentialsUnsupported", "所属应用的目录使用独立连接身份，无法用新应用池身份安全验证；请由操作员核实。"));
                        ownerDirectories.Add(WindowsDirectoryLease.Acquire(mapping.PhysicalPath, _options.AllowedSiteRoots, IdentityName(pool)));
                    }
                }
                if (request.Type == OperationTypes.UpdateSite && request.Parameters.Deserialize<SiteUpdateParameters>(Protocol.Json)!.Bindings is not null)
                {
                    if (actualSite.Bindings.Any(binding => binding.Protocol is not ("http" or "https") ||
                            binding.Protocol == "https" && (binding.SslFlags & ~SslFlags.Sni) != SslFlags.None))
                        errors.Add(new("binding.optionsUnsupported", "现有绑定包含高级 SSL 或非 HTTP 配置；本工具不重写这些设置。"));
                    claims = HttpSysCertificateReader.Read();
                    listeners = TcpListenerGuard.Read();
                    using var store = OpenCertificates();
                    certificates = store.Certificates.Cast<X509Certificate2>().OrderBy(item => item.Thumbprint, StringComparer.Ordinal).Select(ToCertificate).ToArray();
                    var before = preliminary.Before!.Value.Deserialize<SiteTopology>(Protocol.Json)!;
                    CheckMaintenanceTlsOwnership(before, proposed, topology.Sites, claims, errors);
                    var replaceable = ChangedTlsKeys(before, proposed);
                    var unmodifiedClaims = claims.Where(claim => !replaceable.Contains(ClaimKey(claim))).ToArray();
                    foreach (var binding in proposed.Bindings)
                    {
                        CheckHttpSysClaims(binding, unmodifiedClaims, snapshot, errors);
                        TcpListenerGuard.Check(binding, listeners, snapshot, errors);
                        if (binding.Protocol == "https") CheckCertificate(store, binding, errors);
                    }
                }
            }
            var evidence = new { hidden = ConfigurationEvidence(manager), directories = directory?.Evidence ?? [],
                ownerDirectories = ownerDirectories.SelectMany(item => item.Evidence).ToArray(), claims, listeners, certificates };
            var result = BackendRules.ValidateMaintenance(request, snapshot, topology, _options, shared, evidence);
            if (errors.Count > 0) result = result with { Valid = false, Errors = result.Errors.Concat(errors).ToArray() };
            return new(result with { Warnings = result.Warnings.Append("目录访问沿用保守 SID/DACL 证明；未验证所有文件、业务访问或完整 TLS 握手。" ).ToArray() }, directory, claims, ownerDirectories);
        }
        catch (Exception exception)
        {
            directory?.Dispose();
            foreach (var mapping in ownerDirectories) mapping.Dispose();
            return new(preliminary with { Valid = false, Errors = preliminary.Errors.Append(SafeError(exception)).ToArray() }, null);
        }
    }

    private ExecutionResult ExecuteMaintenance(OperationRequest request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (request.ExpiresAt <= DateTimeOffset.UtcNow) return new(OperationStates.Expired, null, new("operation.expired", "操作已过期。"));
        if (string.IsNullOrEmpty(request.ExpectedFingerprint)) return new(OperationStates.Failed, null, new("configuration.fingerprintRequired", "执行必须携带最新预检指纹。"));
        var mayHaveMutated = false;
        ValidationLease? validation = null;
        try
        {
            SiteTopology expected;
            SiteTopology before;
            string preservation;
            HttpSysCertificateRecord[] claims = [];
            using (var manager = new ServerManager())
            {
                validation = ValidateMaintenance(manager, request);
                if (!validation.Result.Valid) return new(OperationStates.Failed, null, validation.Result.Errors.First());
                expected = validation.Result.Proposed!.Value.Deserialize<SiteTopology>(Protocol.Json)!;
                before = validation.Result.Before!.Value.Deserialize<SiteTopology>(Protocol.Json)!;
                preservation = MaintenancePreservationFingerprint(manager, request, before, expected);
                if (request.Type == OperationTypes.UpdateSite && request.Parameters.Deserialize<SiteUpdateParameters>(Protocol.Json)!.Bindings is not null)
                {
                    claims = validation.TlsClaims ?? throw new OperationException("binding.httpSysOwnershipUnverified", "缺少执行前 TLS 记录证明。" );
                    if (CanonicalJson.Hash(claims) != CanonicalJson.Hash(HttpSysCertificateReader.Read()))
                        throw new OperationException("configuration.conflict", "HTTP.sys 记录在执行前发生变化，请重新预检查。" );
                }
                cancellation.ThrowIfCancellationRequested();
                if (request.ExpiresAt <= DateTimeOffset.UtcNow) return new(OperationStates.Expired, null, new("operation.expired", "操作在维护前已过期。"));
                // Binding setters can change HTTP.sys before CommitChanges, so cross the boundary first.
                mayHaveMutated = true;
                ApplyMaintenance(manager, request, expected);
                manager.CommitChanges();
            }
            // A fresh ServerManager reads the full site and all pool states independently.
            using var read = new ServerManager();
            var actual = Topology(read).Sites.SingleOrDefault(site => BackendRules.Equal(site.Name, expected.Name));
            if (actual is null) return new(OperationStates.Unknown, null, new("iis.resultUnknown", "维护后目标无法读回，请按原编号只读核实。"));
            var after = JsonSerializer.SerializeToElement(BackendRules.SortMaintenanceSite(actual), Protocol.Json);
            if (!BackendRules.MaintenanceMatches(actual, expected, before, Snapshot(read)) ||
                MaintenancePreservationFingerprint(read, request, before, expected) != preservation)
                return new(OperationStates.Partial, after, new("iis.readbackMismatch", "维护后配置、状态或未提交字段的独立读回不一致；不自动回滚或重放。"));
            if (request.Type == OperationTypes.UpdateSite && request.Parameters.Deserialize<SiteUpdateParameters>(Protocol.Json)!.Bindings is not null &&
                !MaintenanceTlsMatches(before, expected, claims, HttpSysCertificateReader.Read()))
                return new(OperationStates.Partial, after, new("binding.httpSysReadbackMismatch", "站点已修改，但 HTTP.sys 新旧证书记录或未涉及记录未能通过独立读回；请只读核实。"));
            return new(OperationStates.Succeeded, after);
        }
        catch (OperationCanceledException) when (!mayHaveMutated) { throw; }
        catch (Exception exception)
        {
            if (!mayHaveMutated) return new(OperationStates.Failed, null, SafeError(exception));
            try
            {
                using var manager = new ServerManager();
                var site = Topology(manager).Sites.SingleOrDefault(item => BackendRules.Equal(item.Name, request.Target.SiteName));
                return new(site is null ? OperationStates.Unknown : OperationStates.Partial,
                    site is null ? null : JsonSerializer.SerializeToElement(BackendRules.SortMaintenanceSite(site), Protocol.Json), SafeError(exception));
            }
            catch { return new(OperationStates.Unknown, null, new("iis.resultUnknown", "维护可能已发生，但独立读回失败；禁止自动重放或回滚。")); }
        }
        finally { validation?.Dispose(); }
    }

    private static void ApplyMaintenance(ServerManager manager, OperationRequest request, SiteTopology expected)
    {
        var site = manager.Sites.Single(item => BackendRules.Equal(item.Name, request.Target.SiteName));
        if (request.Type == OperationTypes.UpdateSite)
        {
            var patch = request.Parameters.Deserialize<SiteUpdateParameters>(Protocol.Json)!;
            var root = site.Applications.Single(item => item.Path == "/");
            var desired = expected.Applications.Single(item => item.Path == "/");
            if (patch.PhysicalPath is not null) root.VirtualDirectories.Single(item => item.Path == "/").PhysicalPath = desired.PhysicalPath;
            if (patch.ApplicationPoolName is not null) root.ApplicationPoolName = desired.ApplicationPoolName;
            if (patch.Bindings is not null)
            {
                foreach (var current in site.Bindings.ToArray())
                {
                    var publicBinding = ToBinding(current);
                    if (!expected.Bindings.Any(binding => BindingEndpointKey(binding) == BindingEndpointKey(publicBinding))) site.Bindings.Remove(current);
                }
                foreach (var binding in expected.Bindings)
                {
                    var current = site.Bindings.FirstOrDefault(item => BindingEndpointKey(ToBinding(item)) == BindingEndpointKey(binding));
                    if (current is null)
                    {
                        if (binding.Protocol == "https") site.Bindings.Add(BindingInformation(binding), Convert.FromHexString(binding.CertificateThumbprint!), "My", binding.UseSni ? SslFlags.Sni : SslFlags.None);
                        else site.Bindings.Add(BindingInformation(binding), "http");
                    }
                    else if (binding.Protocol == "https" && !BackendRules.Equal(Convert.ToHexString(current.CertificateHash ?? []), binding.CertificateThumbprint))
                        current.CertificateHash = Convert.FromHexString(binding.CertificateThumbprint!);
                }
            }
            return;
        }
        var proposed = expected.Applications.Single(item => BackendRules.Equal(item.Path, request.Target.ApplicationPath));
        if (request.Type == OperationTypes.CreateApplication)
        {
            var application = site.Applications.Add(proposed.Path, proposed.PhysicalPath);
            application.ApplicationPoolName = proposed.ApplicationPoolName;
            application.EnabledProtocols = "http";
            var root = application.VirtualDirectories.Single(item => item.Path == "/");
            root.UserName = ""; root.Password = "";
        }
        else if (request.Type == OperationTypes.UpdateApplication)
        {
            var patch = request.Parameters.Deserialize<ApplicationParameters>(Protocol.Json)!;
            var application = site.Applications.Single(item => BackendRules.Equal(item.Path, proposed.Path));
            if (patch.ApplicationPoolName is not null) application.ApplicationPoolName = proposed.ApplicationPoolName;
            if (patch.PhysicalPath is not null) application.VirtualDirectories.Single(item => item.Path == "/").PhysicalPath = proposed.PhysicalPath;
        }
        else
        {
            var application = site.Applications.Single(item => BackendRules.Equal(item.Path, proposed.Path));
            var directory = proposed.VirtualDirectories.Single(item => BackendRules.Equal(item.Path, request.Target.VirtualDirectoryPath));
            if (request.Type == OperationTypes.CreateVirtualDirectory)
            {
                var added = application.VirtualDirectories.Add(directory.Path, directory.PhysicalPath);
                added.UserName = ""; added.Password = "";
            }
            else ApplyVirtualDirectoryUpdate(request.Parameters.Deserialize<VirtualDirectoryParameters>(Protocol.Json)!,
                proposed.ApplicationPoolName, directory.PhysicalPath,
                value => application.ApplicationPoolName = value,
                value => application.VirtualDirectories.Single(item => BackendRules.Equal(item.Path, directory.Path)).PhysicalPath = value);
        }
    }

    private static void ApplyVirtualDirectoryUpdate(VirtualDirectoryParameters patch, string pool, string physicalPath,
        Action<string> setOwnerPool, Action<string> setPhysicalPath)
    {
        if (patch.OwnerApplicationPoolName is not null) setOwnerPool(pool);
        if (patch.PhysicalPath is not null) setPhysicalPath(physicalPath);
    }

    private static BindingConfig ToBinding(Binding binding) => new()
    {
        Protocol = binding.Protocol, IpAddress = binding.IsIPPortHostBinding ? BackendRules.NormalizeIp(binding.EndPoint.Address.ToString()) : "*",
        Port = binding.IsIPPortHostBinding ? binding.EndPoint.Port : 0, HostName = binding.IsIPPortHostBinding ? binding.Host ?? "" : "",
        UseSni = binding.Protocol == "https" && (binding.SslFlags & SslFlags.Sni) != 0,
        CertificateThumbprint = binding.Protocol == "https" ? Convert.ToHexString(binding.CertificateHash ?? []) : null
    };
    private static string BindingEndpointKey(BindingConfig binding) => $"{binding.Protocol.ToLowerInvariant()}|{BackendRules.NormalizeIp(binding.IpAddress)}|{binding.Port}|{binding.HostName.ToLowerInvariant()}|{binding.UseSni}";
    private static string TlsKey(BindingConfig binding) => binding.UseSni ? $"sni|{binding.Port}|{binding.HostName.ToLowerInvariant()}" : $"ip|{binding.Port}|{BackendRules.NormalizeIp(binding.IpAddress)}";
    private static string ClaimKey(HttpSysCertificateRecord claim) => claim.Kind == "sni" ? $"sni|{claim.Port}|{claim.HostName.ToLowerInvariant()}" : $"{claim.Kind}|{claim.Port}|{BackendRules.NormalizeIp(claim.IpAddress)}";
    private static HashSet<string> ChangedTlsKeys(SiteTopology before, SiteTopology after) => before.Bindings.Where(binding => binding.Protocol == "https" &&
        !after.Bindings.Any(proposed => BackendRules.BindingKey(proposed) == BackendRules.BindingKey(binding))).Select(TlsKey)
        .Concat(after.Bindings.Where(binding => binding.Protocol == "https" && !before.Bindings.Any(old => BackendRules.BindingKey(old) == BackendRules.BindingKey(binding))).Select(TlsKey)).ToHashSet(StringComparer.Ordinal);

    private static void CheckMaintenanceTlsOwnership(SiteTopology before, SiteTopology after, SiteTopology[] sites, HttpSysCertificateRecord[] claims, List<ApiError> errors)
    {
        var keys = ChangedTlsKeys(before, after);
        foreach (var claim in claims.Where(item => keys.Contains(ClaimKey(item))))
        {
            var owners = sites.SelectMany(site => site.Bindings.Where(binding => binding.Protocol == "https" && TlsKey(binding) == ClaimKey(claim)).Select(binding => (site, binding))).ToArray();
            if (owners.Length != 1 || !BackendRules.Equal(owners[0].site.Name, before.Name) ||
                !BackendRules.Equal(owners[0].binding.CertificateThumbprint, claim.Thumbprint) || !BackendRules.Equal(claim.StoreName, "My"))
                errors.Add(new("binding.httpSysOwnershipUnverified", "待修改 TLS 记录并非本站独占且与现有证书一致，拒绝影响共享或外部记录。"));
            if (claim.CertificateCheckMode != 0 || claim.RevocationFreshness != 0 || claim.RevocationTimeout != 0 || claim.Flags != 0 ||
                !string.IsNullOrEmpty(claim.CtlIdentifier) || !string.IsNullOrEmpty(claim.CtlStore))
                errors.Add(new("binding.httpSysPolicyUnsupported", "TLS 记录包含自定义客户端证书或吊销策略，本工具不覆盖该策略。"));
        }
        foreach (var binding in before.Bindings.Where(item => item.Protocol == "https" && keys.Contains(TlsKey(item))))
        {
            if (!claims.Any(claim => ClaimIsExact(claim, binding) && BackendRules.Equal(claim.Thumbprint, binding.CertificateThumbprint) && BackendRules.Equal(claim.StoreName, "My")))
                errors.Add(new("binding.httpSysOwnershipUnverified", "原 HTTPS 绑定未对应唯一且匹配的 HTTP.sys 记录，拒绝修改。"));
            if (sites.Where(site => !BackendRules.Equal(site.Name, before.Name)).SelectMany(site => site.Bindings)
                .Any(other => other.Protocol == "https" && TlsKey(other) == TlsKey(binding)))
                errors.Add(new("binding.httpSysSharedRecord", "原 HTTPS 记录被其它站点共享，不能在此修改或移除。"));
        }
    }
    private static bool MaintenanceTlsMatches(SiteTopology before, SiteTopology expected, HttpSysCertificateRecord[] prior, HttpSysCertificateRecord[] current)
    {
        var changed = ChangedTlsKeys(before, expected);
        if (CanonicalJson.Hash(prior.Where(claim => !changed.Contains(ClaimKey(claim))).ToArray()) !=
            CanonicalJson.Hash(current.Where(claim => !changed.Contains(ClaimKey(claim))).ToArray())) return false;
        foreach (var binding in expected.Bindings.Where(binding => binding.Protocol == "https"))
        {
            var matches = current.Where(claim => ClaimIsExact(claim, binding) && BackendRules.Equal(claim.Thumbprint, binding.CertificateThumbprint) && BackendRules.Equal(claim.StoreName, "My")).ToArray();
            if (matches.Length != 1) return false;
            var old = prior.SingleOrDefault(claim => ClaimKey(claim) == TlsKey(binding));
            if (old is not null && (old with { Thumbprint = matches[0].Thumbprint }) != matches[0]) return false;
        }
        return before.Bindings.Where(binding => binding.Protocol == "https" && changed.Contains(TlsKey(binding)) &&
            !expected.Bindings.Any(item => item.Protocol == "https" && TlsKey(item) == TlsKey(binding)))
            .All(binding => !current.Any(claim => ClaimKey(claim) == TlsKey(binding)));
    }

    private static string MaintenancePreservationFingerprint(ServerManager manager, OperationRequest request, SiteTopology before, SiteTopology expected)
    {
        var site = manager.Sites.Single(item => BackendRules.Equal(item.Name, before.Name));
        var parameters = request.Parameters;
        bool Submitted(string name) => parameters.EnumerateObject().Any(item => BackendRules.Equal(item.Name, name));
        var applicationPath = request.Type == OperationTypes.UpdateSite ? "/" : request.Target.ApplicationPath;
        var directoryPath = request.Type is OperationTypes.CreateVirtualDirectory or OperationTypes.UpdateVirtualDirectory ? request.Target.VirtualDirectoryPath : "/";
        string Attributes(ConfigurationElement element, params string[] excluded) => ConfigurationAttributesHash(
            element.Attributes.Where(attribute => !excluded.Contains(attribute.Name, StringComparer.OrdinalIgnoreCase)).Select(attribute => (attribute.Name, (object?)attribute.Value)), element is Site or ApplicationPool);
        return BackendRules.Hash(new
        {
            otherSites = manager.Sites.Where(item => !BackendRules.Equal(item.Name, before.Name)).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new { state = item.State.ToString(), configuration = SiteConfigurationEvidence(item) }).ToArray(),
            pools = manager.ApplicationPools.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Select(pool => new
            { pool.Name, state = pool.State.ToString(), config = AttributesHash(pool), process = AttributesHash(pool.ProcessModel), recycling = AttributesHash(pool.Recycling), periodic = AttributesHash(pool.Recycling.PeriodicRestart) }).ToArray(),
            site = AttributesHash(site),
            bindings = before.Bindings.Where(binding => expected.Bindings.Any(item => BackendRules.BindingKey(item) == BackendRules.BindingKey(binding))).OrderBy(BackendRules.BindingKey, StringComparer.Ordinal)
                .Select(binding => AttributesHash(site.Bindings.Single(item => BackendRules.BindingKey(ToBinding(item)) == BackendRules.BindingKey(binding)))).ToArray(),
            applications = before.Applications.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(original =>
            {
                var app = site.Applications.Single(item => BackendRules.Equal(item.Path, original.Path));
                var poolChanged = BackendRules.Equal(original.Path, applicationPath) &&
                    (request.Type is OperationTypes.UpdateSite or OperationTypes.UpdateApplication && Submitted("applicationPoolName") ||
                     request.Type == OperationTypes.UpdateVirtualDirectory && Submitted("ownerApplicationPoolName"));
                return new { original.Path, attributes = poolChanged ? Attributes(app, "applicationPool") : AttributesHash(app),
                    directories = original.VirtualDirectories.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(directory =>
                    {
                        var current = app.VirtualDirectories.Single(item => BackendRules.Equal(item.Path, directory.Path));
                        var pathChanged = BackendRules.Equal(original.Path, applicationPath) && BackendRules.Equal(directory.Path, directoryPath) && Submitted("physicalPath");
                        return new { directory.Path, attributes = pathChanged ? Attributes(current, "physicalPath") : AttributesHash(current) };
                    }).ToArray() };
            }).ToArray()
        });
    }

    private static IisSnapshot Snapshot(ServerManager manager)
    {
        var sites = manager.Sites.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(site =>
        {
            var root = site.Applications.FirstOrDefault(x => x.Path == "/");
            return new SiteConfig
            {
                Name = site.Name, Id = site.Id, State = site.State.ToString(), ServerAutoStart = site.ServerAutoStart,
                PhysicalPath = root?.VirtualDirectories.FirstOrDefault(x => x.Path == "/")?.PhysicalPath ?? "",
                ApplicationPoolName = root?.ApplicationPoolName ?? "",
                Bindings = site.Bindings.Select(binding => new BindingConfig
                {
                    Protocol = binding.Protocol,
                    IpAddress = binding.IsIPPortHostBinding ? BackendRules.NormalizeIp(binding.EndPoint.Address.ToString()) : "*",
                    Port = binding.IsIPPortHostBinding ? binding.EndPoint.Port : 0, HostName = binding.IsIPPortHostBinding ? binding.Host ?? "" : "",
                    UseSni = binding.Protocol == "https" && (binding.SslFlags & SslFlags.Sni) != 0,
                    CertificateThumbprint = binding.Protocol == "https" ? Convert.ToHexString(binding.CertificateHash ?? []) : null
                }).OrderBy(BackendRules.BindingKey, StringComparer.Ordinal).ToArray()
            };
        }).ToArray();
        var pools = manager.ApplicationPools.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(pool =>
        {
            var value = new PoolConfig
            {
                Name = pool.Name, State = pool.State.ToString(), ManagedRuntimeVersion = pool.ManagedRuntimeVersion,
                ManagedPipelineMode = pool.ManagedPipelineMode.ToString(), Enable32BitAppOnWin64 = pool.Enable32BitAppOnWin64,
                AutoStart = pool.AutoStart, StartMode = pool.StartMode.ToString(),
                IdleTimeoutMinutes = checked((int)pool.ProcessModel.IdleTimeout.TotalMinutes),
                IdleTimeoutAction = ReadIdleAction(pool), IdentityType = pool.ProcessModel.IdentityType.ToString(),
                Applications = manager.Sites.SelectMany(site => site.Applications.Where(a => BackendRules.Equal(a.ApplicationPoolName, pool.Name)).Select(a => site.Name + a.Path)).Order(StringComparer.Ordinal).ToArray()
            };
            return value with { Fingerprint = BackendRules.Hash(new { value, pool = AttributesHash(pool), process = AttributesHash(pool.ProcessModel) }) };
        }).ToArray();
        return new(DateTimeOffset.UtcNow, pools, sites);
    }

    private static object ConfigurationEvidence(ServerManager manager) => new
    {
        pools = manager.ApplicationPools.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(x => new
        {
            x.Name, config = AttributesHash(x), process = AttributesHash(x.ProcessModel), recycling = AttributesHash(x.Recycling),
            restart = AttributesHash(x.Recycling.PeriodicRestart), schedule = x.Recycling.PeriodicRestart.Schedule.Select(t => t.Time).Order().ToArray()
        }).ToArray(),
        sites = manager.Sites.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(SiteConfigurationEvidence).ToArray()
    };

    private static object SiteConfigurationEvidence(Site site) => new
    {
        site.Name, config = AttributesHash(site), bindings = site.Bindings.OrderBy(b => b.BindingInformation, StringComparer.Ordinal).Select(AttributesHash).ToArray(),
        applications = site.Applications.OrderBy(a => a.Path, StringComparer.Ordinal).Select(a => new
        {
            a.Path, a.ApplicationPoolName, config = AttributesHash(a),
            directories = a.VirtualDirectories.OrderBy(v => v.Path, StringComparer.Ordinal).Select(v => new { v.Path, v.PhysicalPath, config = AttributesHash(v) }).ToArray()
        }).ToArray()
    };
    private static SiteRestartEvidence SiteEvidence(Site site) => new(site.Id, site.State.ToString(), BackendRules.Hash(SiteConfigurationEvidence(site)));

    private static string AttributesHash(ConfigurationElement element) => ConfigurationAttributesHash(
        element.Attributes.Select(attribute => (attribute.Name, (object?)attribute.Value)), element is Site or ApplicationPool);

    private static string ConfigurationAttributesHash(IEnumerable<(string Name, object? Value)> attributes, bool includesRuntimeState) => BackendRules.Hash(attributes
        // RSCA exposes state through Attributes as well as State. It changes on start/stop and is
        // verified separately; dynamic SSL binding properties remain configuration evidence.
        .Where(attribute => !includesRuntimeState || !BackendRules.Equal(attribute.Name, "state"))
        .OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => new
        {
            x.Name,
            // Credential values remain in memory only, and only their SHA256 enters the fingerprint.
            valueDigest = BackendRules.Hash(new { value = Convert.ToString(x.Value, CultureInfo.InvariantCulture) })
        }).ToArray());

    private static bool IsShared(ServerManager manager) => Convert.ToBoolean(manager.GetRedirectionConfiguration()
        .GetSection("configurationRedirection").GetAttributeValue("enabled"), CultureInfo.InvariantCulture);
    private static string IdentityName(ApplicationPool pool) => pool.ProcessModel.IdentityType switch
    {
        ProcessModelIdentityType.ApplicationPoolIdentity => @"IIS APPPOOL\" + pool.Name,
        ProcessModelIdentityType.LocalSystem => @"NT AUTHORITY\SYSTEM",
        ProcessModelIdentityType.LocalService => @"NT AUTHORITY\LOCAL SERVICE",
        ProcessModelIdentityType.NetworkService => @"NT AUTHORITY\NETWORK SERVICE",
        ProcessModelIdentityType.SpecificUser when !string.IsNullOrWhiteSpace(pool.ProcessModel.UserName) => pool.ProcessModel.UserName,
        _ => throw new OperationException("site.identityUnverified", "实际应用池身份未能核实。")
    };
    private static string ReadIdleAction(ApplicationPool pool)
    {
        var value = Convert.ToString(pool.ProcessModel.GetAttributeValue("idleTimeoutAction"), CultureInfo.InvariantCulture);
        return value switch { "1" or "Suspend" => "Suspend", "0" or "Terminate" or null or "" => "Terminate", _ => "Unknown" };
    }
    private static void ApplyPool(ApplicationPool pool, PoolParameters p)
    {
        if (p.ManagedRuntimeVersion is not null) pool.ManagedRuntimeVersion = p.ManagedRuntimeVersion;
        if (p.ManagedPipelineMode is not null) pool.ManagedPipelineMode = Enum.Parse<ManagedPipelineMode>(p.ManagedPipelineMode);
        if (p.Enable32BitAppOnWin64 is not null) pool.Enable32BitAppOnWin64 = p.Enable32BitAppOnWin64.Value;
        if (p.AutoStart is not null) pool.AutoStart = p.AutoStart.Value;
        if (p.StartMode is not null) pool.StartMode = Enum.Parse<StartMode>(p.StartMode);
        if (p.IdleTimeoutMinutes is not null) pool.ProcessModel.IdleTimeout = TimeSpan.FromMinutes(p.IdleTimeoutMinutes.Value);
        if (p.IdleTimeoutAction is not null) pool.ProcessModel.SetAttributeValue("idleTimeoutAction", p.IdleTimeoutAction == "Suspend" ? 1 : 0);
    }
    private static string BindingInformation(BindingConfig binding)
    {
        var ip = binding.IpAddress.Contains(':') ? "[" + binding.IpAddress + "]" : binding.IpAddress;
        return string.Create(CultureInfo.InvariantCulture, $"{ip}:{binding.Port}:{binding.HostName}");
    }
    private static X509Store OpenCertificates()
    {
        var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        try { store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); return store; }
        catch { store.Dispose(); throw; }
    }
    private static CertificateInfo ToCertificate(X509Certificate2 certificate) => new(certificate.Thumbprint, certificate.Subject,
        certificate.FriendlyName, new DateTimeOffset(certificate.NotBefore.ToUniversalTime()), new DateTimeOffset(certificate.NotAfter.ToUniversalTime()), certificate.HasPrivateKey);
    private static void CheckCertificate(X509Store store, BindingConfig binding, List<ApiError> errors)
    {
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, binding.CertificateThumbprint!, false);
        if (matches.Count != 1) { errors.Add(new("certificate.notFound", "LocalMachine/My 中未找到唯一匹配证书。")); return; }
        var certificate = matches[0];
        if (!certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            errors.Add(new("certificate.invalid", "证书须在有效期内且具有现有私钥。"));
        if (!certificate.MatchesHostname(binding.HostName, allowWildcards: true, allowCommonName: true))
            errors.Add(new("certificate.hostMismatch", "证书 SAN/CN 与绑定主机名不匹配。"));
        foreach (var eku in certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>())
            if (!eku.EnhancedKeyUsages.Cast<Oid>().Any(x => x.Value is "1.3.6.1.5.5.7.3.1" or "2.5.29.37.0"))
                errors.Add(new("certificate.usageInvalid", "证书 EKU 不允许服务器身份验证。"));
    }
    private static void CheckHttpSysClaims(BindingConfig binding, HttpSysCertificateRecord[] claims, IisSnapshot snapshot, List<ApiError> errors)
    {
        foreach (var claim in claims.Where(x => x.Port == binding.Port))
        {
            if (claim.Kind == "ccs") { errors.Add(new("binding.ccsConflict", "HTTP.sys 此端口已启用集中证书存储，首版不覆盖该记录。")); continue; }
            if (!BackendRules.AddressesOverlap(binding.IpAddress, claim.IpAddress)) continue;
            if (binding.Protocol == "http") { errors.Add(new("binding.httpSysProtocolConflict", "此地址/端口已有 HTTP.sys TLS 记录，不能创建 HTTP 绑定。")); continue; }
            if (claim.Kind == "sni" && binding.UseSni && !BackendRules.Equal(claim.HostName, binding.HostName)) continue;
            if (!ClaimIsExact(claim, binding) || !BackendRules.Equal(claim.Thumbprint, binding.CertificateThumbprint) || !BackendRules.Equal(claim.StoreName, "My"))
                errors.Add(new("binding.httpSysCertificateConflict", "HTTP.sys 现有证书/SNI 记录与请求冲突，拒绝覆盖。"));
            else if (!snapshot.Sites.SelectMany(x => x.Bindings).Any(existing =>
                         ClaimIsExact(claim, existing) && BackendRules.Equal(existing.CertificateThumbprint, claim.Thumbprint)))
                errors.Add(new("binding.httpSysExternalRecord", "匹配的 HTTP.sys TLS 记录不属于已读回 IIS 站点，首版拒绝复用或覆盖外部记录。"));
        }
    }
    private static bool ClaimIsExact(HttpSysCertificateRecord claim, BindingConfig binding) => claim.Port == binding.Port &&
        (binding.UseSni ? claim.Kind == "sni" && BackendRules.Equal(claim.HostName, binding.HostName)
            : claim.Kind == "ip" && BackendRules.Equal(BackendRules.NormalizeIp(claim.IpAddress), BackendRules.NormalizeIp(binding.IpAddress)));
    private static object? FindAfter(IisSnapshot snapshot, OperationRequest request) => OperationTypes.IsSite(request.Type)
        ? snapshot.Sites.FirstOrDefault(x => BackendRules.Equal(x.Name, request.Target.SiteName))
        : snapshot.ApplicationPools.FirstOrDefault(x => BackendRules.Equal(x.Name, request.Target.ApplicationPoolName));
    private static ApiError? ReadbackError(OperationRequest request, object after, JsonElement? proposed, bool siteConfigurationMatches)
    {
        if (!siteConfigurationMatches)
            return new("site.configurationChanged", "IIS 已接受动作，但站点的应用、目录或绑定配置指纹与执行前不同。即使站点已达到目标状态，仍需只读核实配置变化；不自动回滚或重放。");
        return MatchesExpected(request, after, proposed) ? null
            : new("iis.readbackMismatch", "IIS 已接受动作，但独立读回的目标状态或公开配置字段与预检查方案不一致；请只读核实，不自动回滚或重放。");
    }
    private static bool MatchesExpected(OperationRequest request, object after, JsonElement? proposed)
    {
        if (after is PoolConfig pool)
        {
            if (request.Type == OperationTypes.Start) return pool.State == "Started";
            if (request.Type == OperationTypes.Recycle) return pool.State == "Started";
            if (request.Type == OperationTypes.Stop) return pool.State == "Stopped";
            var expected = proposed?.Deserialize<PoolConfig>(Protocol.Json);
            return expected is not null && pool.Name == expected.Name && pool.State == expected.State &&
                pool.IdentityType == expected.IdentityType && pool.Applications.Order(StringComparer.Ordinal).SequenceEqual(expected.Applications.Order(StringComparer.Ordinal)) &&
                pool.ManagedRuntimeVersion == expected.ManagedRuntimeVersion &&
                pool.ManagedPipelineMode == expected.ManagedPipelineMode && pool.Enable32BitAppOnWin64 == expected.Enable32BitAppOnWin64 &&
                pool.AutoStart == expected.AutoStart && pool.StartMode == expected.StartMode &&
                pool.IdleTimeoutMinutes == expected.IdleTimeoutMinutes && pool.IdleTimeoutAction == expected.IdleTimeoutAction &&
                (request.Type != OperationTypes.CreatePool || pool.IdentityType == "ApplicationPoolIdentity" &&
                    expected.State == (expected.AutoStart ? "Started" : "Stopped"));
        }
        var site = (SiteConfig)after;
        if (OperationTypes.IsSiteLifecycle(request.Type))
        {
            var expected = proposed?.Deserialize<SiteConfig>(Protocol.Json);
            return expected is not null && CanonicalJson.Hash(site) == CanonicalJson.Hash(expected);
        }
        var parameters = request.Parameters.Deserialize<SiteParameters>(Protocol.Json)!;
        return site.State == "Stopped" && !site.ServerAutoStart && BackendRules.Equal(site.ApplicationPoolName, parameters.ApplicationPoolName) &&
            BackendRules.Equal(Path.GetFullPath(site.PhysicalPath), Path.GetFullPath(parameters.PhysicalPath)) &&
            site.Bindings.Select(BackendRules.BindingKey).Order(StringComparer.Ordinal).SequenceEqual(parameters.Bindings.Select(BackendRules.Normalize).Select(BackendRules.BindingKey).Order(StringComparer.Ordinal));
    }
    private static ApiError SafeError(Exception exception, string? stage = null) => exception switch
    {
        OperationException e => new(e.Code, e.Message),
        UnauthorizedAccessException or SecurityException => new("iis.accessDenied", "Agent 权限不足，拒绝操作。"),
        CryptographicException => new("certificate.unavailable", "证书元数据或私钥状态未能核实。"),
        DllNotFoundException or FileNotFoundException => new("iis.unavailable", "IIS 管理组件或所需文件不可用。"),
        COMException => new("iis.nativeFailure", $"IIS 管理 API 返回错误（HRESULT=0x{exception.HResult:X8}{(stage is null ? "" : "，阶段=" + stage)}），请用只读诊断核实现场状态，禁止重放。"),
        IOException => new("iis.ioFailure", "IIS 配置或目录读取/写入失败，请核实现场状态。"),
        ArgumentException or InvalidOperationException => new("iis.configurationInvalid", "IIS 状态、架构或配置不支持此操作。"),
        _ => new("iis.failure", "IIS 操作未完成，请用只读诊断核实现场状态。")
    };
    private static Task<T> Read<T>(Func<T> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { return Task.FromResult(read()); }
        catch (OperationException) { throw; }
        catch (Exception exception) { var error = SafeError(exception); throw new OperationException(error.Code, error.Message, 503); }
    }
    private sealed class ValidationLease(ValidationResult result, WindowsDirectoryLease? directory, HttpSysCertificateRecord[]? tlsClaims = null,
        IReadOnlyList<WindowsDirectoryLease>? ownerDirectories = null) : IDisposable
    {
        internal ValidationResult Result { get; } = result;
        internal HttpSysCertificateRecord[]? TlsClaims { get; } = tlsClaims;
        public void Dispose()
        {
            directory?.Dispose();
            if (ownerDirectories is not null) foreach (var mapping in ownerDirectories) mapping.Dispose();
        }
    }
}
