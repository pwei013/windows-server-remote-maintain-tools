using System.Text.Json;
using IISManager.Agent;
using IISManager.Agent.Backends;
using IISManager.Contracts;
using IISManager.Core;
using Microsoft.Extensions.Logging.Abstractions;

var poolTests = new (string Name, Func<Task> Test)[]
{
    ("pool patch preserves unspecified settings and applications", PoolPatchPreservesConfiguration),
    ("runtime settings require a stopped pool", RuntimeChangesRequireStop),
    ("pool patch rejects null and unknown fields", InvalidPoolPatch)
}.Concat(PoolCreationSelfTests.Cases).ToArray();
var tests = args is ["--pools-only"] ? poolTests : args is ["--pool-creation-only"] ? PoolCreationSelfTests.Cases : args is ["--site-maintenance-only"] ? SiteMaintenanceSelfTests.Cases : args is ["--site-lifecycle-only"] ? SiteLifecycleSelfTests.Cases : new (string Name, Func<Task> Test)[]
{
    ("canonical hash ignores JSON property order", CanonicalHash),
    ("same operation ID returns existing record", DuplicateRequest),
    ("concurrent duplicate submissions execute once", ConcurrentDuplicateRequests),
    ("same operation ID with other parameters conflicts", ConflictingRequest),
    ("late duplicate returns the existing record", LateDuplicateRequest),
    ("expired queued operation cannot execute", ExpiredRequest),
    ("expiry during validation prevents execution", ExpiryDuringValidation),
    ("changed fingerprint cannot execute", ChangedFingerprint),
    ("running record is persisted before execution", PersistBeforeExecution),
    ("all IIS writes run serially", SerializedWrites),
    ("restart marks unfinished records unknown without replay", RestartDoesNotReplay),
    ("corrupt store stops execution", CorruptStoreFailsClosed),
    ("ledger admits only one owner", ExclusiveLedgerOwnership),
    ("ledger rejects a filename and operation ID mismatch", LedgerIdentifierMismatch),
    ("query detects damaged ledger and disables new writes", QueryStorageFailure),
    ("storage failure while queued disables new writes", QueuedStorageFailure),
    ("exception after crossing execution boundary remains unknown", ExecutionExceptionIsUnknown),
    ("partial backend result remains partial", PartialResultIsPreserved),
    ("pool patch preserves unspecified settings and applications", PoolPatchPreservesConfiguration),
    ("runtime settings require a stopped pool", RuntimeChangesRequireStop),
    ("pool patch rejects null and unknown fields", InvalidPoolPatch),
    ("reserved ports and overlapping site bindings are rejected", SiteBindingGuards),
    ("validation leaves simulation state unchanged", ValidationIsReadOnly),
    ("topology keeps the legacy protocol and JSON shapes", LegacyReadContractsUnchanged),
    ("topology contracts retain application and directory ownership", TopologyContractsPreserveOwnership),
    ("topology contracts expose only safe display fields", TopologyContractsUseWhitelist),
    ("simulation topology exposes roots and cross-site pool associations", SimulationTopologyRoots),
    ("topology reads preserve operation fingerprints and simulation writes", TopologyReadsPreserveOperationValidation)
}.Concat(PoolCreationSelfTests.Cases).Concat(SiteLifecycleSelfTests.Cases).Concat(WindowsServiceSelfTests.Cases).Concat(SiteMaintenanceSelfTests.Cases).ToArray();

var failures = 0;
foreach (var (name, test) in tests)
{
    try
    {
        await test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        // Only test assertions and exception type are printed; backend/configuration details stay private.
        Console.Error.WriteLine($"FAIL {name}: {ex.GetType().Name}");
        if (ex is TestFailure) Console.Error.WriteLine(ex.Message);
    }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static Task CanonicalHash()
{
    var request = NewRequest("{\"autoStart\":true,\"nested\":{\"a\":1,\"b\":2}}");
    var reordered = request with { Parameters = Json("{\"nested\":{\"b\":2,\"a\":1},\"autoStart\":true}") };
    Equal(CanonicalJson.Hash(request), CanonicalJson.Hash(reordered), "Equivalent JSON must have the same hash.");
    var changed = request with { Parameters = Json("{\"autoStart\":false,\"nested\":{\"a\":1,\"b\":2}}") };
    Assert(CanonicalJson.Hash(request) != CanonicalJson.Hash(changed), "Changed values must change the hash.");
    return Task.CompletedTask;
}

static async Task DuplicateRequest()
{
    using var fixture = new Fixture();
    await fixture.Coordinator.InitializeAsync();
    var request = NewRequest("{\"autoStart\":true,\"idleTimeoutMinutes\":0}");
    var original = await fixture.Coordinator.SubmitAsync(request);
    var existing = await fixture.Coordinator.SubmitAsync(request with
    {
        Parameters = Json("{\"idleTimeoutMinutes\":0,\"autoStart\":true}")
    });
    Equal(original.Request.OperationId, existing.Request.OperationId, "A duplicate must retain its ID.");
    Equal(original.AcceptedAt, existing.AcceptedAt, "A duplicate must retain its original receipt.");
    await fixture.RunUntil(request.OperationId, OperationStates.Succeeded);
    Equal(1, fixture.Backend.ExecutionCount, "A duplicate must execute once.");
}

static async Task ConflictingRequest()
{
    using var fixture = new Fixture();
    await fixture.Coordinator.InitializeAsync();
    var request = NewRequest("{\"autoStart\":true}");
    await fixture.Coordinator.SubmitAsync(request);
    try
    {
        await fixture.Coordinator.SubmitAsync(request with { Parameters = Json("{\"autoStart\":false}") });
        throw new TestFailure("Reusing the operation ID with different parameters must conflict.");
    }
    catch (OperationException ex)
    {
        Equal(409, ex.StatusCode, "Conflicting request must use HTTP 409.");
    }
    Equal(0, fixture.Backend.ExecutionCount, "Submitting requests must not execute IIS writes.");
}

static async Task ConcurrentDuplicateRequests()
{
    using var fixture = new Fixture();
    await fixture.Coordinator.InitializeAsync();
    var request = NewRequest();
    var records = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Coordinator.SubmitAsync(request)));
    Assert(records.All(record => record.AcceptedAt == records[0].AcceptedAt), "Concurrent duplicates must share one receipt.");
    await fixture.RunUntil(request.OperationId, OperationStates.Succeeded);
    Equal(1, fixture.Backend.ExecutionCount, "Concurrent duplicates must execute once.");
}

static async Task LateDuplicateRequest()
{
    var clock = new TestClock(DateTimeOffset.UtcNow);
    using var fixture = new Fixture(clock);
    await fixture.Coordinator.InitializeAsync();
    var request = NewRequest() with { ExpiresAt = clock.GetUtcNow().AddSeconds(5) };
    var accepted = await fixture.Coordinator.SubmitAsync(request);
    clock.Advance(TimeSpan.FromSeconds(6));
    var duplicate = await fixture.Coordinator.SubmitAsync(request);
    Equal(accepted.AcceptedAt, duplicate.AcceptedAt, "Past-deadline duplicate must return the original receipt.");
    await fixture.RunUntil(request.OperationId, OperationStates.Expired);
    Equal(0, fixture.Backend.ExecutionCount, "Late duplicates must not renew execution permission.");
}

static async Task ExpiredRequest()
{
    var clock = new TestClock(DateTimeOffset.UtcNow);
    using var fixture = new Fixture(clock);
    await fixture.Coordinator.InitializeAsync();
    var request = NewRequest() with { ExpiresAt = clock.GetUtcNow().AddSeconds(5) };
    await fixture.Coordinator.SubmitAsync(request);
    clock.Advance(TimeSpan.FromSeconds(6));
    await fixture.RunUntil(request.OperationId, OperationStates.Expired);
    Equal(0, fixture.Backend.ExecutionCount, "An expired queued operation must not execute.");
}

static async Task ExpiryDuringValidation()
{
    var clock = new TestClock(DateTimeOffset.UtcNow);
    using var fixture = new Fixture(clock);
    await fixture.Coordinator.InitializeAsync();
    var request = NewRequest() with { ExpiresAt = clock.GetUtcNow().AddSeconds(5) };
    await fixture.Coordinator.SubmitAsync(request);
    fixture.Backend.BeforeValidation = () => clock.Advance(TimeSpan.FromSeconds(6));
    await fixture.RunUntil(request.OperationId, OperationStates.Expired);
    Equal(0, fixture.Backend.ExecutionCount, "Validation that crosses the deadline must prevent IIS execution.");
}

static async Task ChangedFingerprint()
{
    using var fixture = new Fixture();
    await fixture.Coordinator.InitializeAsync();
    var request = NewRequest();
    await fixture.Coordinator.SubmitAsync(request);
    fixture.Backend.Fingerprint = "changed-by-another-manager";
    var record = await fixture.RunUntilFinal(request.OperationId);
    Assert(record.State != OperationStates.Succeeded, "Fingerprint drift must not succeed.");
    Equal(0, fixture.Backend.ExecutionCount, "Fingerprint drift must prevent execution.");
}

static async Task PersistBeforeExecution()
{
    using var fixture = new Fixture();
    await fixture.Coordinator.InitializeAsync();
    var request = NewRequest();
    var diskChecked = false;
    fixture.Backend.BeforeExecute = async operation =>
    {
        var record = await fixture.Store.GetAsync(operation.OperationId);
        Assert(record is not null, "A receipt must already exist before execution.");
        Equal(OperationStates.Running, record!.State, "The running state must be saved before execution.");
        Assert(record.StartedAt is not null, "The start timestamp must be persisted.");
        diskChecked = true;
    };
    await fixture.Coordinator.SubmitAsync(request);
    await fixture.RunUntil(request.OperationId, OperationStates.Succeeded);
    Assert(diskChecked, "The backend must inspect persisted state before execution.");
}

static async Task SerializedWrites()
{
    using var fixture = new Fixture();
    await fixture.Coordinator.InitializeAsync();
    fixture.Backend.Delay = TimeSpan.FromMilliseconds(20);
    var requests = Enumerable.Range(0, 8).Select(_ => NewRequest()).ToArray();
    await Task.WhenAll(requests.Select(request => fixture.Coordinator.SubmitAsync(request)));
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var runner = fixture.Coordinator.RunAsync(cancellation.Token);
    try
    {
        foreach (var request in requests)
            await fixture.WaitForFinal(request.OperationId, cancellation.Token);
        Equal(8, fixture.Backend.ExecutionCount, "All accepted operations must execute.");
        Equal(1, fixture.Backend.MaximumConcurrentExecutions, "The Agent must serialize all IIS writes.");
    }
    finally
    {
        cancellation.Cancel();
        await IgnoreCancellation(runner);
    }
}

static async Task RestartDoesNotReplay()
{
    using var fixture = new Fixture();
    var queued = NewRequest();
    var running = NewRequest();
    await fixture.Store.SaveAsync(Record(queued, OperationStates.Queued));
    await fixture.Store.SaveAsync(Record(running, OperationStates.Running));
    await fixture.Coordinator.InitializeAsync();
    foreach (var request in new[] { queued, running })
    {
        var recovered = await fixture.Coordinator.GetAsync(request.OperationId);
        Assert(recovered is not null, "Recovery must retain the original operation record.");
        Equal(OperationStates.Unknown, recovered!.State, "Unfinished operations must become unknown after restart.");
    }
    using var cancellation = new CancellationTokenSource();
    var runner = fixture.Coordinator.RunAsync(cancellation.Token);
    cancellation.Cancel();
    await IgnoreCancellation(runner);
    Equal(0, fixture.Backend.ExecutionCount, "Restart must not replay unfinished operations.");
}

static async Task CorruptStoreFailsClosed()
{
    using var fixture = new Fixture();
    await File.WriteAllTextAsync(Path.Combine(fixture.Directory, $"{Guid.NewGuid():D}.json"), "{invalid-json");
    var refused = false;
    try { await fixture.Coordinator.InitializeAsync(); }
    catch { refused = true; }
    if (!refused)
    {
        try { await fixture.Coordinator.SubmitAsync(NewRequest()); }
        catch { refused = true; }
    }
    Assert(refused, "Corrupt persisted state must disable acceptance of new writes.");
    Equal(0, fixture.Backend.ExecutionCount, "Corrupt persisted state must not cause writes.");
}

static Task ExclusiveLedgerOwnership()
{
    using var fixture = new Fixture();
    var rejected = false;
    try { using var secondOwner = new FileOperationStore(fixture.Directory); }
    catch (IOException) { rejected = true; }
    Assert(rejected, "The same ledger must not have two owners.");
    return Task.CompletedTask;
}

static async Task LedgerIdentifierMismatch()
{
    using var fixture = new Fixture();
    var request = NewRequest();
    await fixture.Store.SaveAsync(Record(request, OperationStates.Queued));
    var wrongId = Guid.NewGuid();
    File.Copy(Path.Combine(fixture.Directory, $"{request.OperationId:D}.json"), Path.Combine(fixture.Directory, $"{wrongId:D}.json"));
    var rejected = false;
    try { await fixture.Store.GetAsync(wrongId); }
    catch (IOException) { rejected = true; }
    Assert(rejected, "Individual reads must detect a filename/operation ID mismatch.");
}

static async Task QueryStorageFailure()
{
    using var fixture = new Fixture();
    await fixture.Coordinator.InitializeAsync();
    var request = NewRequest();
    await fixture.Coordinator.SubmitAsync(request);
    await File.WriteAllTextAsync(Path.Combine(fixture.Directory, $"{request.OperationId:D}.json"), "{invalid-json");
    var queryRefused = false;
    try { await fixture.Coordinator.GetAsync(request.OperationId); }
    catch (OperationException ex) when (ex.StatusCode == 503) { queryRefused = true; }
    Assert(queryRefused, "A damaged ledger query must return a safe storage error.");
    var newWriteRefused = false;
    try { await fixture.Coordinator.SubmitAsync(NewRequest()); }
    catch (OperationException ex) when (ex.StatusCode == 503) { newWriteRefused = true; }
    Assert(newWriteRefused, "A query-detected storage error must disable subsequent write acceptance.");
    Equal(0, fixture.Backend.ExecutionCount, "Querying a damaged ledger must not execute.");
}

static async Task QueuedStorageFailure()
{
    using var fixture = new Fixture();
    await fixture.Coordinator.InitializeAsync();
    var request = NewRequest();
    await fixture.Coordinator.SubmitAsync(request);
    await File.WriteAllTextAsync(Path.Combine(fixture.Directory, $"{request.OperationId:D}.json"), "{invalid-json");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var runner = fixture.Coordinator.RunAsync(cancellation.Token);
    try
    {
        while (true)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            try { await fixture.Coordinator.SubmitAsync(NewRequest() with { OperationId = Guid.Empty }); }
            catch (OperationException ex) when (ex.StatusCode == 503) { break; }
            catch (OperationException ex) when (ex.StatusCode == 400) { }
            await Task.Delay(5, cancellation.Token);
        }
        Equal(0, fixture.Backend.ExecutionCount, "A corrupt accepted record must prevent execution.");
    }
    finally
    {
        cancellation.Cancel();
        await IgnoreCancellation(runner);
    }
}

static async Task ExecutionExceptionIsUnknown()
{
    using var fixture = new Fixture();
    await fixture.Coordinator.InitializeAsync();
    fixture.Backend.BeforeExecute = _ => throw new InvalidOperationException("TEST_EXECUTION_UNCERTAIN");
    var request = NewRequest();
    await fixture.Coordinator.SubmitAsync(request);
    var record = await fixture.RunUntilFinal(request.OperationId);
    Equal(OperationStates.Unknown, record.State, "An exception after execution begins must not claim no side effect.");
    Equal("EXECUTION_UNKNOWN", record.Error?.Code, "Unknown results require explicit reconciliation.");
}

static async Task PartialResultIsPreserved()
{
    using var fixture = new Fixture();
    await fixture.Coordinator.InitializeAsync();
    fixture.Backend.Result = new ExecutionResult(OperationStates.Partial, null, new ApiError("TEST_PARTIAL", "Test partial result."));
    var request = NewRequest();
    await fixture.Coordinator.SubmitAsync(request);
    var record = await fixture.RunUntilFinal(request.OperationId);
    Equal(OperationStates.Partial, record.State, "Partial results must remain visible to the operator.");
    Equal("TEST_PARTIAL", record.Error?.Code, "Partial details must be preserved.");
}

static async Task PoolPatchPreservesConfiguration()
{
    using var simulation = new SimulationFixture();
    await ExecuteSimulation(simulation.Backend, SimulationRequest(OperationTypes.CreatePool, "PatchPool",
        "{\"managedRuntimeVersion\":\"v4.0\",\"managedPipelineMode\":\"Classic\",\"enable32BitAppOnWin64\":true,\"autoStart\":true,\"startMode\":\"AlwaysRunning\",\"idleTimeoutMinutes\":8,\"idleTimeoutAction\":\"Suspend\"}"));
    await ExecuteSimulation(simulation.Backend, SimulationSiteRequest("PatchSite", "PatchPool", simulation.Directory, 18080));
    var before = await simulation.Backend.GetPoolAsync("PatchPool") ?? throw new TestFailure("Created pool must be queryable.");
    Assert(before.Applications.Contains("PatchSite/"), "Created site must appear in pool associations.");
    await ExecuteSimulation(simulation.Backend, SimulationRequest(OperationTypes.UpdatePool, "PatchPool", "{\"autoStart\":false}"));
    var after = await simulation.Backend.GetPoolAsync("PatchPool") ?? throw new TestFailure("Updated pool must be queryable.");
    Equal(false, after.AutoStart, "Explicit bool false must be applied.");
    Equal(before.ManagedRuntimeVersion, after.ManagedRuntimeVersion, "CLR must be retained.");
    Equal(before.ManagedPipelineMode, after.ManagedPipelineMode, "Pipeline must be retained.");
    Equal(before.Enable32BitAppOnWin64, after.Enable32BitAppOnWin64, "32-bit flag must be retained.");
    Equal(before.State, after.State, "An AutoStart-only update must preserve the current runtime state.");
    Equal(before.StartMode, after.StartMode, "Start mode must be retained.");
    Equal(before.IdleTimeoutMinutes, after.IdleTimeoutMinutes, "Idle timeout must be retained.");
    Equal("Suspend", after.IdleTimeoutAction, "Unspecified idle action must be retained.");
    Assert(before.Applications.SequenceEqual(after.Applications), "Application associations must be retained.");
    await ExecuteSimulation(simulation.Backend, SimulationRequest(OperationTypes.Stop, "PatchPool"));
    await ExecuteSimulation(simulation.Backend, SimulationRequest(OperationTypes.UpdatePool, "PatchPool", "{\"idleTimeoutMinutes\":0}"));
    after = await simulation.Backend.GetPoolAsync("PatchPool") ?? throw new TestFailure("Updated pool must be queryable.");
    Equal(0, after.IdleTimeoutMinutes, "Zero must disable idle timeout.");
    Equal("Suspend", after.IdleTimeoutAction, "Disabling timeout must retain the idle action.");
    await ExecuteSimulation(simulation.Backend, SimulationRequest(OperationTypes.UpdatePool, "PatchPool", "{\"idleTimeoutAction\":\"Terminate\"}"));
    after = await simulation.Backend.GetPoolAsync("PatchPool") ?? throw new TestFailure("Updated pool must be queryable.");
    Equal("Terminate", after.IdleTimeoutAction, "Idle action must support updating to Terminate.");
    Equal(0, after.IdleTimeoutMinutes, "Changing action must retain timeout.");
}

static async Task RuntimeChangesRequireStop()
{
    using var simulation = new SimulationFixture();
    const string name = "SIMULATION-DefaultPool";
    await ExecuteSimulation(simulation.Backend, SimulationRequest(OperationTypes.Start, name));
    foreach (var parameters in new[]
    {
        "{\"managedRuntimeVersion\":\"v4.0\"}", "{\"managedPipelineMode\":\"Classic\"}",
        "{\"enable32BitAppOnWin64\":true}", "{\"startMode\":\"AlwaysRunning\"}",
        "{\"idleTimeoutMinutes\":10}", "{\"idleTimeoutAction\":\"Suspend\"}"
    })
    {
        var validation = await simulation.Backend.ValidateAsync(SimulationRequest(OperationTypes.UpdatePool, name, parameters));
        Assert(!validation.Valid && validation.Errors.Any(e => e.Code == "pool.mustBeStopped"),
            "Every runtime-related update must reject a running pool.");
    }
    var safeConfiguration = await simulation.Backend.ValidateAsync(SimulationRequest(OperationTypes.UpdatePool, name, "{\"autoStart\":true}"));
    Assert(safeConfiguration.Valid, "AutoStart-only configuration update may run while the pool is Started.");
    await ExecuteSimulation(simulation.Backend, SimulationRequest(OperationTypes.Stop, name));
    await ExecuteSimulation(simulation.Backend, SimulationRequest(OperationTypes.UpdatePool, name, "{\"idleTimeoutAction\":\"Suspend\"}"));
}

static async Task InvalidPoolPatch()
{
    using var simulation = new SimulationFixture();
    foreach (var parameters in new[] { "{}", "{\"autoStart\":null}", "{\"customAccount\":\"test\"}", "{\"idleTimeoutAction\":\"Invalid\"}" })
    {
        var validation = await simulation.Backend.ValidateAsync(SimulationRequest(OperationTypes.UpdatePool, "SIMULATION-DefaultPool", parameters));
        Assert(!validation.Valid && validation.Errors.Length > 0, "Empty, null, unknown or invalid patches must be rejected.");
    }
}

static async Task SiteBindingGuards()
{
    using var simulation = new SimulationFixture();
    var reserved = await simulation.Backend.ValidateAsync(SimulationSiteRequest("ReservedSite", "SIMULATION-DefaultPool", simulation.Directory, 7444));
    Assert(!reserved.Valid && reserved.Errors.Any(e => e.Code == "binding.portInvalid"), "Agent listener port must be reserved.");
    await ExecuteSimulation(simulation.Backend, SimulationSiteRequest("BindingSite", "SIMULATION-DefaultPool", simulation.Directory, 18081));
    var overlap = await simulation.Backend.ValidateAsync(SimulationSiteRequest("OverlapSite", "SIMULATION-DefaultPool", simulation.Directory, 18081));
    Assert(!overlap.Valid && overlap.Errors.Any(e => e.Code == "binding.conflict"), "Overlapping binding must be rejected.");
}

static async Task ValidationIsReadOnly()
{
    using var simulation = new SimulationFixture();
    var before = await simulation.Backend.GetSnapshotAsync();
    var validation = await simulation.Backend.ValidateAsync(SimulationRequest(OperationTypes.CreatePool, "ValidationPool"));
    Assert(validation.Valid, "The create-pool preview must be valid.");
    var after = await simulation.Backend.GetSnapshotAsync();
    Equal(CanonicalJson.Hash(before.ApplicationPools), CanonicalJson.Hash(after.ApplicationPools), "Validation must not modify pools.");
    Equal(CanonicalJson.Hash(before.Sites), CanonicalJson.Hash(after.Sites), "Validation must not modify sites.");
}

static Task LegacyReadContractsUnchanged()
{
    Equal("1.0", Protocol.Version, "The existing protocol version must remain unchanged.");
    var snapshot = JsonSerializer.SerializeToElement(new IisSnapshot(DateTimeOffset.UtcNow,
        [new PoolConfig()], [new SiteConfig { Bindings = [new BindingConfig()] }]), Protocol.Json);
    JsonFields(snapshot, "collectedAt,applicationPools,sites");
    JsonFields(snapshot.GetProperty("sites")[0], "name,id,state,serverAutoStart,physicalPath,applicationPoolName,bindings");
    JsonFields(snapshot.GetProperty("sites")[0].GetProperty("bindings")[0],
        "protocol,ipAddress,port,hostName,certificateThumbprint,useSni");
    JsonFields(snapshot.GetProperty("applicationPools")[0],
        "name,state,managedRuntimeVersion,managedPipelineMode,enable32BitAppOnWin64,autoStart,startMode,idleTimeoutMinutes,idleTimeoutAction,identityType,applications,fingerprint");
    JsonFields(JsonSerializer.SerializeToElement(new AgentInfo(Protocol.Version, "test", "test", "Simulation", "test", "test", false, []), Protocol.Json),
        "protocolVersion,agentVersion,machineName,mode,osVersion,iisVersion,sharedConfiguration,reservedPorts");
    RejectUnknownFields<SiteConfig>("{\"name\":\"Legacy\",\"applications\":[]}");
    RejectUnknownFields<IisSnapshot>("{\"collectedAt\":\"2026-10-06T00:00:00Z\",\"applicationPools\":[],\"sites\":[],\"topology\":[]}");
    return Task.CompletedTask;
}

static Task TopologyContractsPreserveOwnership()
{
    const string payload = """
        {
          "collectedAt": "2026-10-06T00:00:00Z",
          "sites": [
            { "id": 1, "name": "Portal", "state": "Started", "bindings": [
                { "protocol": "http", "port": 80, "hostName": "portal.example.test" },
                { "protocol": "https", "port": 443, "hostName": "portal.example.test" }
              ], "applications": [
                { "path": "/", "applicationPoolName": "WebPool", "physicalPath": "C:\\Demo\\Web", "enabledProtocols": "http",
                  "virtualDirectories": [{ "path": "/", "physicalPath": "C:\\Demo\\Web" }] },
                { "path": "/api", "applicationPoolName": "ApiPool", "physicalPath": "C:\\Demo\\Api", "enabledProtocols": "http",
                  "virtualDirectories": [{ "path": "/", "physicalPath": "C:\\Demo\\Api" }, { "path": "/assets", "physicalPath": "D:\\Demo\\Assets" }] }
              ] },
            { "id": 2, "name": "Admin", "state": "Stopped", "bindings": [], "applications": [
                { "path": "/", "applicationPoolName": "apipool", "physicalPath": "C:\\Demo\\Admin", "enabledProtocols": "http",
                  "virtualDirectories": [{ "path": "/", "physicalPath": "C:\\Demo\\Admin" }] }
              ] }
          ],
          "applicationPools": [{ "name": "WebPool" }, { "name": "ApiPool" }]
        }
        """;
    var topology = JsonSerializer.Deserialize<SiteTopologySnapshot>(payload, Protocol.Json)!;
    var roundTrip = JsonSerializer.Deserialize<SiteTopologySnapshot>(JsonSerializer.Serialize(topology, Protocol.Json), Protocol.Json)!;
    var portal = roundTrip.Sites.Single(site => site.Id == 1);
    var root = portal.Applications.Single(application => application.Path == "/");
    var api = portal.Applications.Single(application => application.Path == "/api");
    Equal("WebPool", root.ApplicationPoolName, "The root and child application can use different pools.");
    Equal("ApiPool", api.ApplicationPoolName, "A child application must retain its own pool.");
    Equal("C:\\Demo\\Api", api.VirtualDirectories.Single(directory => directory.Path == "/").PhysicalPath,
        "The root mapping belongs to its application.");
    Equal("D:\\Demo\\Assets", api.VirtualDirectories.Single(directory => directory.Path == "/assets").PhysicalPath,
        "An additional virtual directory must remain nested under its owning application.");
    Assert(root.VirtualDirectories.All(directory => directory.Path != "/assets"), "A child directory must not be flattened into the site root.");
    Equal("http", api.EnabledProtocols, "The IIS http enabled protocol must also retain HTTPS bindings.");
    Equal(2, portal.Bindings.Length, "All site bindings must remain available to application address previews.");
    var sharedSites = roundTrip.Sites.Where(site => site.Applications.Any(application =>
        string.Equals(application.ApplicationPoolName, "ApiPool", StringComparison.OrdinalIgnoreCase))).ToArray();
    Equal(2, sharedSites.Length, "A shared pool must retain associations to applications in multiple sites.");
    return Task.CompletedTask;
}

static Task TopologyContractsUseWhitelist()
{
    var topology = JsonSerializer.SerializeToElement(new SiteTopologySnapshot(DateTimeOffset.UtcNow,
        [new SiteTopology { Applications = [new ApplicationTopology { VirtualDirectories = [new VirtualDirectoryTopology()] }] }],
        [new PoolConfig()]), Protocol.Json);
    JsonFields(topology, "collectedAt,sites,applicationPools");
    var site = topology.GetProperty("sites")[0];
    JsonFields(site, "id,name,state,bindings,applications");
    var application = site.GetProperty("applications")[0];
    JsonFields(application, "path,applicationPoolName,physicalPath,enabledProtocols,virtualDirectories");
    JsonFields(application.GetProperty("virtualDirectories")[0], "path,physicalPath");
    RejectUnknownFields<VirtualDirectoryTopology>("{\"path\":\"/\",\"userName\":\"redacted\"}");
    RejectUnknownFields<VirtualDirectoryTopology>("{\"path\":\"/\",\"password\":\"redacted\"}");
    RejectUnknownFields<VirtualDirectoryTopology>("{\"path\":\"/\",\"applicationPoolName\":\"IndependentPool\"}");
    RejectUnknownFields<ApplicationTopology>("{\"path\":\"/api\",\"attributes\":[]}");
    RejectUnknownFields<SiteTopology>("{\"id\":1,\"serverAutoStart\":true}");
    return Task.CompletedTask;
}

static async Task SimulationTopologyRoots()
{
    using var simulation = new SimulationFixture();
    var empty = await simulation.Backend.GetSiteTopologyAsync();
    Equal(0, empty.Sites.Length, "Topology support must not add synthetic applications or sites to simulation data.");
    Equal("SIMULATION-DefaultPool", empty.ApplicationPools.Single().Name, "The existing simulation pool must be retained.");
    await ExecuteSimulation(simulation.Backend, SimulationSiteRequest("Portal", "SIMULATION-DefaultPool", simulation.Directory, 18081));
    var secureSite = SimulationSiteRequest("SecurePortal", "SIMULATION-DefaultPool", simulation.Directory, 18443);
    secureSite = secureSite with
    {
        Parameters = JsonSerializer.SerializeToElement(new SiteParameters
        {
            PhysicalPath = simulation.Directory, ApplicationPoolName = "SIMULATION-DefaultPool",
            Bindings = [new BindingConfig { Protocol = "https", Port = 18443, HostName = "demo.simulation.test", CertificateThumbprint = new string('A', 40) }]
        }, Protocol.Json)
    };
    await ExecuteSimulation(simulation.Backend, secureSite);
    var before = await simulation.Backend.GetSnapshotAsync();
    var topology = await simulation.Backend.GetSiteTopologyAsync();
    Equal(2, topology.Sites.Length, "Each existing simulation site must expose its root application.");
    foreach (var site in topology.Sites)
    {
        var source = before.Sites.Single(value => value.Id == site.Id);
        var root = site.Applications.Single();
        Equal("/", root.Path, "Simulation only supports root applications.");
        Equal(source.ApplicationPoolName, root.ApplicationPoolName, "Root application pool must match the original site.");
        Equal(source.PhysicalPath, root.PhysicalPath, "Root application directory must match the original site.");
        Equal("http", root.EnabledProtocols, "HTTPS in IIS is covered by the http application protocol.");
        Equal("/", root.VirtualDirectories.Single().Path, "The root virtual directory is a mapping, not an additional directory.");
        Equal(root.PhysicalPath, root.VirtualDirectories.Single().PhysicalPath, "Root mapping path must remain accurate.");
        Equal(CanonicalJson.Hash(source.Bindings), CanonicalJson.Hash(site.Bindings), "Topology must retain site bindings.");
    }
    var pool = topology.ApplicationPools.Single();
    Equal("", pool.Fingerprint, "The display-only topology must not provide an operation fingerprint.");
    Equal(2, pool.Applications.Length, "The shared pool must expose both root application associations.");
    var validation = await simulation.Backend.ValidateAsync(SimulationRequest(OperationTypes.Start, pool.Name));
    Assert(validation.Valid, "Adding topology support must preserve existing pool start validation.");
    Equal(CanonicalJson.Hash(pool.Applications), CanonicalJson.Hash(validation.AffectedApplications), "Operational impact must still cover all related site roots.");
    var after = await simulation.Backend.GetSnapshotAsync();
    Equal(CanonicalJson.Hash(before.ApplicationPools), CanonicalJson.Hash(after.ApplicationPools), "Reading topology must not change pool data or fingerprints.");
    Equal(CanonicalJson.Hash(before.Sites), CanonicalJson.Hash(after.Sites), "Reading topology must not change site data.");
}

static async Task TopologyReadsPreserveOperationValidation()
{
    using var simulation = new SimulationFixture();
    await ExecuteSimulation(simulation.Backend, SimulationSiteRequest("StableSite", "SIMULATION-DefaultPool", simulation.Directory, 18081));
    var request = SimulationRequest(OperationTypes.UpdatePool, "SIMULATION-DefaultPool", "{\"autoStart\":true}");
    var before = await simulation.Backend.ValidateAsync(request);
    Assert(before.Valid, "The pool patch must be valid before reading topology.");
    var snapshot = await simulation.Backend.GetSnapshotAsync();
    await simulation.Backend.GetSiteTopologyAsync();
    var after = await simulation.Backend.ValidateAsync(request);
    Equal(before.Fingerprint, after.Fingerprint, "Topology must not enter or alter the legacy operation fingerprint evidence.");
    var result = await simulation.Backend.ExecuteAsync(request with { ExpectedFingerprint = before.Fingerprint });
    Equal(OperationStates.Succeeded, result.State, "A prevalidated operation must remain executable after a topology read.");
    var updated = await simulation.Backend.GetSnapshotAsync();
    Assert(updated.ApplicationPools.Single().AutoStart, "The original simulation patch must still be persisted in memory.");
    Equal(CanonicalJson.Hash(snapshot.Sites), CanonicalJson.Hash(updated.Sites), "An application pool patch must retain the original site configuration.");
}

static void JsonFields(JsonElement value, string expected)
{
    var names = value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal);
    Equal(string.Join(',', expected.Split(',').Order(StringComparer.Ordinal)), string.Join(',', names),
        "The serialized DTO fields must match the public contract exactly.");
}

static void RejectUnknownFields<T>(string value)
{
    try { JsonSerializer.Deserialize<T>(value, Protocol.Json); }
    catch (JsonException) { return; }
    throw new TestFailure("Undefined contract fields must be rejected.");
}

static OperationRequest SimulationRequest(string type, string name, string parameters = "{}") => NewRequest(parameters) with
{
    Type = type,
    Target = new OperationTarget { ApplicationPoolName = name },
    ExpectedFingerprint = null
};

static OperationRequest SimulationSiteRequest(string name, string pool, string path, int port) => NewRequest() with
{
    Type = OperationTypes.CreateSite,
    Target = new OperationTarget { SiteName = name },
    Parameters = JsonSerializer.SerializeToElement(new SiteParameters
    {
        PhysicalPath = path, ApplicationPoolName = pool,
        Bindings = [new BindingConfig { Port = port, HostName = "test.simulation.test" }]
    }, Protocol.Json),
    ExpectedFingerprint = null
};

static async Task ExecuteSimulation(SimulationIisBackend backend, OperationRequest request)
{
    var validation = await backend.ValidateAsync(request);
    Assert(validation.Valid, "Simulation setup request must pass validation.");
    var result = await backend.ExecuteAsync(request with { ExpectedFingerprint = validation.Fingerprint });
    Equal(OperationStates.Succeeded, result.State, "Simulation setup/update request must succeed.");
}

static OperationRequest NewRequest(string parameters = "{}") => new()
{
    OperationId = Guid.NewGuid(),
    Type = OperationTypes.UpdatePool,
    Target = new OperationTarget { ApplicationPoolName = "TestPool" },
    Parameters = Json(parameters),
    ExpectedFingerprint = new string('a', 64),
    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2)
};

static OperationRecord Record(OperationRequest request, string state) => new()
{
    Request = request,
    RequestHash = CanonicalJson.Hash(request),
    State = state,
    AcceptedAt = DateTimeOffset.UtcNow,
    StartedAt = state == OperationStates.Running ? DateTimeOffset.UtcNow : null
};

static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
static void Assert(bool value, string message) { if (!value) throw new TestFailure(message); }
static void Equal<T>(T expected, T actual, string message) => Assert(EqualityComparer<T>.Default.Equals(expected, actual), message);
static async Task IgnoreCancellation(Task task)
{
    try { await task; }
    catch (OperationCanceledException) { }
}

sealed class TestFailure(string message) : Exception(message);

sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan by) => now += by;
}

sealed class SimulationFixture : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetFullPath("work"), "simulation-selftest-" + Guid.NewGuid().ToString("N"));
    public SimulationIisBackend Backend { get; }
    public SimulationFixture()
    {
        System.IO.Directory.CreateDirectory(Directory);
        Backend = new SimulationIisBackend(new AgentOptions { Mode = "Simulation", AllowedSiteRoots = [Directory] });
    }
    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
}

sealed class Fixture : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetFullPath("work"), "core-selftest-" + Guid.NewGuid().ToString("N"));
    public FileOperationStore Store { get; }
    public FakeBackend Backend { get; } = new();
    public OperationCoordinator Coordinator { get; }

    public Fixture(TimeProvider? clock = null)
    {
        System.IO.Directory.CreateDirectory(Directory);
        Store = new FileOperationStore(Directory);
        Coordinator = new OperationCoordinator(Backend, Store, NullLogger<OperationCoordinator>.Instance, clock);
    }

    public async Task RunUntil(Guid id, string state)
    {
        var record = await RunUntilFinal(id);
        if (record.State != state) throw new TestFailure($"Expected {state}, got {record.State}.");
    }

    public async Task<OperationRecord> RunUntilFinal(Guid id)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runner = Coordinator.RunAsync(cancellation.Token);
        try { return await WaitForFinal(id, cancellation.Token); }
        finally
        {
            cancellation.Cancel();
            try { await runner; }
            catch (OperationCanceledException) { }
        }
    }

    public async Task<OperationRecord> WaitForFinal(Guid id, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = await Coordinator.GetAsync(id, cancellationToken);
            if (record is not null && OperationStates.IsFinal(record.State)) return record;
            await Task.Delay(5, cancellationToken);
        }
    }

    public void Dispose()
    {
        Store.Dispose();
        System.IO.Directory.Delete(Directory, recursive: true);
    }
}

sealed class FakeBackend : IIisBackend
{
    private int active;
    private int executionCount;
    private int maximumConcurrentExecutions;
    public string Fingerprint { get; set; } = new('a', 64);
    public int ExecutionCount => executionCount;
    public int MaximumConcurrentExecutions => maximumConcurrentExecutions;
    public TimeSpan Delay { get; set; }
    public Func<OperationRequest, Task>? BeforeExecute { get; set; }
    public Action? BeforeValidation { get; set; }
    public ExecutionResult Result { get; set; } = new(OperationStates.Succeeded, null);

    public Task<AgentInfo> GetInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentInfo(Protocol.Version, "test", "test", "Simulation", "test", "test", false, []));
    public Task<IisSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new IisSnapshot(DateTimeOffset.UtcNow, [], []));
    public Task<PoolConfig?> GetPoolAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult<PoolConfig?>(new PoolConfig { Name = name, Fingerprint = Fingerprint });
    public Task<CertificateInfo[]> GetCertificatesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Array.Empty<CertificateInfo>());
    public Task<ValidationResult> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        BeforeValidation?.Invoke();
        return Task.FromResult(new ValidationResult(true, Fingerprint, [], [], [], []));
    }

    public async Task<ExecutionResult> ExecuteAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        var current = Interlocked.Increment(ref active);
        int observed;
        do { observed = maximumConcurrentExecutions; }
        while (current > observed && Interlocked.CompareExchange(ref maximumConcurrentExecutions, current, observed) != observed);
        try
        {
            if (BeforeExecute is not null) await BeforeExecute(request);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            Interlocked.Increment(ref executionCount);
            return Result;
        }
        finally { Interlocked.Decrement(ref active); }
    }
}
