using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IISManager.Contracts;
using IISManager.Core;

namespace IISManager.Agent.Backends;

// Shared, deterministic rules. The Windows adapter adds OS evidence before allowing a write.
internal static class BackendRules
{
    private static readonly HashSet<string> PoolFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "managedRuntimeVersion", "managedPipelineMode", "enable32BitAppOnWin64", "autoStart",
        "startMode", "idleTimeoutMinutes", "idleTimeoutAction"
    };
    private static readonly HashSet<string> SiteFields = new(StringComparer.OrdinalIgnoreCase)
        { "physicalPath", "applicationPoolName", "bindings" };
    private static readonly HashSet<string> BindingFields = new(StringComparer.OrdinalIgnoreCase)
        { "protocol", "ipAddress", "port", "hostName", "certificateThumbprint", "useSni" };

    internal static ValidationResult Validate(OperationRequest request, IisSnapshot snapshot, AgentOptions options,
        bool sharedConfiguration, object? evidence = null, Action<SiteParameters, PoolConfig, List<ApiError>>? checkSite = null,
        string[]? siteApplications = null)
    {
        var errors = new List<ApiError>();
        var warnings = new List<string>();
        var changes = new List<FieldChange>();
        PoolConfig? pool = null;
        SiteConfig? site = null;
        string[] affectedApplications = [];
        object? proposed = null;
        object? before = null;
        if (!OperationTypes.Iis.Contains(request.Type, StringComparer.Ordinal))
            errors.Add(new("operation.unsupported", "不支持此操作类型。"));
        var creatingSite = request.Type == OperationTypes.CreateSite;
        var creatingPool = request.Type == OperationTypes.CreatePool;
        var targetingSite = OperationTypes.IsSite(request.Type);
        var name = targetingSite ? request.Target?.SiteName : request.Target?.ApplicationPoolName;
        if (!ValidName(name)) errors.Add(new("target.invalidName", "名称须为 1–64 个字符，不能含路径或控制字符、首尾空白或结尾句点。"));
        if (request.Target?.ServiceName is not null || request.Target?.ApplicationPath is not null || request.Target?.VirtualDirectoryPath is not null ||
            (targetingSite ? request.Target?.ApplicationPoolName is not null : request.Target?.SiteName is not null))
            errors.Add(new("target.invalid", "每项操作只能提供对应的一个目标名称。"));
        if (!targetingSite)
        {
            pool = snapshot.ApplicationPools.FirstOrDefault(x => Equal(x.Name, name));
            before = pool;
            if (creatingPool && pool is not null) errors.Add(new("pool.exists", "同名应用池已存在。"));
            if (!creatingPool && pool is null) errors.Add(new("pool.notFound", "应用池不存在。"));
            affectedApplications = pool?.Applications ?? [];
        }
        else if (!creatingSite)
        {
            site = snapshot.Sites.FirstOrDefault(x => Equal(x.Name, name));
            before = site;
            if (site is null) errors.Add(new("site.notFound", "站点不存在。"));
            else affectedApplications = (siteApplications ?? snapshot.ApplicationPools.SelectMany(x => x.Applications)
                .Where(application => application.StartsWith(site.Name + "/", StringComparison.OrdinalIgnoreCase)).ToArray())
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        }
        var configurationWrite = request.Type is OperationTypes.CreatePool or OperationTypes.UpdatePool or OperationTypes.CreateSite;
        if (configurationWrite && sharedConfiguration)
            errors.Add(new("configuration.shared", "共享 IIS 配置不支持配置写入，请在配置所有者处处理。"));
        if (request.Parameters.ValueKind != JsonValueKind.Object)
            errors.Add(new("parameters.invalid", "parameters 必须是 JSON 对象。"));
        else if (request.Type is OperationTypes.CreatePool or OperationTypes.UpdatePool)
        {
            CheckFields(request.Parameters, PoolFields, errors);
            var p = Deserialize<PoolParameters>(request.Parameters, errors);
            if (p is not null)
            {
                CheckPoolParameters(p, errors);
                if (!creatingPool && !request.Parameters.EnumerateObject().Any())
                    errors.Add(new("parameters.empty", "局部修改至少须包含一个支持字段。"));
                if (!creatingPool && pool is not null && pool.State != "Stopped" && RuntimeFieldsPresent(p))
                    errors.Add(new("pool.mustBeStopped", "修改运行相关字段前，应用池必须已停止。"));
                var basePool = creatingPool ? new PoolConfig { Name = name ?? "", State = "Stopped", AutoStart = false } : pool;
                if (basePool is not null)
                {
                    var configured = Apply(basePool, p);
                    if (creatingPool) configured = configured with { State = configured.AutoStart ? "Started" : "Stopped" };
                    proposed = configured;
                    AddPoolChanges(creatingPool ? null : basePool, (PoolConfig)proposed, changes);
                    if (creatingPool)
                    {
                        changes.Add(new("state", "不存在", configured.State));
                        warnings.Add(configured.AutoStart
                            ? "新应用池按 AutoStart=true 自动启动，须读回 Started；创建后不强制停止。身份固定为 ApplicationPoolIdentity。"
                            : "新应用池按 AutoStart=false 保持停止，须读回 Stopped；后续可单独启动。身份固定为 ApplicationPoolIdentity。");
                    }
                }
            }
        }
        else if (creatingSite)
        {
            before = snapshot.Sites.FirstOrDefault(x => Equal(x.Name, name));
            if (before is not null) errors.Add(new("site.exists", "同名站点已存在。"));
            CheckFields(request.Parameters, SiteFields, errors);
            var p = Deserialize<SiteParameters>(request.Parameters, errors);
            if (p is not null)
            {
                var associated = snapshot.ApplicationPools.FirstOrDefault(x => Equal(x.Name, p.ApplicationPoolName));
                if (!ValidName(p.ApplicationPoolName) || associated is null)
                    errors.Add(new("site.poolNotFound", "站点必须关联一个已存在的有效应用池。"));
                var rawBindings = request.Parameters.EnumerateObject().FirstOrDefault(x => Equal(x.Name, "bindings")).Value;
                if (rawBindings.ValueKind == JsonValueKind.Array)
                    foreach (var raw in rawBindings.EnumerateArray())
                        if (raw.ValueKind == JsonValueKind.Object) CheckFields(raw, BindingFields, errors);
                CheckBindings(p.Bindings, snapshot.Sites, options, errors);
                if (string.IsNullOrWhiteSpace(p.PhysicalPath)) errors.Add(new("site.pathRequired", "必须指定已存在的物理目录。"));
                if (associated is not null && errors.Count == 0) checkSite?.Invoke(p, associated, errors);
                proposed = new SiteConfig
                {
                    Name = name ?? "", Id = snapshot.Sites.Length == 0 ? 1 : snapshot.Sites.Max(x => x.Id) + 1,
                    State = "Stopped", ServerAutoStart = false, PhysicalPath = p.PhysicalPath,
                    ApplicationPoolName = p.ApplicationPoolName, Bindings = (p.Bindings ?? []).Where(b => b is not null && b.Protocol is not null && b.IpAddress is not null && b.HostName is not null).Select(Normalize).ToArray()
                };
                changes.Add(new("site", "不存在", name ?? ""));
                warnings.Add("新站点默认停止且 ServerAutoStart=false；本接口不创建目录、不修改 ACL、不安装证书。");
            }
        }
        else if (OperationTypes.IsSiteLifecycle(request.Type))
        {
            if (request.Parameters.EnumerateObject().Any()) errors.Add(new("parameters.unsupported", "站点启停和重新启动不接受额外参数。"));
            if (site is not null)
            {
                if (site.State is not ("Started" or "Stopped")) errors.Add(new("site.transitioning", "站点状态未稳定，请先读回。"));
                if (request.Type == OperationTypes.SiteRestart && site.State != "Started") errors.Add(new("site.notRunning", "只能重新启动正在运行的站点；已停止的站点请单独启动。"));
                var state = request.Type == OperationTypes.SiteStop ? "Stopped" : "Started";
                proposed = site with { State = state };
                changes.Add(new("state", site.State, state));
                if (request.Type is OperationTypes.SiteStop or OperationTypes.SiteRestart)
                    warnings.Add("停止或重新启动会中断此站点及其全部子应用和虚拟目录的访问。应用池配置和其他站点保持原样。");
                if (request.Type == OperationTypes.SiteRestart)
                    warnings.Add("重新启动先停止、独立确认已停止和配置未变化，再启动；启动阶段失败时可能保持停止，请按原编号核对。此动作不回收应用池。");
                if (request.Type is OperationTypes.SiteStart or OperationTypes.SiteRestart)
                    warnings.Add("本动作仅启动站点，不显式启动或修改应用池；IIS 状态读回不代表应用业务健康。");
            }
        }
        else if (request.Parameters.ValueKind == JsonValueKind.Object)
        {
            if (request.Parameters.EnumerateObject().Any()) errors.Add(new("parameters.unsupported", "启停和回收操作不接受额外参数。"));
            if (pool is not null)
            {
                if (pool.State is not ("Started" or "Stopped")) errors.Add(new("pool.transitioning", "应用池状态未稳定，请先读回。"));
                if (request.Type == OperationTypes.Recycle && pool.State != "Started") errors.Add(new("pool.notRunning", "只能回收已启动的应用池。"));
                var state = request.Type == OperationTypes.Start ? "Started" : request.Type == OperationTypes.Stop ? "Stopped" : pool.State;
                proposed = pool with { State = state, Fingerprint = "" };
                changes.Add(new("state", pool.State, state));
                if (request.Type == OperationTypes.Recycle) warnings.Add("回收完成状态表示 IIS 接受动作且池仍为 Started；业务健康需另行验证。");
            }
        }
        if (pool?.Applications.Length > 0) warnings.Add($"此操作关联 {pool.Applications.Length} 个应用，需在确认前核对影响范围。");
        if (site is not null && affectedApplications.Length > 0) warnings.Add($"此站点关联 {affectedApplications.Length} 个应用，需在确认前核对影响范围。");
        var fingerprint = Hash(new
        {
            target = name, request.Type, sharedConfiguration,
            pools = snapshot.ApplicationPools.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(x => x with { Fingerprint = "", Applications = x.Applications.Order(StringComparer.Ordinal).ToArray() }).ToArray(),
            sites = snapshot.Sites.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(x => x with { Bindings = x.Bindings.OrderBy(BindingKey, StringComparer.Ordinal).ToArray() }).ToArray(),
            evidence
        });
        if (request.ExpectedFingerprint is not null && !EqualFingerprint(request.ExpectedFingerprint, fingerprint))
            errors.Add(new("configuration.conflict", "配置或关联资源已变化，请重新预检并确认。"));
        return new(errors.Count == 0, fingerprint, affectedApplications, changes.ToArray(), warnings.ToArray(), errors.ToArray(),
            before is null ? null : JsonSerializer.SerializeToElement(before, Protocol.Json),
            proposed is null ? null : JsonSerializer.SerializeToElement(proposed, Protocol.Json));
    }

    internal static PoolConfig Apply(PoolConfig pool, PoolParameters p) => pool with
    {
        ManagedRuntimeVersion = p.ManagedRuntimeVersion ?? pool.ManagedRuntimeVersion,
        ManagedPipelineMode = p.ManagedPipelineMode ?? pool.ManagedPipelineMode,
        Enable32BitAppOnWin64 = p.Enable32BitAppOnWin64 ?? pool.Enable32BitAppOnWin64,
        AutoStart = p.AutoStart ?? pool.AutoStart, StartMode = p.StartMode ?? pool.StartMode,
        IdleTimeoutMinutes = p.IdleTimeoutMinutes ?? pool.IdleTimeoutMinutes,
        IdleTimeoutAction = p.IdleTimeoutAction ?? pool.IdleTimeoutAction, Fingerprint = ""
    };

    internal static bool Equal(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    internal static ValidationResult ValidateMaintenance(OperationRequest request, IisSnapshot snapshot, SiteTopologySnapshot topology,
        AgentOptions options, bool sharedConfiguration, object? evidence = null)
    {
        var errors = new List<ApiError>();
        var changes = new List<FieldChange>();
        var warnings = new List<string> { "站点和全部原关联应用池及新关联池必须已停止；本动作不启动、停止或回收池，不复制文件、不修改 ACL、不安装证书。" };
        var target = request.Target;
        var directoryAction = request.Type is OperationTypes.CreateVirtualDirectory or OperationTypes.UpdateVirtualDirectory;
        var applicationAction = request.Type is OperationTypes.CreateApplication or OperationTypes.UpdateApplication;
        if (!OperationTypes.IsSiteMaintenance(request.Type) || target is null || !ValidName(target.SiteName) ||
            target.ApplicationPoolName is not null || target.ServiceName is not null ||
            (request.Type == OperationTypes.UpdateSite && (target.ApplicationPath is not null || target.VirtualDirectoryPath is not null)) ||
            ((applicationAction || directoryAction) && !CanonicalIisPath(target.ApplicationPath, allowRoot: directoryAction)) ||
            (applicationAction && target.VirtualDirectoryPath is not null) ||
            (directoryAction && !CanonicalIisPath(target.VirtualDirectoryPath, allowRoot: false)))
            errors.Add(new("target.invalid", "请提供有效站点及规范应用/虚拟目录路径；每项操作只能指定对应目标。根应用使用站点编辑，虚拟目录路径相对所属应用且不能为 /。"));
        if (sharedConfiguration) errors.Add(new("configuration.shared", "共享 IIS 配置不支持配置写入，请在配置所有者处处理。"));
        var site = topology.Sites.FirstOrDefault(item => Equal(item.Name, target?.SiteName));
        if (site is null) errors.Add(new("site.notFound", "站点不存在。"));
        else if (site.State != "Stopped") errors.Add(new("site.mustBeStopped", "配置维护前站点必须已停止。"));
        var root = site?.Applications.FirstOrDefault(item => item.Path == "/");
        if (site is not null && (root is null || root.VirtualDirectories.Count(item => item.Path == "/") != 1))
            errors.Add(new("site.rootInvalid", "站点根应用或根虚拟目录无法唯一核实。"));
        var allowed = request.Type == OperationTypes.UpdateSite ? SiteFields : applicationAction
            ? new HashSet<string>(["physicalPath", "applicationPoolName"], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(request.Type == OperationTypes.UpdateVirtualDirectory ? ["physicalPath", "ownerApplicationPoolName"] : ["physicalPath"], StringComparer.OrdinalIgnoreCase);
        SiteTopology? proposed = null;
        if (request.Parameters.ValueKind != JsonValueKind.Object) errors.Add(new("parameters.invalid", "parameters 必须是 JSON 对象。"));
        else
        {
            CheckFields(request.Parameters, allowed, errors);
            if (!request.Parameters.EnumerateObject().Any()) errors.Add(new("parameters.empty", "至少提交一个需修改的字段。"));
            if (site is not null && root is not null && errors.Count == 0)
            {
                proposed = site;
                if (request.Type == OperationTypes.UpdateSite)
                {
                    var patch = Deserialize<SiteUpdateParameters>(request.Parameters, errors);
                    if (patch is not null)
                    {
                        var checkedPath = MaintenancePhysicalPath(patch.PhysicalPath ?? root.PhysicalPath, options, errors);
                        var path = patch.PhysicalPath is null ? root.PhysicalPath : checkedPath;
                        var pool = patch.ApplicationPoolName ?? root.ApplicationPoolName;
                        var newRoot = root with { PhysicalPath = path, ApplicationPoolName = pool,
                            VirtualDirectories = root.VirtualDirectories.Select(directory => directory.Path == "/" ? directory with { PhysicalPath = path } : directory).ToArray() };
                        proposed = site with { Applications = site.Applications.Select(item => item.Path == "/" ? newRoot : item).ToArray() };
                        if (patch.Bindings is not null)
                        {
                            var rawBindings = request.Parameters.EnumerateObject().FirstOrDefault(item => Equal(item.Name, "bindings")).Value;
                            if (rawBindings.ValueKind == JsonValueKind.Array)
                                foreach (var binding in rawBindings.EnumerateArray())
                                    if (binding.ValueKind == JsonValueKind.Object) CheckFields(binding, BindingFields, errors);
                            CheckBindings(patch.Bindings, snapshot.Sites.Where(item => !Equal(item.Name, site.Name)).ToArray(), options, errors);
                            if (errors.Count == 0) proposed = proposed with { Bindings = patch.Bindings.Select(Normalize).ToArray() };
                        }
                    }
                }
                else if (applicationAction)
                {
                    var patch = Deserialize<ApplicationParameters>(request.Parameters, errors);
                    var application = site.Applications.FirstOrDefault(item => Equal(item.Path, target!.ApplicationPath));
                    if (request.Type == OperationTypes.CreateApplication && application is not null) errors.Add(new("application.exists", "同路径应用已存在。"));
                    if (request.Type == OperationTypes.UpdateApplication && application is null) errors.Add(new("application.notFound", "应用不存在。"));
                    if (request.Type == OperationTypes.CreateApplication && site.Applications.SelectMany(owner => owner.VirtualDirectories
                            .Where(directory => directory.Path != "/").Select(directory => FullVirtualPath(owner.Path, directory.Path)))
                        .Any(path => Equal(path, target!.ApplicationPath))) errors.Add(new("application.directoryConflict", "该完整路径已有虚拟目录，不能隐式转换或替换。"));
                    if (patch is not null)
                    {
                        if (request.Type == OperationTypes.CreateApplication && (patch.PhysicalPath is null || patch.ApplicationPoolName is null))
                            errors.Add(new("application.fieldsRequired", "创建应用必须指定物理目录和已有应用池。"));
                        var checkedPath = MaintenancePhysicalPath(patch.PhysicalPath ?? application?.PhysicalPath ?? "", options, errors);
                        var path = patch.PhysicalPath is null && application is not null ? application.PhysicalPath : checkedPath;
                        var pool = patch.ApplicationPoolName ?? application?.ApplicationPoolName ?? "";
                        var value = application is null ? new ApplicationTopology { Path = target!.ApplicationPath!, PhysicalPath = path,
                            ApplicationPoolName = pool, EnabledProtocols = "http", VirtualDirectories = [new VirtualDirectoryTopology { Path = "/", PhysicalPath = path }] }
                            : application with { PhysicalPath = path, ApplicationPoolName = pool,
                                VirtualDirectories = application.VirtualDirectories.Select(directory => directory.Path == "/" ? directory with { PhysicalPath = path } : directory).ToArray() };
                        if (application is not null && application.VirtualDirectories.Count(directory => directory.Path == "/") != 1)
                            errors.Add(new("application.rootInvalid", "应用根目录无法唯一核实。"));
                        proposed = site with { Applications = site.Applications.Where(item => !Equal(item.Path, value.Path)).Append(value).ToArray() };
                    }
                }
                else if (directoryAction)
                {
                    var patch = Deserialize<VirtualDirectoryParameters>(request.Parameters, errors);
                    var application = site.Applications.FirstOrDefault(item => Equal(item.Path, target!.ApplicationPath));
                    if (application is null) errors.Add(new("application.notFound", "所属应用不存在。"));
                    else
                    {
                        var full = FullVirtualPath(application.Path, target!.VirtualDirectoryPath!);
                        var owner = site.Applications.Where(item => IisPathWithin(full, item.Path)).OrderByDescending(item => item.Path.Length).FirstOrDefault();
                        if (owner is null || !Equal(owner.Path, application.Path)) errors.Add(new("directory.applicationConflict", "虚拟目录完整路径已属于另一应用，不能跨应用维护。"));
                        var directory = application.VirtualDirectories.FirstOrDefault(item => Equal(item.Path, target.VirtualDirectoryPath));
                        if (request.Type == OperationTypes.CreateVirtualDirectory && directory is not null) errors.Add(new("directory.exists", "所属应用中已有同路径虚拟目录。"));
                        if (request.Type == OperationTypes.UpdateVirtualDirectory && directory is null) errors.Add(new("directory.notFound", "所属应用中的虚拟目录不存在。"));
                        if (patch is not null)
                        {
                            var checkedPath = MaintenancePhysicalPath(patch.PhysicalPath ?? directory?.PhysicalPath ?? "", options, errors);
                            var path = patch.PhysicalPath is null && directory is not null ? directory.PhysicalPath : checkedPath;
                            var value = new VirtualDirectoryTopology { Path = directory?.Path ?? target.VirtualDirectoryPath!, PhysicalPath = path };
                            var app = application with { ApplicationPoolName = patch.OwnerApplicationPoolName ?? application.ApplicationPoolName,
                                VirtualDirectories = application.VirtualDirectories.Where(item => !Equal(item.Path, value.Path)).Append(value).ToArray() };
                            if (patch.OwnerApplicationPoolName is not null)
                            {
                                foreach (var mapping in app.VirtualDirectories.Where(item => !Equal(item.Path, value.Path)))
                                    MaintenancePhysicalPath(mapping.PhysicalPath, options, errors);
                                warnings.Add($"将修改所属应用 {application.Path} 的应用池；该应用及其全部 {app.VirtualDirectories.Length} 个虚拟目录共同使用新池，其他应用保持原关联。");
                            }
                            proposed = site with { Applications = site.Applications.Select(item => Equal(item.Path, app.Path) ? app : item).ToArray() };
                        }
                    }
                }
            }
        }
        var affectedPools = (site?.Applications ?? []).Concat(proposed?.Applications ?? [])
            .Select(item => item.ApplicationPoolName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var poolName in affectedPools)
        {
            var pool = snapshot.ApplicationPools.FirstOrDefault(item => Equal(item.Name, poolName));
            if (!ValidName(poolName) || pool is null) errors.Add(new("site.poolNotFound", "所有关联应用池必须存在且名称有效。"));
            else if (pool.State != "Stopped") errors.Add(new("pool.mustBeStopped", "所有原关联池及新关联池必须已停止，请核对共享池影响范围后单独处理。"));
        }
        if (site is not null && proposed is not null) AddMaintenanceChanges(site, proposed, changes);
        var affected = (site?.Applications.Select(item => site.Name + item.Path) ?? []).Concat(
            snapshot.ApplicationPools.Where(pool => affectedPools.Contains(pool.Name, StringComparer.OrdinalIgnoreCase)).SelectMany(pool => pool.Applications))
            .Concat(proposed?.Applications.Select(item => proposed.Name + item.Path) ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        var fingerprint = Hash(new { request.Type, target, sharedConfiguration,
            sites = topology.Sites.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Select(SortMaintenanceSite).ToArray(),
            pools = snapshot.ApplicationPools.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Select(item => item with { Fingerprint = "", Applications = item.Applications.Order(StringComparer.Ordinal).ToArray() }).ToArray(), evidence });
        if (request.ExpectedFingerprint is not null && !EqualFingerprint(request.ExpectedFingerprint, fingerprint))
            errors.Add(new("configuration.conflict", "配置、目录身份、状态或关联资源已变化，请重新预检。"));
        if (proposed?.Bindings.Any(binding => binding.Protocol == "https") == true && request.Type == OperationTypes.UpdateSite)
            warnings.Add("HTTPS 变更只允许已核实的本站独占 HTTP.sys 记录；共享或外部 TLS 记录拒绝修改。异常和读回差异不自动回滚。" );
        return new(errors.Count == 0, fingerprint, affected, changes.ToArray(), warnings.ToArray(), errors.ToArray(),
            site is null ? null : JsonSerializer.SerializeToElement(SortMaintenanceSite(site), Protocol.Json),
            proposed is null ? null : JsonSerializer.SerializeToElement(SortMaintenanceSite(proposed), Protocol.Json));
    }

    internal static SiteTopology SortMaintenanceSite(SiteTopology site) => site with
    {
        Bindings = site.Bindings.OrderBy(BindingKey, StringComparer.Ordinal).ToArray(),
        Applications = site.Applications.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(item => item with
        { VirtualDirectories = item.VirtualDirectories.OrderBy(directory => directory.Path, StringComparer.OrdinalIgnoreCase).ToArray() }).ToArray()
    };
    internal static bool MaintenanceMatches(SiteTopology actual, SiteTopology expected, SiteTopology before, IisSnapshot snapshot) =>
        CanonicalJson.Hash(SortMaintenanceSite(actual)) == CanonicalJson.Hash(SortMaintenanceSite(expected)) &&
        before.Applications.Concat(expected.Applications).Select(item => item.ApplicationPoolName).Distinct(StringComparer.OrdinalIgnoreCase)
            .All(name => snapshot.ApplicationPools.Any(pool => Equal(pool.Name, name) && pool.State == "Stopped"));
    internal static bool CanonicalIisPath(string? path, bool allowRoot) => path is { Length: > 0 and <= 512 } && path[0] == '/' &&
        (path == "/" ? allowRoot : !path.EndsWith('/') && path.Split('/').Skip(1).All(segment => segment.Length > 0 && segment is not ("." or "..") &&
            segment == segment.Trim() && !segment.EndsWith('.') && !segment.Any(character => char.IsControl(character) || "%\\?#:;*\"<>|".Contains(character))));
    internal static string FullVirtualPath(string application, string directory) => (application == "/" ? "" : application) + directory;
    private static bool IisPathWithin(string path, string application) => application == "/" || Equal(path, application) || path.StartsWith(application + "/", StringComparison.OrdinalIgnoreCase);
    private static string MaintenancePhysicalPath(string value, AgentOptions options, List<ApiError> errors)
    {
        try { SitePathGuard.Validate(value, options.AllowedSiteRoots); return Path.GetFullPath(value); }
        catch (OperationException exception) { errors.Add(new(exception.Code, exception.Message)); return value; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { errors.Add(new("site.directoryUnverified", "目录身份无法核实，拒绝维护。")); return value; }
    }
    private static void AddMaintenanceChanges(SiteTopology before, SiteTopology after, List<FieldChange> changes)
    {
        void Change(string field, string left, string right) { if (left != right) changes.Add(new(field, left, right)); }
        Change("bindings", JsonSerializer.Serialize(before.Bindings.OrderBy(BindingKey), Protocol.Json), JsonSerializer.Serialize(after.Bindings.OrderBy(BindingKey), Protocol.Json));
        foreach (var app in after.Applications)
        {
            var old = before.Applications.FirstOrDefault(item => Equal(item.Path, app.Path));
            Change($"applications[{app.Path}].applicationPoolName", old?.ApplicationPoolName ?? "不存在", app.ApplicationPoolName);
            Change($"applications[{app.Path}].physicalPath", old?.PhysicalPath ?? "不存在", app.PhysicalPath);
            foreach (var directory in app.VirtualDirectories.Where(item => item.Path != "/"))
                Change($"applications[{app.Path}].virtualDirectories[{directory.Path}].physicalPath",
                    old?.VirtualDirectories.FirstOrDefault(item => Equal(item.Path, directory.Path))?.PhysicalPath ?? "不存在", directory.PhysicalPath);
        }
    }
    internal static bool EqualFingerprint(string left, string right) => left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    internal static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json)));
    internal static BindingConfig Normalize(BindingConfig b) => b with
    {
        Protocol = b.Protocol.ToLowerInvariant(), IpAddress = NormalizeIp(b.IpAddress),
        HostName = b.HostName.ToLowerInvariant(), CertificateThumbprint = b.CertificateThumbprint?.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant()
    };
    internal static string BindingKey(BindingConfig b) => $"{b.Protocol}|{NormalizeIp(b.IpAddress)}|{b.Port:D5}|{b.HostName.ToLowerInvariant()}|{b.UseSni}|{b.CertificateThumbprint}";
    internal static string NormalizeIp(string ip) => ip is "*" or "0.0.0.0" or "::" ? "*" : IPAddress.TryParse(ip, out var parsed) ? parsed.ToString() : ip;
    internal static bool AddressesOverlap(string a, string b) => NormalizeIp(a) == "*" || NormalizeIp(b) == "*" || Equal(NormalizeIp(a), NormalizeIp(b));
    internal static bool ValidName(string? name) => name is { Length: > 0 and <= 64 } && name == name.Trim() &&
        !name.EndsWith('.') && !name.Any(x => char.IsControl(x) || "/\\:*?\"<>|".Contains(x));
    internal static T? Deserialize<T>(JsonElement element, List<ApiError> errors) where T : class
    {
        try { return element.Deserialize<T>(Protocol.Json); }
        catch (JsonException) { errors.Add(new("parameters.invalid", "参数类型或字段不符合协议。")); return null; }
    }

    private static void CheckFields(JsonElement element, HashSet<string> allowed, List<ApiError> errors)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in element.EnumerateObject())
        {
            if (!allowed.Contains(field.Name)) errors.Add(new("parameters.unknownField", "参数包含未支持字段。"));
            if (!seen.Add(field.Name)) errors.Add(new("parameters.duplicateField", "参数字段不能重复。"));
            if (field.Value.ValueKind == JsonValueKind.Null && !Equal(field.Name, "certificateThumbprint"))
                errors.Add(new("parameters.nullField", "局部字段不能使用 null；请省略不修改的字段。"));
        }
    }
    private static bool RuntimeFieldsPresent(PoolParameters p) => p.ManagedRuntimeVersion is not null || p.ManagedPipelineMode is not null ||
        p.Enable32BitAppOnWin64 is not null || p.StartMode is not null || p.IdleTimeoutMinutes is not null || p.IdleTimeoutAction is not null;
    private static void CheckPoolParameters(PoolParameters p, List<ApiError> errors)
    {
        if (p.ManagedRuntimeVersion is not null && p.ManagedRuntimeVersion is not ("" or "v2.0" or "v4.0")) errors.Add(new("pool.runtimeInvalid", "运行时仅支持空字符串、v2.0 或 v4.0。"));
        if (p.ManagedPipelineMode is not null && p.ManagedPipelineMode is not ("Integrated" or "Classic")) errors.Add(new("pool.pipelineInvalid", "管道模式须为 Integrated 或 Classic。"));
        if (p.StartMode is not null && p.StartMode is not ("OnDemand" or "AlwaysRunning")) errors.Add(new("pool.startModeInvalid", "启动模式须为 OnDemand 或 AlwaysRunning。"));
        if (p.IdleTimeoutMinutes is < 0 or > 43200) errors.Add(new("pool.idleTimeoutInvalid", "空闲超时范围为 0–43200 分钟；0 表示关闭。"));
        if (p.IdleTimeoutAction is not null && p.IdleTimeoutAction is not ("Terminate" or "Suspend")) errors.Add(new("pool.idleActionInvalid", "空闲动作须为 Terminate 或 Suspend。"));
    }
    private static void AddPoolChanges(PoolConfig? before, PoolConfig after, List<FieldChange> changes)
    {
        var fields = new[] { "managedRuntimeVersion", "managedPipelineMode", "enable32BitAppOnWin64", "autoStart", "startMode", "idleTimeoutMinutes", "idleTimeoutAction" };
        var left = before is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(before, Protocol.Json);
        var right = JsonSerializer.SerializeToElement(after, Protocol.Json);
        foreach (var field in fields)
        {
            var a = left?.GetProperty(field).ToString() ?? "不存在";
            var b = right.GetProperty(field).ToString();
            if (a != b) changes.Add(new(field, a, b));
        }
    }
    private static void CheckBindings(BindingConfig[]? bindings, SiteConfig[] sites, AgentOptions options, List<ApiError> errors)
    {
        if (bindings is null || bindings.Length is < 1 or > 16) { errors.Add(new("binding.countInvalid", "须提供 1–16 个绑定。")); return; }
        var previous = new List<BindingConfig>();
        foreach (var b in bindings)
        {
            if (b is null) { errors.Add(new("binding.invalid", "绑定不能为 null。")); continue; }
            if (b.Protocol is null || b.IpAddress is null || b.HostName is null) { errors.Add(new("binding.invalid", "绑定协议、IP 和主机名不能为 null。")); continue; }
            if (b.Protocol is not ("http" or "https")) errors.Add(new("binding.protocolInvalid", "绑定协议仅支持 http 或 https。"));
            if (b.Port is < 1 or > 65535 || options.ReservedPorts.Contains(b.Port) || b.Port == options.Port) errors.Add(new("binding.portInvalid", "端口无效或属于 Agent/管理保留端口。"));
            if (b.IpAddress != "*" && !IPAddress.TryParse(b.IpAddress, out _)) errors.Add(new("binding.ipInvalid", "IP 必须为 * 或有效 IP 地址。"));
            if (b.HostName.Length > 253 || (b.HostName.Length != 0 && !ValidHost(b.HostName))) errors.Add(new("binding.hostInvalid", "主机名必须为有效 DNS 主机名，不支持通配符或 IDN Unicode。"));
            if (b.Protocol == "http" && (b.UseSni || b.CertificateThumbprint is not null)) errors.Add(new("binding.httpTlsInvalid", "HTTP 绑定不能设置证书或 SNI。"));
            if (b.Protocol == "https")
            {
                var thumbprint = b.CertificateThumbprint?.Replace(" ", "", StringComparison.Ordinal);
                if (thumbprint is not { Length: 40 } || !thumbprint.All(Uri.IsHexDigit)) errors.Add(new("binding.certificateInvalid", "HTTPS 须指定 LocalMachine/My 中现有证书的 SHA1 指纹。"));
                if (string.IsNullOrEmpty(b.HostName)) errors.Add(new("binding.httpsHostRequired", "HTTPS 须提供主机名以验证证书名称。"));
                if (b.UseSni && NormalizeIp(b.IpAddress) != "*") errors.Add(new("binding.sniIpInvalid", "本版本的 SNI 绑定仅支持通配 IP。"));
            }
            foreach (var existing in previous.Concat(sites.SelectMany(x => x.Bindings)))
            {
                if (b.Port != existing.Port || !AddressesOverlap(b.IpAddress, existing.IpAddress)) continue;
                if (!Equal(b.Protocol, existing.Protocol)) { errors.Add(new("binding.protocolConflict", "此地址和端口已用于另一协议。")); continue; }
                if (Equal(b.HostName, existing.HostName) || b.HostName.Length == 0 || existing.HostName.Length == 0)
                    errors.Add(new("binding.conflict", "站点绑定与现有绑定或本次其他绑定重叠。"));
                if (b.Protocol == "https" && (!b.UseSni || !existing.UseSni) &&
                    (!Equal(b.CertificateThumbprint, existing.CertificateThumbprint) || b.UseSni != existing.UseSni))
                    errors.Add(new("binding.tlsConflict", "共享 IP/端口的 TLS 证书或 SNI 模式不兼容。"));
            }
            previous.Add(b);
        }
    }
    private static bool ValidHost(string host)
    {
        if (host.EndsWith('.') || host.Any(x => x > 127)) return false;
        return host.Split('.').All(part => part.Length is > 0 and <= 63 && char.IsAsciiLetterOrDigit(part[0]) &&
            char.IsAsciiLetterOrDigit(part[^1]) && part.All(x => char.IsAsciiLetterOrDigit(x) || x == '-'));
    }
}
