namespace IISManager.Agent;

public sealed class AgentOptions
{
    public string Mode { get; set; } = "Windows";
    public string TransportMode { get; set; } = "Https";
    public string SourceAccessMode { get; set; } = "Allowlist";
    public string AccessToken { get; set; } = "";
    public string[] AllowedConsoleNetworks { get; set; } = [];
    public string StateDirectory { get; set; } = "data/operations";
    public string[] AllowedSiteRoots { get; set; } = [];
    public int[] ReservedPorts { get; set; } = [7443, 7444];
    public string[] AllowedClientThumbprints { get; set; } = [];
    public string ServerCertificateThumbprint { get; set; } = "";
    public int Port { get; set; } = 7444;
    public WindowsServiceOptions WindowsServices { get; set; } = new();
}

public sealed class WindowsServiceOptions
{
    public string[] AllowedNames { get; set; } = [];
    public string[] AllowedNamePrefixes { get; set; } = [];
    public string[] ProtectedNames { get; set; } = [];
    public bool AllowCreate { get; set; }
    public string[] AllowedExecutableRoots { get; set; } = [];
    public string[] AllowedAccounts { get; set; } = ["VirtualAccount", "LocalService"];
    public int OperationTimeoutSeconds { get; set; } = 60;
}
