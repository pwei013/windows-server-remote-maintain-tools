using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IISManager.Console;
using IISManager.Contracts;
using IISManager.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using NLog;
using NLog.Web;

// Connection/certificate settings are startup policy, never partially hot-reloaded.
var builder = WebApplication.CreateBuilder([.. args, "--hostBuilder:reloadConfigOnChange=false"]);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables().AddCommandLine(args);
builder.Host.UseWindowsService(options => options.ServiceName = "IISManager.Console");
builder.Logging.ClearProviders();
builder.Host.UseNLog();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
var settings = builder.Configuration.GetSection("Console").Get<ConsoleOptions>() ?? new ConsoleOptions();
var administrationStore = AdministrationBootstrap.Load(settings, builder.Environment.ContentRootPath, builder.Environment.IsDevelopment());
settings.Validate(builder.Environment.IsDevelopment());
var useHttp = !settings.UsesHttpsListener();
var consoleCertificate = useHttp ? null :
    MachineCertificates.Load(settings.ServerCertificateThumbprint, "1.3.6.1.5.5.7.3.1");
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 64 * 1024;
    options.Listen(IPAddress.Parse(settings.ListenAddress), settings.Port, listen =>
    {
        listen.Protocols = HttpProtocols.Http1AndHttp2;
        if (!useHttp)
            listen.UseHttps(consoleCertificate!);
    });
});
var taskDirectory = Path.IsPathRooted(settings.TaskDirectory)
    ? settings.TaskDirectory : Path.Combine(builder.Environment.ContentRootPath, settings.TaskDirectory);
builder.Services.AddSingleton(settings);
AdministrationBootstrap.Register(builder.Services, administrationStore);
builder.Services.AddSingleton(_ => new ConsoleTaskStore(taskDirectory));
builder.Services.AddSingleton<AgentClient>();
builder.Services.AddSingleton<IMonitoringAgentClient, MonitoringAgentClient>();
builder.Services.AddSingleton<OverviewService>();
builder.Services.AddSingleton<OverviewRefreshService>();
builder.Services.AddSingleton(provider => new ConfigurationBaselineService(provider.GetRequiredService<IMonitoringAgentClient>(),
    Path.Combine(Path.IsPathRooted(settings.Monitoring.DataDirectory) ? settings.Monitoring.DataDirectory :
        Path.Combine(builder.Environment.ContentRootPath, settings.Monitoring.DataDirectory), "configuration-baselines")));
builder.Services.AddSingleton(provider => new HealthMonitoringService(settings,
    provider.GetRequiredService<IMonitoringAgentClient>(), Path.IsPathRooted(settings.Monitoring.DataDirectory)
        ? settings.Monitoring.DataDirectory : Path.Combine(builder.Environment.ContentRootPath, settings.Monitoring.DataDirectory)));
builder.Services.AddHostedService<HealthMonitoringWorker>();
builder.Services.AddSingleton<OperationHealthFollowUpService>();
builder.Services.AddHostedService<OperationHealthFollowUpWorker>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(provider => new AppUserAuthenticationService(provider.GetRequiredService<ConsoleOptions>()));
builder.Services.AddSingleton<IAppUserTokenAccessor, AppUserTokenAccessor>();
builder.Services.AddSingleton<IOperationHistoryRepository>(provider => new ZeroCodeOperationHistoryRepository(settings,
    provider.GetRequiredService<AppUserAuthenticationService>().IsConfigured));
builder.Services.AddSingleton<OperationHistorySynchronizer>();
builder.Services.AddHostedService<OperationHistoryWorker>();
builder.Services.AddSingleton<ISiteAnnotationRepository>(provider => new ZeroCodeSiteAnnotationRepository(
    settings.SiteAnnotations, authentication: provider.GetRequiredService<IAppUserTokenAccessor>()));
builder.Services.AddSingleton(provider =>
{
    var client = provider.GetRequiredService<AgentClient>();
    return new SiteAnnotationService(provider.GetRequiredService<ISiteAnnotationRepository>(), client.Exists,
        (id, cancellation) => client.GetAsync<IisSnapshot>(id, "snapshot", cancellation),
        (id, cancellation) => client.GetAsync<SiteTopologySnapshot>(id, "site-topology", cancellation));
});
var app = builder.Build();
app.Lifetime.ApplicationStopped.Register(() => consoleCertificate?.Dispose());
var events = LogManager.GetLogger("IISManager.Events");
// Create the outbound clients at startup so certificate/configuration problems fail safely.
_ = app.Services.GetRequiredService<AgentClient>();
_ = app.Services.GetRequiredService<ConsoleTaskStore>();
_ = app.Services.GetRequiredService<ISiteAnnotationRepository>();
_ = app.Services.GetRequiredService<AppUserAuthenticationService>();
_ = app.Services.GetRequiredService<IOperationHistoryRepository>();
var privateSources = settings.SourceAccessMode == IntranetTransportSecurity.PrivateNetwork;
ClientNetwork[] networks = privateSources ? [] : settings.AllowedClientNetworks.Select(value =>
{
    ClientNetwork.TryParse(value, out var network);
    return network!;
}).ToArray();
var restrictedNetworks = !privateSources && settings.TransportMode == IntranetTransportSecurity.HttpToken
    ? IntranetTransportSecurity.ParseRestrictedNetworks(settings.AllowedClientNetworks) : null;

app.Use(async (context, next) =>
{
    var host = context.Request.Host.Host.Trim('[', ']');
    var hostPort = context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80);
    if (hostPort != settings.Port || !settings.AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new ApiError("HOST_NOT_ALLOWED", "当前 Host 不在控制台允许的访问地址中。"));
        return;
    }
    var remote = context.Connection.RemoteIpAddress;
    if (remote is null || !(privateSources ? IntranetTransportSecurity.IsPrivateSource(remote) : restrictedNetworks is not null
            ? restrictedNetworks.Any(network => network.Contains(remote))
            : networks.Any(network => network.Contains(remote))))
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new ApiError("CLIENT_NETWORK_DENIED", "当前来源 IP 未获控制台访问授权。"));
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    if (context.Request.IsHttps)
        context.Response.Headers["Strict-Transport-Security"] = settings.UserAuthentication.RequireHttps
            ? "max-age=31536000" : "max-age=0";
    if (HttpMethods.IsPost(context.Request.Method) && !BrowserProtection.IsValid(context))
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new ApiError("SAME_ORIGIN_REQUIRED", "请求来源或 CSRF 校验失败，请刷新控制台。"));
        return;
    }
    try
    {
        if (await AppUserAuthenticationEndpoints.CheckRequestAsync(context,
            context.RequestServices.GetRequiredService<AppUserAuthenticationService>())) return;
        await context.RequestServices.GetRequiredService<ConsoleAccessService>().AuthorizeRequestAsync(context,
            context.RequestServices.GetRequiredService<AppUserAuthenticationService>().CurrentUser(context),
            context.RequestServices.GetRequiredService<HealthMonitoringService>());
        await next();
    }
    catch (AppUserAuthenticationException exception)
    {
        await AppUserAuthenticationEndpoints.WriteErrorAsync(context,
            context.RequestServices.GetRequiredService<AppUserAuthenticationService>(), exception);
    }
    catch (AdministrationException exception)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = exception.StatusCode;
            await context.Response.WriteAsJsonAsync(new ApiError(exception.Code, exception.SafeMessage));
        }
    }
    catch (HealthCheckException exception)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = exception.StatusCode;
            await context.Response.WriteAsJsonAsync(new ApiError(exception.Code, exception.SafeMessage));
        }
    }
    catch (SiteAnnotationException exception)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = exception.StatusCode;
            await context.Response.WriteAsJsonAsync(new ApiError(exception.Code, exception.SafeMessage));
        }
    }
    catch (AgentCallException exception)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = exception.StatusCode;
            await context.Response.WriteAsJsonAsync(new ApiError(exception.Code, "无法完成 Agent 请求，请核对服务器连接及通信认证配置。"));
        }
    }
    catch (Microsoft.AspNetCore.Http.BadHttpRequestException)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new ApiError("REQUEST_INVALID", "请求格式不合法。"));
        }
    }
    catch (Exception)
    {
        events.Error("event=console_error code=INTERNAL_ERROR");
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new ApiError("INTERNAL_ERROR", "控制台处理失败；操作结果请使用原操作编号核对。"));
        }
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
AppUserAuthenticationEndpoints.MapEndpoints(app);
MonitoringEndpoints.MapEndpoints(app);
ConfigurationBaselineEndpoints.MapEndpoints(app);
AdministrationEndpoints.MapEndpoints(app);
app.MapGet("/api/v1/servers", (HttpContext context, ConsoleAccessService access, AppUserAuthenticationService authentication) =>
    settings.Servers.Where(server => access.CanRead(authentication.CurrentUser(context), server.Id)).Select(server =>
        new ServerSummary(server.Id, server.Name, server.Simulation ? "Simulation" : "Windows", new Uri(server.AgentUrl).IdnHost)));
app.MapGet("/api/v1/operations/export", async (HttpContext context, ConsoleTaskStore store, ConsoleAccessService access,
    AppUserAuthenticationService authentication, CancellationToken cancellation) =>
{
    if (!ConsoleOperationQuery.TryParse(context.Request.Query, out var query))
        return Results.BadRequest(new ApiError("OPERATION_QUERY_INVALID", "导出条件无效，请核对服务器和时间范围。"));
    try
    {
        var scope = access.AllowedServerIds(authentication.CurrentUser(context))?.ToHashSet(StringComparer.Ordinal);
        var rows = await store.ExportAsync(query!, scope, cancellation);
        return Results.File(OperationExport.Csv(rows), "text/csv; charset=utf-8", "operations-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + ".csv");
    }
    catch (ConsoleLedgerUnavailableException) { return Results.Json(new ApiError("CONSOLE_LEDGER_UNHEALTHY", "操作账本不可用，请先只读核对。"), statusCode: 503); }
});
app.MapGet("/api/v1/operations", async (HttpContext context, ConsoleTaskStore store, ConsoleAccessService access,
    AppUserAuthenticationService authentication, CancellationToken cancellation) =>
{
    if (!ConsoleOperationQuery.TryParse(context.Request.Query, out var query))
        return Results.BadRequest(new ApiError("OPERATION_QUERY_INVALID", "查询条件不合法；请核对服务器、类型、状态、时间范围与分页参数。"));
    try { return Results.Ok(await store.QueryAsync(query!, cancellation, access.AllowedServerIds(authentication.CurrentUser(context))?.ToHashSet(StringComparer.Ordinal))); }
    catch (ConsoleLedgerUnavailableException)
    {
        return Results.Json(new ApiError("CONSOLE_LEDGER_UNHEALTHY", "操作账本损坏或读取失败；请先只读核对与修复账本。"), statusCode: 503);
    }
});
app.MapGet("/api/v1/servers/{id}/info", async (string id, AgentClient client, CancellationToken cancellation) =>
    Results.Ok(await client.GetAsync<AgentInfo>(id, "info", cancellation)));
app.MapGet("/api/v1/servers/{id}/snapshot", async (string id, AgentClient client, CancellationToken cancellation) =>
    Results.Ok(await client.GetAsync<IisSnapshot>(id, "snapshot", cancellation)));
app.MapGet("/api/v1/servers/{id}/diagnostics/freb", async (string id, AgentClient client, CancellationToken cancellation) =>
{
    try { return Results.Ok(await client.GetAsync<FrebDiagnosticsSnapshot>(id, "diagnostics/freb", cancellation)); }
    catch (AgentCallException exception) when (exception.Code == "AGENT_NOT_FOUND" && exception.StatusCode == 404)
    {
        return Results.Json(new ApiError("FREB_UNSUPPORTED", "当前 Agent 不支持请求级失败跟踪，请更新 Agent。"), statusCode: 501);
    }
});
app.MapGet("/api/v1/servers/{id}/diagnostics", async (string id, AgentClient client, CancellationToken cancellation) =>
{
    try { return Results.Ok(await client.GetAsync<DiagnosticsSnapshot>(id, "diagnostics", cancellation)); }
    catch (AgentCallException exception) when (exception.Code == "AGENT_NOT_FOUND" && exception.StatusCode == 404)
    { return Results.Json(new ApiError("DIAGNOSTICS_UNSUPPORTED", "当前 Agent 不支持资源与故障诊断，请统一更新 Agent。"), statusCode: 501); }
});
app.MapGet("/api/v1/servers/{id}/site-annotations", async (string id, SiteAnnotationService service, CancellationToken cancellation) =>
    Results.Ok(await service.GetAsync(id, cancellation)));
app.MapPost("/api/v1/servers/{id}/site-annotations", async (string id, SaveSiteAnnotationRequest request,
    SiteAnnotationService service, CancellationToken cancellation) =>
    Results.Ok(await service.SaveAsync(id, request, cancellation)));
app.MapGet("/api/v1/servers/{id}/site-topology", async (string id, AgentClient client, CancellationToken cancellation) =>
{
    try { return Results.Ok(await client.GetAsync<SiteTopologySnapshot>(id, "site-topology", cancellation)); }
    catch (AgentCallException exception) when (exception.Code == "AGENT_NOT_FOUND" && exception.StatusCode == 404)
    {
        return Results.Json(new ApiError("SITE_TOPOLOGY_UNSUPPORTED", "当前 Agent 不支持站点应用和虚拟目录详情，请更新 Agent。"),
            statusCode: StatusCodes.Status501NotImplemented);
    }
});
app.MapGet("/api/v1/servers/{id}/certificates", async (string id, AgentClient client, CancellationToken cancellation) =>
    Results.Ok(await client.GetAsync<CertificateInfo[]>(id, "certificates", cancellation)));
app.MapGet("/api/v1/servers/{id}/windows-services", async (string id, AgentClient client, CancellationToken cancellation) =>
{
    try { return Results.Ok(await client.GetAsync<WindowsServiceSnapshot>(id, "windows-services", cancellation)); }
    catch (AgentCallException exception) when (exception.Code == "AGENT_NOT_FOUND" && exception.StatusCode == 404)
    {
        return Results.Json(new ApiError("WINDOWS_SERVICES_UNSUPPORTED", "当前 Agent 不支持 Windows 服务管理，请更新 Agent。"), statusCode: 501);
    }
});
app.MapGet("/api/v1/servers/{id}/windows-services/{name}", async (string id, string name, AgentClient client, CancellationToken cancellation) =>
{
    try { return Results.Ok(await client.GetAsync<WindowsServiceConfig>(id, "windows-services/" + Uri.EscapeDataString(name), cancellation)); }
    catch (AgentCallException exception) when (exception.Code == "AGENT_NOT_FOUND" && exception.StatusCode == 404)
    {
        // Distinguish a missing service from an older Agent by checking its independent capability route.
        try { _ = await client.GetAsync<WindowsServiceSnapshot>(id, "windows-services", cancellation); }
        catch (AgentCallException capability) when (capability.Code == "AGENT_NOT_FOUND" && capability.StatusCode == 404)
        { return Results.Json(new ApiError("WINDOWS_SERVICES_UNSUPPORTED", "当前 Agent 不支持 Windows 服务管理，请更新 Agent。"), statusCode: 501); }
        return Results.NotFound(new ApiError("SERVICE_NOT_FOUND", "服务不存在或当前身份无法查询。"));
    }
});
app.MapGet("/api/v1/servers/{id}/application-pools/{name}", async (string id, string name, AgentClient client, CancellationToken cancellation) =>
    Results.Ok(await client.GetAsync<PoolConfig>(id, "application-pools/" + Uri.EscapeDataString(name), cancellation)));
app.MapPost("/api/v1/servers/{id}/operations/validate", async (string id, OperationRequest request, HttpContext context,
    AgentClient client, CancellationToken cancellation) =>
{
    if (!RequestChecks.IsValid(request, submit: false)) return Results.BadRequest(new ApiError("REQUEST_INVALID", "操作类型、编号、参数或有效期不合法。"));
    if (OperationTypes.WindowsServices.Contains(request.Type, StringComparer.Ordinal))
    {
        var capabilityError = await ServiceCapability.CheckAsync(id, client, cancellation);
        if (capabilityError is not null) return capabilityError;
    }
    var validation = await client.PostAsync<ValidationResult>(id, "operations/validate", request,
        context.Connection.RemoteIpAddress!.ToString(), cancellation);
    events.Info("event=validation server={0} operation={1} type={2} target={3} valid={4}",
        id, request.OperationId, request.Type, EventFields.Target(request), validation.Valid);
    return Results.Ok(validation);
});
app.MapPost("/api/v1/servers/{id}/operations", async (string id, OperationRequest request, HttpContext context,
    AgentClient client, ConsoleTaskStore store, AppUserAuthenticationService authentication,
    OperationHistorySynchronizer history, OperationHealthFollowUpService followUp) =>
{
    if (!client.Exists(id)) return Results.NotFound(new ApiError("SERVER_NOT_FOUND", "服务器不存在。"));
    if (request.OperationId == Guid.Empty || request.Target is null || request.Parameters.ValueKind != JsonValueKind.Object)
        return Results.BadRequest(new ApiError("REQUEST_INVALID", "操作编号或参数不合法。"));
    if (OperationTypes.WindowsServices.Contains(request.Type, StringComparer.Ordinal) && !WindowsServiceRequestSafety.IsValid(request))
        return Results.BadRequest(new ApiError("SERVICE_REQUEST_INVALID", "服务目标或参数无效；启动参数不得包含密码、令牌或连接凭据。"));
    var gate = store.Lock(request.OperationId);
    await gate.WaitAsync();
    try
    {
        var hash = ConsoleTaskStore.RequestHash(id, request);
        var existing = await store.GetAsync(request.OperationId);
        if (existing is not null)
        {
            if (existing.ServerId != id || existing.RequestHash != hash)
                return Results.Conflict(new ApiError("OPERATION_ID_CONFLICT", "此操作编号已用于不同服务器或参数。"));
            followUp.Observe(existing);
            return Results.Accepted($"/api/v1/servers/{id}/operations/{request.OperationId:D}", existing);
        }
        if (!store.Healthy) return Results.Json(new ApiError("CONSOLE_LEDGER_UNHEALTHY",
            "操作账本损坏或读写验证失败，已拒绝新的更改；请先只读核对与修复账本。"), statusCode: 503);
        if (!RequestChecks.IsValid(request, submit: true)) return Results.BadRequest(new ApiError("REQUEST_INVALID", "提交需要有效编号、参数、指纹与未过期的预检查。"));
        if (OperationTypes.WindowsServices.Contains(request.Type, StringComparer.Ordinal))
        {
            var capabilityError = await ServiceCapability.CheckAsync(id, client, context.RequestAborted);
            if (capabilityError is not null) return capabilityError;
        }
        var user = authentication.CurrentUser(context);
        if (authentication.Enabled && user is null)
            throw new AppUserAuthenticationException("APP_USER_SESSION_EXPIRED", 401);
        var task = ConsoleTaskRecord.CreateIntent(id, request, context.Connection.RemoteIpAddress!.ToString(), user, history.Enabled);
        // The durable intent precedes the only outbound mutation attempt. A restart never replays this task.
        await store.SaveAsync(task);
        events.Info("event=submit_intent server={0} operation={1} type={2} target={3} source={4} state=unknown",
            id, request.OperationId, request.Type, EventFields.Target(request), task.SourceIp);
        try
        {
            var result = await client.PostAsync<OperationRecord>(id, "operations", request, task.SourceIp!, CancellationToken.None);
            task = TaskStatus.WithAgent(task, result);
        }
        catch (AgentCallException exception)
        {
            task = task with { UpdatedAt = DateTimeOffset.UtcNow, Error = new ApiError(exception.Code, "提交结果待核对；控制台不会自动重放。") };
            // A timeout/rejected response does not prove that the Agent made no changes. Query the original ID once.
            task = await TaskStatus.QueryAsync(task, client);
        }
        await store.SaveAsync(task);
        followUp.Observe(task);
        if (history.Enabled && authentication.BackgroundAccessToken(task.OperatorUserId!) is { } token)
            task = await history.SyncAsync(task, token, CancellationToken.None);
        events.Info("event=submit_result server={0} operation={1} type={2} target={3} state={4}",
            id, request.OperationId, request.Type, EventFields.Target(request), task.State);
        return Results.Accepted($"/api/v1/servers/{id}/operations/{request.OperationId:D}", task);
    }
    finally { gate.Release(); }
});
app.MapGet("/api/v1/servers/{id}/operations/{operationId:guid}", async (string id, Guid operationId,
    AgentClient client, ConsoleTaskStore store, AppUserAuthenticationService authentication,
    OperationHistorySynchronizer history, OperationHealthFollowUpService followUp) =>
{
    var gate = store.Lock(operationId);
    await gate.WaitAsync();
    try
    {
        var task = await store.GetAsync(operationId);
        if (task is null || task.ServerId != id) return Results.NotFound(new ApiError("CONSOLE_TASK_NOT_FOUND", "控制台没有此操作意向记录；此结果不证明 Agent 未执行。"));
        if (task.State is OperationStates.Queued or OperationStates.Running or OperationStates.Unknown)
        {
            task = await TaskStatus.QueryAsync(task, client);
            if (store.Healthy) await store.SaveAsync(task);
            events.Info("event=query_result server={0} operation={1} type={2} target={3} state={4}",
                id, operationId, task.Request.Type, EventFields.Target(task.Request), task.State);
        }
        if (store.Healthy && task.OperatorUserId is { } operatorId && authentication.BackgroundAccessToken(operatorId) is { } token)
            task = await history.SyncAsync(task, token, CancellationToken.None);
        followUp.Observe(task);
        return Results.Ok(task);
    }
    finally { gate.Release(); }
});
app.Run();

static class EventFields
{
    public static string Target(OperationRequest request) => new((request.Target.ServiceName ?? (request.Target.SiteName is not null ? request.Target.SiteName + request.Target.ApplicationPath + request.Target.VirtualDirectoryPath : request.Target.ApplicationPoolName) ?? "")
        .Take(128).Select(character => char.IsControl(character) ? '_' : character).ToArray());
}

public static class BrowserProtection
{
    public const string CookieName = "IISManager.Csrf";
    public static bool IsValid(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.GetLeftPart(UriPartial.Authority) !=
            $"{context.Request.Scheme}://{context.Request.Host}") return false;
        var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Length != 0 && fetchSite != "same-origin") return false;
        var cookie = context.Request.Cookies[CookieName];
        var header = context.Request.Headers["X-IISManager-CSRF"].ToString();
        return cookie?.Length == 64 && header.Length == 64 &&
               CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(cookie), Encoding.ASCII.GetBytes(header));
    }
}

static class RequestChecks
{
    public static bool IsValid(OperationRequest request, bool submit) => request.OperationId != Guid.Empty && request.Target is not null &&
        OperationTypes.All.Contains(request.Type, StringComparer.Ordinal) && request.Parameters.ValueKind == JsonValueKind.Object &&
        request.ExpiresAt > DateTimeOffset.UtcNow && request.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(15) &&
        HasKnownParameters(request) && (!submit || request.ExpectedFingerprint?.Length == 64 && request.ExpectedFingerprint.All(Uri.IsHexDigit));

    private static bool HasKnownParameters(OperationRequest request)
    {
        if (OperationTypes.WindowsServices.Contains(request.Type, StringComparer.Ordinal))
            return WindowsServiceRequestSafety.IsValid(request);
        if (OperationTypes.IsSiteMaintenance(request.Type)) return SiteMaintenanceRequestSafety.IsValid(request);
        if (request.Target.ApplicationPath is not null || request.Target.VirtualDirectoryPath is not null) return false;
        if (request.Target.ServiceName is not null) return false;
        if (request.Type.StartsWith("applicationPool.", StringComparison.Ordinal) &&
            (string.IsNullOrWhiteSpace(request.Target.ApplicationPoolName) || request.Target.SiteName is not null)) return false;
        if (request.Type == OperationTypes.CreateSite &&
            (string.IsNullOrWhiteSpace(request.Target.SiteName) || request.Target.ApplicationPoolName is not null)) return false;
        try
        {
            if (OperationTypes.IsSiteLifecycle(request.Type))
                return !string.IsNullOrWhiteSpace(request.Target.SiteName) && request.Target.ApplicationPoolName is null &&
                    !request.Parameters.EnumerateObject().Any();
            if (request.Type is OperationTypes.Start or OperationTypes.Stop or OperationTypes.Recycle)
                return !request.Parameters.EnumerateObject().Any();
            if (request.Type is OperationTypes.CreatePool or OperationTypes.UpdatePool)
                return request.Parameters.Deserialize<PoolParameters>(Protocol.Json) is { } parameters &&
                    (parameters.ManagedRuntimeVersion is null or "" or "v2.0" or "v4.0") &&
                    (parameters.ManagedPipelineMode is null or "Integrated" or "Classic") &&
                    (parameters.StartMode is null or "OnDemand" or "AlwaysRunning") &&
                    (parameters.IdleTimeoutAction is null or "Terminate" or "Suspend");
            if (request.Type == OperationTypes.CreateSite)
                return request.Parameters.Deserialize<SiteParameters>(Protocol.Json) is { Bindings: not null } site &&
                    site.Bindings.All(binding => binding is not null && binding.Protocol is "http" or "https");
            return false;
        }
        catch (JsonException) { return false; }
    }
}

static class ServiceCapability
{
    public static async Task<IResult?> CheckAsync(string id, AgentClient client, CancellationToken cancellation)
    {
        try { _ = await client.GetAsync<WindowsServiceSnapshot>(id, "windows-services", cancellation); return null; }
        catch (AgentCallException exception) when (exception.Code == "AGENT_NOT_FOUND" && exception.StatusCode == 404)
        { return Results.Json(new ApiError("WINDOWS_SERVICES_UNSUPPORTED", "当前 Agent 不支持 Windows 服务管理，请更新 Agent。"), statusCode: 501); }
    }
}

static class TaskStatus
{
    public static ConsoleTaskRecord WithAgent(ConsoleTaskRecord task, OperationRecord record)
    {
        if (ConsoleTaskStore.RequestHash(task.ServerId, record.Request) != task.RequestHash ||
            record.State is not (OperationStates.Queued or OperationStates.Running or OperationStates.Succeeded or
                OperationStates.Failed or OperationStates.Partial or OperationStates.Unknown or OperationStates.Expired))
            throw new AgentCallException("AGENT_RESPONSE_MISMATCH");
        if (task.State == record.State && task.Error == record.Error && task.AgentRecord is not null &&
            JsonSerializer.Serialize(task.AgentRecord, Protocol.Json) == JsonSerializer.Serialize(record, Protocol.Json)) return task;
        return task with { State = record.State, UpdatedAt = record.CompletedAt ?? record.StartedAt ?? DateTimeOffset.UtcNow,
            AgentRecord = record, Error = record.Error,
            PersistenceState = task.PersistenceState is null or "notConfigured" ? task.PersistenceState : "pending" };
    }

    public static async Task<ConsoleTaskRecord> QueryAsync(ConsoleTaskRecord task, AgentClient client)
    {
        try
        {
            var record = await client.GetAsync<OperationRecord>(task.ServerId, "operations/" + task.Request.OperationId.ToString("D"), CancellationToken.None);
            return WithAgent(task, record);
        }
        catch (AgentCallException exception)
        {
            var error = new ApiError(exception.Code == "AGENT_NOT_FOUND" ? "AGENT_OPERATION_NOT_FOUND_UNKNOWN" : exception.Code,
                "暂不能确认操作结果；保留原编号与参数，请只读核对，不要重新提交。");
            if (task.State == OperationStates.Unknown && task.Error == error) return task;
            return task with
            {
                State = OperationStates.Unknown, UpdatedAt = DateTimeOffset.UtcNow,
                PersistenceState = task.PersistenceState is null or "notConfigured" ? task.PersistenceState : "pending",
                Error = error
            };
        }
    }
}
