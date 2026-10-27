using IISManager.Contracts;

namespace IISManager.Console;

public static class AdministrationEndpoints
{
    public static void MapEndpoints(WebApplication app)
    {
        static AppUserIdentity? Actor(HttpContext context, AppUserAuthenticationService authentication) => authentication.CurrentUser(context);
        app.MapGet("/api/v1/permissions", (HttpContext context, AppUserAuthenticationService authentication, ConsoleAccessService access) =>
            Results.Ok(access.Effective(Actor(context, authentication))));
        app.MapGet("/api/v1/administration/servers", (HttpContext context, AppUserAuthenticationService authentication, AdministrationService administration) =>
            Results.Ok(administration.Servers(Actor(context, authentication))));
        app.MapPost("/api/v1/administration/servers/preview", (SaveManagedServerRequest request, HttpContext context,
            AppUserAuthenticationService authentication, AdministrationService administration) => Results.Ok(administration.PreviewServer(request, Actor(context, authentication))));
        app.MapPost("/api/v1/administration/servers/save", async (SaveManagedServerRequest request, HttpContext context,
            AppUserAuthenticationService authentication, AdministrationService administration, ILogger<AdministrationService> logger, CancellationToken cancellation) =>
        {
            var saved = await administration.SaveServerAsync(request, Actor(context, authentication), cancellation);
            logger.LogInformation("event=server_configuration_saved serverId={ServerId} restartRequired=true", request.Id);
            return Results.Ok(saved);
        });
        app.MapPost("/api/v1/administration/servers/test", async (SaveManagedServerRequest request, HttpContext context,
            AppUserAuthenticationService authentication, AdministrationService administration, CancellationToken cancellation) =>
            Results.Ok(await administration.TestConnectionAsync(request, Actor(context, authentication), cancellation)));
        app.MapGet("/api/v1/administration/policy", (HttpContext context, AppUserAuthenticationService authentication, AdministrationService administration) =>
            Results.Ok(administration.Policy(Actor(context, authentication))));
        app.MapPost("/api/v1/administration/policy/preview", (SaveAuthorizationPolicyRequest request, HttpContext context,
            AppUserAuthenticationService authentication, AdministrationService administration) => Results.Ok(administration.PreviewPolicy(request, Actor(context, authentication))));
        app.MapPost("/api/v1/administration/policy/save", async (SaveAuthorizationPolicyRequest request, HttpContext context,
            AppUserAuthenticationService authentication, AdministrationService administration, ILogger<AdministrationService> logger, CancellationToken cancellation) =>
        {
            var saved = await administration.SavePolicyAsync(request, Actor(context, authentication), cancellation);
            logger.LogInformation("event=authorization_policy_saved strictMode={StrictMode}", saved.Policy.Enabled);
            return Results.Ok(saved);
        });
    }
}
