using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using IISManager.Console;
using IISManager.Contracts;
using IISManager.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

internal static class ConsoleOperationsSelfTests
{
    public static async Task<int> RunAsync(string[] args)
    {
        var repo = args.Length > 0 ? Path.GetFullPath(args[0]) : FindRepository(AppContext.BaseDirectory);
        var dotnet = args.Length > 1 ? Path.GetFullPath(args[1]) : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? throw new InvalidOperationException("DOTNET_HOST_PATH_REQUIRED");
        var temporary = Path.Combine(repo, "work", "operations-selftests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var checks = 0;
        var failures = 0;
        Process? process = null;
        async Task Check(string name, Func<Task> assertion)
        {
            checks++;
            try { await assertion(); System.Console.WriteLine("PASS " + name); }
            catch (Exception exception) { failures++; System.Console.WriteLine("FAIL " + name + " exceptionType=" + exception.GetType().Name); }
        }
        try
        {
            await Check("history query defaults and supported page sizes", () =>
            {
                var defaults = Query(); Require(defaults.Page == 1 && defaults.PageSize == 20);
                foreach (var size in new[] { "10", "20", "50" }) Require(Query(("pageSize", size)).PageSize == int.Parse(size));
                Require(Query(("search", "  Pool  ")).Search == "Pool");
                Require(Query(("serverId", "removed-server")).ServerId == "removed-server");
                Require(Query(("serverId", new string('a', 64))).ServerId!.Length == 64);
                Require(Query(("search", new string('x', 128))).Search!.Length == 128);
                return Task.CompletedTask;
            });
            await Check("history rejects invalid conditions and pagination", () =>
            {
                foreach (var pair in new[]
                {
                    ("serverId", "../outside"), ("serverId", "test\n"), ("serverId", new string('a', 65)), ("type", "unrecognized"),
                    ("state", "done"), ("search", new string('x', 129)), ("search", "line\nfeed"),
                    ("from", "2026-10-06"), ("from", "2026-10-06T10:00:00Z\n"), ("from", "2026-10-06T10:00:00"), ("from", "2026-13-06T10:00:00Z"),
                    ("to", "invalid"), ("page", "0"), ("page", "-1"), ("page", "1.1"), ("page", ""),
                    ("page", "2147483648"), ("pageSize", "0"), ("pageSize", "100"), ("pageSize", ""), ("unexpected", "value")
                }) Require(!ConsoleOperationQuery.TryParse(Parameters(pair), out _));
                Require(!ConsoleOperationQuery.TryParse(Parameters(("from", "2026-10-06T01:00:00Z"), ("to", "2026-10-06T00:00:00Z")), out _));
                var duplicates = new QueryCollection(new Dictionary<string, StringValues> { ["page"] = new StringValues(["1", "2"]) });
                Require(!ConsoleOperationQuery.TryParse(duplicates, out _));
                Require(Query(("from", "2026-10-06T08:00:00+08:00"), ("to", "2026-10-06T00:00:00Z")).From == Query(("from", "2026-10-06T00:00:00Z")).From);
                return Task.CompletedTask;
            });
            var ledger = Path.Combine(temporary, "tasks");
            var accepted = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
            var records = Enumerable.Range(1, 25).Select(index => Record(index, accepted,
                index % 2 == 0 ? "removed-server" : "test", index % 3 == 0 ? OperationTypes.CreateSite : OperationTypes.Start,
                index % 2 == 0 ? OperationStates.Failed : OperationStates.Unknown)).ToArray();
            using (var store = new ConsoleTaskStore(ledger))
            {
                foreach (var record in records.Reverse()) await store.SaveAsync(record);
                await Check("history combines server type state target and accepted-time filters", async () =>
                {
                    var page = await store.QueryAsync(Query(("serverId", "removed-server"), ("type", OperationTypes.CreateSite),
                        ("state", OperationStates.Failed), ("search", "SITE-"), ("from", accepted.ToString("O")), ("to", accepted.ToString("O"))));
                    Require(page.Total == 4 && page.Items.All(item => item.ServerId == "removed-server" && item.Type == OperationTypes.CreateSite && item.State == OperationStates.Failed));
                    Require((await store.QueryAsync(Query(("search", "000000000025")))).Items.Single().OperationId == records[24].Request.OperationId);
                    Require((await store.QueryAsync(Query(("search", "does-not-exist")))).Total == 0);
                    Require((await store.QueryAsync(Query(("from", accepted.AddSeconds(1).ToString("O"))))).Total == 0);
                });
                await Check("history paging remains deterministic for equal timestamps", async () =>
                {
                    var first = await store.QueryAsync(Query(("pageSize", "10")));
                    var second = await store.QueryAsync(Query(("pageSize", "10"), ("page", "2")));
                    var third = await store.QueryAsync(Query(("pageSize", "10"), ("page", "3")));
                    var expected = records.Select(record => record.Request.OperationId).ToArray();
                    Require(first.Total == 25 && first.Items.Length == 10 && third.Items.Length == 5);
                    Require(first.Items.Concat(second.Items).Concat(third.Items).Select(item => item.OperationId).SequenceEqual(expected));
                    Require((await store.QueryAsync(Query(("pageSize", "10")))).Items.SequenceEqual(first.Items));
                    Require((await store.QueryAsync(Query(("page", "2147483647"), ("pageSize", "50")))).Items.Length == 0);
                });
                await Check("history summary has an exact field whitelist and preserves unknown state", async () =>
                {
                    var page = await store.QueryAsync(Query());
                    using var json = JsonDocument.Parse(JsonSerializer.Serialize(page, Protocol.Json));
                    Require(json.RootElement.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(new[] { "items", "page", "pageSize", "total" }.Order()));
                    foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
                        Require(item.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(new[] { "operationId", "serverId", "type", "target", "state", "acceptedAt", "updatedAt", "errorCode", "operatorName", "persistenceState" }.Order()));
                    Require(page.Items.Any(item => item.State == OperationStates.Unknown && item.ErrorCode == "DISPATCH_PENDING"));
                    Require(!json.RootElement.ToString().Contains("SYNTHETIC_PRIVATE_DATA", StringComparison.Ordinal));
                });
                await Check("history queries preserve ledger bytes and ignore nested files", async () =>
                {
                    Directory.CreateDirectory(Path.Combine(ledger, "nested"));
                    await File.WriteAllTextAsync(Path.Combine(ledger, "nested", "not-a-ledger.json"), "invalid synthetic data");
                    var before = LedgerHashes(ledger);
                    await store.QueryAsync(Query());
                    Require(before.SequenceEqual(LedgerHashes(ledger)) && store.Healthy);
                });
                await Check("history queries coordinate concurrent durable updates", async () =>
                {
                    await Task.WhenAll(Enumerable.Range(1, 12).Select(async index =>
                    {
                        var record = records[index - 1] with { UpdatedAt = accepted.AddMinutes(index) };
                        await store.SaveAsync(record);
                        Require((await store.QueryAsync(Query())).Total == 25);
                    }));
                    Require(store.Healthy);
                    var newer = Record(26, accepted.AddMinutes(1), "test", OperationTypes.Start, OperationStates.Succeeded);
                    await store.SaveAsync(newer);
                    Require((await store.QueryAsync(Query())).Items[0].OperationId == newer.Request.OperationId);
                });
            }
            await Check("history runtime corruption fails closed even outside the requested filter", async () =>
            {
                var badLedger = Path.Combine(temporary, "bad-runtime");
                using var store = new ConsoleTaskStore(badLedger);
                await store.SaveAsync(records[0]);
                await File.WriteAllTextAsync(Path.Combine(badLedger, records[0].Request.OperationId + ".json"), "SYNTHETIC_PRIVATE_DATA");
                await RejectLedger(() => store.QueryAsync(Query(("serverId", "removed-server"))));
                Require(!store.Healthy);
                var rejected = false;
                try { await store.SaveAsync(records[1]); } catch (InvalidOperationException exception) when (exception.Message == "CONSOLE_LEDGER_UNHEALTHY") { rejected = true; }
                Require(rejected);
            });
            await Check("history startup corruption fails closed", async () =>
            {
                var badLedger = Path.Combine(temporary, "bad-startup"); Directory.CreateDirectory(badLedger);
                await File.WriteAllTextAsync(Path.Combine(badLedger, "not-an-id.json"), "SYNTHETIC_PRIVATE_DATA");
                using var store = new ConsoleTaskStore(badLedger);
                Require(!store.Healthy); await RejectLedger(() => store.QueryAsync(Query()));
            });
            await Check("history rejects linked files without reading outside the ledger", async () =>
            {
                var linkedLedger = Path.Combine(temporary, "linked-ledger");
                using var store = new ConsoleTaskStore(linkedLedger);
                var outside = Path.Combine(temporary, "synthetic-outside.json");
                await File.WriteAllTextAsync(outside, JsonSerializer.Serialize(records[0], Protocol.Json));
                File.CreateSymbolicLink(Path.Combine(linkedLedger, records[0].Request.OperationId + ".json"), outside);
                await RejectLedger(() => store.QueryAsync(Query())); Require(!store.Healthy);
            });

            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            using var agentListener = new TcpListener(IPAddress.Loopback, 0); agentListener.Start();
            var agentPort = ((IPEndPoint)agentListener.LocalEndpoint).Port; agentListener.Stop();
            process = StartConsole(repo, dotnet, temporary, ledger, port, agentPort);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            var origin = $"http://127.0.0.1:{port}";
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (true)
            {
                if (process.HasExited || DateTimeOffset.UtcNow >= deadline) throw new InvalidOperationException("TEST_CONSOLE_NOT_READY");
                try { using var ready = await http.GetAsync(origin + "/api/v1/servers"); Require(ready.IsSuccessStatusCode); break; }
                catch (HttpRequestException) { await Task.Delay(100); }
            }
            await Check("history HTTP endpoint works with an offline Agent and removed server", async () =>
            {
                using var response = await http.GetAsync(origin + "/api/v1/operations?serverId=removed-server&pageSize=10");
                Require(response.IsSuccessStatusCode && response.Headers.CacheControl?.NoStore == true);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Require(json.RootElement.GetProperty("total").GetInt32() == 12 && json.RootElement.GetProperty("items").GetArrayLength() == 10);
                Require(!json.RootElement.ToString().Contains("SYNTHETIC_PRIVATE_DATA", StringComparison.Ordinal));
            });
            await Check("history HTTP rejects unknown and invalid filter values safely", async () =>
            {
                foreach (var query in new[] { "page=0", "type=invalid", "pageSize=100", "state=done", "unexpected=1", "page=1&page=2", "from=2026-10-06T12%3A00%3A00Z&to=2026-10-06T11%3A00%3A00Z" })
                {
                    using var response = await http.GetAsync(origin + "/api/v1/operations?" + query);
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    Require(response.StatusCode == HttpStatusCode.BadRequest && json.RootElement.GetProperty("code").GetString() == "OPERATION_QUERY_INVALID");
                }
            });
            await Check("operation HTTP rejects forged actor and persistence fields before dispatch", async () =>
            {
                using var session = await http.GetAsync(origin + "/api/v1/session");
                using var sessionJson = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
                var csrf = sessionJson.RootElement.GetProperty("csrfToken").GetString()!;
                foreach (var field in new[] { "operatorName", "operatorUserId", "persistenceState" })
                {
                    var data = JsonSerializer.SerializeToElement(records[0].Request, Protocol.Json).EnumerateObject()
                        .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
                    data[field] = "FORGED"; var before = LedgerHashes(ledger);
                    using var message = new HttpRequestMessage(HttpMethod.Post, origin + "/api/v1/servers/test/operations");
                    message.Headers.Add("Origin", origin); message.Headers.Add("X-IISManager-CSRF", csrf);
                    message.Content = JsonContent.Create(data, options: Protocol.Json);
                    using var response = await http.SendAsync(message);
                    Require(response.StatusCode == HttpStatusCode.BadRequest && before.SequenceEqual(LedgerHashes(ledger)));
                }
            });
            await Check("history HTTP corruption returns safe 503 without modifying the ledger", async () =>
            {
                await File.WriteAllTextAsync(Path.Combine(ledger, records[0].Request.OperationId + ".json"), "SYNTHETIC_PRIVATE_DATA");
                var before = LedgerHashes(ledger);
                using var response = await http.GetAsync(origin + "/api/v1/operations");
                var content = await response.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(content);
                Require(response.StatusCode == HttpStatusCode.ServiceUnavailable && json.RootElement.GetProperty("code").GetString() == "CONSOLE_LEDGER_UNHEALTHY");
                Require(!content.Contains("SYNTHETIC_PRIVATE_DATA", StringComparison.Ordinal) && before.SequenceEqual(LedgerHashes(ledger)));
            });
        }
        catch (Exception exception) { failures++; System.Console.WriteLine("FAIL history setup exceptionType=" + exception.GetType().Name); }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited && !OperatingSystem.IsWindows())
                {
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
            System.Console.WriteLine("CLEAN history test state and child logs removed");
        }
        System.Console.WriteLine($"Console operation history self-tests: {checks} checks, {failures} failures.");
        return failures == 0 ? 0 : 1;
    }

    private static Process StartConsole(string repo, string dotnet, string temporary, string ledger, int port, int agentPort)
    {
        var application = Path.Combine(repo, "src", "IISManager.Console", "bin", "Release", "net10.0", "IISManager.Console.dll");
        var info = new ProcessStartInfo(dotnet) { WorkingDirectory = temporary, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(application); info.ArgumentList.Add("--contentRoot"); info.ArgumentList.Add(temporary);
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("Console", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.Environment["DOTNET_ENVIRONMENT"] = "Development";
        info.Environment["Console__UserAuthentication__Enabled"] = "false";
        info.Environment["Console__OperationHistory__Enabled"] = "false";
        info.Environment["Console__TransportMode"] = IntranetTransportSecurity.HttpToken;
        info.Environment["Console__SourceAccessMode"] = IntranetTransportSecurity.PrivateNetwork;
        info.Environment["Console__ListenAddress"] = "127.0.0.1"; info.Environment["Console__Port"] = port.ToString();
        info.Environment["Console__TaskDirectory"] = ledger;
        info.Environment["Console__Servers__0__Id"] = "test"; info.Environment["Console__Servers__0__Name"] = "Synthetic test";
        info.Environment["Console__Servers__0__AgentUrl"] = $"http://127.0.0.1:{agentPort}/";
        info.Environment["Console__Servers__0__AccessToken"] = new string('A', 64);
        var process = Process.Start(info) ?? throw new InvalidOperationException("TEST_PROCESS_START_FAILED");
        process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        System.Console.WriteLine($"START pid={process.Id} port={port} command={dotnet} {application} --contentRoot {temporary}");
        return process;
    }

    private static ConsoleTaskRecord Record(int index, DateTimeOffset accepted, string serverId, string type, string state)
    {
        var request = new OperationRequest
        {
            OperationId = Guid.Parse("00000000-0000-0000-0000-" + index.ToString("D12")), Type = type,
            Target = type == OperationTypes.CreateSite ? new OperationTarget { SiteName = "Site-" + index } : new OperationTarget { ApplicationPoolName = "Pool-" + index },
            Parameters = JsonSerializer.SerializeToElement(new { physicalPath = "SYNTHETIC_PRIVATE_DATA", token = "SYNTHETIC_PRIVATE_DATA" }),
            ExpectedFingerprint = new string('A', 64), ExpiresAt = accepted.AddMinutes(1)
        };
        return new ConsoleTaskRecord
        {
            ServerId = serverId, Request = request, RequestHash = ConsoleTaskStore.RequestHash(serverId, request), State = state,
            AcceptedAt = accepted, UpdatedAt = accepted, SourceIp = "SYNTHETIC_PRIVATE_DATA",
            AgentRecord = new OperationRecord { Request = request, Before = JsonSerializer.SerializeToElement(new { privateValue = "SYNTHETIC_PRIVATE_DATA" }) },
            Error = new ApiError(state == OperationStates.Unknown ? "DISPATCH_PENDING" : "TEST_ERROR", "SYNTHETIC_PRIVATE_DATA")
        };
    }

    private static string[] LedgerHashes(string ledger) => Directory.EnumerateFiles(ledger, "*.json", SearchOption.TopDirectoryOnly)
        .Order(StringComparer.Ordinal).Select(file => Path.GetFileName(file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))).ToArray();
    private static QueryCollection Parameters(params (string Key, string Value)[] parameters) => new(parameters.ToDictionary(pair => pair.Key, pair => new StringValues(pair.Value)));
    private static ConsoleOperationQuery Query(params (string Key, string Value)[] parameters)
    {
        Require(ConsoleOperationQuery.TryParse(Parameters(parameters), out var query)); return query!;
    }
    private static async Task RejectLedger(Func<Task<ConsoleOperationPage>> action)
    {
        try { await action(); } catch (ConsoleLedgerUnavailableException exception) when (exception.Message == "CONSOLE_LEDGER_UNHEALTHY") { return; }
        throw new InvalidOperationException("TEST_EXPECTED_SAFE_LEDGER_ERROR");
    }
    private static void Require(bool result) { if (!result) throw new InvalidOperationException("TEST_ASSERTION_FAILED"); }
    private static string FindRepository(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "IISManager.Console", "IISManager.Console.csproj"))) return directory.FullName;
        throw new InvalidOperationException("TEST_REPOSITORY_NOT_FOUND");
    }
}
