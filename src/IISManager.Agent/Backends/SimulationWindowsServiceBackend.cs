using System.Text.Json;
using IISManager.Contracts;
using IISManager.Core;

namespace IISManager.Agent.Backends;

/// <summary>In-memory demo; Program refuses this backend outside Development.</summary>
public sealed class SimulationWindowsServiceBackend : IWindowsServiceBackend
{
    private readonly AgentOptions _options;
    private readonly List<WindowsServiceConfig> _services;
    private readonly object _gate = new();
    private long _generation;

    public SimulationWindowsServiceBackend(AgentOptions options, IEnumerable<WindowsServiceConfig>? seed = null)
    {
        _options = options;
        _services = (seed ?? DefaultServices()).ToList();
    }

    public Task<WindowsServiceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(WindowsServiceRules.Project(_services.ToArray(), _options.WindowsServices));
    }

    public Task<WindowsServiceConfig?> GetServiceAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(WindowsServiceRules.Project(_services.ToArray(), _options.WindowsServices).Services
            .FirstOrDefault(x => BackendRules.Equal(x.Name, name)));
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
            if (request.ExpiresAt <= DateTimeOffset.UtcNow) return Task.FromResult(new ExecutionResult(OperationStates.Expired, null, new("operation.expired", "操作已过期。")));
            if (string.IsNullOrEmpty(request.ExpectedFingerprint)) return Task.FromResult(new ExecutionResult(OperationStates.Failed, null, new("configuration.fingerprintRequired", "执行必须携带最新预检指纹。")));
            var validation = Validate(request);
            if (!validation.Valid) return Task.FromResult(new ExecutionResult(OperationStates.Failed, null, validation.Errors[0]));
            var proposed = validation.Proposed!.Value.Deserialize<WindowsServiceConfig>(Protocol.Json)!;
            if (request.Type == OperationTypes.ServiceCreate) _services.Add(proposed);
            else
            {
                var index = _services.FindIndex(x => BackendRules.Equal(x.Name, request.Target.ServiceName));
                if (request.Type is OperationTypes.ServiceStart or OperationTypes.ServiceRestart)
                    StartDependencies(_services[index]);
                _services[index] = proposed;
            }
            _generation++;
            var after = WindowsServiceRules.Project(_services.ToArray(), _options.WindowsServices).Services.First(x => BackendRules.Equal(x.Name, proposed.Name));
            return Task.FromResult(new ExecutionResult(OperationStates.Succeeded, JsonSerializer.SerializeToElement(after, Protocol.Json)));
        }
    }

    private ValidationResult Validate(OperationRequest request)
    {
        var value = WindowsServiceRules.Validate(request, _services.ToArray(), _options.WindowsServices, new { mode = "SIMULATION", generation = _generation },
            (p, errors) =>
            {
                if (!WindowsServiceRules.WindowsExecutableAllowed(p.ExecutablePath, _options.WindowsServices.AllowedExecutableRoots))
                    errors.Add(new("service.pathNotAllowed", "模拟创建也须使用允许根目录内的本机 Windows 绝对 EXE 路径。"));
            });
        return value with { Warnings = value.Warnings.Append("SIMULATION：仅修改内存演示数据；未注册 Windows 服务，未验证真实程序、身份、权限或服务协议。" ).ToArray() };
    }

    private void StartDependencies(WindowsServiceConfig service)
    {
        foreach (var name in service.Dependencies)
        {
            var index = _services.FindIndex(x => BackendRules.Equal(x.Name, name));
            if (index < 0 || _services[index].State == "Running") continue;
            StartDependencies(_services[index]);
            _services[index] = _services[index] with { State = "Running" };
        }
    }

    private static IEnumerable<WindowsServiceConfig> DefaultServices()
    {
        static WindowsServiceConfig Service(string name, string state, string start = WindowsServiceStartTypes.Manual, string[]? dependencies = null) => new()
        {
            Name = name, DisplayName = name, Description = "SIMULATION 演示服务", State = state, StartType = start,
            AccountName = @"NT AUTHORITY\LocalService", ExecutablePath = @"C:\DemoServices\Worker.exe", Dependencies = dependencies ?? []
        };
        return [Service("SIMULATION-Worker", "Running"), Service("SIMULATION-Job", "Stopped"),
            Service("SIMULATION-Disabled", "Stopped", WindowsServiceStartTypes.Disabled), Service("SIMULATION-Database", "Running"),
            Service("SIMULATION-Dependent", "Running", dependencies: ["SIMULATION-Database"]),
            Service("IISManager.Agent", "Running", WindowsServiceStartTypes.Automatic, ["SIMULATION-Database"]),
            Service("IISManager.Console", "Running", WindowsServiceStartTypes.Automatic)];
    }
}
