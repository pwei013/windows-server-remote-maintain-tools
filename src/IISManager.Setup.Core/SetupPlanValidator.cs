using System.Net;
using System.Text.RegularExpressions;
using IISManager.Networking;

namespace IISManager.Setup.Core;

public static class SetupPlanValidator
{
    public static SetupIssue[] Validate(SetupPlan plan)
    {
        var issues = new List<SetupIssue>();
        void Add(string code, string message) => issues.Add(new(code, message));
        if (!Enum.IsDefined(plan.Component) || !Enum.IsDefined(plan.Mode) || !Enum.IsDefined(plan.IdentityKind))
            Add("PLAN_ENUM_INVALID", "组件、安装方式或服务身份无效。");
        if (!IsWindowsDirectory(plan.InstallDirectory)) Add("INSTALL_PATH_INVALID", "安装目录须为本机磁盘上的绝对子目录，不能使用网络、设备或相对路径。");
        if (plan.Mode == SetupMode.Update) return issues.ToArray();
        if (!IsWindowsDirectory(plan.DataDirectory)) Add("DATA_PATH_INVALID", "数据目录须为本机磁盘上的绝对子目录。");
        if (PathsOverlap(plan.InstallDirectory, plan.DataDirectory)) Add("DIRECTORIES_OVERLAP", "程序和数据须使用互不包含的目录。");
        if (plan.Port is < 1024 or > 65535) Add("PORT_INVALID", "服务端口须为 1024–65535。");
        if (plan.TransportMode is not ("HttpToken" or "Https")) Add("TRANSPORT_MODE_INVALID", "选择 HTTP 访问令牌或 HTTPS 证书模式。");
        if (!IntranetSourcePolicy.IsValidMode(plan.SourceAccessMode)) Add("SOURCE_ACCESS_MODE_INVALID", "管理来源策略无效。");
        var https = plan.TransportMode == "Https";
        if (!IsHost(plan.HostName)) Add("HOST_NAME_INVALID", "填写实际访问主机名或 IP，不能包含端口、路径或通配符。");
        if (https && !IsThumbprint(plan.ServerCertificateThumbprint)) Add("SERVER_CERTIFICATE_REQUIRED", "选择有效的服务器证书。");
        if (!https && plan.GrantCertificateKeyRead) Add("HTTP_KEY_GRANT_FORBIDDEN", "HTTP 模式不需要证书私钥授权。");
        if (plan.IdentityKind == ServiceIdentityKind.ExistingAccount &&
            (string.IsNullOrWhiteSpace(plan.AccountName) || plan.AccountName.Any(c => char.IsControl(c) || c == '"')))
            Add("SERVICE_ACCOUNT_REQUIRED", "填写已经准备好权限的服务账号。");
        if (plan.SourceAccessMode == IntranetSourcePolicy.Allowlist &&
            (plan.AllowedNetworks is null || plan.AllowedNetworks.Length == 0 || plan.AllowedNetworks.Any(n => !IsNetwork(n))))
            Add("NETWORK_ALLOWLIST_REQUIRED", "填写明确的管理来源 IP/CIDR；不能使用全部网络。");
        if (plan.Component == SetupComponent.Agent)
        {
            if (plan.IdentityKind == ServiceIdentityKind.VirtualAccount) Add("AGENT_IDENTITY_REQUIRED", "Agent 请选择具备 IIS 管理权限的账号，或明确选择 LocalSystem。");
            if (https && (plan.AllowedClientThumbprints is null || plan.AllowedClientThumbprints.Length == 0 ||
                plan.AllowedClientThumbprints.Any(t => !IsThumbprint(t)))) Add("CLIENT_ALLOWLIST_REQUIRED", "填写允许连接的控制台客户端证书指纹。");
            if (!https && !IsAccessToken(plan.AgentAccessToken)) Add("AGENT_TOKEN_REQUIRED", "生成或填写 64 位十六进制 Agent 访问令牌。");
            if (plan.AllowedSiteRoots is null || plan.AllowedSiteRoots.Length == 0 || plan.AllowedSiteRoots.Any(p => !IsWindowsDirectory(p, allowDriveRoot: true)))
                Add("SITE_ROOT_REQUIRED", "选择允许创建站点的已有本机目录。");
        }
        else if (plan.Component == SetupComponent.Console)
        {
            if (plan.IdentityKind == ServiceIdentityKind.LocalSystem) Add("CONSOLE_IDENTITY_EXCESSIVE", "控制台使用专用虚拟账号或已准备的服务账号。");
            if (!IPAddress.TryParse(plan.ListenAddress, out _)) Add("LISTEN_ADDRESS_INVALID", "监听地址须为本机 IP，例如 0.0.0.0。");
            if (https && !IsThumbprint(plan.ClientCertificateThumbprint)) Add("CONSOLE_CLIENT_CERTIFICATE_REQUIRED", "选择控制台连接 Agent 的客户端证书。");
            if (plan.AllowedHosts is null || plan.AllowedHosts.Length == 0 || plan.AllowedHosts.Any(h => !IsHost(h)))
                Add("HOST_ALLOWLIST_REQUIRED", "填写精确的控制台访问主机名/IP，不允许通配符。");
            else if (!plan.AllowedHosts.Contains(plan.HostName, StringComparer.OrdinalIgnoreCase))
                Add("HOST_NOT_ALLOWED", "控制台主机名须包含在访问主机白名单中。");
            if (plan.Servers is null || plan.Servers.Length == 0) Add("AGENT_SERVER_REQUIRED", "至少填写一台 Agent。");
            else if (plan.Servers.Any(s => s is null)) Add("AGENT_SERVER_INVALID", "Agent 配置行不能为空。");
            else
            {
                if (plan.Servers.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Servers.Length)
                    Add("AGENT_IDS_DUPLICATED", "Agent 编号不能重复。");
                foreach (var server in plan.Servers)
                {
                    if (!Regex.IsMatch(server.Id ?? "", "^[A-Za-z0-9_-]{1,64}$") || string.IsNullOrWhiteSpace(server.Name) ||
                        !IsAgentUrl(server.AgentUrl, plan.TransportMode) ||
                        (https ? !IsThumbprint(server.ServerCertificateThumbprint) : !IsAccessToken(server.AccessToken)))
                        Add("AGENT_SERVER_INVALID", https
                            ? "Agent 需唯一编号、名称、HTTPS 根地址和服务器证书指纹。"
                            : "Agent 需唯一编号、名称、HTTP 根地址和 64 位十六进制访问令牌。");
                }
            }
        }
        return issues.DistinctBy(i => i.Code).ToArray();
    }

    public static string[] GetEffectiveAllowedNetworks(SetupPlan plan) => plan.SourceAccessMode switch
    {
        IntranetSourcePolicy.PrivateNetwork => IntranetSourcePolicy.DefaultNetworks(),
        IntranetSourcePolicy.Allowlist when plan.Mode == SetupMode.Update && plan.Component == SetupComponent.Agent &&
            plan.TransportMode == "Https" && plan.AllowedNetworks is { Length: 0 } => [],
        IntranetSourcePolicy.Allowlist when plan.AllowedNetworks is not null && plan.AllowedNetworks.Length > 0 &&
            plan.AllowedNetworks.All(IsNetwork) => (string[])plan.AllowedNetworks.Clone(),
        _ => throw new SetupException("SOURCE_ACCESS_MODE_INVALID", "管理来源策略无效或缺少有效来源范围。")
    };

    public static string[] GetFirewallAllowedNetworks(SetupPlan plan) => GetEffectiveAllowedNetworks(plan)
        .Where(network => !IsIPv6Loopback(network)).ToArray();

    private static bool IsIPv6Loopback(string network) => IPAddress.TryParse(network.Split('/')[0], out var address) &&
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && IPAddress.IsLoopback(address);

    public static bool IsThumbprint(string? value) => value is not null && Regex.IsMatch(value.Replace(" ", ""), "^[A-Fa-f0-9]{40}$");
    public static bool IsAccessToken(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    public static bool IsHost(string? value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim() && !value.Any(char.IsControl) &&
        !value.Contains('*') && (IPAddress.TryParse(value, out _) || Uri.CheckHostName(value) == UriHostNameType.Dns &&
            !value.Contains('/') && !value.Contains('\\') && !value.Contains('@') && !value.Contains(':'));
    public static bool IsAgentUrl(string? value, string transportMode = "Https") =>
        transportMode is "HttpToken" or "Https" && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == (transportMode == "HttpToken" ? "http" : "https") && uri.AbsolutePath == "/" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        IsHost(uri.Host.Trim('[', ']'));
    public static bool IsNetwork(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim()) return false;
        var parts = value.Split('/');
        if (parts.Length > 2 || parts[0].Contains('%') || !IPAddress.TryParse(parts[0], out var address)) return false;
        var bits = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (parts.Length == 1) return true;
        return parts[1].Length is >= 1 and <= 3 && int.TryParse(parts[1], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var prefix) && prefix is > 0 && prefix <= bits &&
            (!address.IsIPv4MappedToIPv6 || prefix > 96);
    }
    public static bool IsWindowsDirectory(string? value, bool allowDriveRoot = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || !Regex.IsMatch(value, "^[A-Za-z]:[\\\\/]")) return false;
        var tail = value[3..].Replace('\\', '/').TrimEnd('/');
        if (tail.Length == 0) return allowDriveRoot;
        return tail.Split('/').All(IsSafeSegment);
    }
    public static bool PathsOverlap(string? left, string? right)
    {
        if (left is null || right is null) return false;
        var a = left.Replace('\\', '/').TrimEnd('/'); var b = right.Replace('\\', '/').TrimEnd('/');
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase);
    }
    public static bool IsSafeSegment(string value)
    {
        if (value.Length == 0 || value is "." or ".." || value.EndsWith('.') || value.EndsWith(' ') ||
            value.Any(c => char.IsControl(c) || "<>:\"|?*\\/".Contains(c))) return false;
        return !Regex.IsMatch(value.Split('.')[0], "^(CON|PRN|AUX|NUL|CLOCK\\$|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
