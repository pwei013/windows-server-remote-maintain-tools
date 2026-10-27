using IISManager.Contracts;

namespace IISManager.Console;

public static class MonitoringEndpoints
{
    public static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/overview/progress", async (OverviewRefreshService overview, HealthMonitoringService health,
            ConsoleAccessService access, AppUserAuthenticationService authentication, HttpContext context, CancellationToken cancellation) =>
        {
            var result = overview.GetProgress(cancellation);
            HealthCheckView[] checks = [];
            ApiError? error = null;
            try { checks = await health.GetViewsAsync(cancellation); }
            catch (HealthCheckException exception) { error = new(exception.Code, exception.SafeMessage); }
            var user = authentication.CurrentUser(context);
            var rows = result.Servers.Where(server => access.CanRead(user, server.Id)).ToArray();
            checks = checks.Where(check => access.CanRead(user, check.ServerId)).ToArray();
            return Results.Ok(new MonitoringOverview(result.CollectedAt, rows, checks) { HealthError = error,
                IsRefreshing = rows.Any(server => server.Refreshing), CompletedServers = rows.Count(server => !server.Refreshing && server.ConnectionState != "pending"), TotalServers = rows.Length });
        });
        app.MapGet("/api/v1/servers/{id}/overview/refresh", async (string id, OverviewRefreshService overview, CancellationToken cancellation) =>
            Results.Ok(await overview.RefreshServerAsync(id, cancellation)));
        app.MapGet("/api/v1/overview", async (OverviewService overview, HealthMonitoringService health,
            ConsoleAccessService access, AppUserAuthenticationService authentication, HttpContext context, CancellationToken cancellation) =>
        {
            var result = await overview.GetAsync(cancellation);
            HealthCheckView[] checks = [];
            ApiError? error = null;
            try { checks = await health.GetViewsAsync(cancellation); }
            catch (HealthCheckException exception) { error = new(exception.Code, exception.SafeMessage); }
            var user = authentication.CurrentUser(context);
            var rows = result.Servers.Where(server => access.CanRead(user, server.Id)).ToArray();
            return Results.Ok(new MonitoringOverview(result.CollectedAt, rows, checks.Where(check => access.CanRead(user, check.ServerId)).ToArray())
                { HealthError = error, CompletedServers = rows.Length, TotalServers = rows.Length });
        });
        app.MapGet("/api/v1/health-checks", async (HealthMonitoringService health, ConsoleAccessService access,
            AppUserAuthenticationService authentication, HttpContext context, CancellationToken cancellation) =>
            Results.Ok((await health.GetViewsAsync(cancellation)).Where(check => access.CanRead(authentication.CurrentUser(context), check.ServerId))));
        app.MapPost("/api/v1/health-checks/save", async (SaveHealthCheckRequest request, HealthMonitoringService health,
            ILogger<HealthMonitoringService> logger, CancellationToken cancellation) =>
        {
            var saved = await health.SaveAsync(request, cancellation);
            logger.LogInformation("event=health_config_saved checkId={CheckId} serverId={ServerId} enabled={Enabled}", saved.Id, saved.ServerId, saved.Enabled);
            return Results.Ok(saved);
        });
        app.MapPost("/api/v1/health-checks/{id}/delete", async (string id, HealthCheckRevision request,
            HealthMonitoringService health, ILogger<HealthMonitoringService> logger, CancellationToken cancellation) =>
        {
            await health.DeleteAsync(id, request.Revision, cancellation);
            logger.LogInformation("event=health_config_deleted checkId={CheckId}", id);
            return Results.Ok(new { deleted = true });
        });
        app.MapPost("/api/v1/health-checks/{id}/run", async (string id, HealthMonitoringService health, CancellationToken cancellation) =>
            Results.Ok(await health.RunAsync(id, cancellation)));
    }
}
