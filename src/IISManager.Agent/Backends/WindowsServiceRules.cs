using System.Text.Json;
using IISManager.Contracts;

namespace IISManager.Agent.Backends;

// No raw ImagePath, password or service arguments may enter the public projection.
internal static class WindowsServiceRules
{
    private static readonly string[] BuiltInProtected = ["IISManager.Agent", "IISManager.Console"];
    private static readonly string[] BuiltInAccounts = ["VirtualAccount", "LocalService", "NetworkService", "LocalSystem"];

    internal static bool ValidName(string? name) => WindowsServiceRequestSafety.ValidName(name);

    internal static bool IsAllowed(string name, WindowsServiceOptions options) =>
        (options.AllowedNames ?? []).Any(x => BackendRules.Equal(x, name)) ||
        (options.AllowedNamePrefixes ?? []).Any(prefix => !string.IsNullOrWhiteSpace(prefix) &&
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    internal static bool CanCreate(WindowsServiceOptions options) => options.AllowCreate &&
        ((options.AllowedNames ?? []).Any(ValidName) || (options.AllowedNamePrefixes ?? []).Any(x => !string.IsNullOrWhiteSpace(x))) &&
        (options.AllowedExecutableRoots ?? []).Length > 0 && AllowedAccounts(options).Length > 0;

    private static string CreationUnavailableReason(WindowsServiceOptions options, bool createAccess)
    {
        var reasons = new List<string>();
        if (!options.AllowCreate) reasons.Add("未开启创建服务（AllowCreate）");
        if (!(options.AllowedNames ?? []).Any(ValidName) && !(options.AllowedNamePrefixes ?? []).Any(x => !string.IsNullOrWhiteSpace(x)))
            reasons.Add("未配置允许的服务名称或前缀");
        if ((options.AllowedExecutableRoots ?? []).Length == 0) reasons.Add("未配置允许的服务程序目录（AllowedExecutableRoots）");
        if (AllowedAccounts(options).Length == 0) reasons.Add("未配置支持的服务运行账号");
        if (!createAccess) reasons.Add("Agent 运行身份缺少 Windows 服务创建权限");
        return string.Join("；", reasons) + "。";
    }

    internal static string[] AllowedAccounts(WindowsServiceOptions options) => (options.AllowedAccounts ?? [])
        .Where(value => BuiltInAccounts.Contains(value, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static string ResolveAccount(string name, string kind) => kind.ToUpperInvariant() switch
    {
        "VIRTUALACCOUNT" => @"NT SERVICE\" + name,
        "LOCALSERVICE" => @"NT AUTHORITY\LocalService",
        "NETWORKSERVICE" => @"NT AUTHORITY\NetworkService",
        "LOCALSYSTEM" => "LocalSystem",
        _ => ""
    };

    internal static HashSet<string> ProtectedClosure(WindowsServiceConfig[] services, WindowsServiceOptions options)
    {
        var result = new HashSet<string>(BuiltInProtected.Concat(options.ProtectedNames ?? []), StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(result);
        while (queue.TryDequeue(out var name))
        {
            var service = services.FirstOrDefault(x => BackendRules.Equal(x.Name, name));
            if (service is null) continue;
            foreach (var dependency in service.Dependencies)
                if (result.Add(dependency)) queue.Enqueue(dependency);
        }
        return result;
    }

    internal static HashSet<string> RelevantNames(OperationRequest request, WindowsServiceConfig[] services, WindowsServiceOptions options)
    {
        var names = ProtectedClosure(services, options);
        names.Add(request.Target?.ServiceName ?? "");
        if (request.Type == OperationTypes.ServiceCreate && WindowsServiceRequestSafety.IsValid(request))
            foreach (var name in request.Parameters.Deserialize<WindowsServiceCreateParameters>(Protocol.Json)!.Dependencies) names.Add(name);
        var queue = new Queue<string>(names);
        while (queue.TryDequeue(out var name))
        {
            var service = services.FirstOrDefault(x => BackendRules.Equal(x.Name, name));
            if (service is not null) foreach (var dependency in service.Dependencies) if (names.Add(dependency)) queue.Enqueue(dependency);
            foreach (var dependent in services.Where(x => x.Dependencies.Contains(name, StringComparer.OrdinalIgnoreCase)))
                if (names.Add(dependent.Name)) queue.Enqueue(dependent.Name);
        }
        return names;
    }

    internal static WindowsServiceSnapshot Project(WindowsServiceConfig[] services, WindowsServiceOptions options,
        Func<string, string, bool>? access = null, bool createAccess = true)
    {
        var protectedNames = ProtectedClosure(services, options);
        var result = services.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(service =>
        {
            var managed = IsAllowed(service.Name, options) && !protectedNames.Contains(service.Name) && service.UnavailableReason is null;
            var stable = service.State is "Running" or "Stopped";
            var stopAllowed = service.State == "Stopped" || !services.Any(x => x.Dependencies.Contains(service.Name, StringComparer.OrdinalIgnoreCase) && x.State != "Stopped");
            var permissions = access ?? ((string _, string _) => true);
            var startErrors = new List<ApiError>();
            if (managed && stable) CheckStartDependencies(service, services, options, protectedNames, permissions, [], startErrors);
            var startAllowed = startErrors.Count == 0;
            var canStart = managed && service.State == "Stopped" && startAllowed && service.StartType != WindowsServiceStartTypes.Disabled && permissions(service.Name, OperationTypes.ServiceStart);
            var canStop = managed && service.State == "Running" && stopAllowed && permissions(service.Name, OperationTypes.ServiceStop);
            var canRestart = managed && service.State == "Running" && startAllowed && stopAllowed && service.StartType != WindowsServiceStartTypes.Disabled && permissions(service.Name, OperationTypes.ServiceRestart);
            var canUpdate = managed && stable && permissions(service.Name, OperationTypes.ServiceUpdateStartType);
            return service with
            {
                Managed = managed,
                CanStart = canStart, CanStop = canStop, CanRestart = canRestart, CanUpdateStartType = canUpdate,
                UnavailableReason = protectedNames.Contains(service.Name) ? "管理组件及其依赖服务受保护。" :
                    !IsAllowed(service.Name, options) ? "服务不在允许管理范围内。" : service.UnavailableReason ??
                    (!stable ? "服务处于暂停、过渡或未知状态，请先刷新。" :
                    !canStart && !canStop && !canRestart && !canUpdate ? "实际权限不足，或启动类型与依赖状态不支持操作。" : null),
                DependentServices = services.Where(x => x.Dependencies.Contains(service.Name, StringComparer.OrdinalIgnoreCase))
                    .Select(x => x.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray()
            };
        }).ToArray();
        var canCreate = CanCreate(options) && createAccess;
        return new(DateTimeOffset.UtcNow, result, canCreate, canCreate ? null : CreationUnavailableReason(options, createAccess), AllowedAccounts(options));
    }

    internal static ValidationResult Validate(OperationRequest request, WindowsServiceConfig[] services,
        WindowsServiceOptions options, object? privateEvidence = null, Action<WindowsServiceCreateParameters, List<ApiError>>? checkCreate = null,
        Func<string, string, bool>? access = null, bool createAccess = true)
    {
        var errors = new List<ApiError>();
        var changes = new List<FieldChange>();
        var warnings = new List<string>();
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var name = request.Target?.ServiceName ?? "";
        var protectedNames = ProtectedClosure(services, options);
        var target = services.FirstOrDefault(x => BackendRules.Equal(x.Name, name));
        WindowsServiceConfig? proposed = null;
        if (!OperationTypes.WindowsServices.Contains(request.Type, StringComparer.Ordinal)) errors.Add(new("operation.unsupported", "不支持此服务操作。"));
        if (!WindowsServiceRequestSafety.IsValid(request)) errors.Add(new("service.requestUnsafe", "服务请求字段、参数类型或敏感信息检查未通过。"));
        if (!ValidName(name)) errors.Add(new("service.nameInvalid", "服务名须为 1–128 个普通字符，不能包含路径、控制字符或首尾空白。"));
        if (request.Target is null || request.Target.ApplicationPoolName is not null || request.Target.SiteName is not null)
            errors.Add(new("target.invalid", "服务操作只能指定 serviceName。"));
        if (!IsAllowed(name, options)) errors.Add(new("service.notAllowed", "服务名不在 Agent 允许管理范围内。"));
        if (protectedNames.Contains(name)) errors.Add(new("service.protected", "管理组件及其依赖服务受保护。"));
        if (request.Parameters.ValueKind != JsonValueKind.Object) errors.Add(new("parameters.invalid", "parameters 必须是 JSON 对象。"));
        else
        {
            var allowed = request.Type == OperationTypes.ServiceCreate
                ? new[] { "displayName", "description", "executablePath", "arguments", "startType", "accountName", "dependencies" }
                : request.Type == OperationTypes.ServiceUpdateStartType ? ["startType"] : Array.Empty<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in request.Parameters.EnumerateObject())
            {
                if (!allowed.Contains(field.Name, StringComparer.OrdinalIgnoreCase)) errors.Add(new("parameters.unknownField", "参数包含未支持字段。"));
                if (!seen.Add(field.Name)) errors.Add(new("parameters.duplicateField", "参数字段不能重复。"));
                if (field.Value.ValueKind == JsonValueKind.Null) errors.Add(new("parameters.nullField", "参数不能为 null。"));
            }
        }
        var permissions = access ?? ((string _, string _) => true);
        if (request.Type == OperationTypes.ServiceCreate)
        {
            if (!CanCreate(options) || !createAccess) errors.Add(new("service.createDisabled", "创建服务未启用或实际创建权限不足。"));
            if (target is not null) errors.Add(new("service.exists", "同名服务已存在，不能覆盖注册。"));
            var p = request.Parameters.ValueKind == JsonValueKind.Object ? BackendRules.Deserialize<WindowsServiceCreateParameters>(request.Parameters, errors) : null;
            if (p is not null)
            {
                if (!Text(p.DisplayName, 256, true) || p.Description is null || p.Description.Length > 1024 || p.Description.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t')) errors.Add(new("service.textInvalid", "显示名称最长 256 个字符，描述最长 1024 个字符。"));
                if (!WindowsServiceStartTypes.All.Contains(p.StartType, StringComparer.Ordinal)) errors.Add(new("service.startTypeInvalid", "启动类型仅支持自动、延迟自动、手动或禁用。"));
                if (!AllowedAccounts(options).Contains(p.AccountName, StringComparer.OrdinalIgnoreCase)) errors.Add(new("service.accountNotAllowed", "首期仅使用允许的内置或专用虚拟账号，不接收账号密码。"));
                if (p.Arguments is null || p.Arguments.Length > 32 || p.Arguments.Any(x => !Text(x, 2048, true)) || WindowsServiceRequestSafety.ContainsSensitiveArguments(p.Arguments))
                    errors.Add(new("service.argumentsUnsafe", "参数须为最多 32 个普通字符串；密码、令牌、密钥、凭据和连接字符串须改用受保护配置。"));
                if (p.Dependencies is null || p.Dependencies.Length > 32 || p.Dependencies.Any(x => !ValidName(x) || x.StartsWith('+') || BackendRules.Equal(x, name)) || p.Dependencies.Distinct(StringComparer.OrdinalIgnoreCase).Count() != p.Dependencies.Length)
                    errors.Add(new("service.dependenciesInvalid", "依赖项须为最多 32 个不重复的现有服务名，不能包含自身或服务组。"));
                else foreach (var dependency in p.Dependencies)
                {
                    affected.Add(dependency);
                    var existing = services.FirstOrDefault(x => BackendRules.Equal(x.Name, dependency));
                    if (existing is null || existing.UnavailableReason is not null) errors.Add(new("service.dependencyUnverified", "依赖服务不存在或无法完整查询。"));
                }
                if (errors.Count == 0) checkCreate?.Invoke(p, errors);
                proposed = new()
                {
                    Name = name, DisplayName = string.IsNullOrEmpty(p.DisplayName) ? name : p.DisplayName, Description = p.Description ?? "", State = "Stopped", StartType = p.StartType,
                    ExecutablePath = p.ExecutablePath, AccountName = ResolveAccount(name, p.AccountName), Dependencies = p.Dependencies ?? [], Managed = true
                };
                changes.Add(new("service", "不存在", name));
                changes.Add(new("startType", "不存在", p.StartType));
                warnings.Add("仅注册已支持 Windows 服务协议的现有程序，创建后保持停止；本操作不复制文件、不修改 ACL、不授予账号权限。" );
                if (BackendRules.Equal(p.AccountName, "LocalSystem")) warnings.Add("所选 LocalSystem 账号拥有系统级权限，请核对程序来源和目录权限。" );
            }
        }
        else if (target is null) errors.Add(new("service.notFound", "服务不存在。"));
        else
        {
            if (target.UnavailableReason is not null) errors.Add(new("service.unavailable", "无法完整核实目标服务配置或类型。"));
            if (target.State is not ("Running" or "Stopped")) errors.Add(new("service.transitioning", "服务处于暂停、过渡或未知状态，请先刷新。"));
            if (!permissions(name, request.Type)) errors.Add(new("service.accessDenied", "Agent 实际身份不具备此服务操作所需权限。"));
            if (request.Type == OperationTypes.ServiceUpdateStartType)
            {
                var p = request.Parameters.ValueKind == JsonValueKind.Object ? BackendRules.Deserialize<WindowsServiceStartTypeParameters>(request.Parameters, errors) : null;
                if (p is null || !WindowsServiceStartTypes.All.Contains(p.StartType, StringComparer.Ordinal)) errors.Add(new("service.startTypeInvalid", "必须指定支持的启动类型。"));
                else { proposed = target with { StartType = p.StartType }; changes.Add(new("startType", target.StartType, p.StartType)); }
                warnings.Add("仅修改启动类型；运行状态、服务入口、账号、依赖和其他配置保持原值。" );
            }
            else
            {
                if (request.Type == OperationTypes.ServiceRestart && target.State != "Running") errors.Add(new("service.notRunning", "只能重新启动正在运行的服务。"));
                if (request.Type is OperationTypes.ServiceStart or OperationTypes.ServiceRestart)
                {
                    if (target.StartType == WindowsServiceStartTypes.Disabled) errors.Add(new("service.disabled", "服务启动类型为禁用，须单独修改启动类型后再启动。"));
                    CheckStartDependencies(target, services, options, protectedNames, permissions, affected, errors);
                }
                if (request.Type is OperationTypes.ServiceStop or OperationTypes.ServiceRestart)
                {
                    foreach (var child in services.Where(x => x.Dependencies.Contains(name, StringComparer.OrdinalIgnoreCase) && x.State != "Stopped"))
                    { affected.Add(child.Name); errors.Add(new("service.dependentsRunning", "存在运行中、过渡或未知状态的从属服务，本操作不会级联停止。")); }
                }
                var state = request.Type == OperationTypes.ServiceStop ? "Stopped" : "Running";
                proposed = target with { State = state, ProcessId = state == "Stopped" ? 0u : target.ProcessId };
                changes.Add(new("state", target.State, state));
                if (request.Type == OperationTypes.ServiceRestart) warnings.Add("重新启动先停止再启动；若启动失败，结果保留已停止状态，请只读核实后处理。" );
                warnings.Add("服务状态读回通过仅表示 SCM 状态符合预期，业务健康须另行验证。" );
            }
        }
        var relevant = RelevantNames(request, services, options);
        var fingerprint = BackendRules.Hash(new
        {
            name, request.Type, parameters = request.Parameters,
            services = services.Where(x => relevant.Contains(x.Name)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(x => x with
            { CanStart = false, CanStop = false, CanRestart = false, CanUpdateStartType = false, Managed = false }).ToArray(),
            policy = options, privateEvidence
        });
        if (request.ExpectedFingerprint is not null && !BackendRules.EqualFingerprint(request.ExpectedFingerprint, fingerprint))
            errors.Add(new("configuration.conflict", "服务配置、依赖、状态、权限或策略发生变化，请重新预检。"));
        return new(errors.Count == 0, fingerprint, [], changes.ToArray(), warnings.ToArray(), errors.ToArray(),
            target is null ? null : JsonSerializer.SerializeToElement(target, Protocol.Json),
            proposed is null ? null : JsonSerializer.SerializeToElement(proposed, Protocol.Json))
        { AffectedServices = affected.Order(StringComparer.OrdinalIgnoreCase).ToArray() };
    }

    private static void CheckStartDependencies(WindowsServiceConfig target, WindowsServiceConfig[] services, WindowsServiceOptions options,
        HashSet<string> protectedNames, Func<string, string, bool> access, HashSet<string> affected, List<ApiError> errors)
    {
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(WindowsServiceConfig service)
        {
            if (!visiting.Add(service.Name)) { errors.Add(new("service.dependencyCycle", "服务依赖形成循环，无法安全启动。")); return; }
            foreach (var name in service.Dependencies)
            {
                var dependency = services.FirstOrDefault(x => BackendRules.Equal(x.Name, name));
                if (dependency is null || dependency.UnavailableReason is not null) { errors.Add(new("service.dependencyUnverified", "依赖服务或服务组无法完整核实。")); continue; }
                if (dependency.State != "Running")
                {
                    affected.Add(name);
                    if (dependency.State != "Stopped" || dependency.StartType == WindowsServiceStartTypes.Disabled)
                        errors.Add(new("service.dependencyUnavailable", "依赖服务被禁用或状态不稳定。"));
                    if (!IsAllowed(name, options) || protectedNames.Contains(name) || !access(name, OperationTypes.ServiceStart))
                        errors.Add(new("service.dependencyNotAllowed", "启动会影响不允许管理、受保护或权限不足的依赖服务。"));
                }
                if (!visited.Contains(name)) Visit(dependency);
            }
            visiting.Remove(service.Name); visited.Add(service.Name);
        }
        Visit(target);
    }

    private static bool Text(string? value, int max, bool emptyAllowed) => value is not null && value.Length <= max &&
        (emptyAllowed || !string.IsNullOrWhiteSpace(value)) && !value.Any(char.IsControl);

    internal static bool WindowsExecutableAllowed(string? requestedPath, string[]? roots)
    {
        // This lexical guard is also used by the simulation running on macOS.
        if (requestedPath is null || requestedPath.Length < 4 || !char.IsAsciiLetter(requestedPath[0]) || requestedPath[1] != ':' ||
            requestedPath[2] != '\\' || requestedPath.Any(c => char.IsControl(c) || "\"<>|?*".Contains(c)) || requestedPath[2..].Contains(':') ||
            !requestedPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || requestedPath.Split('\\').Any(x => x is "." or ".." or "" || x.EndsWith(' ') || x.EndsWith('.'))) return false;
        return (roots ?? []).Any(root => !string.IsNullOrWhiteSpace(root) && root.Length >= 3 && char.IsAsciiLetter(root[0]) && root[1] == ':' && root[2] == '\\' &&
            requestedPath.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }
}
