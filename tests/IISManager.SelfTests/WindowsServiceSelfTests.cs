using System.Text.Json;
using IISManager.Agent;
using IISManager.Agent.Backends;
using IISManager.Contracts;
using IISManager.Core;
using Microsoft.Extensions.Logging.Abstractions;

internal static class WindowsServiceSelfTests
{
    internal static readonly (string Name, Func<Task> Test)[] Cases =
    [
        ("service policy defaults to read-only", DefaultPolicyIsReadOnly),
        ("service creation explains missing configuration requirements", CreationRequirementsAreSpecific),
        ("service creation is read-only during validation and cannot overwrite", CreateAndDuplicate),
        ("service creation rejects unsafe paths accounts and dependencies", CreationGuards),
        ("unsafe service commands are rejected before durable admission", SecretsNeverReachLedger),
        ("service start-type changes preserve state and configuration", StartTypePreservesConfiguration),
        ("service controls reject disabled and unstable states", ControlStateGuards),
        ("service dependencies prevent cascading stop and disclose startup effects", DependencyGuards),
        ("management services and transitive dependencies remain protected", ProtectedDependencies),
        ("service configuration drift rejects a previous fingerprint", FingerprintDrift),
        ("operation router dispatches once and service duplicates share a receipt", RouterAndDeduplication),
        ("new service fields preserve legacy IIS JSON and canonical hashes", LegacyIisCompatibility)
    ];

    private static async Task DefaultPolicyIsReadOnly()
    {
        var backend = new SimulationWindowsServiceBackend(new AgentOptions(), [Service("T-Worker", "Stopped")]);
        var snapshot = await backend.GetSnapshotAsync();
        Require(!snapshot.CanCreate && snapshot.Services.All(s => !s.Managed && !s.CanStart && !s.CanUpdateStartType),
            "Empty service policy must expose read-only capabilities.");
        Reject(await backend.ValidateAsync(Request(OperationTypes.ServiceStart, "T-Worker")), "service.notAllowed");
        Equal("Stopped", (await backend.GetServiceAsync("T-Worker"))!.State, "Rejected control must preserve state.");
    }

    private static async Task CreationRequirementsAreSpecific()
    {
        var options = new AgentOptions();
        var backend = new SimulationWindowsServiceBackend(options, []);
        var initial = await backend.GetSnapshotAsync();
        Require(!initial.CanCreate && initial.CreationUnavailableReason?.Contains("AllowCreate") == true &&
            initial.CreationUnavailableReason.Contains("服务名称或前缀") && initial.CreationUnavailableReason.Contains("AllowedExecutableRoots"),
            "Default rejection must explain the independent missing requirements.");
        options.WindowsServices.AllowedNamePrefixes = ["T-"];
        var named = await backend.GetSnapshotAsync();
        Require(!named.CanCreate && named.CreationUnavailableReason?.Contains("服务名称或前缀") == false,
            "Configuring existing-service management must not implicitly allow creation.");
        options.WindowsServices.AllowCreate = true;
        var pathMissing = await backend.GetSnapshotAsync();
        Require(!pathMissing.CanCreate && pathMissing.CreationUnavailableReason?.Contains("AllowedExecutableRoots") == true &&
            !pathMissing.CreationUnavailableReason.Contains("AllowCreate）"), "Creation still requires a program directory.");
        options.WindowsServices.AllowedExecutableRoots = [@"C:\DemoServices"];
        options.WindowsServices.AllowedAccounts = [];
        var accountMissing = await backend.GetSnapshotAsync();
        Require(!accountMissing.CanCreate && accountMissing.CreationUnavailableReason?.Contains("运行账号") == true,
            "A missing account policy must be diagnosed without changing it.");
        options.WindowsServices.AllowedAccounts = ["VirtualAccount"];
        var ready = await backend.GetSnapshotAsync();
        Require(ready.CanCreate && ready.CreationUnavailableReason is null, "Complete simulated policy must allow the creation form.");
    }

    private static async Task CreateAndDuplicate()
    {
        using var fixture = new ServiceFixture();
        var request = Request(OperationTypes.ServiceCreate, "T-New", CreateParameters());
        var before = await fixture.Backend.GetSnapshotAsync();
        var validation = await fixture.Backend.ValidateAsync(request);
        Require(validation.Valid && validation.AffectedApplications.Length == 0 && validation.AffectedServices is not null,
            "Creation must validate with a separate service impact contract.");
        Require(await fixture.Backend.GetServiceAsync("T-New") is null, "Validation must not create a service.");
        Equal(before.Services.Length, (await fixture.Backend.GetSnapshotAsync()).Services.Length, "Validation must preserve inventory.");
        var result = await fixture.Backend.ExecuteAsync(request with { ExpectedFingerprint = validation.Fingerprint });
        Equal(OperationStates.Succeeded, result.State, "Validated simulated creation must succeed.");
        var created = await fixture.Backend.GetServiceAsync("t-new");
        Require(created is not null, "Service lookups must match names without case sensitivity.");
        Equal("Stopped", created!.State, "Creation must not start a service.");
        Equal(WindowsServiceStartTypes.Manual, created.StartType, "Creation must default to manual startup.");
        Equal(@"NT SERVICE\T-New", created.AccountName, "Virtual identity must belong to the exact created service.");
        Reject(await fixture.Backend.ValidateAsync(Request(OperationTypes.ServiceCreate, "t-new", CreateParameters())), "service.exists");
        foreach (var startType in new[] { WindowsServiceStartTypes.Automatic, WindowsServiceStartTypes.AutomaticDelayed })
        {
            var name = "T-New-" + startType;
            await Execute(fixture.Backend, Request(OperationTypes.ServiceCreate, name, CreateParameters() with { StartType = startType }));
            Equal("Stopped", (await fixture.Backend.GetServiceAsync(name))!.State, "Automatic registration must still leave the service stopped.");
        }
    }

    private static async Task CreationGuards()
    {
        using var fixture = new ServiceFixture([Service("T-Dependency", "Stopped")]);
        foreach (var executable in new[]
        {
            @"C:\Other\worker.exe", @"C:\DemoServices-Evil\worker.exe", @"C:\DemoServices\..\worker.exe",
            @"C:\DemoServices\.\worker.exe", @"\\host\share\worker.exe", @"worker.exe", @"C:\DemoServices\worker.cmd",
            @"C:\DemoServices\worker.exe:stream", @"C:\DemoServices\bad.\worker.exe"
        })
            Require(!(await fixture.Backend.ValidateAsync(Request(OperationTypes.ServiceCreate, "T-New",
                CreateParameters() with { ExecutablePath = executable }))).Valid, "Unsafe Windows executable paths must be rejected.");
        Reject(await fixture.Backend.ValidateAsync(Request(OperationTypes.ServiceCreate, "T-New",
            CreateParameters() with { AccountName = "LocalSystem" })), "service.accountNotAllowed");
        Require(!WindowsServiceRequestSafety.IsValid(Request(OperationTypes.ServiceCreate, "T-New",
            CreateParameters() with { AccountName = @".\UnpreparedAccount" })), "Password-based accounts must not be admitted.");
        foreach (var dependencies in new[] { new[] { "T-Missing" }, new[] { "T-Dependency", "t-dependency" }, new[] { "T-New" } })
            Require(!(await fixture.Backend.ValidateAsync(Request(OperationTypes.ServiceCreate, "T-New",
                CreateParameters() with { Dependencies = dependencies }))).Valid, "Missing, duplicate and self dependencies must be rejected.");
        fixture.Options.WindowsServices.AllowCreate = false;
        Reject(await fixture.Backend.ValidateAsync(Request(OperationTypes.ServiceCreate, "T-New", CreateParameters())), "service.createDisabled");
        fixture.Options.WindowsServices.AllowCreate = true;
        fixture.Options.WindowsServices.AllowedExecutableRoots = [];
        Reject(await fixture.Backend.ValidateAsync(Request(OperationTypes.ServiceCreate, "T-New", CreateParameters())), "service.createDisabled");
        foreach (var parameters in new[] { "{\"startType\":null}", "{\"startType\":\"Manual\",\"StartType\":\"Disabled\"}",
            "{\"startType\":\"Manual\",\"displayName\":\"Changed\"}" })
            Require(!WindowsServiceRequestSafety.IsValid(Request(OperationTypes.ServiceUpdateStartType, "T-Dependency", Json(parameters))),
                "Null, duplicate and unrelated update fields must be rejected before admission.");
        var mixedTarget = Request(OperationTypes.ServiceStart, "T-Dependency") with
            { Target = new OperationTarget { ServiceName = "T-Dependency", ApplicationPoolName = "OtherPool" } };
        Require(!WindowsServiceRequestSafety.IsValid(mixedTarget), "Service targets must not mix IIS resource names.");
    }

    private static async Task SecretsNeverReachLedger()
    {
        using var fixture = new ServiceFixture();
        await fixture.Coordinator.InitializeAsync();
        foreach (var arguments in new[]
        {
            new[] { "--password", "SYNTHETIC_TEST_VALUE" }, new[] { "--api-key=SYNTHETIC_TEST_VALUE" },
            new[] { "--connection-string=SYNTHETIC_TEST_VALUE" }, new[] { "--token=SYNTHETIC_TEST_VALUE" },
            new[] { "--pwd", "SYNTHETIC_TEST_VALUE" }, new[] { "--access-key=SYNTHETIC_TEST_VALUE" },
            new[] { "https://example.invalid/?sig=SYNTHETIC_TEST_VALUE" },
            new[] { "https://example.invalid/?signature=SYNTHETIC_TEST_VALUE" },
            new[] { "https://example.invalid/?auth=SYNTHETIC_TEST_VALUE" },
            new[] { "https://example.invalid/?code=SYNTHETIC_TEST_VALUE" },
            Enumerable.Repeat(new string('x', 2048), 32).ToArray(),
            Enumerable.Repeat(new string('\\', 2048), 8).ToArray()
        })
        {
            var request = Request(OperationTypes.ServiceCreate, "T-New", CreateParameters() with { Arguments = arguments });
            Require(!WindowsServiceRequestSafety.IsValid(request), "Secret or oversized command arguments must fail shared admission checks.");
            await RejectEnvelope(() => fixture.Coordinator.ValidateAsync(request));
            await RejectEnvelope(() => fixture.Coordinator.SubmitAsync(request with { ExpectedFingerprint = new string('a', 64) }));
        }
        Equal(0, (await fixture.Store.ReadAllAsync()).Length, "Rejected unsafe requests must not enter the operation ledger.");
        Require(!Directory.EnumerateFiles(fixture.Directory, "*.json").Any(), "No unsafe command task file may be written.");
        Require(await fixture.Backend.GetServiceAsync("T-New") is null, "Rejected unsafe admission must not mutate services.");
    }

    private static async Task StartTypePreservesConfiguration()
    {
        var original = Service("T-Disabled", "Stopped", WindowsServiceStartTypes.Disabled) with
        { DisplayName = "Business worker", Description = "Retain this description", Dependencies = ["T-Dependency"] };
        using var fixture = new ServiceFixture([original, Service("T-Dependency", "Running")]);
        Reject(await fixture.Backend.ValidateAsync(Request(OperationTypes.ServiceStart, original.Name)), "service.disabled");
        foreach (var startType in new[] { WindowsServiceStartTypes.Manual, WindowsServiceStartTypes.Automatic,
            WindowsServiceStartTypes.AutomaticDelayed, WindowsServiceStartTypes.Disabled })
        {
            await Execute(fixture.Backend, Request(OperationTypes.ServiceUpdateStartType, original.Name,
                new WindowsServiceStartTypeParameters { StartType = startType }));
            var current = (await fixture.Backend.GetServiceAsync(original.Name))!;
            Equal(startType, current.StartType, "Requested startup type must be read back.");
            Equal(original.State, current.State, "Startup configuration must not start a stopped service.");
            Equal(original.ProcessId, current.ProcessId, "Startup configuration must preserve the process state.");
            Equal(original.DisplayName, current.DisplayName, "Startup configuration must preserve the display name.");
            Equal(original.Description, current.Description, "Startup configuration must preserve the description.");
            Equal(original.ExecutablePath, current.ExecutablePath, "Startup configuration must preserve the executable.");
            Equal(original.AccountName, current.AccountName, "Startup configuration must preserve the account.");
            Require(original.Dependencies.SequenceEqual(current.Dependencies), "Startup configuration must preserve dependencies.");
        }
        using var running = new ServiceFixture([Service("T-Running", "Running")]);
        await Execute(running.Backend, Request(OperationTypes.ServiceUpdateStartType, "T-Running",
            new WindowsServiceStartTypeParameters { StartType = WindowsServiceStartTypes.Disabled }));
        Equal("Running", (await running.Backend.GetServiceAsync("T-Running"))!.State, "Disabling startup must not stop a running service.");
    }

    private static async Task ControlStateGuards()
    {
        using var fixture = new ServiceFixture([Service("T-Worker", "Running"), Service("T-Disabled", "Stopped", WindowsServiceStartTypes.Disabled)]);
        await Execute(fixture.Backend, Request(OperationTypes.ServiceStop, "T-Worker"));
        Equal("Stopped", (await fixture.Backend.GetServiceAsync("T-Worker"))!.State, "Stop must read back stopped.");
        Reject(await fixture.Backend.ValidateAsync(Request(OperationTypes.ServiceRestart, "T-Worker")), "service.notRunning");
        await Execute(fixture.Backend, Request(OperationTypes.ServiceStart, "T-Worker"));
        await Execute(fixture.Backend, Request(OperationTypes.ServiceRestart, "T-Worker"));
        Equal("Running", (await fixture.Backend.GetServiceAsync("T-Worker"))!.State, "Restart must read back running.");
        Reject(await fixture.Backend.ValidateAsync(Request(OperationTypes.ServiceStart, "T-Disabled")), "service.disabled");
        foreach (var state in new[] { "StartPending", "StopPending", "ContinuePending", "PausePending", "Paused", "Unknown" })
        {
            using var unstable = new ServiceFixture([Service("T-Unstable", state)]);
            foreach (var type in new[] { OperationTypes.ServiceStart, OperationTypes.ServiceStop, OperationTypes.ServiceRestart })
                Reject(await unstable.Backend.ValidateAsync(Request(type, "T-Unstable")), "service.transitioning");
            Reject(await unstable.Backend.ValidateAsync(Request(OperationTypes.ServiceUpdateStartType, "T-Unstable",
                new WindowsServiceStartTypeParameters { StartType = WindowsServiceStartTypes.Manual })), "service.transitioning");
        }
    }

    private static async Task DependencyGuards()
    {
        using var fixture = new ServiceFixture([Service("T-Database", "Running"),
            Service("T-Dependent", "Running") with { Dependencies = ["T-Database"] }]);
        foreach (var type in new[] { OperationTypes.ServiceStop, OperationTypes.ServiceRestart })
        {
            var validation = await fixture.Backend.ValidateAsync(Request(type, "T-Database"));
            Reject(validation, "service.dependentsRunning");
            Require(validation.AffectedServices!.Contains("T-Dependent"), "Rejected stop must disclose the running dependency impact.");
        }
        using var startup = new ServiceFixture([Service("T-Dependency", "Stopped"),
            Service("T-Worker", "Stopped") with { Dependencies = ["T-Dependency"] }]);
        var request = Request(OperationTypes.ServiceStart, "T-Worker");
        var check = await startup.Backend.ValidateAsync(request);
        Require(check.Valid && check.AffectedServices!.Contains("T-Dependency"), "Start must disclose stopped dependencies that it affects.");
        await Execute(startup.Backend, request);
        Equal("Running", (await startup.Backend.GetServiceAsync("T-Dependency"))!.State, "SCM dependency startup must be represented in simulation.");
        startup.Options.WindowsServices.AllowedNames = ["T-Worker"];
        startup.Options.WindowsServices.AllowedNamePrefixes = [];
        await Execute(startup.Backend, Request(OperationTypes.ServiceStop, "T-Worker"));
        // A running dependency does not need another mutation; a stopped one would.
        Require((await startup.Backend.ValidateAsync(Request(OperationTypes.ServiceStart, "T-Worker"))).Valid,
            "A running dependency outside the write policy must not block an otherwise safe start.");
        using var restricted = new ServiceFixture([Service("OutsideDependency", "Stopped"),
            Service("T-Worker", "Stopped") with { Dependencies = ["OutsideDependency"] }]);
        Reject(await restricted.Backend.ValidateAsync(Request(OperationTypes.ServiceStart, "T-Worker")), "service.dependencyNotAllowed");
    }

    private static async Task ProtectedDependencies()
    {
        using var fixture = new ServiceFixture([
            Service("IISManager.Agent", "Running") with { Dependencies = ["T-Middle"] },
            Service("IISManager.Console", "Running"), Service("T-Middle", "Running") with { Dependencies = ["T-Root"] },
            Service("T-Root", "Running"), Service("T-Critical", "Running") with { Dependencies = ["T-CriticalDependency"] },
            Service("T-CriticalDependency", "Running")
        ]);
        fixture.Options.WindowsServices.AllowedNames = ["IISManager.Agent", "IISManager.Console"];
        fixture.Options.WindowsServices.ProtectedNames = ["T-Critical"];
        foreach (var name in new[] { "IISManager.Agent", "IISManager.Console", "T-Middle", "T-Root", "T-Critical", "T-CriticalDependency" })
        {
            Reject(await fixture.Backend.ValidateAsync(Request(OperationTypes.ServiceStop, name)), "service.protected");
            Reject(await fixture.Backend.ValidateAsync(Request(OperationTypes.ServiceUpdateStartType, name,
                new WindowsServiceStartTypeParameters { StartType = WindowsServiceStartTypes.Disabled })), "service.protected");
            Require(!(await fixture.Backend.GetServiceAsync(name))!.Managed, "Protected services cannot be exposed as managed resources.");
        }
    }

    private static async Task FingerprintDrift()
    {
        using var fixture = new ServiceFixture([Service("T-Worker", "Stopped")]);
        var request = Request(OperationTypes.ServiceStart, "T-Worker");
        var check = await fixture.Backend.ValidateAsync(request);
        Require(check.Valid, "Original start must validate.");
        await Execute(fixture.Backend, Request(OperationTypes.ServiceUpdateStartType, "T-Worker",
            new WindowsServiceStartTypeParameters { StartType = WindowsServiceStartTypes.Automatic }));
        var stale = await fixture.Backend.ExecuteAsync(request with { ExpectedFingerprint = check.Fingerprint });
        Equal(OperationStates.Failed, stale.State, "Old fingerprint must prevent execution after configuration drift.");
        Equal("Stopped", (await fixture.Backend.GetServiceAsync("T-Worker"))!.State, "Fingerprint rejection must not start the service.");
        var policyCheck = await fixture.Backend.ValidateAsync(request);
        fixture.Options.WindowsServices.AllowedAccounts = ["LocalService"];
        Reject(await fixture.Backend.ValidateAsync(request with { ExpectedFingerprint = policyCheck.Fingerprint }), "configuration.conflict");
    }

    private static async Task RouterAndDeduplication()
    {
        using var fixture = new ServiceFixture([Service("T-Worker", "Stopped")]);
        var iis = new FakeBackend();
        var counted = new CountingServiceBackend(fixture.Backend);
        var router = new OperationBackendRouter(iis, counted);
        var coordinator = new OperationCoordinator(router, fixture.Store, NullLogger<OperationCoordinator>.Instance);
        await coordinator.InitializeAsync();
        var request = Request(OperationTypes.ServiceStart, "T-Worker");
        var validation = await coordinator.ValidateAsync(request);
        request = request with { ExpectedFingerprint = validation.Fingerprint };
        var accepted = await coordinator.SubmitAsync(request);
        var duplicate = await coordinator.SubmitAsync(request);
        Equal(accepted.AcceptedAt, duplicate.AcceptedAt, "Duplicate service intent must reuse its original receipt.");
        var final = await RunUntilFinal(coordinator, request.OperationId);
        Equal(OperationStates.Succeeded, final.State, "Routed service operation must complete.");
        Equal(1, counted.Executions, "Duplicate service operation must execute once.");
        Equal(0, iis.ExecutionCount, "Service operations must not reach the IIS adapter.");
        Equal(accepted.AcceptedAt, (await coordinator.SubmitAsync(request)).AcceptedAt, "Completed duplicate must reuse its original receipt.");
        await RejectConflict(() => coordinator.SubmitAsync(request with { Type = OperationTypes.ServiceStop }));
        var iisRequest = new OperationRequest { OperationId = Guid.NewGuid(), Type = OperationTypes.Start,
            Target = new OperationTarget { ApplicationPoolName = "TestPool" }, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2) };
        await router.ValidateAsync(iisRequest);
        await router.ExecuteAsync(iisRequest);
        Equal(1, iis.ExecutionCount, "IIS actions must still reach the IIS adapter.");
        Equal(1, counted.Executions, "IIS actions must not reach the service adapter.");
        try { await router.ValidateAsync(request with { Type = "unknown.action" }); }
        catch (OperationException exception) when (exception.Code == "OPERATION_UNSUPPORTED") { return; }
        throw new TestFailure("Unknown operation types must be rejected by the router.");
    }

    private static Task LegacyIisCompatibility()
    {
        var request = new OperationRequest { OperationId = Guid.Parse("11111111-1111-1111-1111-111111111111"), Type = OperationTypes.Start,
            Target = new OperationTarget { ApplicationPoolName = "LegacyPool" }, ExpiresAt = DateTimeOffset.Parse("2026-10-07T00:00:00Z") };
        var legacy = new { request.OperationId, request.Type, Target = new { ApplicationPoolName = "LegacyPool", SiteName = (string?)null },
            request.Parameters, request.ExpectedFingerprint, request.ExpiresAt };
        Equal(JsonSerializer.Serialize(legacy, Protocol.Json), JsonSerializer.Serialize(request, Protocol.Json), "Legacy IIS request JSON must remain byte-compatible.");
        Equal(CanonicalJson.Hash(legacy), CanonicalJson.Hash(request), "Legacy IIS request hash must remain compatible with stored records.");
        var validation = new ValidationResult(true, new string('a', 64), [], [], [], []);
        var legacyValidation = new { validation.Valid, validation.Fingerprint, validation.AffectedApplications, validation.Changes,
            validation.Warnings, validation.Errors, validation.Before, validation.Proposed };
        Equal(JsonSerializer.Serialize(legacyValidation, Protocol.Json), JsonSerializer.Serialize(validation, Protocol.Json),
            "Null service impact must not add fields to legacy IIS validation JSON.");
        var serviceJson = JsonSerializer.Serialize(validation with { AffectedServices = ["T-Worker"] }, Protocol.Json);
        Require(serviceJson.Contains("affectedServices", StringComparison.Ordinal), "Service impacts must serialize when present.");
        return Task.CompletedTask;
    }

    private static WindowsServiceConfig Service(string name, string state, string startType = WindowsServiceStartTypes.Manual) => new()
    {
        Name = name, DisplayName = name, Description = "Synthetic service", State = state, StartType = startType,
        AccountName = @"NT AUTHORITY\LocalService", ExecutablePath = @"C:\DemoServices\worker.exe", ProcessId = state == "Running" ? 42u : 0u
    };
    private static WindowsServiceCreateParameters CreateParameters() => new()
    { DisplayName = "Synthetic new worker", Description = "Non-production test", ExecutablePath = @"C:\DemoServices\worker.exe", Arguments = ["--environment", "Production"] };
    private static OperationRequest Request(string type, string name, object? parameters = null) => new()
    {
        OperationId = Guid.NewGuid(), Type = type, Target = new OperationTarget { ServiceName = name },
        Parameters = parameters is JsonElement json ? json : JsonSerializer.SerializeToElement(parameters ?? new { }, Protocol.Json),
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2)
    };
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static void Require(bool condition, string message) { if (!condition) throw new TestFailure(message); }
    private static void Equal<T>(T expected, T actual, string message) => Require(EqualityComparer<T>.Default.Equals(expected, actual), message);
    private static void Reject(ValidationResult result, string code) => Require(!result.Valid && result.Errors.Any(e => e.Code == code), "Expected service validation rejection: " + code);
    private static async Task Execute(IWindowsServiceBackend backend, OperationRequest request)
    {
        var validation = await backend.ValidateAsync(request);
        Require(validation.Valid, "Simulation operation must pass validation.");
        Equal(OperationStates.Succeeded, (await backend.ExecuteAsync(request with { ExpectedFingerprint = validation.Fingerprint })).State,
            "Simulation operation must complete with independent readback.");
    }
    private static async Task RejectEnvelope<T>(Func<Task<T>> action)
    {
        try { await action(); }
        catch (OperationException) { return; }
        throw new TestFailure("Unsafe service envelopes must fail before admission.");
    }
    private static async Task RejectConflict(Func<Task<OperationRecord>> action)
    {
        try { await action(); }
        catch (OperationException exception) when (exception.Code == "OPERATION_ID_CONFLICT") { return; }
        throw new TestFailure("A service operation ID must not be reused for a different action.");
    }
    private static async Task<OperationRecord> RunUntilFinal(OperationCoordinator coordinator, Guid id)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runner = coordinator.RunAsync(cancellation.Token);
        try
        {
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var record = await coordinator.GetAsync(id, cancellation.Token);
                if (record is not null && OperationStates.IsFinal(record.State)) return record;
                await Task.Delay(5, cancellation.Token);
            }
        }
        finally { cancellation.Cancel(); try { await runner; } catch (OperationCanceledException) { } }
    }

    private sealed class ServiceFixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetFullPath("work"), "services-selftest-" + Guid.NewGuid().ToString("N"));
        internal AgentOptions Options { get; } = new() { Mode = "Simulation", WindowsServices = new()
        { AllowedNamePrefixes = ["T-"], AllowCreate = true, AllowedExecutableRoots = [@"C:\DemoServices"] } };
        internal SimulationWindowsServiceBackend Backend { get; }
        internal FileOperationStore Store { get; }
        internal OperationCoordinator Coordinator { get; }
        internal ServiceFixture(IEnumerable<WindowsServiceConfig>? seed = null)
        {
            System.IO.Directory.CreateDirectory(Directory);
            Backend = new SimulationWindowsServiceBackend(Options, seed ?? []);
            Store = new FileOperationStore(Directory);
            Coordinator = new OperationCoordinator(Backend, Store, NullLogger<OperationCoordinator>.Instance);
        }
        public void Dispose() { Store.Dispose(); System.IO.Directory.Delete(Directory, recursive: true); }
    }

    private sealed class CountingServiceBackend(IWindowsServiceBackend inner) : IWindowsServiceBackend
    {
        internal int Executions { get; private set; }
        public Task<WindowsServiceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => inner.GetSnapshotAsync(cancellationToken);
        public Task<WindowsServiceConfig?> GetServiceAsync(string name, CancellationToken cancellationToken = default) => inner.GetServiceAsync(name, cancellationToken);
        public Task<ValidationResult> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default) => inner.ValidateAsync(request, cancellationToken);
        public Task<ExecutionResult> ExecuteAsync(OperationRequest request, CancellationToken cancellationToken = default)
        { Executions++; return inner.ExecuteAsync(request, cancellationToken); }
    }
}
