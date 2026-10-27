using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IISManager.Contracts;

namespace IISManager.Console;

public sealed class AppUserAuthenticationOptions
{
    public bool Enabled { get; set; } = true;
    public bool RequireHttps { get; set; }
    public int ValidationIntervalSeconds { get; set; } = 60;

    public void Validate()
    {
        if (ValidationIntervalSeconds is < 60 or > 300)
            throw new InvalidOperationException("APP_USER_AUTH_CONFIGURATION_INVALID");
    }
}

public sealed record AppUserIdentity(int AppId, string UserId, string Account, string DisplayName);
public sealed record AppUserLoginRequest
{
    [JsonRequired] public string Account { get; init; } = "";
    [JsonRequired] public string Password { get; init; } = "";
}

public sealed class AppUserChangePasswordRequest
{
    [JsonRequired] public string OldPassword { get; init; } = "";
    [JsonRequired] public string NewPassword { get; init; } = "";
    [JsonRequired] public string ConfirmPassword { get; init; } = "";
}

public sealed class AppUserAuthenticationException(string code, int statusCode = 503) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public string SafeMessage => Code switch
    {
        "APP_USER_LOGIN_INVALID" => "请填写有效的账号和密码。",
        "APP_USER_LOGIN_REQUIRES_HTTPS" => "登录需要通过 HTTPS 访问本控制台，请联系管理员配置访问地址。",
        "APP_USER_LOGIN_REJECTED" => "账号或密码不正确，或账号当前不可用。",
        "APP_USER_PASSWORD_INVALID" => "请填写原密码；新密码须为12至128个字符，并与确认密码一致。",
        "APP_USER_PASSWORD_REJECTED" => "原密码不正确，请重新填写。",
        "APP_USER_PASSWORD_OUTCOME_UNKNOWN" => "无法确认密码修改结果，已退出本控制台；请重新登录核对，勿重复提交修改。",
        "APP_USER_SESSION_REQUIRED" => "请先登录管理工作台。",
        "APP_USER_SESSION_EXPIRED" => "登录已失效，请重新登录。",
        "APP_USER_AUTH_DISABLED" => "当前应用尚未启用用户认证，请联系管理员。",
        "APP_USER_AUTH_NOT_CONFIGURED" => "登录服务尚未配置，请联系管理员完成应用调用配置。",
        "APP_USER_AUTH_RATE_LIMITED" => "登录验证过于频繁，请稍后再试。",
        _ => "暂时无法完成登录验证，请稍后再试。"
    };
}

public interface IAppUserTokenAccessor
{
    bool Required { get; }
    string GetAccessToken();
    void Invalidate();
}

public sealed class AppUserTokenAccessor(IHttpContextAccessor context, AppUserAuthenticationService authentication)
    : IAppUserTokenAccessor
{
    public bool Required => authentication.Enabled;
    public string GetAccessToken() => authentication.GetAccessToken(context.HttpContext
        ?? throw new AppUserAuthenticationException("APP_USER_SESSION_REQUIRED", 401));
    public void Invalidate()
    {
        if (context.HttpContext is { } current) authentication.Invalidate(current);
    }
}

/// <summary>Opaque browser sessions; upstream tokens and passwords never enter a browser response or a ledger.</summary>
public sealed class AppUserAuthenticationService : IDisposable
{
    public const string CookieName = "IISManager.AppUser";
    private const int MaximumSessions = 2048;
    private readonly ConcurrentDictionary<string, UserSession> _sessions = new(StringComparer.Ordinal);
    private readonly object _sessionGate = new();
    private readonly AppUserAuthenticationOptions _options;
    private readonly SiteAnnotationZeroCodeOptions _connection;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly string _publicKey = "";
    public bool Enabled => _options.Enabled;
    public bool HttpsRequired { get; }
    public bool IsConfigured { get; }

    // Deliberately not a record: generated ToString/JSON must not expose an upstream credential.
    private sealed class UserSession(string token, AppUserIdentity user, DateTimeOffset expires)
    {
        internal readonly string AccessToken = token;
        internal AppUserIdentity User = user;
        internal readonly DateTimeOffset ExpiresAt = expires;
        internal DateTimeOffset? ValidatedAt;
        internal DateTimeOffset? RetryAfter;
        internal volatile bool Revoked;
        internal readonly SemaphoreSlim ValidationGate = new(1, 1);
    }

    public AppUserAuthenticationService(ConsoleOptions options, HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        _options = options.UserAuthentication;
        _options.Validate();
        _connection = options.SiteAnnotations;
        HttpsRequired = _options.RequireHttps;
        _time = time ?? TimeProvider.System;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
        { Timeout = TimeSpan.FromSeconds(Math.Clamp(_connection.TimeoutSeconds, 1, 120)) };
        if (!Enabled) return;
        try
        {
            // Authentication uses the existing application connection even when annotations are disabled.
            var check = new SiteAnnotationZeroCodeOptions
            {
                Enabled = true, ApplicationId = _connection.ApplicationId,
                OpenApiBaseUrl = _connection.OpenApiBaseUrl, OpenApiAppId = _connection.OpenApiAppId,
                PublicKeyBase64 = _connection.PublicKeyBase64, TimeoutSeconds = _connection.TimeoutSeconds
            };
            check.Validate();
            _publicKey = Convert.ToBase64String(Convert.FromBase64String(check.PublicKeyBase64));
            _ = ZeroCodeSiteAnnotationRepository.Encrypt("{}", _publicKey);
            IsConfigured = true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or CryptographicException)
        {
            // An incomplete installation can show a login/configuration message, never an open workbench.
            IsConfigured = false;
        }
    }

    public AppUserIdentity? CurrentUser(HttpContext context) => Find(context)?.User;
    // Background history saves reuse the original operator's live session; credentials never enter the ledger.
    public string? BackgroundAccessToken(string userId) => _sessions.Values
        .Where(session => !session.Revoked && session.ExpiresAt > _time.GetUtcNow() &&
            session.User.UserId == userId && (session.RetryAfter is null || session.RetryAfter <= _time.GetUtcNow()))
        .OrderByDescending(session => session.ExpiresAt).Select(session => session.AccessToken).FirstOrDefault();

    public void InvalidateBackgroundToken(string token)
    {
        foreach (var session in _sessions.Values.Where(session => session.AccessToken == token)) session.Revoked = true;
    }
    public DateTimeOffset? ExpiresAt(HttpContext context) => Find(context)?.ExpiresAt;
    public string GetAccessToken(HttpContext context) => Find(context)?.AccessToken
        ?? throw new AppUserAuthenticationException("APP_USER_SESSION_EXPIRED", 401);

    private UserSession? Find(HttpContext context)
    {
        var cookie = context.Request.Cookies[CookieName];
        if (cookie is null || cookie.Length != 64 || !cookie.All(Uri.IsHexDigit)) return null;
        var key = SessionKey(cookie);
        if (!_sessions.TryGetValue(key, out var session)) return null;
        if (!session.Revoked && session.ExpiresAt > _time.GetUtcNow()) return session;
        session.Revoked = true;
        _sessions.TryRemove(key, out _);
        return null;
    }

    public async Task<(AppUserIdentity User, DateTimeOffset ExpiresAt)> LoginAsync(HttpContext context,
        AppUserLoginRequest request, CancellationToken cancellation)
    {
        if (!Enabled) throw new AppUserAuthenticationException("APP_USER_AUTH_DISABLED", 403);
        if (HttpsRequired && !context.Request.IsHttps)
            throw new AppUserAuthenticationException("APP_USER_LOGIN_REQUIRES_HTTPS", 403);
        if (request.Account is null || request.Password is null || string.IsNullOrWhiteSpace(request.Account) ||
            request.Account.Length > 256 || request.Account.Any(char.IsControl) || request.Password.Length is < 1 or > 128)
            throw new AppUserAuthenticationException("APP_USER_LOGIN_INVALID", 400);
        using var reply = await CallAsync("user-auth-login", new Dictionary<string, object?>
        { ["Account"] = request.Account.Trim(), ["Password"] = request.Password }, null, cancellation);
        var root = reply.RootElement;
        var token = Text(root, "AccessToken");
        if (token.Length is < 1 or > 8192 || token.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)) ||
            !string.Equals(Text(root, "TokenType"), "Bearer", StringComparison.OrdinalIgnoreCase) ||
            !DateTimeOffset.TryParse(Text(root, "ExpiresAtUtc"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var expires) || expires <= _time.GetUtcNow()) throw InvalidResponse();
        var user = ReadUser(root);
        var cookie = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var session = new UserSession(token, user, expires.ToUniversalTime());
        lock (_sessionGate)
        {
            foreach (var pair in _sessions)
                if (pair.Value.Revoked || pair.Value.ExpiresAt <= _time.GetUtcNow()) _sessions.TryRemove(pair.Key, out _);
            if (_sessions.Count >= MaximumSessions) throw new AppUserAuthenticationException("APP_USER_AUTH_CAPACITY", 503);
            Invalidate(context);
            _sessions[SessionKey(cookie)] = session;
        }
        context.Response.Cookies.Append(CookieName, cookie, CookieOptions(context, expires));
        return (user, expires.ToUniversalTime());
    }

    public async Task ValidateAsync(HttpContext context, bool fresh, CancellationToken cancellation)
    {
        if (!Enabled) return;
        var session = Find(context) ?? throw new AppUserAuthenticationException("APP_USER_SESSION_REQUIRED", 401);
        await session.ValidationGate.WaitAsync(cancellation);
        try
        {
            if (session.Revoked || session.ExpiresAt <= _time.GetUtcNow())
                throw new AppUserAuthenticationException("APP_USER_SESSION_EXPIRED", 401);
            if (session.RetryAfter is { } retry && retry > _time.GetUtcNow())
                throw new AppUserAuthenticationException("APP_USER_AUTH_RATE_LIMITED", 429);
            if (!fresh && session.ValidatedAt is { } checkedAt &&
                _time.GetUtcNow() - checkedAt < TimeSpan.FromSeconds(_options.ValidationIntervalSeconds)) return;
            using var reply = await CallAsync("user-auth-me", [], session.AccessToken, cancellation);
            var user = ReadUser(reply.RootElement);
            if (user.UserId != session.User.UserId || user.AppId != session.User.AppId) throw InvalidResponse();
            if (session.Revoked || session.ExpiresAt <= _time.GetUtcNow())
                throw new AppUserAuthenticationException("APP_USER_SESSION_EXPIRED", 401);
            session.User = user;
            session.ValidatedAt = _time.GetUtcNow();
            session.RetryAfter = null;
        }
        catch (AppUserAuthenticationException exception) when (exception.StatusCode == 401)
        {
            Invalidate(context);
            throw;
        }
        catch (AppUserAuthenticationException exception) when (exception.StatusCode == 429)
        {
            if (session.RetryAfter is not { } cooldown || cooldown <= _time.GetUtcNow())
                session.RetryAfter = _time.GetUtcNow().AddSeconds(60);
            throw;
        }
        finally { session.ValidationGate.Release(); }
    }

    public async Task<bool> LogoutAsync(HttpContext context, CancellationToken cancellation)
    {
        var session = Find(context);
        Invalidate(context); // Local sign-out is final even if the remote reply is lost.
        if (session is null) return true;
        try
        {
            using var reply = await CallAsync("user-auth-logout", [], session.AccessToken, cancellation);
            return true;
        }
        catch (AppUserAuthenticationException exception) when (exception.StatusCode == 401) { return true; }
        catch (AppUserAuthenticationException) { return false; }
    }

    public async Task ChangePasswordAsync(HttpContext context, AppUserChangePasswordRequest request,
        CancellationToken cancellation)
    {
        if (!Enabled) throw new AppUserAuthenticationException("APP_USER_AUTH_DISABLED", 403);
        if (HttpsRequired && !context.Request.IsHttps)
            throw new AppUserAuthenticationException("APP_USER_LOGIN_REQUIRES_HTTPS", 403);
        if (request.OldPassword is not { Length: > 0 and <= 128 } ||
            request.NewPassword is not { Length: >= 12 and <= 128 } || request.ConfirmPassword != request.NewPassword)
            throw new AppUserAuthenticationException("APP_USER_PASSWORD_INVALID", 400);
        if (!IsConfigured) throw new AppUserAuthenticationException("APP_USER_AUTH_NOT_CONFIGURED", 503);
        var session = Find(context) ?? throw new AppUserAuthenticationException("APP_USER_SESSION_REQUIRED", 401);
        await session.ValidationGate.WaitAsync(cancellation);
        try
        {
            if (session.Revoked || session.ExpiresAt <= _time.GetUtcNow())
                throw new AppUserAuthenticationException("APP_USER_SESSION_EXPIRED", 401);
            try
            {
                using var reply = await CallAsync("user-auth-change-password", new Dictionary<string, object?>
                { ["OldPassword"] = request.OldPassword, ["NewPassword"] = request.NewPassword }, session.AccessToken, cancellation);
            }
            catch (AppUserAuthenticationException exception) when (exception.StatusCode == 401)
            {
                // The provider also uses 401 for an incorrect old password. A read-only check distinguishes it
                // from a revoked token without replaying the password write or exposing upstream messages.
                using var me = await CallAsync("user-auth-me", [], session.AccessToken, cancellation);
                var user = ReadUser(me.RootElement);
                if (user.UserId != session.User.UserId || user.AppId != session.User.AppId) throw InvalidResponse();
                throw new AppUserAuthenticationException("APP_USER_PASSWORD_REJECTED", 400);
            }
            InvalidateUser(context, session.User);
        }
        catch (AppUserAuthenticationException exception) when (exception.StatusCode == 401)
        {
            Invalidate(context);
            throw;
        }
        catch (AppUserAuthenticationException exception) when (exception.StatusCode >= 500)
        {
            // A missing/invalid reply or provider 503 cannot prove that the write did not commit.
            InvalidateUser(context, session.User);
            throw new AppUserAuthenticationException("APP_USER_PASSWORD_OUTCOME_UNKNOWN", 503);
        }
        catch (AppUserAuthenticationException exception) when (exception.StatusCode == 429)
        {
            session.RetryAfter = _time.GetUtcNow().AddSeconds(60);
            throw;
        }
        finally { session.ValidationGate.Release(); }
    }

    private void InvalidateUser(HttpContext context, AppUserIdentity user)
    {
        lock (_sessionGate)
            foreach (var pair in _sessions)
                if (pair.Value.User.AppId == user.AppId && pair.Value.User.UserId == user.UserId)
                {
                    pair.Value.Revoked = true;
                    _sessions.TryRemove(pair.Key, out _);
                }
        context.Response.Cookies.Delete(CookieName, CookieOptions(context));
    }

    public void Invalidate(HttpContext context)
    {
        var cookie = context.Request.Cookies[CookieName];
        if (cookie is { Length: 64 } && cookie.All(Uri.IsHexDigit) && _sessions.TryRemove(SessionKey(cookie), out var session))
            session.Revoked = true;
        context.Response.Cookies.Delete(CookieName, CookieOptions(context));
    }

    private static string SessionKey(string cookie) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(cookie)));
    private static CookieOptions CookieOptions(HttpContext context, DateTimeOffset? expires = null) => new()
    {
        HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict,
        IsEssential = true, Path = "/", Expires = expires
    };

    private AppUserIdentity ReadUser(JsonElement root)
    {
        if (!root.TryGetProperty("User", out var user) || user.ValueKind != JsonValueKind.Object ||
            !int.TryParse(Text(user, "AppID"), NumberStyles.None, CultureInfo.InvariantCulture, out var application) ||
            application != _connection.ApplicationId) throw InvalidResponse();
        var id = Text(user, "UserID"); var account = Text(user, "Account"); var display = Text(user, "DisplayName", optional: true);
        if (id.Length is < 1 or > 128 || account.Length is < 1 or > 256 || display.Length > 256 ||
            id.Any(char.IsControl) || account.Any(char.IsControl) || display.Any(char.IsControl)) throw InvalidResponse();
        return new AppUserIdentity(application, id, account, display);
    }

    private async Task<JsonDocument> CallAsync(string method, Dictionary<string, object?> parameters, string? token,
        CancellationToken cancellation)
    {
        if (!IsConfigured) throw new AppUserAuthenticationException("APP_USER_AUTH_NOT_CONFIGURED", 503);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(_connection.TimeoutSeconds));
            // Authentication V3 signs the encrypted route AppID, method, and Unix milliseconds before RSA encryption.
            var timestamp = _time.GetUtcNow().ToUnixTimeMilliseconds();
            parameters["TimeStamp"] = timestamp;
            parameters["Sign"] = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(
                _connection.OpenApiAppId + method + timestamp.ToString(CultureInfo.InvariantCulture))));
            var encrypted = ZeroCodeSiteAnnotationRepository.Encrypt(JsonSerializer.Serialize(parameters, SiteAnnotationRules.Json), _publicKey);
            var endpoint = new Uri(_connection.OpenApiBaseUrl.TrimEnd('/') + "/" + method + "/" + Uri.EscapeDataString(_connection.OpenApiAppId));
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            { Content = new StringContent(JsonSerializer.Serialize(encrypted), Encoding.UTF8, "application/json") };
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) throw Rejected(method, (int)response.StatusCode);
            if (response.Content.Headers.ContentLength > 64 * 1024) throw InvalidResponse();
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var bytes = new MemoryStream();
            var buffer = new byte[4096]; int count;
            while ((count = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (bytes.Length + count > 64 * 1024) throw InvalidResponse();
                bytes.Write(buffer, 0, count);
            }
            var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            try
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw InvalidResponse();
                var result = Text(document.RootElement, "ResultCode");
                if (result != "200") throw Rejected(method, int.TryParse(result, out var code) ? code : 503);
                return document;
            }
            catch { document.Dispose(); throw; }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or
            CryptographicException or IOException or FormatException or ArgumentException)
        {
            throw new AppUserAuthenticationException("APP_USER_AUTH_UNAVAILABLE", 503);
        }
    }

    private static string Text(JsonElement root, string field, bool optional = false)
    {
        if (root.TryGetProperty(field, out var value))
        {
            if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number) return value.ToString();
            if (optional && value.ValueKind == JsonValueKind.Null) return "";
        }
        if (optional) return "";
        throw InvalidResponse();
    }
    private static AppUserAuthenticationException InvalidResponse() => new("APP_USER_AUTH_RESPONSE_INVALID", 503);
    private static AppUserAuthenticationException Rejected(string method, int status) => status switch
    {
        400 when method == "user-auth-change-password" => new("APP_USER_PASSWORD_INVALID", 400),
        401 => new(method == "user-auth-login" ? "APP_USER_LOGIN_REJECTED" : "APP_USER_SESSION_EXPIRED", 401),
        403 => new("APP_USER_AUTH_DISABLED", 403),
        429 => new("APP_USER_AUTH_RATE_LIMITED", 429),
        _ => new("APP_USER_AUTH_UNAVAILABLE", 503)
    };
    public void Dispose() => _http.Dispose();
}

public static class AppUserAuthenticationEndpoints
{
    public static async Task WriteErrorAsync(HttpContext context, AppUserAuthenticationService authentication,
        AppUserAuthenticationException exception)
    {
        if (context.Response.HasStarted) return;
        if (exception.StatusCode == 401)
        {
            authentication.Invalidate(context);
            if (IsProtectedPage(context.Request.Path))
            {
                context.Response.Redirect("/login.html?reason=expired&returnUrl=" +
                    Uri.EscapeDataString(context.Request.Path + context.Request.QueryString));
                return;
            }
        }
        context.Response.StatusCode = exception.StatusCode;
        if (exception.StatusCode == 429) context.Response.Headers.RetryAfter = "60";
        await context.Response.WriteAsJsonAsync(new ApiError(exception.Code, exception.SafeMessage));
    }

    public static bool IsPublicApi(PathString path) => path == "/api/v1/session" || path == "/api/v1/auth/login" || path == "/api/v1/auth/logout";
    public static bool IsProtectedPage(PathString path) => path == "/" ||
        path.Value?.EndsWith(".html", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsPublicResource(PathString path)
    {
        if (IsPublicApi(path) || path == "/login.html" || path == "/auth-login.css" ||
            path == "/auth-login.js" || path == "/auth-session.js") return true;
        var value = path.Value ?? "";
        return path.StartsWithSegments("/assets") && !value.Contains('\\') && !value.Contains("//", StringComparison.Ordinal) &&
            !value.Split('/').Any(segment => segment is "." or "..");
    }

    public static async Task<bool> CheckRequestAsync(HttpContext context, AppUserAuthenticationService authentication)
    {
        if (!authentication.Enabled || IsPublicResource(context.Request.Path)) return false;
        if (authentication.CurrentUser(context) is null && IsProtectedPage(context.Request.Path))
        {
            authentication.Invalidate(context);
            context.Response.Redirect("/login.html?returnUrl=" + Uri.EscapeDataString(context.Request.Path + context.Request.QueryString));
            return true;
        }
        await authentication.ValidateAsync(context, fresh: HttpMethods.IsPost(context.Request.Method), context.RequestAborted);
        return false;
    }

    public static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/session", (HttpContext context, AppUserAuthenticationService authentication) =>
        {
            var cookie = context.Request.Cookies[BrowserProtection.CookieName];
            var csrf = cookie is { Length: 64 } && cookie.All(Uri.IsHexDigit)
                ? cookie : Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            context.Response.Cookies.Append(BrowserProtection.CookieName, csrf, new CookieOptions
            {
                HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict,
                IsEssential = true, Path = "/"
            });
            return Results.Ok(new { csrfToken = csrf, protocolVersion = Protocol.Version,
                authenticationRequired = authentication.Enabled, authenticationConfigured = authentication.IsConfigured,
                loginRequiresHttps = authentication.HttpsRequired && !context.Request.IsHttps,
                user = authentication.CurrentUser(context), expiresAtUtc = authentication.ExpiresAt(context) });
        });
        app.MapPost("/api/v1/auth/login", async (HttpContext context, AppUserLoginRequest request, AppUserAuthenticationService authentication) =>
        {
            var result = await authentication.LoginAsync(context, request, context.RequestAborted);
            return Results.Ok(new { user = result.User, expiresAtUtc = result.ExpiresAt });
        });
        app.MapGet("/api/v1/auth/me", (HttpContext context, AppUserAuthenticationService authentication) =>
            Results.Ok(new { user = authentication.CurrentUser(context), expiresAtUtc = authentication.ExpiresAt(context) }));
        app.MapPost("/api/v1/auth/logout", async (HttpContext context, AppUserAuthenticationService authentication) =>
        {
            var confirmed = await authentication.LogoutAsync(context, context.RequestAborted);
            return Results.Ok(new { signedOut = true, remoteConfirmed = confirmed });
        });
        app.MapPost("/api/v1/auth/change-password", async (HttpContext context, AppUserChangePasswordRequest request,
            AppUserAuthenticationService authentication) =>
        {
            await authentication.ChangePasswordAsync(context, request, context.RequestAborted);
            return Results.Ok(new { passwordChanged = true, signedOut = true });
        });
    }
}
