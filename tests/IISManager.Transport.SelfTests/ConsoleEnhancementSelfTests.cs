using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IISManager.Console;
using IISManager.Contracts;

internal static class ConsoleEnhancementSelfTests
{
    public static async Task<int> RunAsync()
    {
        var failures = 0; var total = 0;
        async Task Check(string name, Func<Task> action)
        {
            total++;
            try { await action(); System.Console.WriteLine("PASS enhancement " + name); }
            catch (Exception exception) { failures++; System.Console.WriteLine("FAIL enhancement " + name + " type=" + exception.GetType().Name); }
        }
        await Check("fast servers become visible while a slow server is still collecting", async () =>
        {
            var agent = new Fake(); agent.Block("slow"); var options = Options("fast", "slow");
            using var service = new OverviewRefreshService(options, new OverviewService(options, agent));
            var initial = service.GetProgress(); Require(initial.TotalServers == 2);
            await Until(() => service.GetProgress().Servers.Any(server => server.Id == "fast" && server.ConnectionState == "online"));
            var partial = service.GetProgress(); Require(partial.Refreshing && partial.CompletedServers == 1 && partial.Servers.Single(server => server.Id == "slow").ConnectionState == "pending");
            agent.Release("slow"); await Until(() => !service.GetProgress().Refreshing);
            Require(agent.Reads.Values.Sum() == 10 && agent.Writes == 0);
        });
        await Check("a slow server has its own bounded timeout without discarding a healthy neighbor", async () =>
        {
            var agent = new Fake(); agent.Block("slow"); var options = Options("fast", "slow"); options.Monitoring.OverviewTimeoutSeconds = 1;
            using var service = new OverviewRefreshService(options, new OverviewService(options, agent)); service.GetProgress();
            await Until(() => !service.GetProgress().Refreshing, 4000);
            var result = service.GetProgress(); Require(result.Servers.Single(server => server.Id == "fast").ConnectionState == "online");
            Require(result.Servers.Single(server => server.Id == "slow").Errors.All(error => error.Code == "AGENT_TIMEOUT"));
        });
        await Check("manual retries share one read and caller cancellation does not cancel shared collection", async () =>
        {
            var agent = new Fake(); agent.Block("one"); var options = Options("one");
            using var service = new OverviewRefreshService(options, new OverviewService(options, agent));
            using var canceled = new CancellationTokenSource(); var first = service.RefreshServerAsync("one", canceled.Token);
            var second = service.RefreshServerAsync("one", CancellationToken.None); canceled.Cancel();
            try { await first; throw new InvalidOperationException(); } catch (OperationCanceledException) { }
            agent.Release("one"); Require((await second).ConnectionState == "online");
            Require(service.GetProgress().Servers.Single().ConnectionState == "online" && agent.Reads.Values.Sum() == 5);
        });
        await Check("all collectors share the configured server concurrency limit", async () =>
        {
            var agent = new Fake(); foreach (var id in new[] { "one", "two", "three" }) agent.Block(id);
            var options = Options("one", "two", "three"); options.Monitoring.OverviewConcurrency = 2;
            using var service = new OverviewRefreshService(options, new OverviewService(options, agent)); service.GetProgress();
            await Until(() => agent.Reads.Keys.Count(key => key.EndsWith("/info", StringComparison.Ordinal)) == 2);
            Require(agent.Reads.Count == 10);
            foreach (var id in new[] { "one", "two", "three" }) agent.Release(id);
            await Until(() => !service.GetProgress().Refreshing);
            Require(agent.Reads.Count == 15);
        });
        await Check("configuration baselines ignore runtime state and detect actual binding changes", async () =>
        {
            using var temp = new Temp(); var agent = new Fake(); var service = new ConfigurationBaselineService(agent, temp.Path);
            var first = await service.CompareAsync("one", default); Require(first.Baseline is null);
            var saved = await service.SaveAsync("one", new("验收配置", null, first.CurrentFingerprint), "user", default);
            agent.Topology = agent.Topology with { CollectedAt = DateTimeOffset.UtcNow.AddHours(1),
                Sites = agent.Topology.Sites.Select(site => site with { State = "Stopped" }).ToArray(),
                ApplicationPools = agent.Topology.ApplicationPools.Select(pool => pool with { State = "Stopped", Fingerprint = "dynamic" }).ToArray() };
            Require((await service.CompareAsync("one", default)).Matches);
            agent.Topology = agent.Topology with { Sites = [agent.Topology.Sites[0] with { Bindings = [new BindingConfig { Port = 8081 }] }] };
            var changed = await service.CompareAsync("one", default);
            Require(!changed.Matches && changed.DifferenceCount > 0 && changed.Differences.Any(row => row.Path.Contains("Bindings", StringComparison.OrdinalIgnoreCase)));
            await Reject(() => service.SaveAsync("one", new("old preview", saved.Revision, first.CurrentFingerprint), "user", default), "BASELINE_CONFIGURATION_CHANGED");
        });
        await Check("baseline revision checks and filename encoding isolate case-sensitive server identifiers", async () =>
        {
            using var temp = new Temp(); var service = new ConfigurationBaselineService(new Fake(), temp.Path);
            var a = await service.CompareAsync("A", default); var b = await service.CompareAsync("a", default);
            await service.SaveAsync("A", new("A", null, a.CurrentFingerprint), "user", default);
            await service.SaveAsync("a", new("a", null, b.CurrentFingerprint), "user", default);
            Require(Directory.GetFiles(temp.Path, "*.json").Length == 2);
            await Reject(() => service.SaveAsync("A", new("stale", null, a.CurrentFingerprint), "user", default), "BASELINE_REVISION_CONFLICT");
            var reloaded = new ConfigurationBaselineService(new Fake(), temp.Path);
            Require((await reloaded.CompareAsync("A", default)).Baseline?.Label == "A" && (await reloaded.CompareAsync("a", default)).Baseline?.Label == "a");
        });
        await Check("external baseline drift blocks writing and retains the changed evidence", async () =>
        {
            using var temp = new Temp(); var service = new ConfigurationBaselineService(new Fake(), temp.Path);
            var first = await service.CompareAsync("one", default); var saved = await service.SaveAsync("one", new("first", null, first.CurrentFingerprint), "user", default);
            var file = Directory.GetFiles(temp.Path, "*.json").Single(); await File.AppendAllTextAsync(file, " ");
            await Reject(() => service.SaveAsync("one", new("next", saved.Revision, first.CurrentFingerprint), "user", default), "BASELINE_STORE_CHANGED");
            Require((await File.ReadAllTextAsync(file)).EndsWith(' '));
        });
        await Check("history summaries are reused but byte tampering outside the filter is never hidden", async () =>
        {
            using var temp = new Temp(); using var store = new ConsoleTaskStore(temp.Path);
            var record = Record("allowed"); await store.SaveAsync(record);
            var query = new ConsoleOperationQuery(null, null, null, null, null, null, 1, 20);
            Require((await store.QueryAsync(query)).Total == 1 && store.SummaryCacheHits > 0);
            Require((await store.QueryAsync(query, allowedServerIds: new HashSet<string>())).Total == 0);
            var file = System.IO.Path.Combine(temp.Path, record.Request.OperationId.ToString("D") + ".json");
            var stamp = File.GetLastWriteTimeUtc(file); var bytes = await File.ReadAllBytesAsync(file); bytes[0] = (byte)'!'; await File.WriteAllBytesAsync(file, bytes); File.SetLastWriteTimeUtc(file, stamp);
            try { await store.QueryAsync(query, allowedServerIds: new HashSet<string>()); throw new InvalidOperationException(); }
            catch (ConsoleLedgerUnavailableException) { Require(!store.Healthy); }
        });
        await Check("oversized baselines are rejected before replacing the previous valid file", async () =>
        {
            using var temp = new Temp(); var agent = new Fake(); var service = new ConfigurationBaselineService(agent, temp.Path);
            var before = await service.CompareAsync("one", default); var saved = await service.SaveAsync("one", new("first", null, before.CurrentFingerprint), "user", default);
            var file = Directory.GetFiles(temp.Path, "*.json").Single(); var bytes = await File.ReadAllBytesAsync(file); var original = agent.Topology;
            agent.Topology = agent.Topology with { ApplicationPools = [new PoolConfig { Name = "Pool", ManagedRuntimeVersion = new string('x', 8 * 1024 * 1024) }] };
            var large = await service.CompareAsync("one", default);
            await Reject(() => service.SaveAsync("one", new("oversized", saved.Revision, large.CurrentFingerprint), "user", default), "BASELINE_TOO_LARGE");
            var actual = await File.ReadAllBytesAsync(file); Require(bytes.SequenceEqual(actual)); agent.Topology = original;
            Require((await service.CompareAsync("one", default)).Matches);
        });
        await Check("a restarted baseline store rejects extra non-allowlisted configuration fields", async () =>
        {
            using var temp = new Temp(); var service = new ConfigurationBaselineService(new Fake(), temp.Path);
            var first = await service.CompareAsync("one", default); await service.SaveAsync("one", new("first", null, first.CurrentFingerprint), "user", default);
            var file = Directory.GetFiles(temp.Path, "*.json").Single(); var document = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
            document["configuration"]!["password"] = "synthetic-only";
            document["fingerprint"] = IISManager.Core.CanonicalJson.Hash(JsonSerializer.SerializeToElement(document["configuration"], Protocol.Json));
            await File.WriteAllTextAsync(file, document.ToJsonString());
            var restarted = new ConfigurationBaselineService(new Fake(), temp.Path);
            await Reject(() => restarted.CompareAsync("one", default), "BASELINE_STORE_UNAVAILABLE");
        });
        await Check("binding request safety rejects duplicate case variants and unknown nested fields", () =>
        {
            var request = new OperationRequest { Type = OperationTypes.UpdateSite, Target = new() { SiteName = "Example" },
                Parameters = JsonDocument.Parse("{\"bindings\":[{\"port\":80,\"PORT\":81}]}").RootElement.Clone() };
            Require(!SiteMaintenanceRequestSafety.IsValid(request));
            request = request with { Parameters = JsonDocument.Parse("{\"bindings\":[{\"password\":\"synthetic\"}]}").RootElement.Clone() };
            Require(!SiteMaintenanceRequestSafety.IsValid(request)); return Task.CompletedTask;
        });
        await Check("CSV export contains only safe summaries and escapes spreadsheet formulas", () =>
        {
            var now = DateTimeOffset.UtcNow; var row = new ConsoleOperationSummary(Guid.NewGuid(), "one", "site.stop", "=1+1", "succeeded", now, now, null, "+SUM(1,2)");
            var csv = Encoding.UTF8.GetString(OperationExport.Csv([row]));
            Require(csv.Contains("\"'=1+1\"", StringComparison.Ordinal) && csv.Contains("\"'+SUM(1,2)\"", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
        System.Console.WriteLine($"Console enhancements: {total - failures}/{total} passed."); return failures;
    }
    private static ConsoleOptions Options(params string[] ids) => new() { Servers = ids.Select(id => new ServerOptions { Id = id, Name = id, AgentUrl = "http://127.0.0.1:7444/" }).ToArray() };
    private static ConsoleTaskRecord Record(string server)
    {
        var request = new OperationRequest { OperationId = Guid.NewGuid(), Type = OperationTypes.SiteStop, Target = new() { SiteName = "Example" }, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
        return new() { Request = request, RequestHash = ConsoleTaskStore.RequestHash(server, request), ServerId = server, AcceptedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, State = "succeeded" };
    }
    private static async Task Until(Func<bool> condition, int milliseconds = 2000) { var until = DateTimeOffset.UtcNow.AddMilliseconds(milliseconds); while (!condition()) { if (DateTimeOffset.UtcNow > until) throw new TimeoutException(); await Task.Delay(5); } }
    private static async Task Reject(Func<Task> action, string code) { try { await action(); } catch (HealthCheckException exception) when (exception.Code == code) { return; } throw new InvalidOperationException(); }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException(); }
    private sealed class Temp : IDisposable { public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "iis-enhancements-" + Guid.NewGuid().ToString("N")); public Temp() { Directory.CreateDirectory(Path); } public void Dispose() => Directory.Delete(Path, true); }
    private sealed class Fake : IMonitoringAgentClient
    {
        public ConcurrentDictionary<string, int> Reads { get; } = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _blocks = new();
        public int Writes { get; private set; }
        public SiteTopologySnapshot Topology { get; set; } = new(DateTimeOffset.UtcNow,
            [new SiteTopology { Name = "Example", State = "Started", Bindings = [new BindingConfig()], Applications = [new ApplicationTopology { Path = "/", ApplicationPoolName = "Pool", VirtualDirectories = [new VirtualDirectoryTopology { Path = "/", PhysicalPath = "C:\\sites" }] }] }], [new PoolConfig { Name = "Pool", State = "Started" }]);
        public void Block(string id) => _blocks[id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release(string id) { if (_blocks.TryGetValue(id, out var signal)) signal.TrySetResult(); }
        public async Task<T> GetAsync<T>(string id, string route, CancellationToken cancellation)
        {
            Reads.AddOrUpdate(id + "/" + route, 1, (_, value) => value + 1);
            if (_blocks.TryGetValue(id, out var signal)) await signal.Task.WaitAsync(cancellation);
            object value = route switch { "info" => new AgentInfo(Protocol.Version, "fake", id, "Simulation", "fake", "fake", false, []),
                "snapshot" => new IisSnapshot(DateTimeOffset.UtcNow, [], []), "site-topology" => Topology,
                "certificates" => Array.Empty<CertificateInfo>(), "windows-services" => new WindowsServiceSnapshot(DateTimeOffset.UtcNow, [], false, null, []), _ => throw new InvalidOperationException() };
            return (T)value;
        }
        public Task ValidateAsync(string id, HealthProbeRequest request, CancellationToken cancellation) { Writes++; throw new InvalidOperationException(); }
        public Task<HealthProbeResult> ProbeAsync(string id, HealthProbeRequest request, CancellationToken cancellation) { Writes++; throw new InvalidOperationException(); }
    }
}
