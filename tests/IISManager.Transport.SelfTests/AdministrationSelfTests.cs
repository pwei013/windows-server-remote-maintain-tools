using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IISManager.Console;
using IISManager.Contracts;
using Microsoft.AspNetCore.Http;

internal static class AdministrationSelfTests
{
    private static readonly AppUserIdentity Admin = new(1001, "administrator", "synthetic-admin", "Synthetic admin");
    private static readonly AppUserIdentity Viewer = new(1001, "viewer", "synthetic-viewer", "Synthetic viewer");
    private static readonly AppUserIdentity Operator = new(1001, "operator", "synthetic-operator", "Synthetic operator");
    private static readonly AppUserIdentity Configurer = new(1001, "configurer", "synthetic-configurer", "Synthetic configurer");
    private static readonly string Token = new('A', 64);
    public static async Task<int> RunAsync()
    {
        var checks = 0; var failures = 0;
        async Task Check(string name, Func<Task> check)
        {
            checks++;
            try { await check(); System.Console.WriteLine("PASS administration " + name); }
            catch (Exception exception)
            {
                failures++; System.Console.WriteLine("FAIL administration " + name + " type=" + exception.GetType().Name +
                    (exception is AdministrationException error ? " code=" + error.Code : ""));
            }
        }
        await Check("legacy server rights remain compatible without granting administration to every user", () =>
        {
            using var f = new Fixture();
            Require(f.Access.CanRead(Viewer, "alpha") && f.Access.CanOperate(Viewer, "alpha") && f.Access.CanConfigure(Viewer, "alpha"));
            Require(!f.Access.CanAdminister(Viewer) && f.Access.CanAdminister(Admin) && !f.Access.CanRead(null, "alpha"));
            return Task.CompletedTask;
        });
        await Check("safe management views never serialize access tokens", () =>
        {
            using var f = new Fixture(); var json = JsonSerializer.Serialize(f.Service.Servers(Admin), Protocol.Json);
            Require(!json.Contains(Token, StringComparison.Ordinal) && !json.Contains("\"accessToken\"", StringComparison.Ordinal) && json.Contains("hasAccessToken"));
            return Task.CompletedTask;
        });
        await Check("server preview neither writes configuration nor contacts an agent", () =>
        {
            using var f = new Fixture(); var preview = f.Service.PreviewServer(f.Request("new"), Admin);
            Require(preview.Changes.Length > 0 && preview.RequiresRestart && preview.CredentialsChanged && !File.Exists(f.File) && f.Tester.Calls == 0);
            Require(!JsonSerializer.Serialize(preview, Protocol.Json).Contains(Token, StringComparison.Ordinal));
            return Task.CompletedTask;
        });
        await Check("save requires the exact preview and leaves active connections unchanged until reload", async () =>
        {
            using var f = new Fixture(); var request = f.Request("new");
            await Reject(() => f.Service.SaveServerAsync(request, Admin, CancellationToken.None), "ADMINISTRATION_PREVIEW_REQUIRED", 409);
            request.ExpectedFingerprint = f.Service.PreviewServer(request, Admin).Fingerprint;
            var saved = await f.Service.SaveServerAsync(request, Admin, CancellationToken.None);
            Require(saved.PendingRestart && saved.Servers.Single(server => server.Id == "new").RestartRequired);
            Require(f.Store.ActiveServers.Length == 2 && saved.Servers.Length == 3 && !File.ReadAllText(f.File).Contains(Token, StringComparison.Ordinal));
            using var next = f.Reload(); Require(next.ActiveServers.Length == 3 && next.ActiveServers.Single(server => server.Id == "new").AccessToken == Token);
        });
        await Check("blank token preserves the existing credential and previews no secret value", async () =>
        {
            using var f = new Fixture(); var request = f.Request("alpha"); request.AccessToken = ""; request.Name = "Changed name";
            var preview = f.Service.PreviewServer(request, Admin); Require(!preview.CredentialsChanged);
            request.ExpectedFingerprint = preview.Fingerprint; await f.Service.SaveServerAsync(request, Admin, CancellationToken.None);
            using var reloaded = f.Reload(); Require(reloaded.ActiveServers.Single(server => server.Id == "alpha").AccessToken == Token);
        });
        await Check("stale revisions and a secret changed after preview never overwrite the persisted file", async () =>
        {
            using var f = new Fixture(); var old = f.Request("alpha"); old.ExpectedFingerprint = f.Service.PreviewServer(old, Admin).Fingerprint;
            await f.Service.SaveServerAsync(old, Admin, CancellationToken.None); var bytes = await File.ReadAllBytesAsync(f.File);
            await Reject(() => f.Service.SaveServerAsync(old, Admin, CancellationToken.None), "ADMINISTRATION_CONFLICT", 409);
            var current = f.Request("beta"); current.ExpectedFingerprint = f.Service.PreviewServer(current, Admin).Fingerprint; current.AccessToken = new string('B', 64);
            await Reject(() => f.Service.SaveServerAsync(current, Admin, CancellationToken.None), "ADMINISTRATION_PREVIEW_REQUIRED", 409);
            var actual = await File.ReadAllBytesAsync(f.File); Require(bytes.SequenceEqual(actual));
        });
        await Check("connection testing calls only info once and never persists the draft", async () =>
        {
            using var f = new Fixture(); var result = await f.Service.TestConnectionAsync(f.Request("alpha"), Admin, CancellationToken.None);
            Require(result.MachineName == "SyntheticAgent" && f.Tester.Calls == 1 && !File.Exists(f.File));
        });
        await Check("HTTP credential retarget requires explicit reentry and never sends the saved token to a new address", async () =>
        {
            using var f = new Fixture(); var request = f.Request("alpha"); request.AccessToken = ""; request.AgentUrl = "http://different.invalid:7444/";
            await Reject(() => Task.FromResult(f.Service.PreviewServer(request, Admin)), "SERVER_CREDENTIAL_REENTRY_REQUIRED", 400);
            await Reject(() => f.Service.TestConnectionAsync(request, Admin, CancellationToken.None), "SERVER_CREDENTIAL_REENTRY_REQUIRED", 400);
            Require(f.Tester.Calls == 0 && !File.Exists(f.File));
        });
        await Check("a malicious info response cannot echo the saved token back to the browser", async () =>
        {
            using var f = new Fixture(); f.Tester.Info = f.Tester.Info with { MachineName = Token };
            try { await f.Service.TestConnectionAsync(f.Request("alpha"), Admin, CancellationToken.None); }
            catch (AgentCallException exception) when (exception.Code == "AGENT_RESPONSE_INVALID") { return; }
            throw new InvalidOperationException();
        });
        await Check("non administrators cannot preview save test connections or read policy", async () =>
        {
            using var f = new Fixture();
            await Reject(() => Task.FromResult(f.Service.Servers(Viewer)), "ADMINISTRATION_DENIED", 403);
            await Reject(() => Task.FromResult(f.Service.PreviewServer(f.Request("alpha"), Viewer)), "ADMINISTRATION_DENIED", 403);
            await Reject(() => f.Service.TestConnectionAsync(f.Request("alpha"), Viewer, CancellationToken.None), "ADMINISTRATION_DENIED", 403);
            Require(f.Tester.Calls == 0 && !File.Exists(f.File));
        });
        await Check("strict policy isolates app identity server scope and operation classes", async () =>
        {
            using var f = new Fixture(); await f.Strict();
            Require(f.Access.CanRead(Viewer, "alpha") && !f.Access.CanRead(Viewer, "beta") && !f.Access.CanOperate(Viewer, "alpha"));
            Require(f.Access.CanOperate(Operator, "alpha") && !f.Access.CanConfigure(Operator, "alpha"));
            Require(f.Access.CanConfigure(Configurer, "alpha") && !f.Access.CanOperate(Configurer, "alpha"));
            Require(!f.Access.CanRead(Viewer with { AppId = 2002 }, "alpha") && f.Access.CanRead(Admin, "removed-history-server"));
            Require(f.Access.AllowedServerIds(Viewer)!.SequenceEqual(["alpha"]) && f.Access.AllowedServerIds(Admin) is null);
        });
        await Check("a policy cannot lock out its current administrator", async () =>
        {
            using var f = new Fixture(); var request = new SaveAuthorizationPolicyRequest { Revision = f.Store.Revision,
                Policy = new() { Enabled = true, Administrators = [new(1001, "someone-else")] } };
            await Reject(() => Task.FromResult(f.Service.PreviewPolicy(request, Admin)), "AUTHORIZATION_SELF_LOCKOUT", 409);
            Require(!File.Exists(f.File));
        });
        await Check("policy previews show exact user server and action changes without a write", () =>
        {
            using var f = new Fixture(); var request = f.StrictRequest(); var preview = f.Service.PreviewPolicy(request, Admin);
            var changes = string.Join(" ", preview.Changes.Select(change => change.After));
            Require(changes.Contains("viewer") && changes.Contains("alpha") && changes.Contains("查看") && !File.Exists(f.File));
            return Task.CompletedTask;
        });
        await Check("strict policy persists independently and a new process does not fall back to legacy access", async () =>
        {
            using var f = new Fixture(); await f.Strict(); using var next = f.Reload(); var access = new ConsoleAccessService(next);
            Require(!access.CanRead(Viewer, "beta") && access.CanRead(Viewer, "alpha") && access.CanAdminister(Admin));
        });
        await Check("middleware rejects uppercase-path bypass and distinguishes body property casing", async () =>
        {
            using var f = new Fixture(); await f.Strict();
            await Reject(() => f.Access.AuthorizeRequestAsync(Context("/API/V1/SERVERS/alpha/operations", "POST", "{\"Type\":\"site.stop\"}"), Configurer), "SERVER_PERMISSION_DENIED", 403);
            await f.Access.AuthorizeRequestAsync(Context("/API/V1/SERVERS/alpha/operations", "POST", "{\"Type\":\"site.stop\"}"), Operator);
            await Reject(() => f.Access.AuthorizeRequestAsync(Context("/api/v1/servers/beta/snapshot", "GET"), Viewer), "SERVER_PERMISSION_DENIED", 403);
        });
        await Check("duplicate case variants in authorization JSON are rejected before a handler can reinterpret them", async () =>
        {
            using var f = new Fixture(); await f.Strict();
            await Reject(() => f.Access.AuthorizeRequestAsync(Context("/api/v1/health-checks/save", "POST", "{\"serverId\":\"alpha\",\"ServerId\":\"beta\"}"), Configurer), "AUTHORIZATION_BODY_INVALID", 400);
        });
        await Check("authorization preserves request body for the existing operation endpoint", async () =>
        {
            using var f = new Fixture(); await f.Strict(); var context = Context("/api/v1/servers/alpha/operations/validate", "POST", "{\"type\":\"site.stop\"}");
            await f.Access.AuthorizeRequestAsync(context, Operator); Require(context.Request.Body.Position == 0);
        });
        await Check("malformed and wrongly typed authorization JSON is a safe 400 and oversized input is 413", async () =>
        {
            using var f = new Fixture(); await f.Strict();
            foreach (var body in new[] { "{", "[]", "{\"Type\":17}" })
                await Reject(() => f.Access.AuthorizeRequestAsync(Context("/API/V1/SERVERS/alpha/operations", "POST", body), Operator), "AUTHORIZATION_BODY_INVALID", 400);
            await Reject(() => f.Access.AuthorizeRequestAsync(Context("/api/v1/servers/alpha/operations", "POST", new string(' ', 65537)), Operator), "AUTHORIZATION_BODY_TOO_LARGE", 413);
        });
        await Check("invalid schemes credentials query and bad http tokens never contact an agent", async () =>
        {
            using var f = new Fixture();
            foreach (var url in new[] { "file:///private", "http://name:secret@synthetic.invalid/", "http://synthetic.invalid/?token=value", "http://synthetic.invalid/route", "http://synthetic.invalid:0/" })
            {
                var request = f.Request("new"); request.AgentUrl = url;
                await Reject(() => Task.FromResult(f.Service.PreviewServer(request, Admin)), "SERVER_CONFIGURATION_INVALID", 400);
            }
            Require(f.Tester.Calls == 0 && !File.Exists(f.File));
        });
        await Check("external edits are not overwritten and further policy evaluation fails closed", async () =>
        {
            using var f = new Fixture(); var request = f.Request("alpha"); request.ExpectedFingerprint = f.Service.PreviewServer(request, Admin).Fingerprint;
            await f.Service.SaveServerAsync(request, Admin, CancellationToken.None); var bytes = await File.ReadAllBytesAsync(f.File);
            await File.AppendAllTextAsync(f.File, " "); var next = f.Request("beta"); next.ExpectedFingerprint = f.Service.PreviewServer(next, Admin).Fingerprint;
            await Reject(() => f.Service.SaveServerAsync(next, Admin, CancellationToken.None), "ADMINISTRATION_STORE_CHANGED", 503);
            await Reject(() => Task.FromResult(f.Access.CanRead(Viewer, "alpha")), "ADMINISTRATION_STORE_UNAVAILABLE", 503);
            Require(new FileInfo(f.File).Length == bytes.Length + 1);
        });
        await Check("corrupt or incorrectly protected state stops initialization instead of resetting authorization", async () =>
        {
            using var f = new Fixture(); var request = f.Request("alpha"); request.ExpectedFingerprint = f.Service.PreviewServer(request, Admin).Fingerprint;
            await f.Service.SaveServerAsync(request, Admin, CancellationToken.None); await File.WriteAllTextAsync(f.File, "{}");
            try { using var next = f.Reload(); }
            catch (InvalidOperationException exception) when (exception.Message == "ADMINISTRATION_STARTUP_STATE_UNAVAILABLE") { return; }
            throw new InvalidOperationException();
        });
        await Check("missing secure protector does not persist plaintext", async () =>
        {
            using var f = new Fixture(protector: false); var request = f.Request("alpha"); request.ExpectedFingerprint = f.Service.PreviewServer(request, Admin).Fingerprint;
            await Reject(() => f.Service.SaveServerAsync(request, Admin, CancellationToken.None), "ADMIN_SECRET_PROTECTION_UNAVAILABLE", 503);
            Require(!File.Exists(f.File));
        });
        await Check("development plaintext cannot be enabled on a production or non-loopback listener", () =>
        {
            foreach (var pair in new[] { (Development: false, Address: "127.0.0.1"), (Development: true, Address: "0.0.0.0") })
            {
                try { new AdministrationOptions { AllowDevelopmentPlaintext = true }.Validate(pair.Development, pair.Address); }
                catch (InvalidOperationException exception) when (exception.Message == "ADMINISTRATION_OPTIONS_INVALID") { continue; }
                throw new InvalidOperationException();
            }
            return Task.CompletedTask;
        });
        System.Console.WriteLine($"Administration self-tests: {checks - failures}/{checks} passed; synthetic identities and protected temporary state only.");
        return failures;
    }
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("TEST_ASSERTION_FAILED"); }
    private static async Task Reject(Func<Task> action, string code, int status)
    {
        try { await action(); }
        catch (AdministrationException exception) when (exception.Code == code && exception.StatusCode == status) { return; }
        throw new InvalidOperationException("TEST_EXPECTED_REJECTION");
    }
    private static DefaultHttpContext Context(string path, string method, string body = "")
    {
        var context = new DefaultHttpContext(); context.Request.Method = method; context.Request.Path = path;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body)); return context;
    }
    private sealed class Fixture : IDisposable
    {
        // macOS exposes /var as a symlink. Resolve that system alias in test setup,
        // while retaining the production store's refusal to traverse reparse points.
        private readonly string _directory = Path.Combine(RealTemporaryDirectory(), "iis-admin-test-" + Guid.NewGuid().ToString("N"));
        private static string RealTemporaryDirectory()
        {
            var temporary = Path.GetFullPath(Path.GetTempPath());
            return OperatingSystem.IsMacOS() && temporary.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + temporary : temporary;
        }
        public string File => Path.Combine(_directory, "store/administration-state.json");
        public ConsoleOptions Options { get; }
        public AdministrationStateStore Store { get; }
        public AdministrationService Service { get; }
        public ConsoleAccessService Access { get; }
        public FakeTester Tester { get; } = new();
        private readonly TestProtector? _protector;
        public Fixture(bool protector = true)
        {
            Directory.CreateDirectory(_directory); _protector = protector ? new() : null;
            Options = new ConsoleOptions { TransportMode = "HttpToken", ListenAddress = "127.0.0.1", Administration = new()
                { BootstrapAdministrators = [new(Admin.AppId, Admin.UserId)] },
                Servers = [new() { Id = "alpha", Name = "Alpha", AgentUrl = "http://synthetic.invalid:7444/", AccessToken = Token },
                    new() { Id = "beta", Name = "Beta", AgentUrl = "http://synthetic-two.invalid:7444/", AccessToken = Token }] };
            Store = Reload(); Service = new(Options, Store, Tester); Access = new(Store);
        }
        public AdministrationStateStore Reload() => new(Options, Path.Combine(_directory, "content"), Path.Combine(_directory, "store"), true, _protector);
        public SaveManagedServerRequest Request(string id) => new() { Revision = Store.Revision, Id = id, Name = "Synthetic " + id,
            AgentUrl = "http://synthetic.invalid:7444/", AccessToken = Token };
        public SaveAuthorizationPolicyRequest StrictRequest() => new() { Revision = Store.Revision, Policy = new()
        {
            Enabled = true, Administrators = [new(Admin.AppId, Admin.UserId)], Grants = [
                new() { AppId = Viewer.AppId, UserId = Viewer.UserId, ServerIds = ["alpha"], Read = true },
                new() { AppId = Operator.AppId, UserId = Operator.UserId, ServerIds = ["alpha"], Read = true, Operate = true },
                new() { AppId = Configurer.AppId, UserId = Configurer.UserId, ServerIds = ["alpha"], Read = true, Configure = true }]
        } };
        public async Task Strict()
        {
            var request = StrictRequest(); request = request with { ExpectedFingerprint = Service.PreviewPolicy(request, Admin).Fingerprint };
            await Service.SavePolicyAsync(request, Admin, CancellationToken.None);
        }
        public void Dispose() { Store.Dispose(); _protector?.Dispose(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
    private sealed class FakeTester : IAdministrationConnectionTester
    {
        public int Calls { get; private set; }
        public AgentInfo Info { get; set; } = new("1.0", "synthetic", "SyntheticAgent", "Windows", "Synthetic", "Synthetic", false, []);
        public Task<AgentInfo> TestAsync(ServerOptions server, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(Info); }
    }
    private sealed class TestProtector : IAdministrationSecretProtector, IDisposable
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public string Format => "test-only-aes-gcm";
        public byte[] Protect(byte[] clear)
        {
            var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var encrypted = new byte[clear.Length];
            using var aes = new AesGcm(_key, 16); aes.Encrypt(nonce, clear, encrypted, tag);
            return [.. nonce, .. tag, .. encrypted];
        }
        public byte[] Unprotect(byte[] encrypted)
        {
            var clear = new byte[encrypted.Length - 28]; using var aes = new AesGcm(_key, 16);
            aes.Decrypt(encrypted.AsSpan(0, 12), encrypted.AsSpan(28), encrypted.AsSpan(12, 16), clear); return clear;
        }
        public void Dispose() => CryptographicOperations.ZeroMemory(_key);
    }
}
