using System.Security;
using System.Text.Json.Serialization;

namespace IISManager.Setup.Core;

public enum SetupComponent { Agent, Console }
public enum SetupMode { Install, Update }
public enum ServiceIdentityKind { VirtualAccount, LocalSystem, ExistingAccount }
public enum PayloadFileKind { Runtime, DefaultConfiguration }

public sealed record SetupServer(string Id, string Name, string AgentUrl, string ServerCertificateThumbprint,
    [property: JsonIgnore] string AccessToken = "")
{
    public override string ToString() => $"{Id} · {Name} · {AgentUrl} · AccessToken=[redacted]";
}

public sealed record SetupPlan
{
    public SetupComponent Component { get; init; }
    public SetupMode Mode { get; init; }
    public string InstallDirectory { get; init; } = "";
    public string DataDirectory { get; init; } = "";
    public string HostName { get; init; } = "";
    public string ListenAddress { get; init; } = "0.0.0.0";
    public int Port { get; init; }
    public string TransportMode { get; init; } = "HttpToken";
    public string SourceAccessMode { get; init; } = "PrivateNetwork";
    [JsonIgnore]
    public string AgentAccessToken { get; init; } = "";
    public string ServerCertificateThumbprint { get; init; } = "";
    public string ClientCertificateThumbprint { get; init; } = "";
    public string[] AllowedClientThumbprints { get; init; } = [];
    public string[] AllowedHosts { get; init; } = [];
    public string[] AllowedNetworks { get; init; } = [];
    public string[] AllowedSiteRoots { get; init; } = [];
    public SetupServer[] Servers { get; init; } = [];
    public ServiceIdentityKind IdentityKind { get; init; }
    public string AccountName { get; init; } = "";
    public bool GrantCertificateKeyRead { get; init; }
    public bool ConfigureFirewall { get; init; }
    public string ServiceName => "IISManager." + Component;
    public string ExecutableName => ServiceName + ".exe";
    public string ServiceAccount => IdentityKind switch
    {
        ServiceIdentityKind.VirtualAccount => "NT SERVICE\\" + ServiceName,
        ServiceIdentityKind.LocalSystem => "LocalSystem",
        _ => AccountName
    };
    public override string ToString() => $"{Component} · {Mode} · {TransportMode} · {SourceAccessMode} · Port={Port} · AccessToken=[redacted]";
}

public sealed record SetupIssue(string Code, string Message);
public sealed record SetupProgress(string Stage, string Message);
public sealed record SetupOutcome(bool Succeeded, string State, string Code, string Message, string ReceiptPath);
public sealed record SetupInspection(bool ServiceExists, string ServiceState, string ServiceAccount,
    string InstallDirectory, SetupPlan? ExistingPlan, SetupIssue[] Issues);
public sealed record ConsoleFirewallRecoveryInspection(bool CanRecover, SetupPlan? Plan,
    Guid OriginalReceiptId, string ReceiptPath, SetupIssue[] Issues);
public sealed record SetupCertificate(string Thumbprint, string DisplayName, string[] DnsNames,
    DateTimeOffset NotAfter, bool ServerAuthentication, bool ClientAuthentication, bool HasPrivateKey);

public sealed record PayloadFile(string Path, long Size, string Sha256, PayloadFileKind Kind);
public sealed record PayloadManifest(string Component, string TargetFramework, string RuntimeIdentifier,
    bool SelfContained, PayloadFile[] Files, PayloadFile[]? LegacyRuntimeFiles = null);

public interface ISetupPayload : IDisposable
{
    PayloadManifest Manifest { get; }
    void ExtractTo(string directory);
}

public interface ISetupPayloadSource
{
    ISetupPayload Open(SetupComponent component);
}

public interface ISetupBackend
{
    SetupCertificate[] GetCertificates();
    SetupInspection Inspect(SetupComponent component, string installDirectory);
    Task<SetupOutcome> ExecuteAsync(SetupPlan plan, SecureString? password,
        IProgress<SetupProgress> progress, CancellationToken cancellationToken);
}

public sealed class SetupException(string code, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}
