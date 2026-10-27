using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using IISManager.Console;
using IISManager.Contracts;
using IISManager.Core;

internal static class WindowsServiceTransportSelfTests
{
    public static async Task<int> RunAsync(string[] args, bool siteLifecycleOnly = false)
    {
        if (args.Length != 2) throw new InvalidOperationException("TEST_REPO_AND_DOTNET_REQUIRED");
        var repo = Path.GetFullPath(args[0]);
        var dotnet = Path.GetFullPath(args[1]);
        var temporary = Path.Combine(repo, "work", "windows-services-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var children = new List<Process>();
        using var browser = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var legacy = new HttpListener();
        var agentPort = FreePort(); var consolePort = FreePort(); var legacyPort = FreePort();
        var origin = $"http://127.0.0.1:{consolePort}";
        var checks = 0; var failures = 0; var legacyStatus = 404;
        Task? legacyLoop = null;
        async Task Check(string name, Func<Task> assertion)
        {
            checks++;
            try { await assertion(); System.Console.WriteLine("PASS " + name); }
            catch (Exception exception) { failures++; System.Console.WriteLine("FAIL " + name + " exceptionType=" + exception.GetType().Name); }
        }
        try
        {
            legacy.Prefixes.Add($"http://127.0.0.1:{legacyPort}/"); legacy.Start();
            legacyLoop = Task.Run(async () =>
            {
                try
                {
                    while (legacy.IsListening)
                    {
                        var context = await legacy.GetContextAsync();
                        context.Response.StatusCode = Volatile.Read(ref legacyStatus);
                        context.Response.Close();
                    }
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException) { }
            });
            var agent = Start("Agent", agentPort);
            var console = Start("Console", consolePort);
            await Ready(agent, $"http://127.0.0.1:{agentPort}/agent/v1/info", authenticated: true);
            await Ready(console, origin + "/api/v1/servers", authenticated: false);
            using var session = await browser.GetAsync(origin + "/api/v1/session");
            var sessionBody = await session.Content.ReadFromJsonAsync<JsonElement>();
            var csrf = sessionBody.GetProperty("csrfToken").GetString()!;
            async Task<T> Get<T>(string route) => await browser.GetFromJsonAsync<T>(origin + route, Protocol.Json) ?? throw new InvalidOperationException("TEST_RESPONSE_MISSING");
            async Task<HttpResponseMessage> Post(string server, string route, OperationRequest request)
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, origin + $"/api/v1/servers/{server}/{route}");
                message.Headers.Add("Origin", origin); message.Headers.Add("X-IISManager-CSRF", csrf);
                message.Content = JsonContent.Create(request, options: Protocol.Json);
                return await browser.SendAsync(message);
            }
            OperationRequest Request(string type, string name, object parameters) => new()
            {
                OperationId = Guid.NewGuid(), Type = type, Target = new() { ServiceName = name },
                Parameters = JsonSerializer.SerializeToElement(parameters, Protocol.Json), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            async Task<(OperationRequest Request, ConsoleTaskRecord Record)> Execute(OperationRequest request)
            {
                using var validationResponse = await Post("test", "operations/validate", request);
                Require(validationResponse.IsSuccessStatusCode);
                var validation = await validationResponse.Content.ReadFromJsonAsync<ValidationResult>(Protocol.Json);
                Require(validation is { Valid: true });
                request = request with { ExpectedFingerprint = validation!.Fingerprint };
                using var submission = await Post("test", "operations", request); Require(submission.StatusCode == HttpStatusCode.Accepted);
                var record = await submission.Content.ReadFromJsonAsync<ConsoleTaskRecord>(Protocol.Json) ?? throw new InvalidOperationException();
                var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
                while (!OperationStates.IsFinal(record.State) && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(50);
                    record = await Get<ConsoleTaskRecord>($"/api/v1/servers/test/operations/{request.OperationId:D}");
                }
                Require(record.State == OperationStates.Succeeded);
                return (request, record);
            }
            if (siteLifecycleOnly)
            {
                const string name = "SIMULATION-HttpSite";
                OperationRequest SiteRequest(string type, object? parameters = null) => Request(type, name, parameters ?? new { }) with { Target = new() { SiteName = name } };
                SiteConfig? original = null; string? poolsHash = null;
                (OperationRequest Request, ConsoleTaskRecord Record)? restarted = null;
                await Check("create site remains stopped and supplies its immutable baseline", async () =>
                {
                    await Execute(SiteRequest(OperationTypes.CreateSite, new SiteParameters
                    {
                        PhysicalPath = Path.Combine(temporary, "Agent"), ApplicationPoolName = "SIMULATION-DefaultPool",
                        Bindings = [new BindingConfig { Port = FreePort(), HostName = "test.simulation.test" }]
                    }));
                    var snapshot = await Get<IisSnapshot>("/api/v1/servers/test/snapshot");
                    original = snapshot.Sites.Single(site => site.Name == name); poolsHash = CanonicalJson.Hash(snapshot.ApplicationPools);
                    Require(original.State == "Stopped" && !original.ServerAutoStart);
                });
                await Check("restart precheck rejects a stopped site without persisting intent", async () =>
                {
                    var request = SiteRequest(OperationTypes.SiteRestart);
                    using var response = await Post("test", "operations/validate", request); Require(response.IsSuccessStatusCode);
                    var validation = await response.Content.ReadFromJsonAsync<ValidationResult>(Protocol.Json);
                    Require(validation is { Valid: false } && validation.Errors.Any(error => error.Code == "site.notRunning"));
                    Require(!File.Exists(Path.Combine(temporary, "Console", "tasks", request.OperationId + ".json")) &&
                        !File.Exists(Path.Combine(temporary, "Agent", "operations", request.OperationId + ".json")));
                });
                foreach (var type in new[] { OperationTypes.SiteStart, OperationTypes.SiteRestart, OperationTypes.SiteStop })
                    await Check(type + " passes Console checks and reads back the exact site state", async () =>
                    {
                        var request = SiteRequest(type);
                        using var response = await Post("test", "operations/validate", request);
                        var validation = await response.Content.ReadFromJsonAsync<ValidationResult>(Protocol.Json);
                        Require(response.IsSuccessStatusCode && validation is { Valid: true } && validation.AffectedApplications.SequenceEqual([name + "/"]));
                        var executed = await Execute(request); if (type == OperationTypes.SiteRestart) restarted = executed;
                        var expected = type == OperationTypes.SiteStop ? "Stopped" : "Started";
                        var snapshot = await Get<IisSnapshot>("/api/v1/servers/test/snapshot"); var site = snapshot.Sites.Single(item => item.Name == name);
                        Require(site.State == expected && executed.Record.AgentRecord!.After!.Value.GetProperty("state").GetString() == expected);
                        Require(CanonicalJson.Hash(original) == CanonicalJson.Hash(site with { State = "Stopped" }) && CanonicalJson.Hash(snapshot.ApplicationPools) == poolsHash);
                        Require((await Get<SiteTopologySnapshot>("/api/v1/servers/test/site-topology")).Sites.Single().State == expected);
                    });
                await Check("duplicate completed site restart returns its original receipt without restarting", async () =>
                {
                    var saved = restarted ?? throw new InvalidOperationException("TEST_RESTART_MISSING");
                    using var response = await Post("test", "operations", saved.Request); Require(response.StatusCode == HttpStatusCode.Accepted);
                    var duplicate = await response.Content.ReadFromJsonAsync<ConsoleTaskRecord>(Protocol.Json);
                    Require(duplicate is not null && duplicate.AcceptedAt == saved.Record.AcceptedAt && duplicate.Request.OperationId == saved.Request.OperationId);
                    Require((await Get<IisSnapshot>("/api/v1/servers/test/snapshot")).Sites.Single().State == "Stopped");
                });
                await Check("site lifecycle history preserves each action exact target and succeeded state", async () =>
                {
                    foreach (var type in new[] { OperationTypes.SiteStart, OperationTypes.SiteStop, OperationTypes.SiteRestart })
                    {
                        var page = await Get<ConsoleOperationPage>("/api/v1/operations?type=" + type + "&search=" + name);
                        Require(page.Total == 1 && page.Items.Single().Target == name && page.Items.Single().State == OperationStates.Succeeded);
                    }
                });
                await Check("Console rejects extra site parameters and mixed targets before dispatch", async () =>
                {
                    foreach (var invalid in new[]
                    {
                        SiteRequest(OperationTypes.SiteStart, new { serverAutoStart = true }),
                        SiteRequest(OperationTypes.SiteStart) with { Target = new() { SiteName = name, ApplicationPoolName = "SIMULATION-DefaultPool" } },
                        SiteRequest(OperationTypes.SiteStart) with { Target = new() { ServiceName = name } }
                    })
                    {
                        using var response = await Post("test", "operations/validate", invalid); Require(response.StatusCode == HttpStatusCode.BadRequest);
                        Require((await response.Content.ReadFromJsonAsync<ApiError>(Protocol.Json))?.Code == "REQUEST_INVALID");
                    }
                });
                await Check("Agent rejects stale site fingerprints while Console retains the original intent", async () =>
                {
                    var stale = SiteRequest(OperationTypes.SiteStart);
                    using var precheck = await Post("test", "operations/validate", stale);
                    var proof = await precheck.Content.ReadFromJsonAsync<ValidationResult>(Protocol.Json); Require(proof is { Valid: true });
                    await Execute(SiteRequest(OperationTypes.SiteStart));
                    stale = stale with { ExpectedFingerprint = proof!.Fingerprint };
                    using var response = await Post("test", "operations", stale); Require(response.StatusCode == HttpStatusCode.Accepted);
                    var rejected = await response.Content.ReadFromJsonAsync<ConsoleTaskRecord>(Protocol.Json);
                    Require(rejected is not null && rejected.Request.OperationId == stale.OperationId && rejected.State != OperationStates.Succeeded);
                    Require((await Get<IisSnapshot>("/api/v1/servers/test/snapshot")).Sites.Single().State == "Started");
                    using var agentRequest = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{agentPort}/agent/v1/operations/{stale.OperationId:D}");
                    agentRequest.Headers.Add(IntranetTransportSecurity.TokenHeader, new string('A', 64));
                    using var agentResponse = await browser.SendAsync(agentRequest); Require(agentResponse.StatusCode == HttpStatusCode.NotFound);
                });
                System.Console.WriteLine($"Site lifecycle HTTP self-tests: {checks} checks, {failures} failures.");
                return failures == 0 ? 0 : 1;
            }
            var snapshot = await Get<WindowsServiceSnapshot>("/api/v1/servers/test/windows-services");
            await Check("service list and details use authenticated Console to Agent transport", async () =>
            {
                Require(snapshot.CanCreate && snapshot.Services.Length > 0);
                var selected = snapshot.Services.First();
                var detail = await Get<WindowsServiceConfig>("/api/v1/servers/test/windows-services/" + Uri.EscapeDataString(selected.Name));
                Require(detail.Name == selected.Name);
            });
            await Check("missing service is distinguished from unsupported Agent", async () =>
            {
                using var response = await browser.GetAsync(origin + "/api/v1/servers/test/windows-services/SIMULATION-Missing");
                Require(response.StatusCode == HttpStatusCode.NotFound);
                Require((await response.Content.ReadFromJsonAsync<ApiError>(Protocol.Json))?.Code == "SERVICE_NOT_FOUND");
            });
            await Check("disabled service becomes automatic or manual without starting or changing other properties", async () =>
            {
                var before = snapshot.Services.First(s => s.StartType == WindowsServiceStartTypes.Disabled && s.CanUpdateStartType);
                foreach (var startType in new[] { WindowsServiceStartTypes.Automatic, WindowsServiceStartTypes.Manual })
                {
                    await Execute(Request(OperationTypes.ServiceUpdateStartType, before.Name, new { startType }));
                    var after = await Get<WindowsServiceConfig>("/api/v1/servers/test/windows-services/" + Uri.EscapeDataString(before.Name));
                    Require(after.StartType == startType && after.State == before.State && after.ExecutablePath == before.ExecutablePath &&
                        after.AccountName == before.AccountName && after.Description == before.Description && after.Dependencies.SequenceEqual(before.Dependencies));
                }
            });
            const string createdName = "SIMULATION-HttpService";
            (OperationRequest Request, ConsoleTaskRecord Record)? created = null;
            await Check("create service is durable and independently read back as stopped", async () =>
            {
                created = await Execute(Request(OperationTypes.ServiceCreate, createdName, new WindowsServiceCreateParameters
                {
                    DisplayName = "HTTP test service", ExecutablePath = @"C:\DemoServices\worker.exe", Arguments = ["--environment", "Production"]
                }));
                var service = await Get<WindowsServiceConfig>("/api/v1/servers/test/windows-services/" + createdName);
                Require(service.State == "Stopped" && service.StartType == WindowsServiceStartTypes.Manual);
            });
            await Check("duplicate service submission preserves the original operation receipt", async () =>
            {
                Require(created.HasValue);
                var saved = created ?? throw new InvalidOperationException("TEST_CREATE_MISSING");
                using var response = await Post("test", "operations", saved.Request);
                Require(response.StatusCode == HttpStatusCode.Accepted);
                var duplicate = await response.Content.ReadFromJsonAsync<ConsoleTaskRecord>(Protocol.Json);
                Require(duplicate is not null && duplicate.AcceptedAt == saved.Record.AcceptedAt && duplicate.Request.OperationId == saved.Request.OperationId);
            });
            foreach (var action in new[] { OperationTypes.ServiceStart, OperationTypes.ServiceRestart, OperationTypes.ServiceStop })
                await Check(action + " reaches its requested state", async () =>
                {
                    await Execute(Request(action, createdName, new { }));
                    var after = await Get<WindowsServiceConfig>("/api/v1/servers/test/windows-services/" + createdName);
                    Require(after.State == (action == OperationTypes.ServiceStop ? "Stopped" : "Running"));
                });
            await Check("service history can filter by action and exact target name", async () =>
            {
                var page = await Get<ConsoleOperationPage>("/api/v1/operations?type=windowsService.create&search=" + createdName);
                Require(page.Total == 1 && page.Items.Single().Target == createdName);
            });
            await Check("secret startup arguments are rejected before both ledgers receive a request", async () =>
            {
                var request = Request(OperationTypes.ServiceCreate, "SIMULATION-SecretRejected", new WindowsServiceCreateParameters
                { ExecutablePath = @"C:\DemoServices\worker.exe", Arguments = ["--token=SYNTHETIC_TEST_VALUE"] }) with { ExpectedFingerprint = new string('A', 64) };
                using var response = await Post("test", "operations", request); Require(response.StatusCode == HttpStatusCode.BadRequest);
                Require(!File.Exists(Path.Combine(temporary, "Console", "tasks", request.OperationId + ".json")) &&
                    !File.Exists(Path.Combine(temporary, "Agent", "operations", request.OperationId + ".json")));
            });
            await Check("legacy Agent reports feature unsupported and receives no durable service intent", async () =>
            {
                using var response = await browser.GetAsync(origin + "/api/v1/servers/legacy/windows-services");
                Require((int)response.StatusCode == 501 && (await response.Content.ReadFromJsonAsync<ApiError>(Protocol.Json))?.Code == "WINDOWS_SERVICES_UNSUPPORTED");
                var request = Request(OperationTypes.ServiceStart, createdName, new { }) with { ExpectedFingerprint = new string('A', 64) };
                using var rejected = await Post("legacy", "operations", request); Require((int)rejected.StatusCode == 501);
                Require(!File.Exists(Path.Combine(temporary, "Console", "tasks", request.OperationId + ".json")));
            });
            await Check("Agent permission denial stays an authentication error instead of unsupported or empty list", async () =>
            {
                Volatile.Write(ref legacyStatus, 403);
                using var response = await browser.GetAsync(origin + "/api/v1/servers/legacy/windows-services");
                Require(response.StatusCode == HttpStatusCode.BadGateway && (await response.Content.ReadFromJsonAsync<ApiError>(Protocol.Json))?.Code == "AGENT_ACCESS_DENIED");
            });

            Process Start(string role, int port)
            {
                var working = Path.Combine(temporary, role); Directory.CreateDirectory(working);
                var entry = Path.Combine(repo, "src", "IISManager." + role, "bin", "Release", "net10.0", "IISManager." + role + ".dll");
                var info = new ProcessStartInfo(dotnet) { WorkingDirectory = working, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                info.ArgumentList.Add(entry); info.ArgumentList.Add("--contentRoot"); info.ArgumentList.Add(working);
                foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("Agent", StringComparison.OrdinalIgnoreCase) || k.StartsWith("Console", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
                info.Environment["DOTNET_ENVIRONMENT"] = role == "Agent" ? "Development" : "Production";
                info.Environment[role + "__TransportMode"] = IntranetTransportSecurity.HttpToken;
                info.Environment[role + "__SourceAccessMode"] = IntranetTransportSecurity.PrivateNetwork;
                info.Environment[role + "__Port"] = port.ToString();
                if (role == "Agent")
                {
                    if (siteLifecycleOnly) info.Environment["Agent__AllowedSiteRoots__0"] = working;
                    info.Environment["Agent__Mode"] = "Simulation"; info.Environment["Agent__AccessToken"] = new string('A', 64);
                    info.Environment["Agent__StateDirectory"] = Path.Combine(working, "operations");
                    info.Environment["Agent__WindowsServices__AllowedNamePrefixes__0"] = "SIMULATION-";
                    info.Environment["Agent__WindowsServices__AllowCreate"] = "true";
                    info.Environment["Agent__WindowsServices__AllowedExecutableRoots__0"] = @"C:\DemoServices";
                }
                else
                {
                    info.Environment["Console__ListenAddress"] = "127.0.0.1"; info.Environment["Console__TaskDirectory"] = Path.Combine(working, "tasks");
                    foreach (var (index, id, targetPort) in new[] { (0, "test", agentPort), (1, "legacy", legacyPort) })
                    {
                        var prefix = "Console__Servers__" + index + "__";
                        info.Environment[prefix + "Id"] = id; info.Environment[prefix + "Name"] = id;
                        info.Environment[prefix + "AgentUrl"] = $"http://127.0.0.1:{targetPort}/";
                        info.Environment[prefix + "AccessToken"] = new string('A', 64);
                    }
                }
                var process = Process.Start(info) ?? throw new InvalidOperationException("TEST_PROCESS_START_FAILED"); children.Add(process);
                process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                System.Console.WriteLine($"START role={role} pid={process.Id} port={port} command={dotnet} {entry} --contentRoot {working}");
                return process;
            }
            async Task Ready(Process process, string url, bool authenticated)
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    if (process.HasExited) throw new InvalidOperationException("TEST_CHILD_EXITED");
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, url);
                        if (authenticated) request.Headers.Add(IntranetTransportSecurity.TokenHeader, new string('A', 64));
                        using var response = await browser.SendAsync(request);
                        if (response.IsSuccessStatusCode) return;
                    }
                    catch (HttpRequestException) { }
                    await Task.Delay(100);
                }
                throw new InvalidOperationException("TEST_CHILD_NOT_READY");
            }
        }
        catch (Exception exception) { failures++; System.Console.WriteLine("FAIL service HTTP setup exceptionType=" + exception.GetType().Name); }
        finally
        {
            legacy.Stop(); if (legacyLoop is not null) await legacyLoop;
            foreach (var process in children.AsEnumerable().Reverse())
            {
                if (!process.HasExited && !OperatingSystem.IsWindows())
                {
                    var stopInfo = new ProcessStartInfo("/bin/kill") { UseShellExecute = false };
                    stopInfo.ArgumentList.Add("-TERM"); stopInfo.ArgumentList.Add(process.Id.ToString());
                    using var stop = Process.Start(stopInfo); if (stop is not null) await stop.WaitForExitAsync();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { }
                }
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                System.Console.WriteLine("STOP pid=" + process.Id); process.Dispose();
            }
            Directory.Delete(temporary, recursive: true);
            System.Console.WriteLine("CLEAN service HTTP test state and child logs removed");
        }
        System.Console.WriteLine($"Windows service HTTP self-tests: {checks} checks, {failures} failures.");
        return failures == 0 ? 0 : 1;
    }
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("TEST_ASSERTION_FAILED"); }
}
