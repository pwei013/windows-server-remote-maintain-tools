using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IISManager.Contracts;

namespace IISManager.Console;

public interface IAdministrationConnectionTester
{
    Task<AgentInfo> TestAsync(ServerOptions server, CancellationToken cancellation);
}
public sealed class AdministrationConnectionTester(ConsoleOptions options) : IAdministrationConnectionTester
{
    public async Task<AgentInfo> TestAsync(ServerOptions server, CancellationToken cancellation)
    {
        using var client = new AgentClient(new ConsoleOptions { Servers = [server], TransportMode = options.TransportMode,
            AgentClientCertificateThumbprint = options.AgentClientCertificateThumbprint, AgentTimeoutSeconds = Math.Min(options.AgentTimeoutSeconds, 10) });
        var result = await client.GetAsync<AgentInfo>(server.Id, "info", cancellation);
        if (result.ProtocolVersion != Protocol.Version || string.IsNullOrWhiteSpace(result.MachineName) || result.AgentVersion is null)
            throw new AgentCallException("AGENT_PROTOCOL_UNSUPPORTED");
        return result;
    }
}

public sealed class AdministrationService(ConsoleOptions options, AdministrationStateStore store,
    IAdministrationConnectionTester tester)
{
    private readonly SemaphoreSlim _connections = new(2, 2);
    public AdministrationServerList Servers(AppUserIdentity? actor)
    {
        store.RequireAdministrator(actor);
        var servers = store.PendingServers.Select(server => View(server)).ToArray();
        return new(store.Revision, servers, servers.Any(server => server.RestartRequired), store.CanPersist, store.Protection,
            options.TransportMode, !string.IsNullOrWhiteSpace(options.AgentClientCertificateThumbprint));
    }

    public AdministrationPolicyView Policy(AppUserIdentity? actor)
    {
        store.RequireAdministrator(actor);
        return new(store.Revision, ClonePolicy(store.Policy), store.CanPersist);
    }

    public AdministrationPreview PreviewServer(SaveManagedServerRequest request, AppUserIdentity? actor)
    {
        store.RequireAdministrator(actor); store.RequireRevision(request.Revision);
        var candidate = Candidate(request);
        return ServerPreview(request.Revision, candidate, actor);
    }

    public async Task<AdministrationServerList> SaveServerAsync(SaveManagedServerRequest request, AppUserIdentity? actor, CancellationToken cancellation)
    {
        store.RequireAdministrator(actor);
        await store.ChangeAsync(request.Revision, actor, current =>
        {
            var candidate = Candidate(request);
            var preview = ServerPreview(current.Revision, candidate, actor);
            RequireFingerprint(request.ExpectedFingerprint, preview.Fingerprint);
            var next = new AdministrationDocument { Policy = current.Policy,
                ServerOverrides = current.ServerOverrides.Where(server => server.Id != candidate.Id).Append(candidate).ToArray() };
            return (next, true);
        }, cancellation);
        return Servers(actor);
    }

    public async Task<AdministrationConnectionResult> TestConnectionAsync(SaveManagedServerRequest request,
        AppUserIdentity? actor, CancellationToken cancellation)
    {
        store.RequireAdministrator(actor); store.RequireRevision(request.Revision);
        var candidate = Candidate(request);
        if (!await _connections.WaitAsync(0, cancellation))
            throw new AdministrationException("ADMIN_CONNECTION_BUSY", "正在核对其它接入连接，请稍后再试。", 429);
        try
        {
            var info = await tester.TestAsync(candidate, cancellation);
            if (!string.IsNullOrEmpty(candidate.AccessToken) && new[] { info.MachineName, info.AgentVersion, info.ProtocolVersion, info.Mode }
                .Any(value => value?.Contains(candidate.AccessToken, StringComparison.Ordinal) == true))
                throw new AgentCallException("AGENT_RESPONSE_INVALID");
            return new(info.MachineName, info.AgentVersion, info.ProtocolVersion, info.Mode, DateTimeOffset.UtcNow);
        }
        finally { _connections.Release(); }
    }

    public AdministrationPreview PreviewPolicy(SaveAuthorizationPolicyRequest request, AppUserIdentity? actor)
    {
        store.RequireAdministrator(actor); store.RequireRevision(request.Revision);
        var proposed = ClonePolicy(request.Policy);
        AdministrationRules.ValidatePolicy(proposed, store.PendingServers.Select(server => server.Id), actor, store.DevelopmentAdministration);
        return PolicyPreview(request.Revision, proposed, actor);
    }

    public async Task<AdministrationPolicyView> SavePolicyAsync(SaveAuthorizationPolicyRequest request, AppUserIdentity? actor, CancellationToken cancellation)
    {
        store.RequireAdministrator(actor);
        await store.ChangeAsync(request.Revision, actor, current =>
        {
            var proposed = ClonePolicy(request.Policy);
            AdministrationRules.ValidatePolicy(proposed, store.PendingServers.Select(server => server.Id), actor, store.DevelopmentAdministration);
            RequireFingerprint(request.ExpectedFingerprint, PolicyPreview(current.Revision, proposed, actor).Fingerprint);
            return (new AdministrationDocument { ServerOverrides = current.ServerOverrides, Policy = proposed }, true);
        }, cancellation);
        return Policy(actor);
    }

    private ServerOptions Candidate(SaveManagedServerRequest request)
    {
        if (request is null || !AdministrationRules.ValidId(request.Id) || request.Name is not { Length: > 0 and <= 100 } ||
            request.Name != request.Name.Trim() || request.Name.Any(char.IsControl) || request.Group is null || request.Environment is null ||
            request.AgentUrl is null || request.AgentUrl.Length > 2048 || request.AgentUrl.Any(char.IsWhiteSpace) || request.AgentUrl.Contains('\\') ||
            request.AccessToken is { Length: > 8192 } || request.AccessToken?.Any(char.IsControl) == true || request.ServerCertificateThumbprint is null)
            throw new AdministrationException("SERVER_CONFIGURATION_INVALID", "请核对编号、名称、分组、地址及认证字段。");
        var previous = store.PendingServers.FirstOrDefault(server => server.Id == request.Id);
        if (previous is null && store.PendingServers.Length >= 500)
            throw new AdministrationException("SERVER_LIMIT_REACHED", "最多管理 500 台服务器。");
        var candidate = new ServerOptions { Id = request.Id, Name = request.Name, Group = request.Group.Trim(), Environment = request.Environment.Trim(),
            AgentUrl = request.AgentUrl, AccessToken = string.IsNullOrEmpty(request.AccessToken) ? previous?.AccessToken ?? "" : request.AccessToken,
            ServerCertificateThumbprint = MachineCertificates.Normalize(request.ServerCertificateThumbprint), Simulation = previous?.Simulation ?? false };
        if (previous is not null && string.IsNullOrEmpty(request.AccessToken) && Uri.TryCreate(candidate.AgentUrl, UriKind.Absolute, out var target) &&
            target.Scheme == "http" && Uri.TryCreate(previous.AgentUrl, UriKind.Absolute, out var prior) &&
            (!string.Equals(target.Scheme, prior.Scheme, StringComparison.OrdinalIgnoreCase) || target.Port != prior.Port ||
             !string.Equals(target.IdnHost.TrimEnd('.'), prior.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)))
            throw new AdministrationException("SERVER_CREDENTIAL_REENTRY_REQUIRED", "HTTP 接入地址已改变，请重新输入该目标的 Token；不会向新地址转发原有凭据。");
        if (candidate.ServerCertificateThumbprint.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(candidate.ServerCertificateThumbprint, "^[A-F0-9]{40}$"))
            throw new AdministrationException("SERVER_CONFIGURATION_INVALID", "证书指纹须为 40 位 SHA1 十六进制值。");
        try { options.ValidateServer(candidate, store.IsDevelopment); }
        catch (InvalidOperationException exception)
        {
            throw new AdministrationException(exception.Message is "AGENT_HTTP_TOKEN_REQUIRED" or "AGENT_MTLS_REQUIRED" ? exception.Message : "SERVER_CONFIGURATION_INVALID",
                "服务器地址或认证不符合当前 Console 通信策略，请核对 HTTP Token 或 HTTPS 双向证书配置。");
        }
        candidate.AgentUrl = new Uri(candidate.AgentUrl).AbsoluteUri;
        if (new Uri(candidate.AgentUrl).Port is < 1 or > 65535 || new Uri(candidate.AgentUrl).HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
            throw new AdministrationException("SERVER_CONFIGURATION_INVALID", "请填写合法的 Agent 主机名或 IP 与端口。");
        if (new Uri(candidate.AgentUrl).Scheme == "https") candidate.AccessToken = "";
        return candidate;
    }

    private AdministrationPreview ServerPreview(string revision, ServerOptions candidate, AppUserIdentity? actor)
    {
        var previous = store.PendingServers.FirstOrDefault(server => server.Id == candidate.Id);
        var changes = new List<AdministrationFieldChange>();
        void Add(string field, string? before, string? after) { if (before != after) changes.Add(new(field, before ?? "未配置", after ?? "未配置")); }
        Add("名称", previous?.Name, candidate.Name); Add("项目分组", previous?.Group, candidate.Group);
        Add("环境", previous?.Environment, candidate.Environment); Add("Agent 地址", previous?.AgentUrl, candidate.AgentUrl);
        Add("服务端证书指纹", previous?.ServerCertificateThumbprint, candidate.ServerCertificateThumbprint);
        var secretChanged = !string.Equals(previous?.AccessToken ?? "", candidate.AccessToken, StringComparison.Ordinal);
        if (secretChanged) changes.Add(new("访问凭据", previous is null || string.IsNullOrEmpty(previous.AccessToken) ? "未配置" : "已配置（不展示）",
            string.IsNullOrEmpty(candidate.AccessToken) ? "不使用 HTTP Token" : "将保存新的受保护凭据"));
        return new(revision, Fingerprint(revision, actor, candidate), changes.ToArray(), true, secretChanged);
    }

    private AdministrationPreview PolicyPreview(string revision, ConsoleAuthorizationPolicy proposed, AppUserIdentity? actor)
    {
        var current = store.Policy;
        var changes = new List<AdministrationFieldChange>();
        if (current.Enabled != proposed.Enabled) changes.Add(new("严格授权", current.Enabled ? "已开启" : "沿用原有权限", proposed.Enabled ? "未授权默认拒绝" : "沿用原有权限"));
        if (JsonSerializer.Serialize(current.Administrators, Protocol.Json) != JsonSerializer.Serialize(proposed.Administrators, Protocol.Json))
            changes.Add(new("管理员", string.Join("；", current.Administrators.Select(principal => principal.AppId + "/" + principal.UserId)),
                string.Join("；", proposed.Administrators.Select(principal => principal.AppId + "/" + principal.UserId))));
        if (JsonSerializer.Serialize(current.Grants, Protocol.Json) != JsonSerializer.Serialize(proposed.Grants, Protocol.Json))
            changes.Add(new("服务器授权", FormatGrants(current.Grants), FormatGrants(proposed.Grants)));
        return new(revision, Fingerprint(revision, actor, proposed), changes.ToArray(), false, false);
    }
    private static string FormatGrants(AdministrationGrant[] grants) => grants.Length == 0 ? "无单独授权" : string.Join("\n", grants.Select(grant =>
        grant.AppId + "/" + grant.UserId + " → " + string.Join("、", grant.ServerIds) + "：" +
        string.Join("、", new[] { grant.Read ? "查看" : "", grant.Operate ? "启停/回收" : "", grant.Configure ? "修改配置" : "" }.Where(value => value.Length > 0))));

    private ManagedServerView View(ServerOptions server)
    {
        var active = store.ActiveServers.FirstOrDefault(value => value.Id == server.Id);
        return new(server.Id, server.Name, server.Group, server.Environment, server.AgentUrl, !string.IsNullOrEmpty(server.AccessToken),
            server.ServerCertificateThumbprint, server.Simulation, active is not null,
            active is null || Fingerprint("server", null, active) != Fingerprint("server", null, server));
    }
    private static string Fingerprint<T>(string revision, AppUserIdentity? actor, T value)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json);
        try
        {
            var prefix = Encoding.UTF8.GetBytes(revision + "\n" + actor?.AppId + "\n" + actor?.UserId + "\n");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(prefix); hash.AppendData(payload); return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }
    private static void RequireFingerprint(string? expected, string current)
    {
        if (expected is null || !string.Equals(expected, current, StringComparison.Ordinal))
            throw new AdministrationException("ADMINISTRATION_PREVIEW_REQUIRED", "配置与预览不一致，请重新预览后明确提交。", 409);
    }
    private static ConsoleAuthorizationPolicy ClonePolicy(ConsoleAuthorizationPolicy policy)
    {
        if (policy is null || policy.Administrators is null || policy.Grants is null || policy.Grants.Any(grant => grant is null || grant.ServerIds is null))
            throw new AdministrationException("AUTHORIZATION_POLICY_INVALID", "授权配置结构不完整。");
        return policy with { Administrators = policy.Administrators.ToArray(), Grants = policy.Grants.Select(grant => grant with { ServerIds = grant.ServerIds.ToArray() }).ToArray() };
    }
}
