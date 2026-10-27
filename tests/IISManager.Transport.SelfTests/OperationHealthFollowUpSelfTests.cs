using System.Collections.Concurrent;
using System.Text.Json;
using IISManager.Console;
using IISManager.Contracts;

internal static class OperationHealthFollowUpSelfTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 27, 2, 0, 0, TimeSpan.Zero);
    public static async Task<int> RunAsync()
    {
        var checks = 0; var failures = 0;
        async Task Check(string name, Func<Task> assertion)
        {
            checks++;
            try { await assertion(); System.Console.WriteLine("PASS operation-health " + name); }
            catch (Exception exception)
            { failures++; System.Console.WriteLine("FAIL operation-health " + name + " type=" + exception.GetType().Name); }
        }
        await Check("only new eligible IIS lifecycle intents receive metadata", () =>
        {
            using var f = new Fixture();
            foreach (var type in OperationTypes.All)
            {
                var task = f.Task(type);
                var eligible = type is OperationTypes.Start or OperationTypes.Stop or OperationTypes.Recycle or
                    OperationTypes.SiteStart or OperationTypes.SiteStop or OperationTypes.SiteRestart;
                Require((task.HealthFollowUp is not null) == eligible && (!eligible || task.HealthFollowUp!.RequestedAt != default));
            }
            return Task.CompletedTask;
        });
        await Check("success checks only enabled matching site and server and persists results separately", async () =>
        {
            using var f = new Fixture(); await f.Check("a", "Orders", "/"); await f.Check("a", "Orders", "/api");
            await f.Check("a", "Orders", "/off", false); await f.Check("a", "Other", "/"); await f.Check("b", "Orders", "/");
            var task = f.Task(); await f.Store.SaveAsync(task); await f.Service.RunPendingAsync();
            var saved = await f.Read(task); Require(saved.State == OperationStates.Succeeded && saved.HealthFollowUp!.State == "completed");
            Require(saved.HealthFollowUp!.Results.Length == 2 && f.Agent.Probes.Count == 2 && f.Agent.Probes.All(probe => probe.Server == "a" && probe.Site == "Orders"));
        });
        await Check("historical successful tasks without metadata never catch up on probes", async () =>
        {
            using var f = new Fixture(); await f.Check(); var task = f.Task() with { HealthFollowUp = null };
            await f.Store.SaveAsync(task); f.Service.Observe(task); await f.Service.RunPendingAsync();
            Require((await f.Read(task)).HealthFollowUp is null && f.Agent.Probes.IsEmpty && f.Agent.Reads.IsEmpty);
        });
        foreach (var state in new[] { OperationStates.Failed, OperationStates.Expired, OperationStates.Partial, OperationStates.Unknown })
            await Check("an explicitly " + state + " Agent result never probes", async () =>
            {
                using var f = new Fixture(); await f.Check(); var task = f.Task(state: state); f.Agent.Operations[task.Request.OperationId] = task.AgentRecord!;
                await f.Store.SaveAsync(task); await f.Service.RunPendingAsync(); var saved = await f.Read(task);
                Require(saved.State == state && saved.HealthFollowUp!.State is "skipped" or "unknown" && f.Agent.Probes.IsEmpty);
            });
        await Check("unconfirmed pre-dispatch intent waits and later confirmed success runs without a browser", async () =>
        {
            using var f = new Fixture(); await f.Check(); var task = f.Task(state: OperationStates.Unknown) with { AgentRecord = null };
            await f.Store.SaveAsync(task); await f.Service.RunPendingAsync();
            Require((await f.Read(task)).HealthFollowUp!.State == "pending" && f.Agent.Probes.IsEmpty);
            f.Agent.Operations[task.Request.OperationId] = f.Record(task, OperationStates.Succeeded); f.Clock.Advance(TimeSpan.FromSeconds(5));
            await f.Service.RunPendingAsync(); var saved = await f.Read(task);
            Require(saved.State == OperationStates.Succeeded && saved.HealthFollowUp!.State == "completed" && f.Agent.Probes.Count == 1);
        });
        await Check("a confirmed queued operation remains pending and is queried only by its original id", async () =>
        {
            using var f = new Fixture(); var task = f.Task(state: OperationStates.Queued); f.Agent.Operations[task.Request.OperationId] = task.AgentRecord!;
            await f.Store.SaveAsync(task); await f.Service.RunPendingAsync();
            Require((await f.Read(task)).HealthFollowUp!.State == "pending" && f.Agent.Probes.IsEmpty && f.Agent.Reads.Single().Route == "operations/" + task.Request.OperationId.ToString("D"));
        });
        await Check("pending timeout becomes terminal unknown and later reads never probe", async () =>
        {
            using var f = new Fixture(); await f.Check(); var task = f.Task(state: OperationStates.Unknown) with { AgentRecord = null };
            await f.Store.SaveAsync(task); f.Clock.Advance(TimeSpan.FromMinutes(10)); await f.Service.RunPendingAsync();
            var expired = await f.Read(task); Require(expired.HealthFollowUp!.ErrorCode == "HEALTH_OPERATION_WAIT_TIMEOUT");
            f.Agent.Operations[task.Request.OperationId] = f.Record(task, OperationStates.Succeeded); f.Service.Observe(expired); await f.Service.RunPendingAsync();
            Require(f.Agent.Probes.IsEmpty && f.Agent.Reads.IsEmpty);
        });
        await Check("shared pool scope uses exact application identities from Agent before and after", async () =>
        {
            using var f = new Fixture(); await f.Check("a", "Orders", "/"); await f.Check("a", "Orders", "/api");
            await f.Check("a", "Billing", "/api"); await f.Check("a", "Orders", "/unrelated");
            var task = f.Task(OperationTypes.Recycle, before: JsonSerializer.SerializeToElement(new { applications = new[] { "Orders/", "Billing/api" } }, Protocol.Json));
            await f.Store.SaveAsync(task); await f.Service.RunPendingAsync(); var saved = await f.Read(task);
            Require(saved.HealthFollowUp!.ScopeSource == "agentRecord" && saved.HealthFollowUp.Results.Length == 2 && f.Agent.Reads.IsEmpty);
            Require(f.Agent.Probes.All(probe => probe.Site + probe.Path is "Orders/" or "Billing/api"));
        });
        await Check("missing pool snapshots fall back to current topology without scanning unrelated applications", async () =>
        {
            using var f = new Fixture(); await f.Check("a", "Orders", "/api"); await f.Check("a", "Orders", "/"); await f.Check("a", "Billing", "/");
            f.Agent.Topology = new(Epoch, [new() { Name = "Orders", Applications = [new() { Path = "/api", ApplicationPoolName = "Shared" }, new() { Path = "/", ApplicationPoolName = "Other" }] }], []);
            var task = f.Task(OperationTypes.Recycle); await f.Store.SaveAsync(task); await f.Service.RunPendingAsync();
            Require((await f.Read(task)).HealthFollowUp!.ScopeSource == "topology" && f.Agent.Probes.Single().Path == "/api");
        });
        await Check("unresolved pool scope is explicit and never expands to the whole server", async () =>
        {
            using var f = new Fixture(); await f.Check(); var task = f.Task(OperationTypes.Recycle);
            await f.Store.SaveAsync(task); await f.Service.RunPendingAsync();
            Require((await f.Read(task)).HealthFollowUp!.ErrorCode == "HEALTH_SCOPE_UNRESOLVED" && f.Agent.Probes.IsEmpty);
        });
        await Check("topology read failure is isolated from a successful site task", async () =>
        {
            using var f = new Fixture(); await f.Check(); f.Agent.FailTopology = true;
            var pool = f.Task(OperationTypes.Recycle); var site = f.Task();
            await f.Store.SaveAsync(pool); await f.Store.SaveAsync(site); await f.Service.RunPendingAsync();
            Require((await f.Read(pool)).HealthFollowUp!.ErrorCode == "HEALTH_SCOPE_UNAVAILABLE");
            Require((await f.Read(site)).HealthFollowUp!.State == "completed" && f.Agent.Probes.Count == 1);
        });
        await Check("concurrent Observe and timer calls cannot duplicate a batch and running is durable before probing", async () =>
        {
            using var f = new Fixture(); await f.Check(); var task = f.Task(); await f.Store.SaveAsync(task);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Agent.Probe = async (_, _, cancellation) =>
            { Require((await f.Read(task)).HealthFollowUp!.State == "running"); entered.SetResult(); await release.Task.WaitAsync(cancellation); return Healthy(); };
            var first = f.Service.RunPendingAsync(); await entered.Task;
            f.Service.Observe(task); await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => f.Service.RunPendingAsync())); release.SetResult(); await first;
            f.Service.Observe(task); await f.Service.RunPendingAsync(); Require(f.Agent.Probes.Count == 1);
        });
        await Check("follow-up saves merge the latest task and retain concurrent history receipt fields", async () =>
        {
            using var f = new Fixture(); await f.Check(); var task = f.Task(); await f.Store.SaveAsync(task);
            f.Agent.Probe = async (_, _, _) =>
            {
                var gate = f.Store.Lock(task.Request.OperationId); await gate.WaitAsync();
                try { var latest = await f.Read(task); await f.Store.SaveAsync(latest with { PersistenceState = "synced", PersistencePrimaryId = "synthetic-history", PersistenceErrorCode = null }); }
                finally { gate.Release(); }
                return Healthy();
            };
            await f.Service.RunPendingAsync(); var saved = await f.Read(task);
            Require(saved.PersistenceState == "synced" && saved.PersistencePrimaryId == "synthetic-history" && saved.HealthFollowUp!.State == "completed");
        });
        await Check("restart marks a durable running batch unknown without retrying its probes", async () =>
        {
            using var f = new Fixture(); await f.Check(); var task = f.Task();
            task = task with { HealthFollowUp = task.HealthFollowUp! with { State = "running", StartedAt = Epoch } };
            await f.Store.SaveAsync(task); f.Restart(); await f.Service.RunPendingAsync(); var recovered = await f.Read(task);
            Require(recovered.HealthFollowUp!.State == "unknown" && recovered.HealthFollowUp.ErrorCode == "HEALTH_BATCH_INTERRUPTED" && f.Agent.Probes.IsEmpty);
            f.Service.Observe(recovered); await f.Service.RunPendingAsync(); Require(f.Agent.Probes.IsEmpty);
        });
        await Check("revisions changed before a queued follow-up check are skipped rather than probing new configuration", async () =>
        {
            using var f = new Fixture(); var first = await f.Check("a", "Orders", "/a"); var second = await f.Check("a", "Orders", "/b");
            f.Agent.Probe = async (_, request, _) =>
            {
                if (request.ApplicationPath == "/a") await f.Health.SaveAsync(new SaveHealthCheckRequest { Id = second.Id, Revision = second.Revision,
                    ServerId = second.ServerId, SiteName = second.SiteName, ApplicationPath = second.ApplicationPath, Url = second.Url, Enabled = false }, CancellationToken.None);
                return Healthy();
            };
            var task = f.Task(); await f.Store.SaveAsync(task); await f.Service.RunPendingAsync();
            var results = (await f.Read(task)).HealthFollowUp!.Results;
            Require(f.Agent.Probes.Count == 1 && results.Single(result => result.CheckId == second.Id).State == "skipped");
            Require(results.Single(result => result.CheckId == first.Id).State == "healthy");
        });
        await Check("a revision change during a probe cannot reuse a different check result", async () =>
        {
            using var f = new Fixture(); var check = await f.Check();
            f.Agent.Probe = async (_, _, _) =>
            {
                await f.Health.SaveAsync(new SaveHealthCheckRequest { Id = check.Id, Revision = check.Revision, ServerId = check.ServerId, SiteName = check.SiteName,
                    ApplicationPath = check.ApplicationPath, Url = check.Url, Enabled = false }, CancellationToken.None); return Healthy();
            };
            var task = f.Task(); await f.Store.SaveAsync(task); await f.Service.RunPendingAsync();
            var result = (await f.Read(task)).HealthFollowUp!.Results.Single(); Require(result.State == "unknown" && result.ErrorCode == "HEALTH_CHECK_RESULT_CHANGED" && result.StatusCode is null);
        });
        await Check("busy checks are skipped once while independent scheduled or manual checks remain valid", async () =>
        {
            using var f = new Fixture(); var check = await f.Check(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Agent.Probe = async (_, _, cancellation) => { entered.SetResult(); await release.Task.WaitAsync(cancellation); return Healthy(); };
            var manual = f.Health.RunAsync(check.Id, CancellationToken.None); await entered.Task;
            var task = f.Task(); await f.Store.SaveAsync(task); await f.Service.RunPendingAsync();
            Require((await f.Read(task)).HealthFollowUp!.Results.Single().ErrorCode == "HEALTH_CHECK_BUSY"); release.SetResult(); await manual;
            await f.Service.RunPendingAsync(); Require(f.Agent.Probes.Count == 1);
        });
        await Check("stop success and failed health remain distinct with no arbitrary Agent error text", async () =>
        {
            using var f = new Fixture(); await f.Check(); f.Agent.Probe = (_, _, _) => Task.FromResult(new HealthProbeResult { State = "unavailable", CheckedAt = Epoch, ErrorCode = "HEALTH_SITE_STOPPED" });
            var task = f.Task(OperationTypes.SiteStop); await f.Store.SaveAsync(task); await f.Service.RunPendingAsync(); var saved = await f.Read(task);
            Require(saved.State == OperationStates.Succeeded && saved.HealthFollowUp!.Results.Single().ErrorCode == "HEALTH_SITE_STOPPED");
            f.Agent.Probe = (_, _, _) => Task.FromResult(new HealthProbeResult { State = "unavailable", CheckedAt = Epoch, ErrorCode = "SYNTHETIC_UNTRUSTED_ERROR" });
            var next = f.Task(); await f.Store.SaveAsync(next); await f.Service.RunPendingAsync();
            Require((await f.Read(next)).HealthFollowUp!.Results.Single().ErrorCode == "HEALTH_CHECK_UNAVAILABLE");
        });
        await Check("check count is capped and the omitted matched count remains explicit", async () =>
        {
            using var f = new Fixture(); for (var i = 0; i < 21; i++) await f.Check("a", "Orders", "/app" + i.ToString("D2"));
            var task = f.Task(); await f.Store.SaveAsync(task); await f.Service.RunPendingAsync(); var batch = (await f.Read(task)).HealthFollowUp!;
            Require(batch.State == "completed" && batch.MatchedChecks == 21 && batch.Results.Length == 20 && batch.ErrorCode == "HEALTH_CHECK_LIMIT_REACHED" && f.Agent.Probes.Count == 20);
        });
        await Check("host cancellation records unknown and never automatically replays after a new service instance", async () =>
        {
            using var f = new Fixture(); await f.Check(); using var stop = new CancellationTokenSource();
            f.Agent.Probe = (_, _, cancellation) => { stop.Cancel(); cancellation.ThrowIfCancellationRequested(); return Task.FromResult(Healthy()); };
            var task = f.Task(); await f.Store.SaveAsync(task); await f.Service.RunPendingAsync(stop.Token);
            Require((await f.Read(task)).HealthFollowUp!.ErrorCode == "HEALTH_BATCH_INTERRUPTED");
            f.Restart(); await f.Service.RunPendingAsync(); Require(f.Agent.Probes.Count == 1);
        });
        await Check("a malformed status response never manufactures success or health probes", async () =>
        {
            using var f = new Fixture(); await f.Check(); var task = f.Task(state: OperationStates.Unknown) with { AgentRecord = null };
            var other = f.Task(); f.Agent.Operations[task.Request.OperationId] = f.Record(other, OperationStates.Succeeded);
            await f.Store.SaveAsync(task); await f.Service.RunPendingAsync();
            Require((await f.Read(task)).HealthFollowUp!.ErrorCode == "HEALTH_OPERATION_STATUS_INVALID" && f.Agent.Probes.IsEmpty);
        });
        System.Console.WriteLine($"Operation health follow-up self-tests: {checks - failures}/{checks} passed; synthetic ledger, Agent reads and health probes only.");
        return failures;
    }
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("TEST_ASSERTION_FAILED"); }
    private static HealthProbeResult Healthy() => new() { State = "healthy", StatusCode = 200, CheckedAt = Epoch, ElapsedMilliseconds = 2 };
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = Epoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "iis-follow-up-" + Guid.NewGuid().ToString("N"));
        public Clock Clock { get; } = new();
        public FakeAgent Agent { get; } = new();
        public HealthMonitoringService Health { get; }
        public ConsoleTaskStore Store { get; private set; }
        public OperationHealthFollowUpService Service { get; private set; }
        public Fixture()
        {
            var options = new ConsoleOptions { Servers = [new() { Id = "a" }, new() { Id = "b" }] };
            Health = new(options, Agent, Path.Combine(_root, "health"), Clock); Store = new(Path.Combine(_root, "tasks")); Service = new(Store, Health, Agent, Clock);
        }
        public Task<HealthCheckView> Check(string server = "a", string site = "Orders", string path = "/", bool enabled = true) => Health.SaveAsync(new()
        { ServerId = server, SiteName = site, ApplicationPath = path, Url = "http://synthetic-business.invalid" + path, Enabled = enabled }, CancellationToken.None);
        public ConsoleTaskRecord Task(string type = OperationTypes.SiteStart, string state = OperationStates.Succeeded, JsonElement? before = null)
        {
            var request = new OperationRequest { OperationId = Guid.NewGuid(), Type = type, Parameters = JsonSerializer.SerializeToElement(new { }),
                Target = OperationTypes.IsSiteLifecycle(type) ? new() { SiteName = "Orders" } : new() { ApplicationPoolName = "Shared" }, ExpiresAt = Epoch.AddMinutes(5) };
            var task = ConsoleTaskRecord.CreateIntent("a", request, "127.0.0.1", null, false);
            task = task with { HealthFollowUp = task.HealthFollowUp is null ? null : task.HealthFollowUp with { RequestedAt = Epoch }, State = state };
            return task with { AgentRecord = Record(task, state) with { Before = before } };
        }
        public OperationRecord Record(ConsoleTaskRecord task, string state) => new() { Request = task.Request, State = state, AcceptedAt = Epoch, CompletedAt = Epoch };
        public async Task<ConsoleTaskRecord> Read(ConsoleTaskRecord task) => (await Store.GetAsync(task.Request.OperationId))!;
        public void Restart() { Store.Dispose(); Store = new(Path.Combine(_root, "tasks")); Service = new(Store, Health, Agent, Clock); }
        public void Dispose() { Store.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
    private sealed class FakeAgent : IMonitoringAgentClient
    {
        public ConcurrentQueue<(string Server, string Site, string Path)> Probes { get; } = new();
        public ConcurrentQueue<(string Server, string Route)> Reads { get; } = new();
        public ConcurrentDictionary<Guid, OperationRecord> Operations { get; } = new();
        public SiteTopologySnapshot Topology { get; set; } = new(Epoch, [], []);
        public bool FailTopology { get; set; }
        public Func<string, HealthProbeRequest, CancellationToken, Task<HealthProbeResult>> Probe { get; set; } = (_, _, _) => System.Threading.Tasks.Task.FromResult(Healthy());
        public Task<T> GetAsync<T>(string id, string route, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); Reads.Enqueue((id, route));
            if (route == "site-topology")
            { if (FailTopology) throw new AgentCallException("AGENT_UNREACHABLE"); return System.Threading.Tasks.Task.FromResult((T)(object)Topology); }
            if (route.StartsWith("operations/", StringComparison.Ordinal) && Guid.TryParse(route[11..], out var operation) && Operations.TryGetValue(operation, out var record))
                return System.Threading.Tasks.Task.FromResult((T)(object)record);
            throw new AgentCallException("AGENT_NOT_FOUND", 404);
        }
        public Task ValidateAsync(string id, HealthProbeRequest request, CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); return System.Threading.Tasks.Task.CompletedTask; }
        public Task<HealthProbeResult> ProbeAsync(string id, HealthProbeRequest request, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); Probes.Enqueue((id, request.SiteName, request.ApplicationPath)); return Probe(id, request, cancellation); }
    }
}
