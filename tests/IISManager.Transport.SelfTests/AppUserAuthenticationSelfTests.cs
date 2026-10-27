using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IISManager.Console;
using IISManager.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

internal static class AppUserAuthenticationSelfTests
{
    private const string Account = "synthetic-admin";
    private const string Password = "Synthetic-only-密码-2026";
    private const string NewPassword = " New-synthetic-密码-2026 ";
    private const string AppId = "SYNTHETIC-APP-ID";
    private const string ApiBase = "https://synthetic.example.invalid/open";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string root)
    {
        var count = 0; var failures = 0;
        async Task Check(string name, Func<Task> action)
        {
            count++;
            try { await action(); System.Console.WriteLine("PASS " + name); }
            catch (Exception exception) { failures++; System.Console.WriteLine("FAIL " + name + " type=" + exception.GetType().Name); }
        }
        await Check("operation intent preserves the authenticated operator while tokens stay out of the ledger", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service();
            var context = await SignedIn(fixture, service);
            var user = service.CurrentUser(context) ?? throw new InvalidOperationException();
            var request = new OperationRequest { OperationId = Guid.NewGuid(), Type = OperationTypes.Start,
                Target = new() { ApplicationPoolName = "SyntheticPool" }, Parameters = JsonSerializer.SerializeToElement(new { }),
                ExpectedFingerprint = new string('A', 64), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1) };
            var task = ConsoleTaskRecord.CreateIntent("demo", request, "127.0.0.1", user, true);
            Require(task.OperatorUserId == user.UserId && task.OperatorName == "合成管理员");
            Require(ConsoleTaskRecord.CreateIntent("demo", request, "127.0.0.1", user with { DisplayName = "" }, true).OperatorName == user.Account);
            Require(ConsoleTaskRecord.CreateIntent("demo", request, "127.0.0.1", null, false).OperatorName is null);
            var temporary = Path.Combine(root, "work", "operator-identity-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var store = new ConsoleTaskStore(temporary); await store.SaveAsync(task);
                var persisted = await File.ReadAllTextAsync(Path.Combine(temporary, request.OperationId.ToString("D") + ".json"));
                Require(!persisted.Contains(service.GetAccessToken(context), StringComparison.Ordinal) && !persisted.Contains(Password, StringComparison.Ordinal));
                Require((await store.GetAsync(request.OperationId))?.OperatorName == user.DisplayName);
                Require(service.BackgroundAccessToken(user.UserId) == service.GetAccessToken(context));
                await service.LogoutAsync(context, default);
                Require(service.BackgroundAccessToken(user.UserId) is null);
            }
            finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
        });
        await Check("unconfigured authentication closes workbench and APIs without outgoing calls", async () =>
        {
            using var fixture = new Fixture();
            using var service = new AppUserAuthenticationService(new ConsoleOptions(), fixture.Handler());
            Require(service.Enabled && !service.IsConfigured);
            await Reject(() => service.LoginAsync(Context(), Login(), default), "APP_USER_AUTH_NOT_CONFIGURED", 503);
            var page = Context(); page.Request.Path = "/"; page.Request.QueryString = new QueryString("?operation=synthetic");
            Require(await AppUserAuthenticationEndpoints.CheckRequestAsync(page, service) && page.Response.StatusCode == 302);
            Require(page.Response.Headers.Location.ToString().Contains("returnUrl=", StringComparison.Ordinal));
            var api = Context(); api.Request.Path = "/api/v1/servers";
            await Reject(() => AppUserAuthenticationEndpoints.CheckRequestAsync(api, service), "APP_USER_SESSION_REQUIRED", 401);
            Require(fixture.Calls.Count == 0);
        });
        await Check("explicit HTTPS-only login policy rejects HTTP before touching the authentication provider", async () =>
        {
            using var fixture = new Fixture(); var settings = fixture.Options(); settings.UserAuthentication.RequireHttps = true;
            using var service = new AppUserAuthenticationService(settings, fixture.Handler(), fixture.Clock);
            var context = Context(); context.Request.Scheme = "http";
            await Reject(() => service.LoginAsync(context, Login(), default), "APP_USER_LOGIN_REQUIRES_HTTPS", 403);
            Require(fixture.Calls.Count == 0);
            settings.UserAuthentication.Enabled = false;
            try { settings.Validate(false); throw new InvalidOperationException("TEST_REQUIRED_AUTHENTICATION"); }
            catch (InvalidOperationException exception) when (exception.Message == "APP_USER_AUTH_REQUIRED_IN_PRODUCTION") { }
            settings.ListenAddress = "10.20.30.40";
            try { settings.Validate(true); throw new InvalidOperationException("TEST_LOOPBACK_BYPASS_REQUIRED"); }
            catch (InvalidOperationException exception) when (exception.Message == "APP_USER_AUTH_BYPASS_REQUIRES_DEVELOPMENT_LOOPBACK") { }
        });
        await Check("production HTTP login is allowed by default with an opaque browser session", async () =>
        {
            using var fixture = new Fixture(); var settings = fixture.Options(); settings.Validate(false);
            Require(!settings.AllowDevelopmentHttp && !settings.UsesHttpsListener());
            using var service = new AppUserAuthenticationService(settings, fixture.Handler(), fixture.Clock);
            var context = Context(); context.Request.Scheme = "http";
            await service.LoginAsync(context, Login(), default);
            Require(!service.HttpsRequired && fixture.Calls.Count == 1);
            var header = context.Response.Headers.SetCookie.Last()!;
            Require(header.Contains("httponly", StringComparison.OrdinalIgnoreCase) &&
                header.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase) &&
                !header.Contains("secure", StringComparison.OrdinalIgnoreCase));
            Require(Cookie(context).Length == 64 && Cookie(context) != fixture.LastToken);
        });
        await Check("segmented UTF8 RSA login includes the authentication V3 signature", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service();
            var context = Context(); var result = await service.LoginAsync(context, Login() with { Account = new string('中', 160) }, default);
            Require(result.User.AppId == 1001 && fixture.Calls.Single().Fields.SetEquals(["Account", "Password", "TimeStamp", "Sign"]));
            Require(fixture.Calls.Single().Method == "user-auth-login" && !fixture.Calls.Single().HasBearer);
            var cookie = Cookie(context); Require(cookie.Length == 64 && cookie != fixture.LastToken);
            Require(!JsonSerializer.Serialize(result.User).Contains(fixture.LastToken, StringComparison.Ordinal));
            var header = context.Response.Headers.SetCookie.Last()!;
            Require(header.Contains("httponly", StringComparison.OrdinalIgnoreCase) && header.Contains("secure", StringComparison.OrdinalIgnoreCase) && header.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
        });
        await Check("Console HTTPS listener retains HTTP-token Agent connection compatibility", () =>
        {
            using var fixture = new Fixture(); var settings = fixture.Options();
            settings.AllowDevelopmentHttp = false; settings.BrowserHttps = true;
            settings.TransportMode = "HttpToken"; settings.SourceAccessMode = "PrivateNetwork";
            settings.ServerCertificateThumbprint = new string('A', 40);
            settings.Servers = [new ServerOptions { Id = "demo", Name = "Synthetic", AgentUrl = "http://10.20.30.40:7444", AccessToken = new string('A', 64) }];
            settings.Validate(false); Require(settings.UsesHttpsListener());
            settings.BrowserHttps = false; Require(!settings.UsesHttpsListener());
            settings.TransportMode = "Https";
            settings.AgentClientCertificateThumbprint = new string('B', 40);
            settings.Servers = [new ServerOptions { Id = "demo", Name = "Synthetic", AgentUrl = "https://10.20.30.40:7444", ServerCertificateThumbprint = new string('C', 40) }];
            settings.ServerCertificateThumbprint = "";
            settings.Validate(false); Require(!settings.UsesHttpsListener());
            settings.BrowserHttps = null;
            try { settings.Validate(false); throw new InvalidOperationException("TEST_LEGACY_HTTPS_CERTIFICATE_REQUIRED"); }
            catch (InvalidOperationException exception) when (exception.Message == "CONSOLE_CERTIFICATE_REQUIRED") { }
            settings.ServerCertificateThumbprint = new string('A', 40);
            settings.Validate(false); Require(settings.UsesHttpsListener());
            return Task.CompletedTask;
        });
        await Check("invalid input cannot create an upstream login session", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service();
            foreach (var request in new[] { Login() with { Account = " " }, Login() with { Account = new string('a', 257) },
                Login() with { Account = "user\n" }, Login() with { Password = "" }, Login() with { Password = new string('a', 129) } })
                await Reject(() => service.LoginAsync(Context(), request, default), "APP_USER_LOGIN_INVALID", 400);
            Require(fixture.Calls.Count == 0);
        });
        await Check("HTTP and business authentication rejections use safe fixed messages", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service();
            foreach (var http in new[] { false, true })
            {
                fixture.FailureMethod = "user-auth-login"; fixture.FailureCode = 401; fixture.HttpFailure = http;
                await Reject(() => service.LoginAsync(Context(), Login(), default), "APP_USER_LOGIN_REJECTED", 401);
            }
            fixture.FailureCode = 429;
            await Reject(() => service.LoginAsync(Context(), Login(), default), "APP_USER_AUTH_RATE_LIMITED", 429);
            fixture.FailureCode = 403;
            await Reject(() => service.LoginAsync(Context(), Login(), default), "APP_USER_AUTH_DISABLED", 403);
        });
        await Check("malformed success envelopes cannot grant a local identity", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service();
            foreach (var transform in new Action<Dictionary<string, object?>>[]
            {
                body => body.Remove("AccessToken"), body => body["AccessToken"] = "bad\r\nheader", body => body["TokenType"] = "PlatformJWT",
                body => body["ExpiresAtUtc"] = "invalid", body => body["ExpiresAtUtc"] = fixture.Clock.GetUtcNow().AddSeconds(-1).ToString("O"),
                body => ((Dictionary<string, object?>)body["User"]!)["AppID"] = 1002,
                body => ((Dictionary<string, object?>)body["User"]!).Remove("UserID"), body => body["User"] = "invalid"
            })
            {
                fixture.LoginTransform = transform; var context = Context();
                await Reject(() => service.LoginAsync(context, Login(), default), "APP_USER_AUTH_RESPONSE_INVALID", 503);
                Require(service.CurrentUser(context) is null && !context.Response.Headers.SetCookie.Any());
            }
        });
        await Check("me signs each request and caches only read validations", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service();
            var context = await SignedIn(fixture, service);
            await service.ValidateAsync(context, false, default); await service.ValidateAsync(context, false, default);
            Require(fixture.Calls.Count(call => call.Method == "user-auth-me") == 1);
            fixture.Clock.Advance(TimeSpan.FromSeconds(61)); await service.ValidateAsync(context, false, default);
            await service.ValidateAsync(context, true, default); await service.ValidateAsync(context, true, default);
            var calls = fixture.Calls.Where(call => call.Method == "user-auth-me").ToArray();
            Require(calls.Length == 4 && calls.All(call => call.HasBearer && call.Fields.SetEquals(["TimeStamp", "Sign"])));
        });
        await Check("parallel reads share one validation and users remain isolated", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service();
            var first = await SignedIn(fixture, service); var second = await SignedIn(fixture, service, "synthetic-other");
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.ValidateAsync(first, false, default)));
            await service.ValidateAsync(second, false, default);
            Require(fixture.Calls.Count(call => call.Method == "user-auth-me") == 2);
            Require(service.CurrentUser(first)!.Account != service.CurrentUser(second)!.Account);
            Require(service.GetAccessToken(first) != service.GetAccessToken(second));
        });
        await Check("expires at UTC deadline without making another remote call", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service();
            var context = await SignedIn(fixture, service); var before = fixture.Calls.Count;
            fixture.Clock.Advance(TimeSpan.FromHours(9));
            await Reject(() => service.ValidateAsync(context, false, default), "APP_USER_SESSION_REQUIRED", 401);
            Require(service.CurrentUser(context) is null && fixture.Calls.Count == before);
        });
        await Check("remote HTTP or business me 401 invalidates only the affected session", async () =>
        {
            foreach (var http in new[] { true, false })
            {
                using var fixture = new Fixture(); using var service = fixture.Service();
                var first = await SignedIn(fixture, service); var second = await SignedIn(fixture, service, "synthetic-other");
                fixture.FailureMethod = "user-auth-me"; fixture.FailureCode = 401; fixture.HttpFailure = http;
                await Reject(() => service.ValidateAsync(first, true, default), "APP_USER_SESSION_EXPIRED", 401);
                Require(service.CurrentUser(first) is null && service.CurrentUser(second) is not null);
            }
        });
        await Check("dependency failures block access without deleting a still valid session", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service(); var context = await SignedIn(fixture, service);
            fixture.FailureMethod = "user-auth-me"; fixture.FailureCode = 503;
            await Reject(() => service.ValidateAsync(context, true, default), "APP_USER_AUTH_UNAVAILABLE", 503);
            Require(service.CurrentUser(context) is not null);
        });
        await Check("logout signs the request revokes locally and does not replay", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service(); var context = await SignedIn(fixture, service);
            Require(await service.LogoutAsync(context, default)); Require(service.CurrentUser(context) is null);
            Require(await service.LogoutAsync(context, default));
            var calls = fixture.Calls.Where(call => call.Method == "user-auth-logout").ToArray();
            Require(calls.Length == 1 && calls[0].Fields.SetEquals(["TimeStamp", "Sign"]) && calls[0].HasBearer);
        });
        await Check("me rate limiting backs off polling and write validation without dropping the session", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service(); var context = await SignedIn(fixture, service);
            fixture.FailureMethod = "user-auth-me"; fixture.FailureCode = 429;
            await Reject(() => service.ValidateAsync(context, true, default), "APP_USER_AUTH_RATE_LIMITED", 429);
            var before = fixture.Calls.Count;
            await Reject(() => service.ValidateAsync(context, false, default), "APP_USER_AUTH_RATE_LIMITED", 429);
            await Reject(() => service.ValidateAsync(context, true, default), "APP_USER_AUTH_RATE_LIMITED", 429);
            Require(fixture.Calls.Count == before && service.CurrentUser(context) is not null);
            fixture.FailureMethod = ""; fixture.Clock.Advance(TimeSpan.FromSeconds(61));
            await service.ValidateAsync(context, false, default); Require(fixture.Calls.Count == before + 1);
        });
        await Check("lost logout reply still ends local access and reports unconfirmed remote state", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service(); var context = await SignedIn(fixture, service);
            fixture.FailureMethod = "user-auth-logout"; fixture.ThrowTransport = true;
            Require(!await service.LogoutAsync(context, default)); Require(service.CurrentUser(context) is null);
            Require(fixture.Calls.Count(call => call.Method == "user-auth-logout") == 1);
        });
        await Check("business query create and update include current user bearer and retain V3 signature", async () =>
        {
            using var fixture = new Fixture(); using var authentication = fixture.Service();
            var accessor = new HttpContextAccessor { HttpContext = await SignedIn(fixture, authentication) };
            using var repository = new ZeroCodeSiteAnnotationRepository(fixture.Options().SiteAnnotations, fixture.Handler(), new AppUserTokenAccessor(accessor, authentication));
            Require((await repository.QueryAsync("demo", null, default)).Length == 0);
            var entry = new StoredSiteAnnotation("", SiteAnnotationRules.SiteKey("demo", 11, "Portal.Web"), "demo", 11, "Portal.Web", "门户", ["API"]);
            await repository.CreateAsync(entry, default); var read = await repository.QueryAsync("demo", entry.SiteKey, default);
            Require(read.Single().Alias == "门户"); await repository.UpdateAsync(read.Single() with { Alias = "新门户" }, default);
            Require(fixture.Calls.Where(call => !call.Method.StartsWith("user-auth-", StringComparison.Ordinal)).All(call => call.HasBearer && call.Account == Account && call.Fields.Contains("sign")));
            accessor.HttpContext = null;
        });
        await Check("concurrent business requests do not mutate shared default authentication headers", async () =>
        {
            using var fixture = new Fixture(); using var authentication = fixture.Service();
            var first = await SignedIn(fixture, authentication); var second = await SignedIn(fixture, authentication, "synthetic-other");
            var accessor = new HttpContextAccessor();
            using var repository = new ZeroCodeSiteAnnotationRepository(fixture.Options().SiteAnnotations, fixture.Handler(), new AppUserTokenAccessor(accessor, authentication));
            await Task.WhenAll(Task.Run(async () => { accessor.HttpContext = first; await repository.QueryAsync("demo", null, default); }),
                Task.Run(async () => { accessor.HttpContext = second; await repository.QueryAsync("demo", null, default); }));
            var accounts = fixture.Calls.Where(call => call.Method == ZeroCodeSiteAnnotationRepository.QueryMethod).Select(call => call.Account).ToHashSet();
            Require(accounts.SetEquals([Account, "synthetic-other"]));
        });
        await Check("business HTTP and business-code 401 invalidate the session and preserve authentication error", async () =>
        {
            foreach (var http in new[] { true, false })
            {
                using var fixture = new Fixture(); using var authentication = fixture.Service(); var context = await SignedIn(fixture, authentication);
                var accessor = new HttpContextAccessor { HttpContext = context };
                using var repository = new ZeroCodeSiteAnnotationRepository(fixture.Options().SiteAnnotations, fixture.Handler(), new AppUserTokenAccessor(accessor, authentication));
                fixture.FailureMethod = ZeroCodeSiteAnnotationRepository.QueryMethod; fixture.FailureCode = 401; fixture.HttpFailure = http;
                await Reject(() => repository.QueryAsync("demo", null, default), "APP_USER_SESSION_EXPIRED", 401);
                Require(authentication.CurrentUser(context) is null); accessor.HttpContext = null;
            }
        });
        await Check("write outcome remains unknown if subsequent read loses authentication", async () =>
        {
            var repository = new LostReadRepository(); var service = new SiteAnnotationService(repository, _ => true, (_, _) => Task.FromResult(Snapshot()));
            try { await service.SaveAsync("demo", new SaveSiteAnnotationRequest { SiteId = 11, SiteName = "Portal.Web", Alias = "门户", Tags = ["API"] }, default); }
            catch (SiteAnnotationException exception) when (exception.Code == "SITE_ANNOTATION_OUTCOME_UNKNOWN") { Require(repository.Writes == 1); return; }
            throw new InvalidOperationException("TEST_UNKNOWN_WRITE_REQUIRED");
        });
        await Check("password validation blocks invalid or mismatched input without a password write", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service(); var context = await SignedIn(fixture, service);
            foreach (var request in new[]
            {
                PasswordChange(oldPassword: ""), PasswordChange(oldPassword: new string('a', 129)),
                PasswordChange(newPassword: new string('a', 11)), PasswordChange(newPassword: new string('a', 129)),
                PasswordChange(confirmPassword: "different-synthetic-password")
            }) await Reject(() => service.ChangePasswordAsync(context, request, default), "APP_USER_PASSWORD_INVALID", 400);
            Require(!fixture.Calls.Any(call => call.Method == "user-auth-change-password") && service.CurrentUser(context) is not null);
        });
        await Check("password change uses RSA JSON string and bearer revoking all local sessions for only this user", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service();
            var context = await SignedIn(fixture, service); var sameUser = await SignedIn(fixture, service);
            var otherUser = await SignedIn(fixture, service, "synthetic-other-user");
            await service.ChangePasswordAsync(context, PasswordChange(), default);
            var call = fixture.Calls.Single(item => item.Method == "user-auth-change-password");
            Require(call.HasBearer && call.Fields.SetEquals(["OldPassword", "NewPassword", "TimeStamp", "Sign"]));
            Require(service.CurrentUser(context) is null && service.CurrentUser(sameUser) is null && service.CurrentUser(otherUser) is not null);
            await Reject(() => service.LoginAsync(Context(), Login(), default), "APP_USER_LOGIN_REJECTED", 401);
            var login = Context(); await service.LoginAsync(login, Login() with { Password = NewPassword }, default);
            Require(Cookie(login).Length == 64);
        });
        await Check("incorrect old password is a recoverable error when the current token still verifies", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service(); var context = await SignedIn(fixture, service);
            await Reject(() => service.ChangePasswordAsync(context, PasswordChange(oldPassword: "synthetic-wrong-password"), default), "APP_USER_PASSWORD_REJECTED", 400);
            Require(service.CurrentUser(context) is not null && fixture.Calls.Last().Method == "user-auth-me");
            await service.ChangePasswordAsync(context, PasswordChange(), default);
            Require(service.CurrentUser(context) is null);
        });
        await Check("token revoked during password change signs out without claiming success", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service(); var context = await SignedIn(fixture, service);
            fixture.ExpireDuringPasswordChange = true;
            await Reject(() => service.ChangePasswordAsync(context, PasswordChange(), default), "APP_USER_SESSION_EXPIRED", 401);
            Require(service.CurrentUser(context) is null && fixture.Calls.Count(call => call.Method == "user-auth-change-password") == 1);
        });
        await Check("lost password write reply revokes local sessions and never replays a possibly committed write", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service();
            var context = await SignedIn(fixture, service); var sameUser = await SignedIn(fixture, service);
            fixture.ThrowAfterPasswordWrite = true;
            await Reject(() => service.ChangePasswordAsync(context, PasswordChange(), default), "APP_USER_PASSWORD_OUTCOME_UNKNOWN", 503);
            Require(service.CurrentUser(context) is null && service.CurrentUser(sameUser) is null);
            await Reject(() => service.ChangePasswordAsync(context, PasswordChange(), default), "APP_USER_SESSION_REQUIRED", 401);
            Require(fixture.Calls.Count(call => call.Method == "user-auth-change-password") == 1);
            var login = Context(); await service.LoginAsync(login, Login() with { Password = NewPassword }, default);
            Require(Cookie(login).Length == 64);
        });
        await Check("password throttling blocks further checks during cooldown without dropping the session", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service(); var context = await SignedIn(fixture, service);
            fixture.FailureMethod = "user-auth-change-password"; fixture.FailureCode = 429;
            await Reject(() => service.ChangePasswordAsync(context, PasswordChange(), default), "APP_USER_AUTH_RATE_LIMITED", 429);
            var countBefore = fixture.Calls.Count;
            await Reject(() => service.ValidateAsync(context, true, default), "APP_USER_AUTH_RATE_LIMITED", 429);
            Require(fixture.Calls.Count == countBefore && service.CurrentUser(context) is not null);
        });
        await Check("production HTTP password endpoint requires session CSRF and fresh verification then returns sign-out", async () =>
        {
            using var fixture = new Fixture(); await using var app = await StartApp(root, fixture); var address = Address(app);
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = new Uri(address) };
            using var bootstrap = await client.GetAsync("/api/v1/session"); using var session = JsonDocument.Parse(await bootstrap.Content.ReadAsStringAsync());
            using var noCsrf = await client.PostAsync("/api/v1/auth/change-password", Body(PasswordChange()));
            Require(noCsrf.StatusCode == HttpStatusCode.Forbidden && fixture.Calls.Count == 0);
            client.DefaultRequestHeaders.Add("Origin", address); client.DefaultRequestHeaders.Add("X-IISManager-CSRF", session.RootElement.GetProperty("csrfToken").GetString()!);
            using var noLogin = await client.PostAsync("/api/v1/auth/change-password", Body(PasswordChange())); Require(noLogin.StatusCode == HttpStatusCode.Unauthorized);
            using var loggedIn = await client.PostAsync("/api/v1/auth/login", Body(Login())); Require(loggedIn.IsSuccessStatusCode);
            using var changed = await client.PostAsync("/api/v1/auth/change-password", Body(PasswordChange()));
            var body = await changed.Content.ReadAsStringAsync(); using var result = JsonDocument.Parse(body);
            Require(changed.IsSuccessStatusCode && result.RootElement.GetProperty("passwordChanged").GetBoolean() && result.RootElement.GetProperty("signedOut").GetBoolean());
            Require(!body.Contains(Password, StringComparison.Ordinal) && !body.Contains(NewPassword, StringComparison.Ordinal) && !body.Contains(fixture.LastToken, StringComparison.Ordinal));
            Require(fixture.Calls.TakeLast(2).Select(call => call.Method).SequenceEqual(["user-auth-me", "user-auth-change-password"]));
            using var denied = await client.GetAsync("/api/v1/servers"); Require(denied.StatusCode == HttpStatusCode.Unauthorized);
        });
        await Check("production HTTP flow enables login while enforcing CSRF and protecting pages and APIs", async () =>
        {
            using var fixture = new Fixture(); await using var app = await StartApp(root, fixture);
            var address = Address(app); var cookies = new CookieContainer();
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = new Uri(address) };
            using var rootReply = await client.GetAsync("/"); Require(rootReply.StatusCode == HttpStatusCode.Redirect);
            using var denied = await client.GetAsync("/api/v1/servers"); Require(denied.StatusCode == HttpStatusCode.Unauthorized);
            using var alias = await client.GetAsync(new Uri(address + "//index.html")); Require(alias.StatusCode == HttpStatusCode.Redirect);
            using var script = await client.GetAsync("/app.js"); Require(script.StatusCode == HttpStatusCode.Unauthorized);
            using var logo = await client.GetAsync("/assets/auth/company-logo.png"); Require(logo.StatusCode == HttpStatusCode.OK);
            using var bootstrap = await client.GetAsync("/api/v1/session"); using var session = JsonDocument.Parse(await bootstrap.Content.ReadAsStringAsync());
            Require(!session.RootElement.GetProperty("loginRequiresHttps").GetBoolean());
            var csrf = session.RootElement.GetProperty("csrfToken").GetString()!;
            using var noCsrf = await client.PostAsync("/api/v1/auth/login", Body(Login())); Require(noCsrf.StatusCode == HttpStatusCode.Forbidden && fixture.Calls.Count == 0);
            client.DefaultRequestHeaders.Add("Origin", address); client.DefaultRequestHeaders.Add("X-IISManager-CSRF", csrf);
            using var loggedIn = await client.PostAsync("/api/v1/auth/login", Body(Login())); var text = await loggedIn.Content.ReadAsStringAsync();
            Require(loggedIn.IsSuccessStatusCode && !text.Contains(fixture.LastToken, StringComparison.Ordinal) && !text.Contains(Password, StringComparison.Ordinal));
            using var secondBootstrap = await client.GetAsync("/api/v1/session"); using var second = JsonDocument.Parse(await secondBootstrap.Content.ReadAsStringAsync());
            Require(second.RootElement.GetProperty("csrfToken").GetString() == csrf);
            using var workbench = await client.GetAsync("/"); Require(workbench.StatusCode == HttpStatusCode.OK);
            using var servers = await client.GetAsync("/api/v1/servers"); Require(servers.IsSuccessStatusCode);
            using var post = await client.PostAsync("/api/v1/protected-check", Body(new { })); Require(post.IsSuccessStatusCode);
            using var me = await client.GetAsync("/api/v1/auth/me"); Require(me.IsSuccessStatusCode);
            using var logout = await client.PostAsync("/api/v1/auth/logout", null); Require(logout.IsSuccessStatusCode);
            using var after = await client.GetAsync("/api/v1/servers"); Require(after.StatusCode == HttpStatusCode.Unauthorized);
            using var history = await client.GetAsync("/operations.html"); Require(history.StatusCode == HttpStatusCode.Redirect);
        });
        System.Console.WriteLine($"{count - failures}/{count} application user authentication checks passed");
        return failures == 0 ? 0 : 1;
    }

    public static async Task<int> PreviewAsync(string root)
    {
        using var fixture = new Fixture(); await using var app = await StartApp(root, fixture);
        System.Console.WriteLine("AUTH_PREVIEW " + Address(app));
        System.Console.WriteLine("Synthetic in-memory authentication and site annotations only; no external calls.");
        await app.WaitForShutdownAsync(); return 0;
    }

    private static async Task<WebApplication> StartApp(string root, Fixture fixture)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { ContentRootPath = root, WebRootPath = Path.Combine(root, "src/IISManager.Console/wwwroot"), EnvironmentName = "Production",
            Args = ["--hostBuilder:reloadConfigOnChange=false"] });
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        var options = fixture.Options(); options.Validate(false); builder.Services.AddSingleton(options); builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(_ => fixture.Service()); builder.Services.AddSingleton<IAppUserTokenAccessor, AppUserTokenAccessor>();
        builder.Services.AddSingleton<ISiteAnnotationRepository>(provider => new ZeroCodeSiteAnnotationRepository(options.SiteAnnotations, fixture.Handler(), provider.GetRequiredService<IAppUserTokenAccessor>()));
        builder.Services.AddSingleton(provider => new SiteAnnotationService(provider.GetRequiredService<ISiteAnnotationRepository>(), id => id == "demo", (_, _) => Task.FromResult(Snapshot())));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            if (context.Request.Method == "POST" && !BrowserProtection.IsValid(context))
            { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new ApiError("SAME_ORIGIN_REQUIRED", "请求来源或 CSRF 校验失败。")); return; }
            try
            {
                if (await AppUserAuthenticationEndpoints.CheckRequestAsync(context, context.RequestServices.GetRequiredService<AppUserAuthenticationService>())) return;
                await next();
            }
            catch (AppUserAuthenticationException exception)
            { await AppUserAuthenticationEndpoints.WriteErrorAsync(context, context.RequestServices.GetRequiredService<AppUserAuthenticationService>(), exception); }
            catch (SiteAnnotationException exception)
            { context.Response.StatusCode = exception.StatusCode; await context.Response.WriteAsJsonAsync(new ApiError(exception.Code, exception.SafeMessage)); }
        });
        app.UseDefaultFiles(); app.UseStaticFiles(); AppUserAuthenticationEndpoints.MapEndpoints(app);
        app.MapGet("/api/v1/servers", () => new[] { new ServerSummary("demo", "本机合成服务器", "Simulation", "127.0.0.1") });
        app.MapGet("/api/v1/servers/demo/info", () => new AgentInfo(Protocol.Version, "1.0.0", "合成预览", "Simulation", "IIS SIMULATION", "模拟", false, []));
        app.MapGet("/api/v1/servers/demo/snapshot", Snapshot);
        app.MapGet("/api/v1/servers/demo/site-topology", () => new SiteTopologySnapshot(DateTimeOffset.UtcNow, [], Snapshot().ApplicationPools));
        app.MapGet("/api/v1/servers/demo/certificates", () => Array.Empty<CertificateInfo>());
        app.MapGet("/api/v1/servers/demo/site-annotations", async (SiteAnnotationService service) => await service.GetAsync("demo", default));
        app.MapPost("/api/v1/servers/demo/site-annotations", async (SaveSiteAnnotationRequest request, SiteAnnotationService service) => await service.SaveAsync("demo", request, default));
        app.MapGet("/api/v1/operations", () => new { items = Array.Empty<object>(), total = 0, page = 1, pageSize = 20 });
        app.MapPost("/api/v1/protected-check", () => Results.Ok(new { accepted = true }));
        await app.StartAsync(); return app;
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private static IisSnapshot Snapshot() => new(DateTimeOffset.UtcNow,
        [new PoolConfig { Name = "Portal.Pool", State = "Started", AutoStart = true, Applications = ["Portal.Web/"] }],
        [new SiteConfig { Id = 11, Name = "Portal.Web", State = "Started", ApplicationPoolName = "Portal.Pool", PhysicalPath = @"D:\Sites\Portal", Bindings = [new BindingConfig { Protocol = "https", Port = 443, HostName = "portal.example.test" }] }]);
    private static AppUserLoginRequest Login() => new() { Account = Account, Password = Password };
    private static AppUserChangePasswordRequest PasswordChange(string oldPassword = Password, string newPassword = NewPassword, string? confirmPassword = null)
        => new() { OldPassword = oldPassword, NewPassword = newPassword, ConfirmPassword = confirmPassword ?? newPassword };
    private static HttpContext Context(string? cookie = null)
    {
        var context = new DefaultHttpContext(); context.Request.Scheme = "https";
        if (cookie is not null) context.Request.Headers.Cookie = AppUserAuthenticationService.CookieName + "=" + cookie;
        return context;
    }
    private static string Cookie(HttpContext context) => context.Response.Headers.SetCookie
        .Last(value => value!.StartsWith(AppUserAuthenticationService.CookieName + "=", StringComparison.Ordinal))!.Split(';')[0].Split('=', 2)[1];
    private static async Task<HttpContext> SignedIn(Fixture fixture, AppUserAuthenticationService service, string account = Account)
    {
        var login = Context(); await service.LoginAsync(login, Login() with { Account = account }, default); return Context(Cookie(login));
    }
    private static StringContent Body<T>(T value) => new(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json");
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("TEST_ASSERTION_FAILED"); }
    private static async Task Reject(Func<Task> action, string code, int status)
    {
        try { await action(); }
        catch (AppUserAuthenticationException exception) when (exception.Code == code && exception.StatusCode == status) { return; }
        throw new InvalidOperationException("TEST_EXPECTED_AUTH_REJECTION");
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
    private sealed record Call(string Method, string Account, bool HasBearer, HashSet<string> Fields);
    private sealed class Fixture : IDisposable
    {
        private readonly RSA _rsa = RSA.Create(1024);
        private readonly ConcurrentDictionary<string, string> _tokens = new();
        private readonly ConcurrentDictionary<string, string> _passwords = new();
        private StoredSiteAnnotation? _annotation;
        public readonly Clock Clock = new();
        public readonly ConcurrentQueue<Call> Calls = new();
        public string LastToken = "";
        public string FailureMethod = "";
        public int FailureCode = 503;
        public bool HttpFailure;
        public bool ThrowTransport;
        public bool ThrowAfterPasswordWrite;
        public bool ExpireDuringPasswordChange;
        public Action<Dictionary<string, object?>>? LoginTransform;
        public ConsoleOptions Options() => new()
        {
            BrowserHttps = false,
            SourceAccessMode = "PrivateNetwork",
            SiteAnnotations = new SiteAnnotationZeroCodeOptions
            { Enabled = true, ApplicationId = 1001, OpenApiAppId = AppId, OpenApiBaseUrl = ApiBase, PublicKeyBase64 = Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo()) }
        };
        public AppUserAuthenticationService Service() => new(Options(), Handler(), Clock);
        public HttpMessageHandler Handler() => new HandlerImpl(this);
        private sealed class HandlerImpl(Fixture owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => owner.SendAsync(request, cancellation);
        }
        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Require(request.Method == HttpMethod.Post && request.RequestUri!.Scheme == "https" && request.Content!.Headers.ContentType!.MediaType == "application/json");
            var parts = request.RequestUri!.AbsolutePath.Split('/'); var method = parts[^2]; Require(parts[^1] == AppId);
            var encrypted = JsonSerializer.Deserialize<string>(await request.Content!.ReadAsStringAsync(cancellation))!;
            Require(encrypted.Length > 0 && encrypted.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F'));
            var data = Convert.FromHexString(encrypted); using var plaintext = new MemoryStream();
            lock (_rsa)
                for (var offset = 0; offset < data.Length; offset += _rsa.KeySize / 8)
                { var block = _rsa.Decrypt(data.AsSpan(offset, _rsa.KeySize / 8).ToArray(), RSAEncryptionPadding.Pkcs1); plaintext.Write(block); }
            using var payload = JsonDocument.Parse(plaintext.ToArray()); var root = payload.RootElement;
            var fields = root.EnumerateObject().Select(property => property.Name).ToHashSet();
            if (method.StartsWith("user-auth-", StringComparison.Ordinal))
            {
                var authTimestamp = root.GetProperty("TimeStamp").GetInt64();
                Require(authTimestamp == Clock.GetUtcNow().ToUnixTimeMilliseconds());
                Require(root.GetProperty("Sign").GetString() == Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(
                    AppId + method + authTimestamp.ToString(CultureInfo.InvariantCulture)))));
            }
            var bearer = request.Headers.Authorization;
            var account = method == "user-auth-login" ? root.GetProperty("Account").GetString()! :
                bearer?.Scheme == "Bearer" && _tokens.TryGetValue(bearer.Parameter!, out var user) ? user : "";
            Calls.Enqueue(new Call(method, account, bearer is not null, fields));
            if (method == FailureMethod)
            {
                if (ThrowTransport) throw new HttpRequestException("SYNTHETIC_TRANSPORT_FAILURE");
                return Reply(new { ResultCode = FailureCode.ToString(CultureInfo.InvariantCulture), ResultMsg = "SYNTHETIC_PRIVATE_UPSTREAM_MESSAGE" }, HttpFailure ? FailureCode : 200);
            }
            if (method == "user-auth-login")
            {
                Require(fields.SetEquals(["Account", "Password", "TimeStamp", "Sign"]) && bearer is null);
                if (root.GetProperty("Password").GetString() != _passwords.GetOrAdd(account, Password)) return Reply(new { ResultCode = "401" });
                LastToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); _tokens[LastToken] = account;
                var response = new Dictionary<string, object?>
                { ["ResultCode"] = "200", ["AccessToken"] = LastToken, ["TokenType"] = "Bearer", ["ExpiresAtUtc"] = Clock.GetUtcNow().AddHours(8).ToString("O"), ["User"] = User(account) };
                LoginTransform?.Invoke(response); return Reply(response);
            }
            if (account.Length == 0 || bearer?.Scheme != "Bearer") return Reply(new { ResultCode = "401" });
            if (method == "user-auth-me") { Require(fields.SetEquals(["TimeStamp", "Sign"])); return Reply(new Dictionary<string, object?> { ["ResultCode"] = "200", ["User"] = User(account) }); }
            if (method == "user-auth-logout") { Require(fields.SetEquals(["TimeStamp", "Sign"])); _tokens.TryRemove(bearer!.Parameter!, out _); return Reply(new { ResultCode = "200" }); }
            if (method == "user-auth-change-password")
            {
                Require(fields.SetEquals(["OldPassword", "NewPassword", "TimeStamp", "Sign"]));
                if (ExpireDuringPasswordChange) { _tokens.TryRemove(bearer!.Parameter!, out _); return Reply(new { ResultCode = "401" }); }
                if (root.GetProperty("OldPassword").GetString() != _passwords.GetOrAdd(account, Password)) return Reply(new { ResultCode = "401" });
                var newPassword = root.GetProperty("NewPassword").GetString()!; Require(newPassword.Length is >= 12 and <= 128);
                _passwords[account] = newPassword;
                foreach (var pair in _tokens) if (pair.Value == account) _tokens.TryRemove(pair.Key, out _);
                if (ThrowAfterPasswordWrite) throw new HttpRequestException("SYNTHETIC_LOST_PASSWORD_REPLY");
                return Reply(new { ResultCode = "200" });
            }
            var timestamp = root.GetProperty("timeStamp").GetInt64();
            Require(root.GetProperty("sign").GetString() == Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(AppId + method + timestamp.ToString(CultureInfo.InvariantCulture)))));
            if (method == ZeroCodeSiteAnnotationRepository.QueryMethod)
            {
                var rows = _annotation is null ? Array.Empty<object>() : [Row(_annotation)];
                return Reply(new { ResultCode = "200", ReturnValue = rows, TotalCount = rows.Length });
            }
            if (method == ZeroCodeSiteAnnotationRepository.CreateMethod)
                _annotation = new StoredSiteAnnotation("1", root.GetProperty(ZeroCodeSiteAnnotationRepository.KeyField).GetString()!, root.GetProperty(ZeroCodeSiteAnnotationRepository.ServerField).GetString()!,
                    root.GetProperty(ZeroCodeSiteAnnotationRepository.SiteIdField).GetInt64(), root.GetProperty(ZeroCodeSiteAnnotationRepository.SiteNameField).GetString()!,
                    root.GetProperty(ZeroCodeSiteAnnotationRepository.AliasField).GetString()!, JsonSerializer.Deserialize<string[]>(root.GetProperty(ZeroCodeSiteAnnotationRepository.TagsField).GetString()!)!);
            else if (method == ZeroCodeSiteAnnotationRepository.UpdateMethod)
                _annotation = _annotation! with { Alias = root.GetProperty(ZeroCodeSiteAnnotationRepository.AliasField).GetString()!, Tags = JsonSerializer.Deserialize<string[]>(root.GetProperty(ZeroCodeSiteAnnotationRepository.TagsField).GetString()!)! };
            else throw new InvalidOperationException("TEST_UNKNOWN_METHOD");
            return Reply(new { ResultCode = "200", ReturnValue = new { primaryId = "1" } });
        }
        private static Dictionary<string, object?> User(string account) => new()
        { ["AppID"] = 1001, ["UserID"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account))), ["Account"] = account, ["DisplayName"] = "合成管理员" };
        private static object Row(StoredSiteAnnotation entry) => new Dictionary<string, object?>
        {
            ["primaryId"] = entry.PrimaryId, [ZeroCodeSiteAnnotationRepository.KeyField] = entry.SiteKey,
            [ZeroCodeSiteAnnotationRepository.ServerField] = entry.ServerId, [ZeroCodeSiteAnnotationRepository.SiteIdField] = entry.SiteId,
            [ZeroCodeSiteAnnotationRepository.SiteNameField] = entry.SiteName, [ZeroCodeSiteAnnotationRepository.AliasField] = entry.Alias,
            [ZeroCodeSiteAnnotationRepository.TagsField] = JsonSerializer.Serialize(entry.Tags), ["lastModifiedAt"] = "synthetic"
        };
        private static HttpResponseMessage Reply<T>(T value, int code = 200) => new((HttpStatusCode)code)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        public void Dispose() => _rsa.Dispose();
    }
    private sealed class LostReadRepository : ISiteAnnotationRepository
    {
        public int Writes;
        public Task<StoredSiteAnnotation[]> QueryAsync(string serverId, string? siteKey, CancellationToken cancellation)
        {
            if (Writes > 0) throw new AppUserAuthenticationException("APP_USER_SESSION_EXPIRED", 401);
            return Task.FromResult(Array.Empty<StoredSiteAnnotation>());
        }
        public Task CreateAsync(StoredSiteAnnotation entry, CancellationToken cancellation)
        { Writes++; throw new SiteAnnotationException("SITE_ANNOTATIONS_UNAVAILABLE"); }
        public Task UpdateAsync(StoredSiteAnnotation entry, CancellationToken cancellation) => throw new InvalidOperationException();
    }
}
