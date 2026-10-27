using System.Net;
using IISManager.Agent;
using IISManager.Contracts;
using IISManager.Core;

internal static class AgentHealthSelfTests
{
    public static async Task<int> RunAsync()
    {
        var checks = 0; var failures = 0;
        async Task Check(string name, Func<Task> assertion)
        {
            checks++;
            try { await assertion(); System.Console.WriteLine("PASS " + name); }
            catch (Exception exception) { failures++; System.Console.WriteLine("FAIL " + name + " type=" + exception.GetType().Name); }
        }

        await Check("health validation checks the current topology without sending HTTP", async () =>
        {
            var backend = new Topology(); var sends = 0;
            using var service = Service(backend, (_, _, _) => { sends++; return Task.FromResult(200); });
            await service.ValidateAsync(Request());
            Require(backend.Reads == 1 && sends == 0);
        });
        await Check("health GET pins wildcard bindings to loopback and carries no credentials", async () =>
        {
            var sent = 0;
            using var service = Service(new Topology(), (message, address, _) =>
            {
                sent++;
                Require(message.Method == HttpMethod.Get && message.RequestUri!.Host == "app.internal");
                Require(address.Equals(IPAddress.Loopback) && message.Content is null);
                Require(message.Headers.Authorization is null && !message.Headers.Contains("X-IISManager-Token") &&
                    !message.Headers.Contains("Cookie"));
                return Task.FromResult(200);
            });
            var result = await service.ProbeAsync(Request());
            Require(result.State == "healthy" && result.StatusCode == 200 && result.ErrorCode is null &&
                result.CheckedAt != default && result.ElapsedMilliseconds >= 0 && sent == 1);
        });
        await Check("health uses the exact configured success status", async () =>
        {
            using var service = Service(new Topology(), (_, _, _) => Task.FromResult(204));
            var mismatch = await service.ProbeAsync(Request());
            Require(mismatch.State == "unhealthy" && mismatch.StatusCode == 204 && mismatch.ErrorCode == "HEALTH_STATUS_MISMATCH");
            Require((await service.ProbeAsync(Request() with { ExpectedStatusCode = 204 })).State == "healthy");
        });
        await Check("health redirect is an unhealthy result without another request", async () =>
        {
            var sends = 0;
            using var service = Service(new Topology(), (_, _, _) => { sends++; return Task.FromResult(302); });
            var result = await service.ProbeAsync(Request());
            Require(result.State == "unhealthy" && result.StatusCode == 302 && sends == 1);
        });
        await Check("health transport errors never disclose exception text", async () =>
        {
            using var service = Service(new Topology(), (_, _, _) => throw new HttpRequestException("SYNTHETIC-PRIVATE-DETAIL"));
            var result = await service.ProbeAsync(Request());
            Require(result.State == "unavailable" && result.ErrorCode == "HEALTH_CONNECTION_FAILED" && result.StatusCode is null);
        });
        await Check("health TLS failures have a safe distinct code", async () =>
        {
            using var service = Service(new Topology(), (_, _, _) =>
                throw new HttpRequestException(HttpRequestError.SecureConnectionError, "SYNTHETIC-PRIVATE-DETAIL"));
            var result = await service.ProbeAsync(Request() with { Url = "https://app.internal/health" });
            Require(result.State == "unavailable" && result.ErrorCode == "HEALTH_TLS_ERROR");
        });
        await Check("health timeout cancels the in-flight transport", async () =>
        {
            var cancelled = false;
            using var service = Service(new Topology(), async (_, _, cancellation) =>
            {
                try { await Task.Delay(Timeout.Infinite, cancellation); }
                catch (OperationCanceledException) { cancelled = true; throw; }
                return 200;
            });
            var result = await service.ProbeAsync(Request() with { TimeoutSeconds = 1 });
            Require(cancelled && result.State == "unavailable" && result.ErrorCode == "HEALTH_TIMEOUT");
        });
        await Check("health caller cancellation is propagated", async () =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            using var service = Service(new Topology());
            try { await service.ProbeAsync(Request(), cancellation.Token); }
            catch (OperationCanceledException) { return; }
            throw new InvalidOperationException("EXPECTED_CANCELLATION");
        });
        await Check("health simulation is explicit and never sends HTTP", async () =>
        {
            var sends = 0;
            using var service = Service(new Topology(), (_, _, _) => { sends++; return Task.FromResult(200); }, "Simulation");
            var result = await service.ProbeAsync(Request());
            Require(result.State == "simulation" && result.StatusCode is null && sends == 0);
        });
        await Check("health saves stopped targets but never probes a non-running site", async () =>
        {
            var backend = new Topology(); var sends = 0;
            using var service = Service(backend, (_, _, _) => { sends++; return Task.FromResult(200); });
            foreach (var state in new[] { "Stopped", "Starting", "Stopping", "Unknown" })
            {
                backend.Snapshot = backend.Snapshot with { Sites = [backend.Snapshot.Sites[0] with { State = state }] };
                await service.ValidateAsync(Request());
                var result = await service.ProbeAsync(Request());
                Require(result.State == "unavailable" && result.StatusCode is null && sends == 0 &&
                    result.ErrorCode == (state == "Stopped" ? "HEALTH_SITE_STOPPED" : "HEALTH_TARGET_NOT_RUNNING"));
            }
        });

        foreach (var url in new[]
        {
            "http://user:synthetic@app.internal/health", "http://app.internal/health?key=synthetic",
            "http://app.internal/health?", "http://app.internal/health#fragment", "http://app.internal/health#",
            "file:///health", "ftp://app.internal/health", " http://app.internal/health", "http://app.internal/health\n"
        })
            await Check("health rejects unsafe URL " + checks, async () =>
            {
                using var service = Service(new Topology());
                await Reject(() => service.ValidateAsync(Request() with { Url = url }), "HEALTH_URL_INVALID");
            });
        foreach (var path in new[] { "/app/../health", "/app/%2e%2e/health", "/app/%252e%252e/health", "/app%2fhealth", "/app//health", "/app/%00health", "/app/%5chealth", "/app/%GGhealth" })
            await Check("health rejects ambiguous path " + checks, async () =>
            {
                using var service = Service(new Topology());
                await Reject(() => service.ValidateAsync(Request() with { Url = "http://app.internal" + path }), "HEALTH_PATH_INVALID");
            });
        foreach (var change in new Func<HealthProbeRequest, HealthProbeRequest>[]
        {
            request => request with { TimeoutSeconds = 0 }, request => request with { TimeoutSeconds = 16 },
            request => request with { ExpectedStatusCode = 302 }, request => request with { ExpectedStatusCode = 199 },
            request => request with { ApplicationPath = "/app/" }, request => request with { SiteName = " Demo" }
        })
            await Check("health rejects invalid options " + checks, async () =>
            {
                using var service = Service(new Topology());
                await Reject(() => service.ValidateAsync(change(Request())), "HEALTH_REQUEST_INVALID");
            });

        await Check("health application must exist and own the requested URL", async () =>
        {
            using var service = Service(new Topology());
            await service.ValidateAsync(Request() with { ApplicationPath = "/app", Url = "http://app.internal/app/health" });
            await Reject(() => service.ValidateAsync(Request() with { ApplicationPath = "/missing" }), "HEALTH_APPLICATION_NOT_FOUND");
            await Reject(() => service.ValidateAsync(Request() with { ApplicationPath = "/app", Url = "http://app.internal/application/health" }), "HEALTH_APPLICATION_PATH_MISMATCH");
            await Reject(() => service.ValidateAsync(Request() with { ApplicationPath = "/", Url = "http://app.internal/app/health" }), "HEALTH_APPLICATION_PATH_MISMATCH");
            await Reject(() => service.ValidateAsync(Request() with { ApplicationPath = "/app", Url = "http://app.internal/app/nested/health" }), "HEALTH_APPLICATION_PATH_MISMATCH");
        });
        await Check("health rechecks binding drift before every probe", async () =>
        {
            var backend = new Topology(); var sends = 0;
            using var service = Service(backend, (_, _, _) => { sends++; return Task.FromResult(200); });
            await service.ValidateAsync(Request());
            backend.Snapshot = backend.Snapshot with { Sites = [backend.Snapshot.Sites[0] with { Bindings = [] }] };
            var result = await service.ProbeAsync(Request());
            Require(result.ErrorCode == "HEALTH_BINDING_MISMATCH" && sends == 0 && backend.Reads == 2);
        });
        await Check("health rejects unbound hosts ports and reserved management ports", async () =>
        {
            using var service = Service(new Topology());
            await Reject(() => service.ValidateAsync(Request() with { Url = "http://other.internal/health" }), "HEALTH_BINDING_MISMATCH");
            await Reject(() => service.ValidateAsync(Request() with { Url = "http://app.internal:8088/health" }), "HEALTH_BINDING_MISMATCH");
            await Reject(() => service.ValidateAsync(Request() with { Url = "http://app.internal:7443/health" }), "HEALTH_MANAGEMENT_PORT_DENIED");
        });
        await Check("health explicit binding must be a verified local IP", async () =>
        {
            var backend = new Topology();
            backend.Binding(new BindingConfig { Protocol = "http", HostName = "app.internal", IpAddress = "203.0.113.8", Port = 80 });
            using var denied = Service(backend);
            await Reject(() => denied.ValidateAsync(Request()), "HEALTH_BINDING_MISMATCH");
            backend.Binding(new BindingConfig { Protocol = "http", HostName = "app.internal", IpAddress = "10.8.0.4", Port = 80 });
            using var allowed = Service(backend, (_, address, _) =>
            {
                Require(address.Equals(IPAddress.Parse("10.8.0.4"))); return Task.FromResult(200);
            });
            Require((await allowed.ProbeAsync(Request())).State == "healthy");
        });
        await Check("health hostless bindings accept local IPs and reject external DNS names", async () =>
        {
            var backend = new Topology(); backend.Binding(new BindingConfig { Protocol = "http", HostName = "", IpAddress = "*", Port = 80 });
            using var service = Service(backend);
            await service.ValidateAsync(Request() with { Url = "http://10.8.0.4/health" });
            await service.ValidateAsync(Request() with { Url = "http://localhost/health" });
            await Reject(() => service.ValidateAsync(Request() with { Url = "http://external.invalid/health" }), "HEALTH_BINDING_MISMATCH");
            await Reject(() => service.ValidateAsync(Request() with { Url = "http://203.0.113.8/health" }), "HEALTH_BINDING_MISMATCH");
        });
        await Check("health IPv6 wildcard connects to IPv6 loopback", async () =>
        {
            var backend = new Topology(); backend.Binding(new BindingConfig { Protocol = "http", HostName = "", IpAddress = "::", Port = 80 });
            using var service = Service(backend, (_, address, _) =>
            {
                Require(address.Equals(IPAddress.IPv6Loopback)); return Task.FromResult(200);
            });
            Require((await service.ProbeAsync(Request() with { Url = "http://[::1]/health" })).State == "healthy");
        });
        await Check("health wildcard address families cannot cross IPv4 and IPv6", async () =>
        {
            var backend = new Topology();
            using var service = Service(backend);
            backend.Binding(new BindingConfig { Protocol = "http", IpAddress = "0.0.0.0", Port = 80 });
            await service.ValidateAsync(Request() with { Url = "http://127.0.0.1/health" });
            await Reject(() => service.ValidateAsync(Request() with { Url = "http://[::1]/health" }), "HEALTH_BINDING_MISMATCH");
            backend.Binding(new BindingConfig { Protocol = "http", IpAddress = "::", Port = 80 });
            await Reject(() => service.ValidateAsync(Request() with { Url = "http://127.0.0.1/health" }), "HEALTH_BINDING_MISMATCH");
        });
        await Check("health IP URLs connect to the displayed IP and explicit IP bindings must agree", async () =>
        {
            var backend = new Topology(); backend.Binding(new BindingConfig { Protocol = "http", IpAddress = "*", Port = 80 });
            using var service = Service(backend, (_, address, _) =>
            {
                Require(address.Equals(IPAddress.Parse("10.8.0.4"))); return Task.FromResult(200);
            });
            Require((await service.ProbeAsync(Request() with { Url = "http://10.8.0.4/health" })).State == "healthy");
            backend.Binding(new BindingConfig { Protocol = "http", IpAddress = "10.8.0.4", Port = 80 });
            await Reject(() => service.ValidateAsync(Request() with { Url = "http://127.0.0.1/health" }), "HEALTH_BINDING_MISMATCH");
        });
        await Check("health refuses a hostless URL routed to a different site's hostname binding", async () =>
        {
            var backend = new Topology(); backend.Binding(new BindingConfig { Protocol = "http", HostName = "", IpAddress = "*", Port = 80 });
            backend.Snapshot = backend.Snapshot with { Sites = [backend.Snapshot.Sites[0], new SiteTopology
            {
                Name = "Other", Bindings = [new BindingConfig { Protocol = "http", HostName = "localhost", IpAddress = "*", Port = 80 }]
            }] };
            using var service = Service(backend);
            await Reject(() => service.ValidateAsync(Request() with { Url = "http://localhost/health" }), "HEALTH_BINDING_AMBIGUOUS");
        });
        await Check("health limits active work and returns a safe busy result", async () =>
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var started = 0;
            using var service = Service(new Topology(), async (_, _, cancellation) =>
            {
                Interlocked.Increment(ref started); await release.Task.WaitAsync(cancellation); return 200;
            });
            var active = Enumerable.Range(0, 4).Select(_ => service.ProbeAsync(Request())).ToArray();
            Require(started == 4);
            try { Require((await service.ProbeAsync(Request())).ErrorCode == "HEALTH_BUSY"); }
            finally { release.SetResult(); await Task.WhenAll(active); }
        });
        System.Console.WriteLine($"Agent health checks: {checks}, failures: {failures}");
        return failures;
    }

    private static HealthProbeRequest Request() => new() { SiteName = "Demo", ApplicationPath = "/", Url = "http://app.internal/health" };
    private static HealthProbeService Service(Topology backend,
        Func<HttpRequestMessage, IPAddress, CancellationToken, Task<int>>? send = null, string mode = "Windows") =>
        new(new AgentOptions { Mode = mode, ReservedPorts = [7443, 7444] }, backend,
            send ?? ((_, _, _) => Task.FromResult(200)),
            () => [IPAddress.Loopback, IPAddress.IPv6Loopback, IPAddress.Parse("10.8.0.4")]);
    private static void Require(bool valid) { if (!valid) throw new InvalidOperationException("TEST_ASSERTION_FAILED"); }
    private static async Task Reject(Func<Task> action, string code)
    {
        try { await action(); }
        catch (OperationException exception) when (exception.Code == code) { return; }
        throw new InvalidOperationException("EXPECTED_REJECTION");
    }
    private sealed class Topology : IIisTopologyBackend
    {
        public int Reads;
        public SiteTopologySnapshot Snapshot = new(DateTimeOffset.UtcNow,
            [new SiteTopology
            {
                Id = 1, Name = "Demo", State = "Started",
                Bindings = [new BindingConfig { Protocol = "http", HostName = "app.internal", Port = 80 },
                    new BindingConfig { Protocol = "https", HostName = "app.internal", Port = 443 }],
                Applications = [new ApplicationTopology { Path = "/" }, new ApplicationTopology { Path = "/app" },
                    new ApplicationTopology { Path = "/app/nested" }]
            }], []);
        public Task<SiteTopologySnapshot> GetSiteTopologyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads++; return Task.FromResult(Snapshot);
        }
        public void Binding(BindingConfig binding) => Snapshot = Snapshot with { Sites = [Snapshot.Sites[0] with { Bindings = [binding] }] };
    }
}
