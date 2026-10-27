using IISManager.Contracts;
using System.Text.Json;

namespace IISManager.Console;

public sealed class ConsoleAccessService(AdministrationStateStore store)
{
    public bool CanAdminister(AppUserIdentity? user) => AdministrationRules.IsAdministrator(store.Policy, user, store.DevelopmentAdministration);
    public bool CanRead(AppUserIdentity? user, string serverId) => AdministrationRules.Allows(store.Policy, user, serverId, "read", store.DevelopmentBypass);
    public bool CanOperate(AppUserIdentity? user, string serverId) => AdministrationRules.Allows(store.Policy, user, serverId, "operate", store.DevelopmentBypass);
    public bool CanConfigure(AppUserIdentity? user, string serverId) => AdministrationRules.Allows(store.Policy, user, serverId, "configure", store.DevelopmentBypass);
    public bool HasUnrestrictedRead(AppUserIdentity? user) => (user is not null || store.DevelopmentBypass) &&
        (!store.Policy.Enabled || CanAdminister(user));
    public string[]? AllowedServerIds(AppUserIdentity? user) => HasUnrestrictedRead(user) ? null :
        store.ActiveServers.Where(server => CanRead(user, server.Id)).Select(server => server.Id).ToArray();
    public EffectiveConsolePermissions Effective(AppUserIdentity? user) => new(CanAdminister(user), store.ActiveServers
        .Select(server => new EffectiveServerPermission(server.Id, CanRead(user, server.Id), CanOperate(user, server.Id), CanConfigure(user, server.Id)))
        .Where(permission => permission.Read).ToArray()) { StrictMode = store.Policy.Enabled };
    public void RequireAdministration(AppUserIdentity? user) => store.RequireAdministrator(user);
    public void Require(AppUserIdentity? user, string serverId, string permission)
    {
        if (!AdministrationRules.Allows(store.Policy, user, serverId, permission, store.DevelopmentBypass))
            throw new AdministrationException("SERVER_PERMISSION_DENIED", "当前账号没有该服务器的相应操作权限。", 403);
    }
    public static string OperationPermission(string type) => type is OperationTypes.Start or OperationTypes.Stop or OperationTypes.Recycle or
        OperationTypes.SiteStart or OperationTypes.SiteStop or OperationTypes.SiteRestart or OperationTypes.ServiceStart or
        OperationTypes.ServiceStop or OperationTypes.ServiceRestart ? "operate" : "configure";

    /// <summary>Call after application authentication, before existing server-specific handlers.</summary>
    public async Task AuthorizeRequestAsync(HttpContext context, AppUserIdentity? user, HealthMonitoringService? health = null)
    {
        var segments = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        static bool Same(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        if (segments.Length < 3 || !Same(segments[0], "api") || !Same(segments[1], "v1")) return;
        if (Same(segments[2], "administration")) { RequireAdministration(user); return; }
        if (Same(segments[2], "servers") && segments.Length >= 4)
        {
            var id = segments[3];
            if (HttpMethods.IsGet(context.Request.Method)) { Require(user, id, "read"); return; }
            if (!HttpMethods.IsPost(context.Request.Method)) { Require(user, id, "configure"); return; }
            if (segments.Length >= 5 && Same(segments[4], "operations"))
            {
                var type = (await ReadBodyAsync<OperationRequest>(context))?.Type ?? "";
                Require(user, id, OperationPermission(type)); return;
            }
            Require(user, id, "configure"); return;
        }
        if (Same(segments[2], "health-checks") && HttpMethods.IsPost(context.Request.Method))
        {
            string? id;
            if (segments.Length == 4 && Same(segments[3], "save"))
            {
                id = (await ReadBodyAsync<SaveHealthCheckRequest>(context))?.ServerId;
            }
            else
            {
                if (health is null) throw new AdministrationException("AUTHORIZATION_TARGET_UNAVAILABLE", "无法核对权限目标。", 503);
                id = (await health.GetViewsAsync(context.RequestAborted)).FirstOrDefault(check => check.Id == segments.ElementAtOrDefault(3))?.ServerId;
            }
            if (id is null) throw new AdministrationException("SERVER_PERMISSION_DENIED", "无法核对目标或没有相应权限。", 403);
            Require(user, id, Same(segments.LastOrDefault(), "run") ? "operate" : "configure");
        }
    }
    private static async Task<T?> ReadBodyAsync<T>(HttpContext context)
    {
        const int limit = 64 * 1024;
        if (context.Request.ContentLength > limit || context.Request.Body.CanSeek && context.Request.Body.Length > limit)
            throw new AdministrationException("AUTHORIZATION_BODY_TOO_LARGE", "请求内容超出允许范围。", 413);
        context.Request.EnableBuffering(bufferThreshold: 64 * 1024, bufferLimit: 64 * 1024);
        try
        {
            using var result = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (result.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object || result.RootElement.EnumerateObject()
                .Select(property => property.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.RootElement.EnumerateObject().Count())
            {
                throw new AdministrationException("AUTHORIZATION_BODY_INVALID", "请求字段重复或结构不合法。", 400);
            }
            return result.RootElement.Deserialize<T>(Protocol.Json);
        }
        catch (JsonException)
        {
            throw new AdministrationException("AUTHORIZATION_BODY_INVALID", "请求字段或 JSON 结构不合法。", 400);
        }
        catch (IOException)
        {
            throw new AdministrationException("AUTHORIZATION_BODY_TOO_LARGE", "请求内容超出允许范围或无法读取。", 413);
        }
        finally { if (context.Request.Body.CanSeek) context.Request.Body.Position = 0; }
    }
}
