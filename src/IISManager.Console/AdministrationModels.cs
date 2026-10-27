using System.Net;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using IISManager.Contracts;

namespace IISManager.Console;

public sealed class AdministrationOptions
{
    public string DataDirectory { get; set; } = "";
    public bool AllowDevelopmentPlaintext { get; set; }
    public AdministrationPrincipal[] BootstrapAdministrators { get; set; } = [];
    public void Validate(bool development, string listenAddress)
    {
        if (DataDirectory is null || BootstrapAdministrators is null || BootstrapAdministrators.Length > 100 ||
            BootstrapAdministrators.Any(principal => !AdministrationRules.ValidPrincipal(principal)) ||
            AllowDevelopmentPlaintext && (!development || !IPAddress.TryParse(listenAddress, out var address) || !IPAddress.IsLoopback(address)))
            throw new InvalidOperationException("ADMINISTRATION_OPTIONS_INVALID");
    }
}

public sealed record AdministrationPrincipal(int AppId, string UserId);
public sealed record AdministrationGrant
{
    public int AppId { get; init; }
    public string UserId { get; init; } = "";
    public string[] ServerIds { get; init; } = [];
    public bool Read { get; init; } = true;
    public bool Operate { get; init; }
    public bool Configure { get; init; }
}
public sealed record ConsoleAuthorizationPolicy
{
    public bool Enabled { get; init; }
    public AdministrationPrincipal[] Administrators { get; init; } = [];
    public AdministrationGrant[] Grants { get; init; } = [];
}
public sealed record EffectiveServerPermission(string ServerId, bool Read, bool Operate, bool Configure);
public sealed record EffectiveConsolePermissions(bool Administration, EffectiveServerPermission[] Servers)
{
    public bool StrictMode { get; init; }
}
public sealed record AdministrationFieldChange(string Field, string Before, string After);
public sealed record ManagedServerView(string Id, string Name, string Group, string Environment, string AgentUrl,
    bool HasAccessToken, string ServerCertificateThumbprint, bool Simulation, bool Effective, bool RestartRequired);
public sealed record AdministrationServerList(string Revision, ManagedServerView[] Servers, bool PendingRestart,
    bool CanPersist, string Protection, string TransportMode, bool ClientCertificateConfigured);
public sealed record AdministrationPolicyView(string Revision, ConsoleAuthorizationPolicy Policy, bool CanPersist);
public sealed record AdministrationPreview(string Revision, string Fingerprint, AdministrationFieldChange[] Changes,
    bool RequiresRestart, bool CredentialsChanged);

// Deliberately not a record: generated ToString must not expose a submitted access token.
public sealed class SaveManagedServerRequest
{
    public string Revision { get; set; } = "";
    public string? ExpectedFingerprint { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Environment { get; set; } = "";
    public string AgentUrl { get; set; } = "";
    public string? AccessToken { get; set; }
    public string ServerCertificateThumbprint { get; set; } = "";
}
public sealed record SaveAuthorizationPolicyRequest
{
    public string Revision { get; init; } = "";
    public string? ExpectedFingerprint { get; init; }
    public ConsoleAuthorizationPolicy Policy { get; init; } = new();
}
public sealed record AdministrationConnectionResult(string MachineName, string AgentVersion, string ProtocolVersion,
    string Mode, DateTimeOffset CheckedAt);

internal sealed class AdministrationDocument
{
    public int Version { get; set; } = 1;
    public string Revision { get; set; } = Guid.NewGuid().ToString("N");
    public ServerOptions[] ServerOverrides { get; set; } = [];
    public ConsoleAuthorizationPolicy Policy { get; set; } = new();
}
internal sealed record AdministrationEnvelope(int Version, string Protection, string Revision, string Payload);

public sealed class AdministrationException(string code, string message, int statusCode = 400) : Exception(code)
{
    public string Code { get; } = code;
    public string SafeMessage { get; } = message;
    public int StatusCode { get; } = statusCode;
}

internal static class AdministrationRules
{
    internal static bool ValidPrincipal(AdministrationPrincipal? principal) => principal is not null && principal.AppId > 0 &&
        principal.UserId is { Length: > 0 and <= 128 } && principal.UserId == principal.UserId.Trim() && !principal.UserId.Any(char.IsControl);
    internal static bool ValidId(string value) => Regex.IsMatch(value ?? "", "^[A-Za-z0-9_-]{1,64}$");
    internal static bool IsAdministrator(ConsoleAuthorizationPolicy policy, AppUserIdentity? user, bool developmentBypass) =>
        user is null ? developmentBypass : policy.Administrators.Any(principal => principal.AppId == user.AppId && principal.UserId == user.UserId);
    internal static bool Allows(ConsoleAuthorizationPolicy policy, AppUserIdentity? user, string id, string permission, bool developmentBypass)
    {
        if (user is null && !developmentBypass) return false;
        if (IsAdministrator(policy, user, developmentBypass) || !policy.Enabled) return true;
        return user is not null && policy.Grants.Any(grant => grant.AppId == user.AppId && grant.UserId == user.UserId &&
            grant.ServerIds.Contains(id, StringComparer.Ordinal) && (permission switch
            { "read" => grant.Read, "operate" => grant.Operate, "configure" => grant.Configure, _ => false }));
    }
    internal static void ValidatePolicy(ConsoleAuthorizationPolicy policy, IEnumerable<string> serverIds, AppUserIdentity? actor, bool developmentBypass)
    {
        var ids = serverIds.ToHashSet(StringComparer.Ordinal);
        if (policy is null || policy.Administrators is null || policy.Grants is null || policy.Administrators.Length > 100 || policy.Grants.Length > 500 ||
            policy.Administrators.Any(principal => !ValidPrincipal(principal)) ||
            policy.Administrators.Distinct().Count() != policy.Administrators.Length ||
            policy.Grants.Any(grant => grant is null || !ValidPrincipal(new(grant.AppId, grant.UserId)) ||
                grant.ServerIds is null || grant.ServerIds.Length is < 1 or > 500 ||
                grant.ServerIds.Any(id => !ValidId(id) || !ids.Contains(id)) ||
                grant.ServerIds.Distinct(StringComparer.Ordinal).Count() != grant.ServerIds.Length || !grant.Read && (grant.Operate || grant.Configure)))
            throw new AdministrationException("AUTHORIZATION_POLICY_INVALID", "请核对用户 AppId/UserId、服务器范围和权限配置。");
        if (policy.Enabled && policy.Administrators.Length == 0 || !IsAdministrator(policy, actor, developmentBypass))
            throw new AdministrationException("AUTHORIZATION_SELF_LOCKOUT", "保存后必须保留当前操作者的管理员身份。", 409);
    }
    internal static ServerOptions Clone(ServerOptions value) => new()
    {
        Id = value.Id, Name = value.Name, Group = value.Group, Environment = value.Environment, AgentUrl = value.AgentUrl,
        AccessToken = value.AccessToken, ServerCertificateThumbprint = value.ServerCertificateThumbprint, Simulation = value.Simulation
    };
    internal static ServerOptions[] Merge(ServerOptions[] original, ServerOptions[] overrides)
    {
        var mapped = original.Select(Clone).ToDictionary(server => server.Id, StringComparer.Ordinal);
        foreach (var server in overrides) mapped[server.Id] = Clone(server);
        return mapped.Values.ToArray();
    }
}
