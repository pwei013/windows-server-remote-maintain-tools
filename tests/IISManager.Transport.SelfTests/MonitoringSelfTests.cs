using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using IISManager.Console;
using IISManager.Contracts;

internal static class MonitoringSelfTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 25, 2, 0, 0, TimeSpan.Zero);

    public static async Task<int> RunAsync()
    {
        var checks = 0;
        var failures = 0;
        async Task Check(string name, Func<Task> assertion)
        {
            checks++;
            try { await assertion(); System.Console.WriteLine("PASS monitoring " + name); }
            catch (Exception exception)
            {
                failures++;
                System.Console.WriteLine("FAIL monitoring " + name + " type=" + exception.GetType().Name +
                    (exception is HealthCheckException health ? " code=" + health.Code : ""));
            }
        }

        await Check("overview collects all modules and excludes unmanaged services", async () =>
        {
            using var fixture = new Fixture();
            var overview = await fixture.Overview.GetAsync(CancellationToken.None);
            var server = overview.Servers.Single();
            Require(server.ConnectionState == "online" && server.Info is not null && server.Snapshot is not null &&
                server.Topology is not null && server.Services is not null && server.Errors.Length == 0);
            Require(server.Services!.Services.Select(service => service.Name).SequenceEqual(["ManagedService"]));
            Require(server.HostName == "synthetic-agent.invalid" && fixture.Agent.Reads.Count == 5);
        });

        await Check("failed certificate module preserves the other modules without inventing missing certificates", async () =>
        {
            using var fixture = new Fixture();
            fixture.Agent.Errors["certificates"] = "AGENT_UNAVAILABLE";
            var server = (await fixture.Overview.GetAsync(CancellationToken.None)).Servers.Single();
            Require(server.ConnectionState == "partial" && server.Info is not null && server.Snapshot is not null &&
                server.Topology is not null && server.Services is not null && server.Certificates.Length == 0);
            Require(server.Errors.Single() == new OverviewModuleError("certificates", "AGENT_UNAVAILABLE"));
        });

        await Check("available topology and services mean partial availability when info and snapshot fail", async () =>
        {
            using var fixture = new Fixture();
            fixture.Agent.Errors["info"] = "AGENT_UNAVAILABLE";
            fixture.Agent.Errors["snapshot"] = "AGENT_UNAVAILABLE";
            var server = (await fixture.Overview.GetAsync(CancellationToken.None)).Servers.Single();
            Require(server.ConnectionState == "partial" && server.Info is null && server.Snapshot is null &&
                server.Topology is not null && server.Services is not null && server.Errors.Length == 2);
        });

        await Check("a malformed successful module remains isolated from valid inventories", async () =>
        {
            using var fixture = new Fixture();
            foreach (var malformed in new[] { new IisSnapshot(Epoch, null!, []), new IisSnapshot(Epoch, [],
                [new SiteConfig { Name = "Malformed", Bindings = [new BindingConfig { Protocol = null! }] }]) })
            {
                fixture.Agent.Payloads["snapshot"] = malformed;
                var isolated = new OverviewService(fixture.Options, fixture.Agent, fixture.Clock);
                var server = (await isolated.GetAsync(CancellationToken.None)).Servers.Single();
                Require(server.ConnectionState == "partial" && server.Snapshot is null && server.Topology is not null);
                Require(server.Errors.Single() == new OverviewModuleError("snapshot", "AGENT_RESPONSE_INVALID"));
            }
        });

        await Check("all failed modules report offline rather than an empty healthy inventory", async () =>
        {
            using var fixture = new Fixture();
            foreach (var route in new[] { "info", "snapshot", "site-topology", "certificates", "windows-services" })
                fixture.Agent.Errors[route] = "AGENT_UNAVAILABLE";
            var server = (await fixture.Overview.GetAsync(CancellationToken.None)).Servers.Single();
            Require(server.ConnectionState == "offline" && server.Errors.Length == 5 && server.Snapshot is null &&
                server.Topology is null && server.Services is null && server.Certificates.Length == 0);
        });

        await Check("overview cache expires at its configured boundary using the injected clock", async () =>
        {
            using var fixture = new Fixture();
            var first = await fixture.Overview.GetAsync(CancellationToken.None);
            fixture.Clock.Advance(TimeSpan.FromSeconds(14));
            var cached = await fixture.Overview.GetAsync(CancellationToken.None);
            Require(fixture.Agent.Reads.Count == 5 && cached.CollectedAt == first.CollectedAt);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            var refreshed = await fixture.Overview.GetAsync(CancellationToken.None);
            Require(fixture.Agent.Reads.Count == 10 && refreshed.CollectedAt == Epoch.AddSeconds(15));
        });

        await Check("unsupported agent protocol does not expose inventories as compatible data", async () =>
        {
            using var fixture = new Fixture();
            fixture.Agent.Info = fixture.Agent.Info with { ProtocolVersion = "999.0" };
            var server = (await fixture.Overview.GetAsync(CancellationToken.None)).Servers.Single();
            Require(server.ConnectionState != "online" && server.Snapshot is null && server.Topology is null &&
                server.Services is null && server.Certificates.Length == 0 &&
                server.Errors.Any(error => error.Code == "AGENT_PROTOCOL_UNSUPPORTED"));
        });

        var boundaries = new (string Name, TimeSpan Remaining, string Severity)[]
        {
            ("above thirty days", TimeSpan.FromDays(30).Add(TimeSpan.FromTicks(1)), "normal"),
            ("exactly thirty days", TimeSpan.FromDays(30), "notice"),
            ("above fifteen days", TimeSpan.FromDays(15).Add(TimeSpan.FromTicks(1)), "notice"),
            ("exactly fifteen days", TimeSpan.FromDays(15), "warning"),
            ("above seven days", TimeSpan.FromDays(7).Add(TimeSpan.FromTicks(1)), "warning"),
            ("exactly seven days", TimeSpan.FromDays(7), "critical"),
            ("positive fraction of one day", TimeSpan.FromHours(1), "critical"),
            ("exact expiry instant", TimeSpan.Zero, "expired"),
            ("already expired", TimeSpan.FromDays(-1), "expired")
        };
        foreach (var boundary in boundaries)
            await Check("certificate threshold " + boundary.Name, () =>
            {
                var alert = OverviewService.BuildCertificateAlerts([Certificate("AA", Epoch + boundary.Remaining)], [], Epoch).Single();
                Require(alert.Severity == boundary.Severity);
                if (boundary.Remaining == TimeSpan.FromHours(1)) Require(alert.DaysRemaining == 1);
                return Task.CompletedTask;
            });

        await Check("future certificate and missing private key remain explicitly visible", () =>
        {
            var certificates = new[]
            {
                Certificate("AA", Epoch.AddDays(60)) with { NotBefore = Epoch.AddDays(1) },
                Certificate("BB", Epoch.AddDays(60)) with { HasPrivateKey = false }
            };
            var alerts = OverviewService.BuildCertificateAlerts(certificates, [], Epoch);
            Require(alerts.Single(alert => alert.Thumbprint == "AA").Severity == "notYetValid");
            Require(!alerts.Single(alert => alert.Thumbprint == "BB").HasPrivateKey);
            return Task.CompletedTask;
        });

        await Check("certificate and binding deduplication preserves distinct affected sites and missing bindings", () =>
        {
            var first = new SiteConfig { Name = "First", Bindings =
                [Binding("aa bb"), Binding("AA BB"), Binding("CCDD"), Binding("EEFF") with { Protocol = "http" }] };
            var second = new SiteConfig { Name = "Second", Bindings = [Binding("AABB")] };
            var alerts = OverviewService.BuildCertificateAlerts(
                [Certificate("aa bb", Epoch.AddDays(20)), Certificate("AABB", Epoch.AddDays(20))], [first, second], Epoch);
            Require(alerts.Length == 2);
            var known = alerts.Single(alert => alert.Thumbprint == "AABB");
            Require(known.Bindings.Length == 2 && known.Bindings.Select(binding => binding.SiteName).Order().SequenceEqual(["First", "Second"]));
            var missing = alerts.Single(alert => alert.Thumbprint == "CCDD");
            Require(missing.Severity == "missing" && missing.NotAfter is null && missing.DaysRemaining is null &&
                missing.Bindings.Single().SiteName == "First");
            return Task.CompletedTask;
        });

        await Check("saving validates the selected target without probing and defaults to disabled", async () =>
        {
            using var fixture = new Fixture();
            var view = await fixture.Service.SaveAsync(Request(), CancellationToken.None);
            Require(fixture.Agent.Validations == 1 && fixture.Agent.Probes == 0 && !view.Enabled &&
                view.Result is null && view.LastSuccessAt is null && view.NextCheckAt is null);
            Require(fixture.Agent.LastValidation == Request().ToProbe() && File.Exists(fixture.StoreFile));
            Require((await fixture.Reload().GetViewsAsync()).Single().Id == view.Id);
        });

        await Check("disabled checks cannot run manually or through the scheduler", async () =>
        {
            using var fixture = new Fixture();
            var view = await fixture.Service.SaveAsync(Request(), CancellationToken.None);
            await Reject(() => fixture.Service.RunAsync(view.Id, CancellationToken.None), "HEALTH_CHECK_DISABLED", 409);
            await fixture.Service.RunDueAsync(CancellationToken.None);
            Require(fixture.Agent.Probes == 0 && (await fixture.Service.GetViewsAsync()).Single().Result is null);
        });

        await Check("stale edit revision is rejected without replacing the persisted configuration", async () =>
        {
            using var fixture = new Fixture();
            var original = await fixture.Service.SaveAsync(Request(), CancellationToken.None);
            var updated = await fixture.Service.SaveAsync(Edit(original) with { Url = "https://example.invalid/ready" }, CancellationToken.None);
            var bytes = await File.ReadAllBytesAsync(fixture.StoreFile);
            await Reject(() => fixture.Service.SaveAsync(Edit(original) with { Enabled = true }, CancellationToken.None), "HEALTH_CONFIG_CONFLICT", 409);
            Require(updated.Revision != original.Revision && (await fixture.Service.GetViewsAsync()).Single() == updated);
            var persisted = await File.ReadAllBytesAsync(fixture.StoreFile);
            Require(bytes.SequenceEqual(persisted) && fixture.Agent.Probes == 0);
        });

        await Check("an existing check cannot be moved across server authorization scopes", async () =>
        {
            using var fixture = new Fixture();
            var original = await fixture.Service.SaveAsync(Request(), CancellationToken.None);
            var validations = fixture.Agent.Validations;
            await Reject(() => fixture.Service.SaveAsync(Edit(original) with { ServerId = "other" }, CancellationToken.None), "HEALTH_SERVER_IMMUTABLE", 409);
            Require((await fixture.Service.GetViewsAsync()).Single() == original && fixture.Agent.Validations == validations);
        });

        await Check("a second check for the same server site and application is rejected", async () =>
        {
            using var fixture = new Fixture();
            var original = await fixture.Service.SaveAsync(Request(), CancellationToken.None);
            var bytes = await File.ReadAllBytesAsync(fixture.StoreFile);
            await Reject(() => fixture.Service.SaveAsync(Request() with { Url = "https://example.invalid/another-health" }, CancellationToken.None),
                "HEALTH_TARGET_DUPLICATED", 409);
            await Reject(() => fixture.Service.SaveAsync(Request() with { SiteName = "EXAMPLESITE" }, CancellationToken.None),
                "HEALTH_TARGET_DUPLICATED", 409);
            Require((await fixture.Service.GetViewsAsync()).Single() == original && ((IEnumerable<byte>)bytes).SequenceEqual(await File.ReadAllBytesAsync(fixture.StoreFile)));
        });

        await Check("saved configuration and observations survive a new service instance", async () =>
        {
            using var fixture = new Fixture();
            var saved = await fixture.Service.SaveAsync(Request() with { Enabled = true }, CancellationToken.None);
            fixture.Agent.Probe = (_, _, _) => Task.FromResult(Result("healthy", fixture.Clock.GetUtcNow(), 200));
            var completed = await fixture.Service.RunAsync(saved.Id, CancellationToken.None);
            var reloaded = (await fixture.Reload().GetViewsAsync()).Single();
            Require(reloaded == completed && reloaded.Result?.State == "healthy" && reloaded.LastSuccessAt == Epoch &&
                reloaded.NextCheckAt == Epoch.AddSeconds(60));
            Require(!Directory.EnumerateFiles(fixture.DirectoryPath).Any(path => path.EndsWith(".tmp", StringComparison.Ordinal)));
        });

        await Check("an unhealthy response retains the last success and schedules the next check", async () =>
        {
            using var fixture = new Fixture();
            var saved = await fixture.Service.SaveAsync(Request() with { Enabled = true }, CancellationToken.None);
            fixture.Agent.Probe = (_, _, _) => Task.FromResult(Result("healthy", fixture.Clock.GetUtcNow(), 200));
            await fixture.Service.RunAsync(saved.Id, CancellationToken.None);
            fixture.Clock.Advance(TimeSpan.FromSeconds(60));
            fixture.Agent.Probe = (_, _, _) => Task.FromResult(Result("unhealthy", fixture.Clock.GetUtcNow(), 503));
            var failed = await fixture.Service.RunAsync(saved.Id, CancellationToken.None);
            Require(failed.Result?.State == "unhealthy" && failed.Result.StatusCode == 503 && failed.LastSuccessAt == Epoch &&
                failed.NextCheckAt == Epoch.AddSeconds(120) && (await fixture.Reload().GetViewsAsync()).Single() == failed);
        });

        await Check("an unavailable agent retains the prior success without reporting business success", async () =>
        {
            using var fixture = new Fixture();
            var saved = await fixture.Service.SaveAsync(Request() with { Enabled = true }, CancellationToken.None);
            fixture.Agent.Probe = (_, _, _) => Task.FromResult(Result("healthy", fixture.Clock.GetUtcNow(), 200));
            await fixture.Service.RunAsync(saved.Id, CancellationToken.None);
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            fixture.Agent.Probe = (_, _, _) => throw new AgentCallException("AGENT_UNAVAILABLE");
            var failed = await fixture.Service.RunAsync(saved.Id, CancellationToken.None);
            Require(failed.Result?.State == "unavailable" && failed.Result.ErrorCode == "AGENT_UNAVAILABLE" &&
                failed.Result.StatusCode is null && failed.LastSuccessAt == Epoch);
        });

        await Check("simulation results never create a successful business health timestamp", async () =>
        {
            using var fixture = new Fixture(simulation: true);
            var saved = await fixture.Service.SaveAsync(Request() with { Enabled = true }, CancellationToken.None);
            fixture.Agent.Probe = (_, _, _) => Task.FromResult(Result("simulation", fixture.Clock.GetUtcNow()) with { ErrorCode = "HEALTH_SIMULATION" });
            var completed = await fixture.Service.RunAsync(saved.Id, CancellationToken.None);
            Require(completed.Result?.State == "simulation" && completed.Result.StatusCode is null && completed.LastSuccessAt is null);
            Require((await fixture.Reload().GetViewsAsync()).Single().LastSuccessAt is null);
        });

        await Check("a result in flight cannot overwrite an edited and disabled target", async () =>
        {
            using var fixture = new Fixture();
            var saved = await fixture.Service.SaveAsync(Request() with { Enabled = true }, CancellationToken.None);
            var entered = NewSignal(); var release = new TaskCompletionSource<HealthProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Agent.Probe = (_, _, _) => { entered.TrySetResult(true); return release.Task; };
            var running = fixture.Service.RunAsync(saved.Id, CancellationToken.None);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var edited = await fixture.Service.SaveAsync(Edit(saved) with { Enabled = false, Url = "https://example.invalid/changed" }, CancellationToken.None);
                release.TrySetResult(Result("healthy", fixture.Clock.GetUtcNow(), 200));
                Require(await running.WaitAsync(TimeSpan.FromSeconds(5)) == edited);
                var current = (await fixture.Reload().GetViewsAsync()).Single();
                Require(current == edited && current.Result is null && current.LastSuccessAt is null && current.NextCheckAt is null);
            }
            finally { release.TrySetResult(Result("unavailable", fixture.Clock.GetUtcNow())); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        });

        await Check("concurrent manual and scheduled runs never probe a target twice and busy deletion is rejected", async () =>
        {
            using var fixture = new Fixture();
            var saved = await fixture.Service.SaveAsync(Request() with { Enabled = true }, CancellationToken.None);
            var entered = NewSignal(); var release = new TaskCompletionSource<HealthProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Agent.Probe = (_, _, _) => { entered.TrySetResult(true); return release.Task; };
            var running = fixture.Service.RunAsync(saved.Id, CancellationToken.None);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Reject(() => fixture.Service.RunAsync(saved.Id, CancellationToken.None), "HEALTH_CHECK_BUSY", 409);
                await fixture.Service.RunDueAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                await Reject(() => fixture.Service.DeleteAsync(saved.Id, saved.Revision, CancellationToken.None), "HEALTH_CHECK_BUSY", 409);
                Require(fixture.Agent.Probes == 1 && (await fixture.Service.GetViewsAsync()).Length == 1);
            }
            finally { release.TrySetResult(Result("healthy", fixture.Clock.GetUtcNow(), 200)); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            await fixture.Service.RunAsync(saved.Id, CancellationToken.None);
            Require(fixture.Agent.Probes == 2);
        });

        await Check("the scheduler respects the next due time including its exact boundary", async () =>
        {
            using var fixture = new Fixture();
            await fixture.Service.SaveAsync(Request() with { Enabled = true }, CancellationToken.None);
            fixture.Agent.Probe = (_, _, _) => Task.FromResult(Result("healthy", fixture.Clock.GetUtcNow(), 200));
            await fixture.Service.RunDueAsync(CancellationToken.None);
            Require(fixture.Agent.Probes == 1);
            fixture.Clock.Advance(TimeSpan.FromSeconds(59));
            await fixture.Service.RunDueAsync(CancellationToken.None);
            Require(fixture.Agent.Probes == 1);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.Service.RunDueAsync(CancellationToken.None);
            Require(fixture.Agent.Probes == 2 && (await fixture.Service.GetViewsAsync()).Single().LastSuccessAt == Epoch.AddMinutes(1));
        });

        await Check("delete requires the current revision and stays deleted after reload", async () =>
        {
            using var fixture = new Fixture();
            var saved = await fixture.Service.SaveAsync(Request(), CancellationToken.None);
            await Reject(() => fixture.Service.DeleteAsync(saved.Id, Guid.NewGuid().ToString("N"), CancellationToken.None), "HEALTH_CONFIG_CONFLICT", 409);
            Require((await fixture.Service.GetViewsAsync()).Length == 1);
            await fixture.Service.DeleteAsync(saved.Id, saved.Revision, CancellationToken.None);
            Require((await fixture.Reload().GetViewsAsync()).Length == 0 && fixture.Agent.Probes == 0);
            await Reject(() => fixture.Service.RunAsync(saved.Id, CancellationToken.None), "HEALTH_CHECK_NOT_FOUND", 404);
        });

        await Check("malformed stored JSON blocks reads and writes without replacing the damaged evidence", async () =>
        {
            using var fixture = new Fixture();
            var bytes = Encoding.UTF8.GetBytes("{not valid json");
            await File.WriteAllBytesAsync(fixture.StoreFile, bytes);
            var damaged = fixture.Reload();
            await Reject(() => damaged.GetViewsAsync(), "HEALTH_STORE_UNAVAILABLE", 503);
            await Reject(() => damaged.SaveAsync(Request(), CancellationToken.None), "HEALTH_STORE_UNAVAILABLE", 503);
            await Reject(() => damaged.DeleteAsync(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("N"), CancellationToken.None),
                "HEALTH_STORE_UNAVAILABLE", 503);
            Require(((IEnumerable<byte>)bytes).SequenceEqual(await File.ReadAllBytesAsync(fixture.StoreFile)) && fixture.Agent.Probes == 0);
        });

        await Check("semantically invalid stored version is unavailable rather than accepted or reset", async () =>
        {
            using var fixture = new Fixture();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new HealthCheckFile(99, []), Protocol.Json);
            await File.WriteAllBytesAsync(fixture.StoreFile, bytes);
            var damaged = fixture.Reload();
            await Reject(() => damaged.GetViewsAsync(), "HEALTH_STORE_UNAVAILABLE", 503);
            await Reject(() => damaged.SaveAsync(Request(), CancellationToken.None), "HEALTH_STORE_UNAVAILABLE", 503);
            Require(((IEnumerable<byte>)bytes).SequenceEqual(await File.ReadAllBytesAsync(fixture.StoreFile)));
        });

        await Check("duplicate identities in stored configuration cannot be loaded or silently repaired", async () =>
        {
            using var fixture = new Fixture();
            var saved = await fixture.Service.SaveAsync(Request(), CancellationToken.None);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new HealthCheckFile(1, [saved, saved]), Protocol.Json);
            await File.WriteAllBytesAsync(fixture.StoreFile, bytes);
            var damaged = fixture.Reload();
            await Reject(() => damaged.GetViewsAsync(), "HEALTH_STORE_UNAVAILABLE", 503);
            Require(((IEnumerable<byte>)bytes).SequenceEqual(await File.ReadAllBytesAsync(fixture.StoreFile)));
        });

        await Check("external file edits prevent overwriting and poison subsequent writes", async () =>
        {
            using var fixture = new Fixture();
            var saved = await fixture.Service.SaveAsync(Request(), CancellationToken.None);
            var bytes = (await File.ReadAllBytesAsync(fixture.StoreFile)).Concat(Encoding.UTF8.GetBytes("\n ")).ToArray();
            await File.WriteAllBytesAsync(fixture.StoreFile, bytes);
            await Reject(() => fixture.Service.SaveAsync(Edit(saved) with { Enabled = true }, CancellationToken.None), "HEALTH_STORE_CHANGED", 503);
            await Reject(() => fixture.Service.DeleteAsync(saved.Id, saved.Revision, CancellationToken.None), "HEALTH_STORE_UNAVAILABLE", 503);
            Require(((IEnumerable<byte>)bytes).SequenceEqual(await File.ReadAllBytesAsync(fixture.StoreFile)) && fixture.Agent.Probes == 0);
        });

        await Check("externally deleted configuration is never recreated by a stale instance", async () =>
        {
            using var fixture = new Fixture();
            var saved = await fixture.Service.SaveAsync(Request(), CancellationToken.None);
            File.Delete(fixture.StoreFile);
            await Reject(() => fixture.Service.DeleteAsync(saved.Id, saved.Revision, CancellationToken.None), "HEALTH_STORE_CHANGED", 503);
            Require(!File.Exists(fixture.StoreFile));
            await Reject(() => fixture.Service.SaveAsync(Edit(saved), CancellationToken.None), "HEALTH_STORE_UNAVAILABLE", 503);
        });

        await Check("a store created by another instance cannot be replaced by an initially empty instance", async () =>
        {
            using var fixture = new Fixture();
            var fresh = fixture.Reload();
            await fixture.Service.SaveAsync(Request(), CancellationToken.None);
            var bytes = await File.ReadAllBytesAsync(fixture.StoreFile);
            await Reject(() => fresh.SaveAsync(Request() with { SiteName = "AnotherSite" }, CancellationToken.None), "HEALTH_STORE_CHANGED", 503);
            Require(((IEnumerable<byte>)bytes).SequenceEqual(await File.ReadAllBytesAsync(fixture.StoreFile)));
        });

        await Check("an old agent blocks saving without a business probe or configuration write", async () =>
        {
            using var fixture = new Fixture();
            fixture.Agent.Validation = (_, _, _) => throw new AgentCallException("AGENT_NOT_FOUND");
            await Reject(() => fixture.Service.SaveAsync(Request() with { Enabled = true }, CancellationToken.None), "HEALTH_AGENT_UPGRADE_REQUIRED", 501);
            Require(fixture.Agent.Probes == 0 && !File.Exists(fixture.StoreFile) && (await fixture.Service.GetViewsAsync()).Length == 0);
        });

        await Check("an agent downgraded after saving reports unsupported and never fabricates success", async () =>
        {
            using var fixture = new Fixture();
            var saved = await fixture.Service.SaveAsync(Request() with { Enabled = true }, CancellationToken.None);
            fixture.Agent.Probe = (_, _, _) => throw new AgentCallException("AGENT_NOT_FOUND");
            var result = await fixture.Service.RunAsync(saved.Id, CancellationToken.None);
            Require(result.Result?.State == "unsupported" && result.Result.ErrorCode == "HEALTH_AGENT_UPGRADE_REQUIRED" &&
                result.LastSuccessAt is null && result.Result.StatusCode is null);
            Require((await fixture.Reload().GetViewsAsync()).Single() == result);
        });

        await Check("invalid agent results cannot be persisted as business success", async () =>
        {
            using var fixture = new Fixture();
            var saved = await fixture.Service.SaveAsync(Request() with { Enabled = true }, CancellationToken.None);
            foreach (var invalid in new[] { new HealthProbeResult { State = "healthy" }, Result("healthy", Epoch, 204),
                Result("healthy", Epoch), Result("unhealthy", Epoch, 200),
                Result("healthy", Epoch, 200) with { ErrorCode = "HEALTH_TLS_ERROR" } })
            {
                fixture.Agent.Probe = (_, _, _) => Task.FromResult(invalid);
                var result = await fixture.Service.RunAsync(saved.Id, CancellationToken.None);
                Require(result.Result?.State == "unavailable" && result.Result.ErrorCode == "AGENT_RESPONSE_INVALID" && result.LastSuccessAt is null);
            }
        });

        await Check("unsafe addresses are rejected before validation or probing", async () =>
        {
            using var fixture = new Fixture();
            foreach (var url in new[] { "https://example.invalid/health?token=synthetic", "https://example.invalid/health#fragment",
                         "https://synthetic:synthetic@example.invalid/health", "file:///synthetic", " https://example.invalid/health" })
                await Reject(() => fixture.Service.SaveAsync(Request() with { Url = url }, CancellationToken.None), "HEALTH_CONFIG_INVALID", 400);
            Require(fixture.Agent.Validations == 0 && fixture.Agent.Probes == 0 && !File.Exists(fixture.StoreFile));
        });

        System.Console.WriteLine($"Monitoring self-tests: {checks - failures}/{checks} passed. Synthetic agents and temporary files only; no network calls.");
        return failures;
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("TEST_ASSERTION_FAILED");
    }

    private static async Task Reject(Func<Task> action, string code, int status)
    {
        try { await action(); }
        catch (HealthCheckException exception) when (exception.Code == code && exception.StatusCode == status) { return; }
        throw new InvalidOperationException("TEST_EXPECTED_REJECTION");
    }

    private static SaveHealthCheckRequest Request() => new()
    {
        ServerId = "synthetic", SiteName = "ExampleSite", ApplicationPath = "/", Url = "https://example.invalid/health"
    };

    private static SaveHealthCheckRequest Edit(HealthCheckView view) => new()
    {
        Id = view.Id, Revision = view.Revision, ServerId = view.ServerId, SiteName = view.SiteName,
        ApplicationPath = view.ApplicationPath, Url = view.Url, ExpectedStatusCode = view.ExpectedStatusCode,
        TimeoutSeconds = view.TimeoutSeconds, IntervalSeconds = view.IntervalSeconds, Enabled = view.Enabled
    };

    private static HealthProbeResult Result(string state, DateTimeOffset at, int? status = null) => new()
    {
        State = state, CheckedAt = at, StatusCode = status, ElapsedMilliseconds = status is null ? null : 25
    };

    private static CertificateInfo Certificate(string thumbprint, DateTimeOffset expires) =>
        new(thumbprint, "CN=example.invalid", "Synthetic certificate", Epoch.AddDays(-1), expires, true);

    private static BindingConfig Binding(string thumbprint) => new()
    {
        Protocol = "https", HostName = "example.invalid", Port = 443, CertificateThumbprint = thumbprint, UseSni = true
    };

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = Epoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "iis-monitoring-selftest-" + Guid.NewGuid().ToString("N"));
        public string StoreFile => Path.Combine(DirectoryPath, "health-checks.json");
        public Clock Clock { get; } = new();
        public FakeAgent Agent { get; } = new();
        public ConsoleOptions Options { get; }
        public HealthMonitoringService Service { get; }
        public OverviewService Overview { get; }

        public Fixture(bool simulation = false)
        {
            Directory.CreateDirectory(DirectoryPath);
            Options = new ConsoleOptions
            {
                Servers = [new ServerOptions { Id = "synthetic", Name = "Synthetic server", AgentUrl = "https://synthetic-agent.invalid/", Simulation = simulation }],
                Monitoring = new MonitoringOptions { DataDirectory = DirectoryPath, OverviewCacheSeconds = 15 }
            };
            Service = Reload();
            Overview = new OverviewService(Options, Agent, Clock);
        }

        public HealthMonitoringService Reload() => new(Options, Agent, DirectoryPath, Clock);
        public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true); }
    }

    private sealed class FakeAgent : IMonitoringAgentClient
    {
        public readonly ConcurrentQueue<string> Reads = new();
        public readonly ConcurrentDictionary<string, string> Errors = new(StringComparer.Ordinal);
        public readonly Dictionary<string, object> Payloads = new(StringComparer.Ordinal);
        private int _validations;
        private int _probes;
        public int Validations => Volatile.Read(ref _validations);
        public int Probes => Volatile.Read(ref _probes);
        public HealthProbeRequest? LastValidation { get; private set; }
        public AgentInfo Info { get; set; } = new(Protocol.Version, "synthetic", "SyntheticMachine", "Windows", "SyntheticOS", "SyntheticIIS", false, [7443, 7444]);
        public Func<string, HealthProbeRequest, CancellationToken, Task>? Validation { get; set; }
        public Func<string, HealthProbeRequest, CancellationToken, Task<HealthProbeResult>> Probe { get; set; } =
            (_, _, _) => Task.FromResult(Result("unavailable", Epoch));

        public Task<T> GetAsync<T>(string id, string route, CancellationToken cancellation)
        {
            Require(id == "synthetic"); cancellation.ThrowIfCancellationRequested(); Reads.Enqueue(route);
            if (Errors.TryGetValue(route, out var code)) throw new AgentCallException(code);
            if (Payloads.TryGetValue(route, out var payload)) return Task.FromResult((T)payload);
            object value = route switch
            {
                "info" => Info,
                "snapshot" => new IisSnapshot(Epoch, [new PoolConfig { Name = "ExamplePool", State = "Started" }],
                    [new SiteConfig { Name = "ExampleSite", State = "Started", ApplicationPoolName = "ExamplePool", Bindings = [Binding("AABB")] }]),
                "site-topology" => new SiteTopologySnapshot(Epoch,
                    [new SiteTopology { Name = "ExampleSite", State = "Started", Bindings = [Binding("AABB")],
                        Applications = [new ApplicationTopology { Path = "/", ApplicationPoolName = "ExamplePool" }] }], []),
                "certificates" => new[] { Certificate("AABB", Epoch.AddDays(20)) },
                "windows-services" => new WindowsServiceSnapshot(Epoch,
                    [new WindowsServiceConfig { Name = "ManagedService", Managed = true, State = "Running" },
                     new WindowsServiceConfig { Name = "UnmanagedService", Managed = false, State = "Stopped" }], false, null, []),
                _ => throw new InvalidOperationException("TEST_UNEXPECTED_AGENT_ROUTE")
            };
            return Task.FromResult((T)value);
        }

        public Task ValidateAsync(string id, HealthProbeRequest request, CancellationToken cancellation)
        {
            Require(id == "synthetic"); cancellation.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _validations); LastValidation = request;
            return Validation?.Invoke(id, request, cancellation) ?? Task.CompletedTask;
        }

        public Task<HealthProbeResult> ProbeAsync(string id, HealthProbeRequest request, CancellationToken cancellation)
        {
            Require(id == "synthetic"); cancellation.ThrowIfCancellationRequested(); Interlocked.Increment(ref _probes);
            return Probe(id, request, cancellation);
        }
    }
}
