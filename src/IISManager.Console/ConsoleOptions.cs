using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using IISManager.Core;

namespace IISManager.Console;

public sealed class ConsoleOptions
{
    public string ListenAddress { get; set; } = "127.0.0.1";
    public string TransportMode { get; set; } = "Https";
    public string SourceAccessMode { get; set; } = "Allowlist";
    public int Port { get; set; } = 7443;
    public string[] AllowedHosts { get; set; } = ["localhost", "127.0.0.1", "::1"];
    public string ServerCertificateThumbprint { get; set; } = "";
    public string AgentClientCertificateThumbprint { get; set; } = "";
    public bool AllowDevelopmentHttp { get; set; }
    public bool? BrowserHttps { get; set; }
    public string[] AllowedClientNetworks { get; set; } = [];
    public string TaskDirectory { get; set; } = "data/console-tasks";
    public int AgentTimeoutSeconds { get; set; } = 30;
    public ServerOptions[] Servers { get; set; } = [];
    public SiteAnnotationZeroCodeOptions SiteAnnotations { get; set; } = new();
    public AppUserAuthenticationOptions UserAuthentication { get; set; } = new();
    public OperationHistoryOptions OperationHistory { get; set; } = new();
    public MonitoringOptions Monitoring { get; set; } = new();
    public AdministrationOptions Administration { get; set; } = new();

    public bool UsesHttpsListener() => !AllowDevelopmentHttp &&
        (BrowserHttps ?? TransportMode != IntranetTransportSecurity.HttpToken);

    public void Validate(bool development)
    {
        UserAuthentication.Validate();
        OperationHistory.Validate();
        if (Monitoring is null) throw new InvalidOperationException("MONITORING_OPTIONS_INVALID");
        Monitoring.Validate();
        if (Administration is null) throw new InvalidOperationException("ADMINISTRATION_OPTIONS_INVALID");
        Administration.Validate(development, ListenAddress);
        if (!development && !UserAuthentication.Enabled)
            throw new InvalidOperationException("APP_USER_AUTH_REQUIRED_IN_PRODUCTION");
        SiteAnnotations.Validate();
        if (!IntranetTransportSecurity.IsTransportModeValid(TransportMode))
            throw new InvalidOperationException("CONSOLE_TRANSPORT_MODE_INVALID");
        var httpToken = TransportMode == IntranetTransportSecurity.HttpToken;
        if (!IntranetTransportSecurity.IsSourceAccessModeValid(SourceAccessMode))
            throw new InvalidOperationException("CONSOLE_SOURCE_ACCESS_MODE_INVALID");
        var privateSources = SourceAccessMode == IntranetTransportSecurity.PrivateNetwork;
        if (!IPAddress.TryParse(ListenAddress, out var address) || Port is < 1024 or > 65535)
            throw new InvalidOperationException("CONSOLE_LISTENER_INVALID");
        if (!UserAuthentication.Enabled && !IPAddress.IsLoopback(address))
            throw new InvalidOperationException("APP_USER_AUTH_BYPASS_REQUIRES_DEVELOPMENT_LOOPBACK");
        if (AgentTimeoutSeconds is < 1 or > 120 || string.IsNullOrWhiteSpace(TaskDirectory))
            throw new InvalidOperationException("CONSOLE_OPTIONS_INVALID");
        if (AllowedHosts.Length == 0 || AllowedHosts.Any(host => string.IsNullOrWhiteSpace(host) ||
                host == "*" || host.Contains('/') || host.Contains('\\') || host.Contains('@') || host.Contains('*') ||
                !IPAddress.TryParse(host, out _) && Uri.CheckHostName(host) != UriHostNameType.Dns))
            throw new InvalidOperationException("CONSOLE_HOST_ALLOWLIST_REQUIRED");
        if (AllowDevelopmentHttp && (!development || !IPAddress.IsLoopback(address)))
            throw new InvalidOperationException("DEVELOPMENT_HTTP_REQUIRES_LOOPBACK");
        if (UsesHttpsListener() && string.IsNullOrWhiteSpace(ServerCertificateThumbprint))
            throw new InvalidOperationException("CONSOLE_CERTIFICATE_REQUIRED");
        if (!privateSources && (AllowedClientNetworks.Length == 0 || AllowedClientNetworks.Any(n => !ClientNetwork.TryParse(n, out _))))
            throw new InvalidOperationException("CONSOLE_CLIENT_NETWORKS_REQUIRED");
        if (httpToken && !privateSources) _ = IntranetTransportSecurity.ParseRestrictedNetworks(AllowedClientNetworks);
        if (Servers.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != Servers.Length)
            throw new InvalidOperationException("SERVER_IDS_DUPLICATED");
        foreach (var server in Servers) ValidateServer(server, development);
    }

    public void ValidateServer(ServerOptions server, bool development)
    {
        var httpToken = TransportMode == IntranetTransportSecurity.HttpToken;
            if (!Regex.IsMatch(server.Id, "^[A-Za-z0-9_-]{1,64}$") || string.IsNullOrWhiteSpace(server.Name) ||
                server.Group is null || server.Environment is null || server.Group.Length > 64 || server.Environment.Length > 64 ||
                server.Group.Any(char.IsControl) || server.Environment.Any(char.IsControl) ||
                !Uri.TryCreate(server.AgentUrl, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
                throw new InvalidOperationException("SERVER_CONFIGURATION_INVALID");
            if (uri.Scheme == "http")
            {
                if (server.Simulation && (!development || !IPAddress.TryParse(uri.Host, out var host) || !IPAddress.IsLoopback(host)))
                    throw new InvalidOperationException("AGENT_HTTP_REQUIRES_DEVELOPMENT_SIMULATION_LOOPBACK");
                if (!server.Simulation && (!httpToken || !IntranetTransportSecurity.IsValidAccessToken(server.AccessToken)))
                    throw new InvalidOperationException("AGENT_HTTP_TOKEN_REQUIRED");
            }
            else if (uri.Scheme != "https" || string.IsNullOrWhiteSpace(server.ServerCertificateThumbprint) ||
                     string.IsNullOrWhiteSpace(AgentClientCertificateThumbprint))
                throw new InvalidOperationException("AGENT_MTLS_REQUIRED");
    }
}

public sealed class ServerOptions
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Environment { get; set; } = "";
    public string AgentUrl { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string ServerCertificateThumbprint { get; set; } = "";
    public bool Simulation { get; set; }
}

public sealed record ServerSummary(string Id, string Name, string Mode, string HostName);

public static class MachineCertificates
{
    public static string Normalize(string value) => value.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();

    public static X509Certificate2 Load(string thumbprint, string requiredEku)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("LOCAL_MACHINE_CERTIFICATE_REQUIRES_WINDOWS");
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var certificates = store.Certificates.Find(X509FindType.FindByThumbprint, Normalize(thumbprint), validOnly: true);
        if (certificates.Count != 1 || !certificates[0].HasPrivateKey)
            throw new InvalidOperationException("LOCAL_MACHINE_CERTIFICATE_NOT_AVAILABLE");
        var eku = certificates[0].Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
        if (eku.Length != 1 || !eku[0].EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(oid => oid.Value == requiredEku))
            throw new InvalidOperationException("LOCAL_MACHINE_CERTIFICATE_EKU_INVALID");
        return certificates[0];
    }
}

public sealed record ClientNetwork(IPAddress Address, int Prefix)
{
    public static bool TryParse(string value, out ClientNetwork? network)
    {
        network = null;
        var parts = value.Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address)) return false;
        var bits = address.GetAddressBytes().Length * 8;
        var prefix = bits;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > bits)) return false;
        network = new ClientNetwork(address, prefix);
        return true;
    }

    public bool Contains(IPAddress candidate)
    {
        if (candidate.IsIPv4MappedToIPv6) candidate = candidate.MapToIPv4();
        var source = candidate.GetAddressBytes();
        var target = Address.GetAddressBytes();
        if (source.Length != target.Length) return false;
        for (var i = 0; i < Prefix; i++)
            if ((source[i / 8] & (1 << (7 - i % 8))) != (target[i / 8] & (1 << (7 - i % 8)))) return false;
        return true;
    }
}
