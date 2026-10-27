using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using IISManager.Agent;
using IISManager.Console;
using IISManager.Contracts;
using IISManager.Core;

if (args.Length > 0 && args[0] == "--agent-health-only")
    return await AgentHealthSelfTests.RunAsync();
if (args.Length > 0 && args[0] == "--enhancements-only")
    return await ConsoleEnhancementSelfTests.RunAsync();
if (args.Length > 0 && args[0] == "--diagnostics-only")
    return await DiagnosticsSelfTests.RunAsync();
if (args.Length > 0 && args[0] == "--administration-only")
    return await AdministrationSelfTests.RunAsync();
if (args.Length > 0 && args[0] == "--operation-health-only")
    return await OperationHealthFollowUpSelfTests.RunAsync();
if (args.Length > 0 && args[0] == "--freb-only")
    return await FrebDiagnosticsSelfTests.RunAsync();
if (args.Length > 0 && args[0] == "--monitoring-only")
    return await MonitoringSelfTests.RunAsync();
if (args.Length > 0 && args[0] == "--site-annotations-only")
    return await SiteAnnotationSelfTests.RunAsync();
if (args.Length > 0 && args[0] == "--operation-history-only")
    return await OperationHistorySelfTests.RunAsync() + await OperationHistoryWireSelfTests.RunAsync();
if (args.Length > 0 && args[0] == "--user-auth-only")
    return await AppUserAuthenticationSelfTests.RunAsync(args.Length > 1 ? args[1] : Directory.GetCurrentDirectory());
if (args.Length > 1 && args[0] == "--user-auth-preview")
    return await AppUserAuthenticationSelfTests.PreviewAsync(args[1]);
if (args.Length > 0 && args[0] == "--operations-only")
    return await ConsoleOperationsSelfTests.RunAsync(args.Skip(1).ToArray());
if (args.Length > 0 && args[0] == "--windows-services-only")
    return await WindowsServiceTransportSelfTests.RunAsync(args.Skip(1).ToArray());
if (args.Length > 0 && args[0] == "--site-lifecycle-only")
    return await WindowsServiceTransportSelfTests.RunAsync(args.Skip(1).ToArray(), siteLifecycleOnly: true);

// These are synthetic test values, never credentials used by an installed service.
var testToken = new string('A', 64);
var wrongToken = new string('B', 64);
var count = 0;
var failures = 0;
void Check(string name, Action assertion)
{
    count++;
    try { assertion(); System.Console.WriteLine($"PASS {name}"); }
    catch { failures++; System.Console.WriteLine($"FAIL {name}"); }
}
async Task CheckAsync(string name, Func<Task> assertion)
{
    count++;
    try { await assertion(); System.Console.WriteLine($"PASS {name}"); }
    catch { failures++; System.Console.WriteLine($"FAIL {name}"); }
}
static void Require(bool result) { if (!result) throw new InvalidOperationException("TEST_ASSERTION_FAILED"); }
static void Reject(Action action, string code)
{
    try { action(); }
    catch (InvalidOperationException exception) when (exception.Message == code) { return; }
    throw new InvalidOperationException("TEST_EXPECTED_REJECTION");
}

Check("64hex token accepted", () => Require(IntranetTransportSecurity.IsValidAccessToken(testToken)));
Check("token hex case normalized", () => Require(IntranetTransportSecurity.FixedTimeTokenEquals(testToken.ToLowerInvariant(), testToken)));
Check("different token rejected", () => Require(!IntranetTransportSecurity.FixedTimeTokenEquals(wrongToken, testToken)));
Check("short token rejected", () => Require(!IntranetTransportSecurity.IsValidAccessToken("1234")));
Check("nonhex token rejected", () => Require(!IntranetTransportSecurity.IsValidAccessToken(new string('Z', 64))));
Check("padded token rejected", () => Require(!IntranetTransportSecurity.IsValidAccessToken(" " + testToken)));
Check("missing token rejected", () => Require(!IntranetTransportSecurity.FixedTimeTokenEquals(null, testToken)));
Check("missing token header rejected", () => Require(!IntranetTransportSecurity.IsValidTokenHeader([], testToken)));
Check("duplicate token headers rejected", () => Require(!IntranetTransportSecurity.IsValidTokenHeader([testToken, testToken], testToken)));
Check("comma joined token headers rejected", () => Require(!IntranetTransportSecurity.IsValidTokenHeader([testToken + "," + testToken], testToken)));
Check("single token header accepted", () => Require(IntranetTransportSecurity.IsValidTokenHeader([testToken], testToken)));
Check("exact IPv4 allowlist", () =>
{
    Require(IntranetTransportSecurity.TryParseNetwork("10.20.0.11", out var network));
    Require(network!.Contains(IPAddress.Parse("10.20.0.11")) && !network.Contains(IPAddress.Parse("10.20.0.12")));
});
Check("IPv4 CIDR boundary", () =>
{
    Require(IntranetTransportSecurity.TryParseNetwork("10.20.0.0/24", out var network));
    Require(network!.Contains(IPAddress.Parse("10.20.0.255")) && !network.Contains(IPAddress.Parse("10.20.1.0")));
});
Check("mapped IPv6 TCP peer normalized", () =>
{
    Require(IntranetTransportSecurity.TryParseNetwork("10.20.0.0/24", out var network));
    Require(network!.Contains(IPAddress.Parse("::ffff:10.20.0.11")));
});
Check("mapped IPv6 configured network normalized", () =>
{
    Require(IntranetTransportSecurity.TryParseNetwork("::ffff:10.20.0.0/120", out var network));
    Require(network!.Prefix == 24 && network.Contains(IPAddress.Parse("10.20.0.11")));
});
Check("native IPv6 CIDR", () =>
{
    Require(IntranetTransportSecurity.TryParseNetwork("fd00:1234::/64", out var network));
    Require(network!.Contains(IPAddress.Parse("fd00:1234::9")) && !network.Contains(IPAddress.Parse("fd00:1235::9")));
});
foreach (var invalid in new[] { "0.0.0.0/0", "::/0", "::ffff:0.0.0.0/96", "::ffff:10.20.0.0/95", "10.20.0.0/33", "fd00::/129", "10.20.0.0/-1", "10.20.0.0/24/1", "*", "", "domain.internal", "10.20.0.0/+1", "fe80::1%1/64" })
    Check("invalid network denied " + invalid, () => Require(!IntranetTransportSecurity.TryParseNetwork(invalid, out _)));
Check("empty source allowlist denied", () => Reject(() => IntranetTransportSecurity.ParseRestrictedNetworks([]), "HTTP_CLIENT_NETWORKS_REQUIRED"));
Check("missing TCP peer denied", () => Require(!IntranetTransportSecurity.ParseRestrictedNetworks(["127.0.0.1"])[0].Contains(null)));
Check("Agent defaults preserve HTTPS", () => Require(new AgentOptions().TransportMode == IntranetTransportSecurity.Https));
Check("Agent legacy source policy remains Allowlist", () => Require(new AgentOptions().SourceAccessMode == IntranetTransportSecurity.Allowlist));
Check("Console legacy source policy remains Allowlist", () => Require(new ConsoleOptions().SourceAccessMode == IntranetTransportSecurity.Allowlist));
foreach (var range in new[]
{
    (Name: "RFC1918 10", First: "10.0.0.0", Last: "10.255.255.255", Before: "9.255.255.255", After: "11.0.0.0"),
    (Name: "RFC1918 172", First: "172.16.0.0", Last: "172.31.255.255", Before: "172.15.255.255", After: "172.32.0.0"),
    (Name: "RFC1918 192", First: "192.168.0.0", Last: "192.168.255.255", Before: "192.167.255.255", After: "192.169.0.0"),
    (Name: "IPv4 loopback", First: "127.0.0.0", Last: "127.255.255.255", Before: "126.255.255.255", After: "128.0.0.0"),
    (Name: "IPv4 link local", First: "169.254.0.0", Last: "169.254.255.255", Before: "169.253.255.255", After: "169.255.0.0"),
    (Name: "shared VPN", First: "100.64.0.0", Last: "100.127.255.255", Before: "100.63.255.255", After: "100.128.0.0"),
    (Name: "IPv6 loopback", First: "::1", Last: "::1", Before: "::", After: "::2"),
    (Name: "IPv6 ULA", First: "fc00::", Last: "fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", Before: "fbff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", After: "fe00::"),
    (Name: "IPv6 link local", First: "fe80::", Last: "febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff", Before: "fe7f:ffff:ffff:ffff:ffff:ffff:ffff:ffff", After: "fec0::")
})
{
    Check(range.Name + " first allowed", () => Require(IntranetTransportSecurity.IsPrivateSource(IPAddress.Parse(range.First))));
    Check(range.Name + " last allowed", () => Require(IntranetTransportSecurity.IsPrivateSource(IPAddress.Parse(range.Last))));
    Check(range.Name + " below denied", () => Require(!IntranetTransportSecurity.IsPrivateSource(IPAddress.Parse(range.Before))));
    Check(range.Name + " above denied", () => Require(!IntranetTransportSecurity.IsPrivateSource(IPAddress.Parse(range.After))));
}
foreach (var external in new[] { "0.0.0.0", "8.8.8.8", "224.0.0.1", "255.255.255.255", "::", "2001:4860:4860::8888", "ff02::1", "::ffff:8.8.8.8", "::ffff:0.0.0.0", "::ffff:224.0.0.1" })
    Check("public unspecified multicast denied " + external, () => Require(!IntranetTransportSecurity.IsPrivateSource(IPAddress.Parse(external))));
Check("null private source denied", () => Require(!IntranetTransportSecurity.IsPrivateSource(null)));
Check("mapped IPv4 private source accepted", () => Require(IntranetTransportSecurity.IsPrivateSource(IPAddress.Parse("::ffff:10.20.0.11"))));
Check("mapped IPv4 shared VPN source accepted", () => Require(IntranetTransportSecurity.IsPrivateSource(IPAddress.Parse("::ffff:100.80.0.11"))));
Check("default firewall network set matches source classifier", () =>
{
    var networks = IntranetTransportSecurity.DefaultPrivateNetworks(); Require(networks.Length == 9);
    Require(networks.All(value => IntranetTransportSecurity.TryParseNetwork(value, out _)));
    var parsed = IntranetTransportSecurity.ParseRestrictedNetworks(networks);
    foreach (var address in new[] { "10.0.0.1", "172.16.0.1", "192.168.0.1", "127.0.0.1", "169.254.0.1", "100.64.0.1", "::1", "fc00::1", "fe80::1", "8.8.8.8", "2001:4860:4860::8888" })
    {
        var source = IPAddress.Parse(address);
        Require(parsed.Any(network => network.Contains(source)) == IntranetTransportSecurity.IsPrivateSource(source));
    }
    networks[0] = "0.0.0.0/0"; Require(!IntranetTransportSecurity.DefaultPrivateNetworks().Contains("0.0.0.0/0"));
});

ConsoleOptions Options() => new()
{
    TransportMode = IntranetTransportSecurity.HttpToken, AllowedClientNetworks = ["127.0.0.1/32"],
    Servers = [new ServerOptions { Id = "test", Name = "Test", AgentUrl = "http://127.0.0.1:7444/", AccessToken = testToken }]
};
Check("Production HTTP explicitly authorized", () => Options().Validate(development: false));
Check("automatic private-network HTTP requires no source list", () =>
{
    var options = Options(); options.SourceAccessMode = IntranetTransportSecurity.PrivateNetwork; options.AllowedClientNetworks = [];
    options.Validate(false);
});
Check("automatic private-network HTTP still requires Agent token", () =>
{
    var options = Options(); options.SourceAccessMode = IntranetTransportSecurity.PrivateNetwork; options.AllowedClientNetworks = [];
    options.Servers[0].AccessToken = ""; Reject(() => options.Validate(false), "AGENT_HTTP_TOKEN_REQUIRED");
});
Check("automatic private-network HTTPS requires no source list", () =>
{
    var options = Options(); options.SourceAccessMode = IntranetTransportSecurity.PrivateNetwork; options.AllowedClientNetworks = [];
    options.TransportMode = IntranetTransportSecurity.Https; options.ServerCertificateThumbprint = new string('A', 40);
    options.Servers[0].AgentUrl = "https://127.0.0.1:7444/"; options.Servers[0].ServerCertificateThumbprint = new string('B', 40);
    options.AgentClientCertificateThumbprint = new string('C', 40); options.Validate(false);
});
Check("old Allowlist still requires browser source list", () =>
{
    var options = Options(); options.AllowedClientNetworks = [];
    Reject(() => options.Validate(false), "CONSOLE_CLIENT_NETWORKS_REQUIRED");
});
Check("unknown source policy rejected", () =>
{
    var options = Options(); options.SourceAccessMode = "All";
    Reject(() => options.Validate(false), "CONSOLE_SOURCE_ACCESS_MODE_INVALID");
});
Check("old HTTPS settings cannot silently allow HTTP", () =>
{
    var options = Options(); options.TransportMode = new ConsoleOptions().TransportMode;
    options.ServerCertificateThumbprint = new string('A', 40);
    Reject(() => options.Validate(false), "AGENT_HTTP_TOKEN_REQUIRED");
});
Check("Production HTTP requires Agent token", () =>
{
    var options = Options(); options.Servers[0].AccessToken = "";
    Reject(() => options.Validate(false), "AGENT_HTTP_TOKEN_REQUIRED");
});
Check("Production HTTP rejects malformed Agent token", () =>
{
    var options = Options(); options.Servers[0].AccessToken = wrongToken[..16];
    Reject(() => options.Validate(false), "AGENT_HTTP_TOKEN_REQUIRED");
});
Check("Production simulation HTTP remains prohibited", () =>
{
    var options = Options(); options.Servers[0].Simulation = true;
    Reject(() => options.Validate(false), "AGENT_HTTP_REQUIRES_DEVELOPMENT_SIMULATION_LOOPBACK");
});
Check("HTTP Console disallows unrestricted browser networks", () =>
{
    var options = Options(); options.AllowedClientNetworks = ["0.0.0.0/0"];
    Reject(() => options.Validate(false), "HTTP_CLIENT_NETWORK_INVALID");
});
Check("HTTP Console retains Host allowlist", () =>
{
    var options = Options(); options.AllowedHosts = ["*"];
    Reject(() => options.Validate(false), "CONSOLE_HOST_ALLOWLIST_REQUIRED");
});
Check("invalid transport mode fails closed", () =>
{
    var options = Options(); options.TransportMode = "http";
    Reject(() => options.Validate(false), "CONSOLE_TRANSPORT_MODE_INVALID");
});
Check("legacy development simulation remains available", () =>
{
    var options = Options(); options.TransportMode = IntranetTransportSecurity.Https;
    options.AllowDevelopmentHttp = true; options.Servers[0].Simulation = true; options.Servers[0].AccessToken = "";
    options.Validate(true);
});
Check("HTTPS targets retain mTLS requirement", () =>
{
    var options = Options(); options.Servers[0].AgentUrl = "https://127.0.0.1:7444/";
    Reject(() => options.Validate(false), "AGENT_MTLS_REQUIRED");
});

var repo = args.Length > 0 ? Path.GetFullPath(args[0]) : FindRepository(AppContext.BaseDirectory);
var dotnet = args.Length > 1 ? Path.GetFullPath(args[1]) : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
    ?? throw new InvalidOperationException("DOTNET_HOST_PATH_REQUIRED");
var temporary = Path.Combine(repo, "work", "transport-selftests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var processes = new List<Process>();
using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
try
{
    var agentPort = FreePort();
    var deniedAgentPort = FreePort();
    var consolePort = FreePort();
    var deniedConsolePort = FreePort();
    var legacyConsolePort = FreePort();
    Process Start(string role, string name, int port, string? allowed, string sourceMode = IntranetTransportSecurity.Allowlist)
    {
        var working = Path.Combine(temporary, name); Directory.CreateDirectory(working);
        var info = new ProcessStartInfo(dotnet) { WorkingDirectory = working, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        var application = Path.Combine(repo, "src", "IISManager." + role, "bin", "Release", "net10.0", "IISManager." + role + ".dll");
        info.ArgumentList.Add(application);
        info.ArgumentList.Add("--contentRoot"); info.ArgumentList.Add(working);
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("Agent", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("Console", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.Environment["DOTNET_ENVIRONMENT"] = role == "Agent" ? "Development" : "Production";
        info.Environment[role + "__TransportMode"] = IntranetTransportSecurity.HttpToken;
        info.Environment[role + "__SourceAccessMode"] = sourceMode;
        info.Environment[role + "__Port"] = port.ToString();
        if (role == "Agent")
        {
            info.Environment["Agent__Mode"] = "Simulation";
            info.Environment["Agent__AccessToken"] = testToken;
            if (allowed is not null) info.Environment["Agent__AllowedConsoleNetworks__0"] = allowed;
            info.Environment["Agent__StateDirectory"] = Path.Combine(working, "operations");
        }
        else
        {
            info.Environment["Console__ListenAddress"] = "127.0.0.1";
            if (allowed is not null) info.Environment["Console__AllowedClientNetworks__0"] = allowed;
            info.Environment["Console__TaskDirectory"] = Path.Combine(working, "tasks");
            info.Environment["Console__Servers__0__Id"] = "test";
            info.Environment["Console__Servers__0__Name"] = "Test";
            info.Environment["Console__Servers__0__AgentUrl"] = $"http://127.0.0.1:{agentPort}/";
            info.Environment["Console__Servers__0__AccessToken"] = testToken;
        }
        var process = Process.Start(info) ?? throw new InvalidOperationException("TEST_PROCESS_START_FAILED");
        processes.Add(process);
        // Drain output without exposing or persisting child logs/configuration.
        process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        System.Console.WriteLine($"START role={role} pid={process.Id} port={port} command={dotnet} {application} --contentRoot {working}");
        return process;
    }
    var agentProcess = Start("Agent", "agent", agentPort, null, IntranetTransportSecurity.PrivateNetwork);
    var deniedAgentProcess = Start("Agent", "agent-denied", deniedAgentPort, "127.0.0.2/32");
    var consoleProcess = Start("Console", "console", consolePort, null, IntranetTransportSecurity.PrivateNetwork);
    var deniedConsoleProcess = Start("Console", "console-denied", deniedConsolePort, "127.0.0.2/32");
    var legacyConsoleProcess = Start("Console", "console-legacy", legacyConsolePort, "::ffff:127.0.0.1/128");
    await Task.WhenAll(Ready(agentProcess, agentPort, "/agent/v1/info"), Ready(deniedAgentProcess, deniedAgentPort, "/agent/v1/info"),
        Ready(consoleProcess, consolePort, "/api/v1/servers"), Ready(deniedConsoleProcess, deniedConsolePort, "/api/v1/servers"), Ready(legacyConsoleProcess, legacyConsolePort, "/api/v1/servers"));

    async Task<HttpResponseMessage> AgentRequest(int port, params string[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/agent/v1/info");
        if (headers.Length > 0) request.Headers.TryAddWithoutValidation(IntranetTransportSecurity.TokenHeader, headers);
        return await browser.SendAsync(request);
    }
    await CheckAsync("HTTP missing token rejected", async () => { using var response = await AgentRequest(agentPort); Require(response.StatusCode == HttpStatusCode.Forbidden); });
    await CheckAsync("HTTP wrong token rejected", async () => { using var response = await AgentRequest(agentPort, wrongToken); Require(response.StatusCode == HttpStatusCode.Forbidden); });
    await CheckAsync("HTTP malformed token rejected", async () => { using var response = await AgentRequest(agentPort, "bad"); Require(response.StatusCode == HttpStatusCode.Forbidden); });
    await CheckAsync("HTTP duplicate token rejected", async () => { using var response = await AgentRequest(agentPort, testToken, testToken); Require(response.StatusCode == HttpStatusCode.Forbidden); });
    await CheckAsync("HTTP source allowlist enforced", async () => { using var response = await AgentRequest(deniedAgentPort, testToken); Require(response.StatusCode == HttpStatusCode.Forbidden); });
    await CheckAsync("Agent ignores forged forwarded peer", async () =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{deniedAgentPort}/agent/v1/info");
        request.Headers.Add(IntranetTransportSecurity.TokenHeader, testToken); request.Headers.Add("X-Forwarded-For", "127.0.0.2");
        using var response = await browser.SendAsync(request); Require(response.StatusCode == HttpStatusCode.Forbidden);
    });
    await CheckAsync("private-network Agent uses actual peer despite forwarded public address", async () =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{agentPort}/agent/v1/info");
        request.Headers.Add(IntranetTransportSecurity.TokenHeader, testToken); request.Headers.Add("X-Forwarded-For", "8.8.8.8");
        using var response = await browser.SendAsync(request); Require(response.IsSuccessStatusCode);
    });
    var clientOptions = Options(); clientOptions.Servers[0].AgentUrl = $"http://127.0.0.1:{agentPort}/";
    using var client = new AgentClient(clientOptions);
    await CheckAsync("AgentClient supplies token for info", async () => Require((await client.GetAsync<AgentInfo>("test", "info", CancellationToken.None)).Mode == "Simulation"));
    await CheckAsync("AgentClient authenticated snapshot", async () => Require((await client.GetAsync<IisSnapshot>("test", "snapshot", CancellationToken.None)).ApplicationPools.Length > 0));
    await CheckAsync("Console production HTTP requires user login before Agent info", async () =>
    {
        using var response = await browser.GetAsync($"http://127.0.0.1:{consolePort}/api/v1/servers/test/info"); Require(response.StatusCode == HttpStatusCode.Unauthorized);
        var content = await response.Content.ReadAsStringAsync(); Require(!content.Contains(testToken, StringComparison.OrdinalIgnoreCase));
    });
    await CheckAsync("Console production HTTP requires user login before Agent snapshot", async () =>
    {
        using var response = await browser.GetAsync($"http://127.0.0.1:{consolePort}/api/v1/servers/test/snapshot"); Require(response.StatusCode == HttpStatusCode.Unauthorized);
    });
    await CheckAsync("legacy mapped IPv6 Allowlist reaches the user authentication gate", async () =>
    {
        using var response = await browser.GetAsync($"http://127.0.0.1:{legacyConsolePort}/api/v1/servers/test/info"); Require(response.StatusCode == HttpStatusCode.Unauthorized);
    });
    await CheckAsync("private-network Console uses actual peer despite forwarded public address", async () =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{consolePort}/api/v1/servers"); request.Headers.Add("X-Forwarded-For", "8.8.8.8");
        using var response = await browser.SendAsync(request); Require(response.StatusCode == HttpStatusCode.Unauthorized);
    });
    await CheckAsync("unauthenticated server listing never exposes access token", async () =>
    {
        using var response = await browser.GetAsync($"http://127.0.0.1:{consolePort}/api/v1/servers");
        var content = await response.Content.ReadAsStringAsync(); Require(response.StatusCode == HttpStatusCode.Unauthorized && !content.Contains(testToken, StringComparison.OrdinalIgnoreCase) && !content.Contains("accessToken", StringComparison.OrdinalIgnoreCase));
    });
    await CheckAsync("Console rejects unexpected Host", async () =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{consolePort}/api/v1/servers"); request.Headers.Host = $"untrusted.internal:{consolePort}";
        using var response = await browser.SendAsync(request); Require(response.StatusCode == HttpStatusCode.BadRequest);
    });
    await CheckAsync("Console retains TCP source allowlist", async () =>
    {
        using var response = await browser.GetAsync($"http://127.0.0.1:{deniedConsolePort}/api/v1/servers"); Require(response.StatusCode == HttpStatusCode.Forbidden);
    });
    await CheckAsync("Console ignores forged forwarded peer", async () =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{deniedConsolePort}/api/v1/servers"); request.Headers.Add("X-Forwarded-For", "127.0.0.2");
        using var response = await browser.SendAsync(request); Require(response.StatusCode == HttpStatusCode.Forbidden);
    });
    await CheckAsync("Console POST without CSRF rejected", async () =>
    {
        using var response = await browser.PostAsync($"http://127.0.0.1:{consolePort}/api/v1/servers/test/operations/validate", new StringContent("{}", Encoding.UTF8, "application/json")); Require(response.StatusCode == HttpStatusCode.Forbidden);
    });
    string csrf = "";
    await CheckAsync("session exposes only browser CSRF token", async () =>
    {
        using var response = await browser.GetAsync($"http://127.0.0.1:{consolePort}/api/v1/session"); var content = await response.Content.ReadAsStringAsync();
        Require(response.IsSuccessStatusCode && !content.Contains(testToken, StringComparison.OrdinalIgnoreCase) && !content.Contains("accessToken", StringComparison.OrdinalIgnoreCase));
        using var json = JsonDocument.Parse(content); csrf = json.RootElement.GetProperty("csrfToken").GetString()!; Require(csrf.Length == 64);
    });
    async Task<HttpResponseMessage> Validation(string origin, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{consolePort}/api/v1/servers/test/operations/validate");
        request.Headers.Add("Origin", origin); request.Headers.Add("X-IISManager-CSRF", token); request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json"); return await browser.SendAsync(request);
    }
    await CheckAsync("Console rejects foreign Origin despite CSRF", async () => { using var response = await Validation("http://untrusted.internal", csrf); Require(response.StatusCode == HttpStatusCode.Forbidden); });
    await CheckAsync("Console rejects wrong browser CSRF", async () => { using var response = await Validation($"http://127.0.0.1:{consolePort}", wrongToken); Require(response.StatusCode == HttpStatusCode.Forbidden); });
    await CheckAsync("valid same-origin CSRF still requires application user login", async () =>
    {
        using var response = await Validation($"http://127.0.0.1:{consolePort}", csrf); Require(response.StatusCode == HttpStatusCode.Unauthorized);
    });
    async Task Ready(Process process, int port, string route)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited) throw new InvalidOperationException("TEST_CHILD_EXITED");
            try { using var response = await browser.GetAsync($"http://127.0.0.1:{port}{route}"); return; }
            catch (HttpRequestException) { await Task.Delay(100); }
        }
        throw new InvalidOperationException("TEST_CHILD_NOT_READY");
    }
}
catch (Exception exception)
{
    failures++; System.Console.WriteLine("FAIL HTTP integration setup exceptionType=" + exception.GetType().Name);
}
finally
{
    foreach (var process in processes)
    {
        if (!process.HasExited && !OperatingSystem.IsWindows())
        {
            // Request normal .NET host shutdown only for this test's recorded process.
            var stopInfo = new ProcessStartInfo("/bin/kill") { UseShellExecute = false };
            stopInfo.ArgumentList.Add("-TERM"); stopInfo.ArgumentList.Add(process.Id.ToString());
            using var stop = Process.Start(stopInfo); if (stop is not null) await stop.WaitForExitAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(deadline.Token); } catch (OperationCanceledException) { }
        }
        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        System.Console.WriteLine("STOP pid=" + process.Id); process.Dispose();
    }
    Directory.Delete(temporary, recursive: true);
    System.Console.WriteLine("CLEAN temporary state and child logs removed");
}
System.Console.WriteLine($"Transport self-tests: {count} checks, {failures} failures.");
return failures == 0 ? 0 : 1;

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
}
static string FindRepository(string start)
{
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "src", "IISManager.Agent", "IISManager.Agent.csproj"))) return directory.FullName;
    throw new InvalidOperationException("TEST_REPOSITORY_NOT_FOUND");
}
