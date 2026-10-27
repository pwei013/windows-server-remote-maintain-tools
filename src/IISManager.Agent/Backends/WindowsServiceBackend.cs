using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using IISManager.Contracts;
using IISManager.Core;

namespace IISManager.Agent.Backends;

[SupportedOSPlatform("windows")]
public sealed class WindowsServiceBackend : IWindowsServiceBackend
{
    private readonly AgentOptions _options;
    public WindowsServiceBackend(AgentOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new OperationException("service.windowsRequired", "Windows 服务模式只能在 Windows Server 上使用。", 503);
        _options = options;
    }

    public Task<WindowsServiceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inventory = WindowsServiceScm.Inventory(_options.WindowsServices.ProtectedNames);
        return Task.FromResult(WindowsServiceRules.Project(inventory.Select(x => x.Public).ToArray(), _options.WindowsServices,
            WindowsServiceScm.HasAccess, WindowsServiceScm.CanCreate()));
    }

    public async Task<WindowsServiceConfig?> GetServiceAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!WindowsServiceRules.ValidName(name)) throw new OperationException("service.nameInvalid", "服务名无效。" );
        var snapshot = await GetSnapshotAsync(cancellationToken);
        return snapshot.Services.FirstOrDefault(x => BackendRules.Equal(x.Name, name));
    }

    public Task<ValidationResult> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var proof = Validate(request);
        return Task.FromResult(proof.Result);
    }

    public async Task<ExecutionResult> ExecuteAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ExpiresAt <= DateTimeOffset.UtcNow) return new(OperationStates.Expired, null, new("operation.expired", "操作已过期。"));
        if (string.IsNullOrEmpty(request.ExpectedFingerprint)) return new(OperationStates.Failed, null, new("configuration.fingerprintRequired", "执行必须携带最新预检指纹。"));
        var mayHaveMutated = false;
        var created = false;
        WindowsServiceScm.Evidence? before = null;
        try
        {
            // Retain the executable lease through creation and readback. Existing-service handles
            // stay open through all mutations, so a deletion cannot substitute a different object.
            using var proof = Validate(request);
            if (!proof.Result.Valid) return new(OperationStates.Failed, null, proof.Result.Errors[0]);
            cancellationToken.ThrowIfCancellationRequested();
            if (request.ExpiresAt <= DateTimeOffset.UtcNow) return new(OperationStates.Expired, null, new("operation.expired", "操作在执行前已过期。"));
            var name = request.Target.ServiceName!;
            before = proof.Inventory.FirstOrDefault(x => BackendRules.Equal(x.Public.Name, name));
            var watch = Stopwatch.StartNew();
            var timeout = TimeSpan.FromSeconds(Math.Clamp(_options.WindowsServices.OperationTimeoutSeconds, 5, 300));
            if (request.Type == OperationTypes.ServiceCreate)
            {
                var p = request.Parameters.Deserialize<WindowsServiceCreateParameters>(Protocol.Json)!;
                // Readback of name/absence and the held PE/ACL evidence is performed immediately before registration.
                using var recheck = Validate(request);
                if (!recheck.Result.Valid) return new(OperationStates.Failed, null, recheck.Result.Errors[0]);
                cancellationToken.ThrowIfCancellationRequested();
                if (request.ExpiresAt <= DateTimeOffset.UtcNow) return new(OperationStates.Expired, null, new("operation.expired", "操作在注册前已过期。"));
                mayHaveMutated = true;
                using var service = WindowsServiceScm.Create(name, p);
                created = true;
                WindowsServiceScm.CompleteCreation(service, p);
                var after = WindowsServiceScm.Read(service, name);
                if (!WindowsServiceScm.MatchesCreation(after, name, p))
                    return new(OperationStates.Partial, Public(after.Public), new("service.createReadbackMismatch", "服务已创建，但入口、账号、配置或状态与计划不符，请只读核实。"));
                return new(OperationStates.Succeeded, Public(after.Public));
            }
            uint access = WindowsServiceScm.QueryConfig | WindowsServiceScm.QueryStatus | (request.Type switch
            {
                OperationTypes.ServiceStart => WindowsServiceScm.Start,
                OperationTypes.ServiceStop => WindowsServiceScm.Stop | WindowsServiceScm.EnumDependents,
                OperationTypes.ServiceRestart => WindowsServiceScm.Start | WindowsServiceScm.Stop | WindowsServiceScm.EnumDependents,
                OperationTypes.ServiceUpdateStartType => WindowsServiceScm.ChangeConfig,
                _ => 0u
            });
            using var target = WindowsServiceScm.Open(name, access);
            // Recompute all relevant topology/configuration/access evidence after opening the exact object.
            using var finalCheck = Validate(request);
            if (!finalCheck.Result.Valid) return new(OperationStates.Failed, null, finalCheck.Result.Errors[0]);
            var actual = WindowsServiceScm.Read(target, name);
            if (before is null || actual.ConfigurationHash != before.ConfigurationHash || actual.Public.State != before.Public.State || actual.Public.ProcessId != before.Public.ProcessId)
                return new(OperationStates.Failed, null, new("configuration.conflict", "服务在执行前发生变化，请重新预检。"));
            cancellationToken.ThrowIfCancellationRequested();
            if (request.ExpiresAt <= DateTimeOffset.UtcNow) return new(OperationStates.Expired, null, new("operation.expired", "操作在执行前已过期。"));
            if (request.Type == OperationTypes.ServiceUpdateStartType)
            {
                var start = request.Parameters.Deserialize<WindowsServiceStartTypeParameters>(Protocol.Json)!.StartType;
                if (actual.Public.StartType != start) { mayHaveMutated = true; WindowsServiceScm.UpdateStartType(target, start); }
            }
            else
            {
                if (request.Type is OperationTypes.ServiceStop or OperationTypes.ServiceRestart && actual.Public.State == "Running")
                {
                    if (WindowsServiceScm.RunningDependents(name).Length != 0) return new(OperationStates.Failed, null, new("service.dependentsRunning", "从属服务状态变化，本操作不会级联停止。"));
                    mayHaveMutated = true;
                    WindowsServiceScm.StopServiceChecked(target);
                    await WindowsServiceScm.WaitAsync(target, 1, watch, timeout);
                    if (WindowsServiceScm.Read(target, name).Public.State != "Stopped") throw new OperationException("service.stopReadbackMismatch", "停止读回未通过。" );
                }
                if (request.Type is OperationTypes.ServiceStart or OperationTypes.ServiceRestart && WindowsServiceScm.Status(target).State == 1)
                {
                    if (watch.Elapsed >= timeout) throw new OperationException("service.waitTimeout", "停止阶段已耗尽本次总体期限。" );
                    var stopped = WindowsServiceScm.Read(target, name);
                    if (stopped.ConfigurationHash != before.ConfigurationHash) throw new OperationException("service.configurationChanged", "停止后服务配置发生变化，启动阶段已暂停。" );
                    RecheckStartStage(request, proof);
                    if (watch.Elapsed >= timeout) throw new OperationException("service.waitTimeout", "启动前重新核验已耗尽本次总体期限，已暂停启动。" );
                    if (!mayHaveMutated)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (request.ExpiresAt <= DateTimeOffset.UtcNow)
                            return new(OperationStates.Expired, null, new("operation.expired", "操作在启动前已过期。"));
                    }
                    mayHaveMutated = true;
                    WindowsServiceScm.StartServiceChecked(target);
                    await WindowsServiceScm.WaitAsync(target, 4, watch, timeout);
                }
            }
            var readback = WindowsServiceScm.Read(target, name);
            var expected = proof.Result.Proposed!.Value.Deserialize<WindowsServiceConfig>(Protocol.Json)!;
            var immutableMatches = request.Type == OperationTypes.ServiceUpdateStartType
                ? readback.ImmutableHash == before.ImmutableHash : readback.ConfigurationHash == before.ConfigurationHash;
            if (!immutableMatches || readback.Public.State != expected.State || readback.Public.StartType != expected.StartType)
                return new(mayHaveMutated ? OperationStates.Partial : OperationStates.Failed, Public(readback.Public), new("service.readbackMismatch", "实际服务配置或状态与计划不符，请只读核实。"));
            return new(OperationStates.Succeeded, Public(readback.Public));
        }
        catch (OperationCanceledException) when (!mayHaveMutated) { throw; }
        catch (Exception exception)
        {
            var error = exception is OperationException operation ? new ApiError(operation.Code, operation.Message)
                : new ApiError("service.operationUnverified", "Windows 服务操作结果未能核实，请只读检查；不会自动重放或回滚。" );
            WindowsServiceScm.Evidence? after = null;
            try { if (WindowsServiceRules.ValidName(request.Target?.ServiceName)) after = WindowsServiceScm.Read(request.Target!.ServiceName!); }
            catch { /* Never disclose exception text containing service entry or environment data. */ }
            var state = !mayHaveMutated ? OperationStates.Failed : created || request.Type == OperationTypes.ServiceCreate && after is not null ||
                request.Type == OperationTypes.ServiceRestart && after?.Public.State == "Stopped" ||
                request.Type == OperationTypes.ServiceUpdateStartType && after is not null && before is not null && after.ConfigurationHash != before.ConfigurationHash
                ? OperationStates.Partial : OperationStates.Unknown;
            return new(state, after is null ? null : Public(after.Public), error);
        }
    }

    private ValidationProof Validate(OperationRequest request)
    {
        var proof = new ValidationProof();
        try
        {
            proof.Inventory = WindowsServiceScm.Inventory((_options.WindowsServices.ProtectedNames ?? []).Append(request.Target?.ServiceName ?? ""));
            var services = proof.Inventory.Select(x => x.Public).ToArray();
            var relevant = WindowsServiceRules.RelevantNames(request, services, _options.WindowsServices);
            var rights = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            bool Access(string name, string type)
            {
                var key = name + "|" + type;
                if (!rights.TryGetValue(key, out var allowed)) rights.Add(key, allowed = WindowsServiceScm.HasAccess(name, type));
                return allowed;
            }
            foreach (var service in services.Where(x => relevant.Contains(x.Name)))
                foreach (var type in OperationTypes.WindowsServices.Where(x => x != OperationTypes.ServiceCreate)) Access(service.Name, type);
            proof.Permissions = rights;
            var dependents = request.Type is OperationTypes.ServiceStop or OperationTypes.ServiceRestart &&
                services.Any(x => BackendRules.Equal(x.Name, request.Target?.ServiceName)) && Access(request.Target!.ServiceName!, request.Type)
                ? WindowsServiceScm.RunningDependents(request.Target!.ServiceName!) : [];
            var createAllowed = WindowsServiceScm.CanCreate();
            if (request.Type == OperationTypes.ServiceCreate && WindowsServiceRequestSafety.IsValid(request))
            {
                var p = request.Parameters.Deserialize<WindowsServiceCreateParameters>(Protocol.Json)!;
                if (WindowsServiceRules.CanCreate(_options.WindowsServices) && WindowsServiceRules.IsAllowed(request.Target!.ServiceName!, _options.WindowsServices))
                    try { proof.PathLease = WindowsServicePathLease.Acquire(p.ExecutablePath, _options.WindowsServices.AllowedExecutableRoots, WindowsServiceRules.ResolveAccount(request.Target!.ServiceName!, p.AccountName)); }
                    catch (OperationException ex) { proof.AdditionalErrors.Add(new(ex.Code, ex.Message)); }
            }
            proof.Result = WindowsServiceRules.Validate(request, services, _options.WindowsServices, new
            {
                configs = proof.Inventory.Where(x => relevant.Contains(x.Public.Name)).Select(x => new { name = x.Public.Name, x.ConfigurationHash, x.Controls }).ToArray(),
                permissions = rights.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToArray(), dependents, createAllowed,
                executable = proof.PathLease?.Evidence
            }, access: Access, createAccess: createAllowed);
            // Unknown ordinary-service configuration could conceal a dependency on the management channel or target.
            if (proof.Inventory.Any(x => x.Public.UnavailableReason is not null && x.ServiceType is 16 or 32))
                proof.AdditionalErrors.Add(new("service.inventoryIncomplete", "部分普通服务配置无法查询，不能完整核实依赖和保护范围。"));
            var protectedClosure = WindowsServiceRules.ProtectedClosure(services, _options.WindowsServices);
            if (services.Where(x => protectedClosure.Contains(x.Name)).Any(x => x.Dependencies.Any(dep => dep.StartsWith('+') || !services.Any(s => BackendRules.Equal(s.Name, dep)))))
                proof.AdditionalErrors.Add(new("service.protectionUnverified", "保护服务依赖了无法核实的服务或服务组，无法安全确定保护范围。"));
            if (dependents.Length != 0) proof.AdditionalErrors.Add(new("service.dependentsRunning", "存在运行中的从属服务，本操作不会级联停止。"));
            if (proof.AdditionalErrors.Count > 0) proof.Result = proof.Result with { Valid = false, Errors = [.. proof.Result.Errors, .. proof.AdditionalErrors] };
            return proof;
        }
        catch { proof.Dispose(); throw; }
    }

    private static JsonElement Public(WindowsServiceConfig service) => JsonSerializer.SerializeToElement(service, Protocol.Json);
    private void RecheckStartStage(OperationRequest request, ValidationProof before)
    {
        var inventory = WindowsServiceScm.Inventory((_options.WindowsServices.ProtectedNames ?? []).Append(request.Target.ServiceName!));
        var services = inventory.Select(x => x.Public).ToArray();
        var oldNames = WindowsServiceRules.RelevantNames(request, before.Inventory.Select(x => x.Public).ToArray(), _options.WindowsServices);
        var currentNames = WindowsServiceRules.RelevantNames(request, services, _options.WindowsServices);
        if (!oldNames.SetEquals(currentNames) || inventory.Any(x => x.Public.UnavailableReason is not null && (x.ServiceType is 16 or 32 || currentNames.Contains(x.Public.Name))))
            throw new OperationException("service.configurationChanged", "启动阶段的相关服务或保护范围发生变化，已暂停启动。" );
        if (inventory.FirstOrDefault(x => BackendRules.Equal(x.Public.Name, request.Target.ServiceName))?.Public.State != "Stopped")
            throw new OperationException("service.configurationChanged", "启动阶段目标已不再处于停止状态，已暂停启动。" );
        foreach (var old in before.Inventory.Where(x => oldNames.Contains(x.Public.Name)))
        {
            var current = inventory.FirstOrDefault(x => BackendRules.Equal(x.Public.Name, old.Public.Name));
            var target = BackendRules.Equal(old.Public.Name, request.Target.ServiceName);
            if (current is null || current.ConfigurationHash != old.ConfigurationHash || !target &&
                (current.Public.State != old.Public.State || current.Public.ProcessId != old.Public.ProcessId || current.Controls != old.Controls))
                throw new OperationException("service.configurationChanged", "启动阶段的配置、依赖状态或进程发生变化，已暂停启动。" );
        }
        foreach (var permission in before.Permissions)
        {
            var separator = permission.Key.LastIndexOf('|');
            if (WindowsServiceScm.HasAccess(permission.Key[..separator], permission.Key[(separator + 1)..]) != permission.Value)
                throw new OperationException("service.accessChanged", "启动阶段的实际服务操作权限发生变化，已暂停启动。" );
        }
        var stage = request with { Type = OperationTypes.ServiceStart, ExpectedFingerprint = null };
        var result = WindowsServiceRules.Validate(stage, services, _options.WindowsServices, access: WindowsServiceScm.HasAccess);
        if (!result.Valid) throw new OperationException("service.startStageRejected", "启动阶段的依赖、状态或权限预检未通过，已暂停启动。" );
    }
    private sealed class ValidationProof : IDisposable
    {
        internal WindowsServiceScm.Evidence[] Inventory { get; set; } = [];
        internal WindowsServicePathLease? PathLease { get; set; }
        internal Dictionary<string, bool> Permissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        internal List<ApiError> AdditionalErrors { get; } = [];
        internal ValidationResult Result { get; set; } = new(false, "", [], [], [], []);
        public void Dispose() => PathLease?.Dispose();
    }
}
