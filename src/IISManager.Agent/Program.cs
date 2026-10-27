using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IISManager.Agent;
using IISManager.Agent.Backends;
using IISManager.Contracts;
using IISManager.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using NLog.Web;

// Server identities and execution policy are loaded once and require a controlled restart.
var builder = WebApplication.CreateBuilder([.. args, "--hostBuilder:reloadConfigOnChange=false"]);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);
builder.Logging.ClearProviders();
builder.Host.UseNLog();
builder.Services.AddWindowsService(options => options.ServiceName = "IISManager.Agent");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
});
var agentOptions = builder.Configuration.GetSection("Agent").Get<AgentOptions>() ?? new();
if (agentOptions.WindowsServices is null || agentOptions.WindowsServices.OperationTimeoutSeconds is < 5 or > 300)
    throw new InvalidOperationException("WINDOWS_SERVICE_POLICY_INVALID");
var simulation = agentOptions.Mode == "Simulation";
if (!IntranetTransportSecurity.IsTransportModeValid(agentOptions.TransportMode))
    throw new InvalidOperationException("AGENT_TRANSPORT_MODE_INVALID");
var httpToken = agentOptions.TransportMode == IntranetTransportSecurity.HttpToken;
if (!IntranetTransportSecurity.IsSourceAccessModeValid(agentOptions.SourceAccessMode))
    throw new InvalidOperationException("AGENT_SOURCE_ACCESS_MODE_INVALID");
var privateSources = agentOptions.SourceAccessMode == IntranetTransportSecurity.PrivateNetwork;
if (!simulation && agentOptions.Mode != "Windows") throw new InvalidOperationException("AGENT_MODE_INVALID");
if (simulation && !builder.Environment.IsDevelopment()) throw new InvalidOperationException("SIMULATION_REQUIRES_DEVELOPMENT");
if (!simulation && !OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WINDOWS_AGENT_REQUIRES_WINDOWS");
if (agentOptions.Port is < 1024 or > 65535) throw new InvalidOperationException("AGENT_PORT_INVALID");
agentOptions.ReservedPorts = agentOptions.ReservedPorts.Append(agentOptions.Port).Distinct().ToArray();
X509Certificate2? serverCertificate = null;
IntranetTransportSecurity.Network[] consoleNetworks = [];
if (httpToken)
{
    if (!IntranetTransportSecurity.IsValidAccessToken(agentOptions.AccessToken))
        throw new InvalidOperationException("AGENT_HTTP_TOKEN_REQUIRED");
    if (!privateSources) consoleNetworks = IntranetTransportSecurity.ParseRestrictedNetworks(agentOptions.AllowedConsoleNetworks);
}
if (!simulation && !httpToken)
{
    if (agentOptions.AllowedClientThumbprints.Length == 0) throw new InvalidOperationException("CLIENT_CERTIFICATE_ALLOWLIST_REQUIRED");
    serverCertificate = CertificateSecurity.LoadServerCertificate(agentOptions.ServerCertificateThumbprint);
}
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = 65536;
    if (simulation) kestrel.Listen(IPAddress.Loopback, agentOptions.Port);
    else if (httpToken) kestrel.ListenAnyIP(agentOptions.Port);
    else kestrel.ListenAnyIP(agentOptions.Port, listen => listen.UseHttps(https =>
    {
        https.ServerCertificate = serverCertificate;
        https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
        https.CheckCertificateRevocation = true;
        https.ClientCertificateValidation = (certificate, chain, errors) =>
            errors == System.Net.Security.SslPolicyErrors.None &&
            CertificateSecurity.IsAllowedClient(certificate, agentOptions.AllowedClientThumbprints);
    }));
});
builder.Services.AddSingleton(agentOptions);
builder.Services.AddSingleton<IIisBackend>(_ =>
{
    if (simulation) return new SimulationIisBackend(agentOptions);
    if (OperatingSystem.IsWindows()) return new WindowsIisBackend(agentOptions);
    throw new PlatformNotSupportedException("WINDOWS_AGENT_REQUIRES_WINDOWS");
});
builder.Services.AddSingleton<IIisTopologyBackend>(provider =>
    (IIisTopologyBackend)provider.GetRequiredService<IIisBackend>());
builder.Services.AddSingleton<DiagnosticsReadService>();
builder.Services.AddSingleton<FrebDiagnosticsReadService>();
builder.Services.AddSingleton<HealthProbeService>();
builder.Services.AddSingleton<IWindowsServiceBackend>(_ =>
{
    if (simulation) return new SimulationWindowsServiceBackend(agentOptions);
    if (OperatingSystem.IsWindows()) return new WindowsServiceBackend(agentOptions);
    throw new PlatformNotSupportedException("WINDOWS_AGENT_REQUIRES_WINDOWS");
});
builder.Services.AddSingleton<IOperationBackend, OperationBackendRouter>();
builder.Services.AddSingleton(_ => new FileOperationStore(Path.IsPathRooted(agentOptions.StateDirectory)
    ? agentOptions.StateDirectory : Path.Combine(builder.Environment.ContentRootPath, agentOptions.StateDirectory)));
builder.Services.AddSingleton<OperationCoordinator>();
builder.Services.AddHostedService<OperationWorker>();
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    try
    {
        if (simulation)
        {
            if (context.Connection.RemoteIpAddress is null || !IPAddress.IsLoopback(context.Connection.RemoteIpAddress))
            { context.Response.StatusCode = 403; return; }
        }
        // Source authorization always uses the TCP peer, for both HTTP and HTTPS.
        if (privateSources && !IntranetTransportSecurity.IsPrivateSource(context.Connection.RemoteIpAddress))
        { context.Response.StatusCode = 403; return; }
        if (httpToken)
        {
            // Check the actual TCP peer before considering credentials. Never trust forwarded headers.
            if (!privateSources && !consoleNetworks.Any(network => network.Contains(context.Connection.RemoteIpAddress)))
            { context.Response.StatusCode = 403; return; }
            var token = context.Request.Headers[IntranetTransportSecurity.TokenHeader];
            if (!IntranetTransportSecurity.IsValidTokenHeader(token, agentOptions.AccessToken))
            { context.Response.StatusCode = 403; return; }
        }
        else if (!simulation)
        {
            var certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted);
            if (!context.Request.IsHttps || certificate is null ||
                !CertificateSecurity.IsAllowedClient(certificate, agentOptions.AllowedClientThumbprints))
            { context.Response.StatusCode = 403; return; }
        }
        await next(context);
    }
    catch (OperationException exception)
    {
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = exception.StatusCode;
        await context.Response.WriteAsJsonAsync(new ApiError(exception.Code, exception.Message));
    }
    catch (Exception exception)
    {
        if (context.Response.HasStarted) throw;
        app.Logger.LogError("api.failed exceptionType={ExceptionType} hresult={HResult}", exception.GetType().Name, exception.HResult);
        context.Response.StatusCode = exception is BadHttpRequestException or System.Text.Json.JsonException ? 400 : 503;
        await context.Response.WriteAsJsonAsync(new ApiError(context.Response.StatusCode == 400 ? "REQUEST_INVALID" : "AGENT_UNAVAILABLE",
            context.Response.StatusCode == 400 ? "请求格式无效。" : "Agent 暂时无法确认结果，请查询原操作编号并核实现场。"));
    }
});
var api = app.MapGroup("/agent/v1");
api.MapGet("/info", (IIisBackend backend, CancellationToken ct) => backend.GetInfoAsync(ct));
api.MapGet("/diagnostics", (DiagnosticsReadService service, CancellationToken ct) => service.GetAsync(ct));
api.MapGet("/diagnostics/freb", (FrebDiagnosticsReadService service, CancellationToken ct) => service.GetAsync(ct));
api.MapGet("/snapshot", (IIisBackend backend, CancellationToken ct) => backend.GetSnapshotAsync(ct));
api.MapGet("/site-topology", (IIisTopologyBackend backend, CancellationToken ct) => backend.GetSiteTopologyAsync(ct));
api.MapPost("/health-check/validate", async (HealthProbeRequest request, HealthProbeService service, CancellationToken ct) =>
{
    await service.ValidateAsync(request, ct);
    return Results.Ok(new { valid = true });
});
api.MapPost("/health-check", (HealthProbeRequest request, HealthProbeService service, CancellationToken ct) => service.ProbeAsync(request, ct));
api.MapGet("/windows-services", (IWindowsServiceBackend backend, CancellationToken ct) => backend.GetSnapshotAsync(ct));
api.MapGet("/windows-services/{name}", async (string name, IWindowsServiceBackend backend, CancellationToken ct) =>
    await backend.GetServiceAsync(name, ct) is { } service ? Results.Ok(service)
        : Results.NotFound(new ApiError("SERVICE_NOT_FOUND", "服务不存在或当前身份无法查询。")));
api.MapGet("/certificates", (IIisBackend backend, CancellationToken ct) => backend.GetCertificatesAsync(ct));
api.MapGet("/application-pools/{name}", async (string name, IIisBackend backend, CancellationToken ct) =>
    await backend.GetPoolAsync(name, ct) is { } pool ? Results.Ok(pool) : Results.NotFound(new ApiError("POOL_NOT_FOUND", "应用池不存在。")));
api.MapPost("/operations/validate", (OperationRequest request, OperationCoordinator coordinator, CancellationToken ct) => coordinator.ValidateAsync(request, ct));
api.MapPost("/operations", async (OperationRequest request, OperationCoordinator coordinator, HttpContext context) =>
{
    // Only an authenticated Console may supply this header. Never consume browser forwarded headers.
    var source = context.Request.Headers["X-IISManager-Source-IP"].ToString();
    var sourceIp = IPAddress.TryParse(source, out var parsed) ? parsed.ToString() : context.Connection.RemoteIpAddress?.ToString();
    var record = await coordinator.SubmitAsync(request, sourceIp, context.RequestAborted);
    return Results.Accepted($"/agent/v1/operations/{request.OperationId:D}", record);
});
api.MapGet("/operations/{id:guid}", async (Guid id, OperationCoordinator coordinator, CancellationToken ct) =>
    await coordinator.GetAsync(id, ct) is { } record ? Results.Ok(record) : Results.NotFound(new ApiError("OPERATION_NOT_FOUND", "未找到任务记录；不能据此认定操作未发生。")));
app.Lifetime.ApplicationStopped.Register(() => serverCertificate?.Dispose());
app.Run();
