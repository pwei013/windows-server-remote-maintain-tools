using System.Text.Json;
using System.Reflection;
using IISManager.Agent.Backends;
using IISManager.Contracts;
using IISManager.Core;
using Microsoft.Extensions.Logging.Abstractions;

internal static class SiteLifecycleSelfTests
{
    internal static readonly (string Name, Func<Task> Test)[] Cases =
    [
        ("site lifecycle preserves configuration pools and other sites", LifecyclePreservesConfiguration),
        ("site lifecycle requires the matching target and empty parameters", LifecycleGuards),
        ("Windows readback accepts site lifecycle with auto-start enabled and HTTPS SNI", NativeReadbackAcceptsLifecycle),
        ("Windows readback rejects changed public fields and missing plans", NativeReadbackRejectsDrift),
        ("Windows readback distinguishes hidden configuration drift from target mismatch", NativeReadbackDistinguishesDrift),
        ("IIS site and pool configuration hashes exclude runtime state transitions", RuntimeStateIsNotConfiguration),
        ("IIS configuration hashes retain actual settings and SSL extension evidence", PersistentConfigurationStillChangesHash),
        ("IIS readback waits through the old stable state before the desired state", ReadbackWaitsForTarget),
        ("IIS readback returns unconfirmed old state at the deadline", ReadbackTimeoutKeepsEvidence),
        ("site restart rejects stale prechecks even when the final state is unchanged", RestartInvalidatesPrecheck),
        ("site restart waits for stopped evidence before one start", RestartOrder),
        ("site restart waits through old started state and still blocks configuration drift", RestartWaitsAndGuardsDrift),
        ("site restart blocks missing replaced changed or unstable targets", RestartEvidenceGuards),
        ("site restart never retries failed stop or start", RestartFailureDoesNotReplay),
        ("site restart is persisted and deduplicated by the shared queue", RestartLedger)
    ];

    private static OperationRequest Request(string type, string name = "LifecycleSite", object? parameters = null) => new()
    {
        OperationId = Guid.NewGuid(), Type = type, Target = new() { SiteName = name },
        Parameters = JsonSerializer.SerializeToElement(parameters ?? new { }, Protocol.Json),
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2)
    };

    private static async Task CreateSite(SimulationFixture fixture, string name = "LifecycleSite", int port = 18081) =>
        _ = await Execute(fixture.Backend, Request(OperationTypes.CreateSite, name, new SiteParameters
        {
            PhysicalPath = fixture.Directory, ApplicationPoolName = "SIMULATION-DefaultPool",
            Bindings = [new BindingConfig { Port = port, HostName = "site.simulation.test" }]
        }));

    private static async Task<ExecutionResult> Execute(SimulationIisBackend backend, OperationRequest request)
    {
        var validation = await backend.ValidateAsync(request);
        Require(validation.Valid, "Lifecycle setup or action must pass validation.");
        var result = await backend.ExecuteAsync(request with { ExpectedFingerprint = validation.Fingerprint });
        Require(result.State == OperationStates.Succeeded, "Lifecycle action must succeed.");
        return result;
    }

    private static async Task LifecyclePreservesConfiguration()
    {
        using var fixture = new SimulationFixture();
        await CreateSite(fixture);
        await CreateSite(fixture, "OtherSite", 18082);
        var before = await fixture.Backend.GetSnapshotAsync();
        var original = before.Sites.Single(site => site.Name == "LifecycleSite");
        foreach (var type in new[] { OperationTypes.SiteStart, OperationTypes.SiteStart, OperationTypes.SiteRestart, OperationTypes.SiteStop, OperationTypes.SiteStop })
        {
            var request = Request(type);
            var precheck = await fixture.Backend.ValidateAsync(request);
            Require(precheck.AffectedApplications.SequenceEqual(["LifecycleSite/"]), "Impact must be scoped to the selected site.");
            Require(CanonicalJson.Hash(before.Sites) == CanonicalJson.Hash((await fixture.Backend.GetSnapshotAsync()).Sites), "Prechecks must be read-only.");
            var result = await Execute(fixture.Backend, request);
            var expected = type == OperationTypes.SiteStop ? "Stopped" : "Started";
            var after = await fixture.Backend.GetSnapshotAsync();
            var selected = after.Sites.Single(site => site.Name == original.Name);
            Require(selected.State == expected && result.After!.Value.Deserialize<SiteConfig>(Protocol.Json)!.State == expected, "Readback must contain the site and its expected state.");
            Require(CanonicalJson.Hash(original) == CanonicalJson.Hash(selected with { State = original.State }), "All site configuration fields must be preserved.");
            Require(CanonicalJson.Hash(before.ApplicationPools) == CanonicalJson.Hash(after.ApplicationPools), "Site actions must not operate on application pools.");
            Require(CanonicalJson.Hash(before.Sites.Single(site => site.Name == "OtherSite")) == CanonicalJson.Hash(after.Sites.Single(site => site.Name == "OtherSite")), "Other sites must remain unchanged.");
            before = after;
        }
    }

    private static async Task LifecycleGuards()
    {
        using var fixture = new SimulationFixture();
        await CreateSite(fixture);
        var before = await fixture.Backend.GetSnapshotAsync();
        foreach (var type in new[] { OperationTypes.SiteStart, OperationTypes.SiteStop, OperationTypes.SiteRestart })
        {
            foreach (var invalid in new[]
            {
                Request(type, "MissingSite"),
                Request(type) with { Target = new() { ApplicationPoolName = "SIMULATION-DefaultPool" } },
                Request(type) with { Target = new() { SiteName = "LifecycleSite", ApplicationPoolName = "SIMULATION-DefaultPool" } },
                Request(type) with { Target = new() { SiteName = "LifecycleSite", ServiceName = "OtherService" } },
                Request(type, parameters: new { serverAutoStart = true }),
                Request(type) with { Parameters = JsonSerializer.SerializeToElement<object?>(null) }
            }) Require(!(await fixture.Backend.ValidateAsync(invalid)).Valid, "Invalid targets and parameters must be rejected.");
        }
        var stoppedRestart = await fixture.Backend.ValidateAsync(Request(OperationTypes.SiteRestart));
        Require(stoppedRestart.Errors.Any(error => error.Code == "site.notRunning"), "A stopped site requires a separate start action.");
        var noFingerprint = await fixture.Backend.ExecuteAsync(Request(OperationTypes.SiteStart));
        Require(noFingerprint.State == OperationStates.Failed, "Execution requires a precheck fingerprint.");
        Require(CanonicalJson.Hash(before.Sites) == CanonicalJson.Hash((await fixture.Backend.GetSnapshotAsync()).Sites), "Rejected requests must not mutate the site.");
    }

    private static async Task RestartInvalidatesPrecheck()
    {
        using var fixture = new SimulationFixture(); await CreateSite(fixture);
        await Execute(fixture.Backend, Request(OperationTypes.SiteStart));
        var request = Request(OperationTypes.SiteRestart);
        var validation = await fixture.Backend.ValidateAsync(request);
        await Execute(fixture.Backend, request);
        var stale = await fixture.Backend.ExecuteAsync(request with { OperationId = Guid.NewGuid(), ExpectedFingerprint = validation.Fingerprint });
        Require(stale.State == OperationStates.Failed && stale.Error?.Code == "configuration.conflict", "A stale restart precheck must not authorize another restart.");
    }

    private static SiteConfig ReadbackSite() => new()
    {
        Name = "ReadbackSite", Id = 5, State = "Started", ServerAutoStart = true,
        PhysicalPath = @"D:\Sites\ReadbackSite", ApplicationPoolName = "ReadbackPool",
        Bindings = [new() { Protocol = "https", Port = 443, HostName = "readback.example.test", CertificateThumbprint = new('A', 40), UseSni = true }]
    };

    private static ApiError? NativeReadbackError(string type, SiteConfig after, JsonElement? proposed, bool configurationMatches = true)
    {
        // Exercise the Windows adapter's actual pure comparison without opening native IIS handles.
        var backend = typeof(SimulationIisBackend).Assembly.GetType("IISManager.Agent.Backends.WindowsIisBackend", throwOnError: true)!;
        var compare = backend.GetMethod("ReadbackError", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (ApiError?)compare.Invoke(null, [Request(type, "ReadbackSite"), after, proposed, configurationMatches]);
    }

    private static Task NativeReadbackAcceptsLifecycle()
    {
        foreach (var type in new[] { OperationTypes.SiteStart, OperationTypes.SiteStop, OperationTypes.SiteRestart })
        {
            var expected = ReadbackSite() with { State = type == OperationTypes.SiteStop ? "Stopped" : "Started" };
            var proposed = JsonSerializer.SerializeToElement(expected, Protocol.Json);
            var independentlyRead = proposed.Deserialize<SiteConfig>(Protocol.Json)!;
            Require(NativeReadbackError(type, independentlyRead, proposed) is null, "The site's auto-start setting and pool association must not invalidate a correct lifecycle readback.");
        }
        return Task.CompletedTask;
    }

    private static Task NativeReadbackRejectsDrift()
    {
        var expected = ReadbackSite() with { State = "Stopped" };
        var proposed = JsonSerializer.SerializeToElement(expected, Protocol.Json);
        foreach (var actual in new[]
        {
            expected with { State = "Started" }, expected with { Id = 6 }, expected with { Name = "OtherSite" },
            expected with { ServerAutoStart = false }, expected with { PhysicalPath = @"D:\Sites\Other" },
            expected with { ApplicationPoolName = "OtherPool" }, expected with { Bindings = [] },
            expected with { Bindings = [expected.Bindings[0] with { UseSni = false }] },
            expected with { Bindings = [expected.Bindings[0] with { CertificateThumbprint = new('B', 40) }] }
        })
            Require(NativeReadbackError(OperationTypes.SiteStop, actual, proposed)?.Code == "iis.readbackMismatch", "Changed state, identity or visible configuration must remain unverified.");
        Require(NativeReadbackError(OperationTypes.SiteStop, expected, null)?.Code == "iis.readbackMismatch", "Missing precheck evidence cannot be declared successful.");
        return Task.CompletedTask;
    }

    private static Task NativeReadbackDistinguishesDrift()
    {
        var expected = ReadbackSite() with { State = "Stopped" };
        var proposed = JsonSerializer.SerializeToElement(expected, Protocol.Json);
        Require(NativeReadbackError(OperationTypes.SiteStop, expected, proposed, false)?.Code == "site.configurationChanged", "Correct visible state must not hide changed application or directory configuration evidence.");
        Require(NativeReadbackError(OperationTypes.SiteStop, expected, proposed) is null, "Stable configuration and matching public evidence must pass.");
        return Task.CompletedTask;
    }

    private static string NativeAttributesHash((string Name, object? Value)[] attributes, bool includesRuntimeState = true)
    {
        var backend = typeof(SimulationIisBackend).Assembly.GetType("IISManager.Agent.Backends.WindowsIisBackend", throwOnError: true)!;
        var hash = backend.GetMethod("ConfigurationAttributesHash", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)hash.Invoke(null, [attributes, includesRuntimeState])!;
    }

    private static (string Name, object? Value)[] SiteAttributes(int state) =>
        [("name", "ReadbackSite"), ("id", 5L), ("serverAutoStart", true), ("state", state)];

    private static Task RuntimeStateIsNotConfiguration()
    {
        var baseline = NativeAttributesHash(SiteAttributes(1));
        foreach (var state in new[] { 0, 1, 2, 3, 4 })
            Require(baseline == NativeAttributesHash(SiteAttributes(state)), "RSCA Starting/Started/Stopping/Stopped/Unknown changes must not change the configuration fingerprint.");
        Require(baseline == NativeAttributesHash(SiteAttributes(1).Where(attribute => attribute.Name != "state").ToArray()), "State must be removed rather than replaced by a fixed digest.");
        var customState = new (string Name, object? Value)[] { ("state", "enabled") };
        Require(NativeAttributesHash(customState, false) != NativeAttributesHash([("state", "disabled")], false), "Only the site's or pool's RSCA state may be omitted; other elements retain their configuration attributes.");
        return Task.CompletedTask;
    }

    private static Task PersistentConfigurationStillChangesHash()
    {
        var original = SiteAttributes(1);
        var baseline = NativeAttributesHash(original);
        foreach (var change in new (string Name, object? Value)[] { ("name", "OtherSite"), ("id", 6L), ("serverAutoStart", false) })
        {
            var changed = original.Select(attribute => attribute.Name == change.Name ? change : attribute).ToArray();
            Require(NativeAttributesHash(changed) != baseline, "Persistent site identity and settings must still affect the fingerprint.");
        }
        var ssl = new (string Name, object? Value)[] { ("certificateStoreName", "My"), ("isDsMapperEnabled", false) };
        Require(NativeAttributesHash(ssl, false) != NativeAttributesHash([("certificateStoreName", "WebHosting"), ("isDsMapperEnabled", false)], false), "Dynamic SSL configuration evidence must not be discarded with runtime state.");
        Require(NativeAttributesHash(ssl, false) != NativeAttributesHash([("certificateStoreName", "My"), ("isDsMapperEnabled", true)], false), "DS Mapper configuration remains part of the binding fingerprint.");
        return Task.CompletedTask;
    }

    private static async Task ReadbackWaitsForTarget()
    {
        foreach (var target in new[] { "Started", "Stopped" })
        {
            var states = target == "Started" ? new[] { "Stopped", "Starting", "Started" } : ["Started", "Stopping", "Stopped"];
            var reads = 0;
            var after = await IisStatePolling.ReadUntilAsync(() => states[reads++], current => current == target, TimeSpan.FromSeconds(1), TimeSpan.Zero);
            Require(after == target && reads == 3, "A stale stable state must not end native readback before the requested state is observed.");
        }
    }

    private static async Task ReadbackTimeoutKeepsEvidence()
    {
        var reads = 0;
        var after = await IisStatePolling.ReadUntilAsync(() => { reads++; return "Stopped"; }, current => current == "Started", TimeSpan.Zero);
        Require(after == "Stopped" && reads == 1, "The deadline must return actual evidence rather than assume success or retry the action.");
    }

    private static async Task RestartWaitsAndGuardsDrift()
    {
        var before = new SiteRestartEvidence(7, "Started", "configuration");
        foreach (var changed in new[] { false, true })
        {
            var stops = 0; var starts = 0; var reads = 0;
            try
            {
                await SiteRestartSequence.ExecuteAsync(before, () => stops++, () =>
                {
                    reads++;
                    return reads == 1 ? before : before with { State = "Stopped", ConfigurationFingerprint = changed ? "changed" : "configuration" };
                }, _ => starts++, TimeSpan.FromSeconds(1));
                Require(!changed, "Configuration changes during stop must prevent start.");
            }
            catch (OperationException exception) { Require(changed && exception.Code == "site.configurationChanged", "Retain the configuration guard while waiting through stale state."); }
            Require(stops == 1 && reads == 2 && starts == (changed ? 0 : 1), "Restart may stop and start only once and must independently read the stopped target.");
        }
    }

    private static async Task RestartOrder()
    {
        var before = new SiteRestartEvidence(7, "Started", "configuration");
        var calls = new List<string>(); var reads = 0;
        await SiteRestartSequence.ExecuteAsync(before, () => calls.Add("stop"), () =>
        {
            calls.Add("read"); return before with { State = ++reads == 1 ? "Stopping" : "Stopped" };
        }, stopped => { Require(stopped == before with { State = "Stopped" }, "Start requires unchanged stopped evidence."); calls.Add("start"); });
        Require(calls.SequenceEqual(["stop", "read", "read", "start"]), "Restart must stop, wait and independently read before exactly one start.");
    }

    private static async Task RestartEvidenceGuards()
    {
        var before = new SiteRestartEvidence(7, "Started", "configuration");
        foreach (var evidence in new SiteRestartEvidence?[]
        {
            null, before with { Id = 8, State = "Stopped" }, before with { State = "Stopped", ConfigurationFingerprint = "changed" },
            before, before with { State = "Unknown" }, before with { State = "Stopping" }
        })
        {
            var stops = 0; var starts = 0;
            try
            {
                await SiteRestartSequence.ExecuteAsync(before, () => stops++, () => evidence, _ => starts++, TimeSpan.Zero);
                throw new TestFailure("Unverified stop evidence must prevent start.");
            }
            catch (OperationException) { }
            Require(stops == 1 && starts == 0, "An unverified stopped target must never be restarted.");
        }
        var mutations = 0;
        try
        {
            await SiteRestartSequence.ExecuteAsync(before with { State = "Stopped" }, () => mutations++, () => before, _ => mutations++);
            throw new TestFailure("Restart requires a running target.");
        }
        catch (OperationException exception) { Require(exception.Code == "site.notRunning" && mutations == 0, "A stopped target must fail before mutation."); }
    }

    private static async Task RestartFailureDoesNotReplay()
    {
        var before = new SiteRestartEvidence(7, "Started", "configuration");
        foreach (var failStop in new[] { true, false })
        {
            var stops = 0; var starts = 0;
            try
            {
                await SiteRestartSequence.ExecuteAsync(before, () => { stops++; if (failStop) throw new OperationException("TEST_STOP", "test"); },
                    () => before with { State = "Stopped" }, _ => { starts++; throw new OperationException("TEST_START", "test"); });
                throw new TestFailure("Native failures must propagate to the adapter for readback.");
            }
            catch (OperationException exception) { Require(exception.Code == (failStop ? "TEST_STOP" : "TEST_START"), "Preserve the failed stage."); }
            Require(stops == 1 && starts == (failStop ? 0 : 1), "Neither mutation may be retried after failure.");
        }
    }

    private static async Task RestartLedger()
    {
        using var fixture = new SimulationFixture(); await CreateSite(fixture);
        await Execute(fixture.Backend, Request(OperationTypes.SiteStart));
        using var store = new FileOperationStore(Path.Combine(fixture.Directory, "ledger"));
        var coordinator = new OperationCoordinator(fixture.Backend, store, NullLogger<OperationCoordinator>.Instance);
        await coordinator.InitializeAsync();
        var request = Request(OperationTypes.SiteRestart);
        var precheck = await coordinator.ValidateAsync(request); request = request with { ExpectedFingerprint = precheck.Fingerprint };
        var accepted = await coordinator.SubmitAsync(request);
        var duplicate = await coordinator.SubmitAsync(request);
        Require(accepted.AcceptedAt == duplicate.AcceptedAt, "Duplicate site requests must share one durable receipt.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runner = coordinator.RunAsync(cancellation.Token);
        try
        {
            OperationRecord? record;
            do { await Task.Delay(10, cancellation.Token); record = await coordinator.GetAsync(request.OperationId, cancellation.Token); }
            while (record is null || !OperationStates.IsFinal(record.State));
            Require(record.State == OperationStates.Succeeded && record.Before!.Value.GetProperty("state").GetString() == "Started" && record.After!.Value.GetProperty("state").GetString() == "Started", "The ledger must persist site before and after readback.");
            var fingerprint = (await fixture.Backend.ValidateAsync(Request(OperationTypes.SiteRestart))).Fingerprint;
            var existing = await coordinator.SubmitAsync(request);
            Require(existing.State == OperationStates.Succeeded && fingerprint == (await fixture.Backend.ValidateAsync(Request(OperationTypes.SiteRestart))).Fingerprint, "A completed duplicate must not run again.");
        }
        finally { cancellation.Cancel(); try { await runner; } catch (OperationCanceledException) { } }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new TestFailure(message); }
}
