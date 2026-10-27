using System.Text.Json;
using System.Text.Json.Serialization;

namespace IISManager.Contracts;

public static class Protocol
{
    public const string Version = "1.0";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

public static class OperationTypes
{
    public const string Start = "applicationPool.start";
    public const string Stop = "applicationPool.stop";
    public const string Recycle = "applicationPool.recycle";
    public const string CreatePool = "applicationPool.create";
    public const string UpdatePool = "applicationPool.update";
    public const string CreateSite = "site.create";
    public const string UpdateSite = "site.update";
    public const string CreateApplication = "application.create";
    public const string UpdateApplication = "application.update";
    public const string CreateVirtualDirectory = "virtualDirectory.create";
    public const string UpdateVirtualDirectory = "virtualDirectory.update";
    public const string SiteStart = "site.start";
    public const string SiteStop = "site.stop";
    public const string SiteRestart = "site.restart";
    public const string ServiceStart = "windowsService.start";
    public const string ServiceStop = "windowsService.stop";
    public const string ServiceRestart = "windowsService.restart";
    public const string ServiceCreate = "windowsService.create";
    public const string ServiceUpdateStartType = "windowsService.updateStartType";
    public static readonly string[] Iis = [Start, Stop, Recycle, CreatePool, UpdatePool, CreateSite, SiteStart, SiteStop, SiteRestart,
        UpdateSite, CreateApplication, UpdateApplication, CreateVirtualDirectory, UpdateVirtualDirectory];
    public static readonly string[] WindowsServices = [ServiceStart, ServiceStop, ServiceRestart, ServiceCreate, ServiceUpdateStartType];
    public static readonly string[] All = [.. Iis, .. WindowsServices];
    public static bool IsSiteLifecycle(string type) => type is SiteStart or SiteStop or SiteRestart;
    public static bool IsSiteMaintenance(string type) => type is UpdateSite or CreateApplication or UpdateApplication or CreateVirtualDirectory or UpdateVirtualDirectory;
    public static bool IsSite(string type) => type == CreateSite || IsSiteLifecycle(type) || IsSiteMaintenance(type);
}

public static class OperationStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Partial = "partial";
    public const string Unknown = "unknown";
    public const string Expired = "expired";
    public static bool IsFinal(string state) => state is Succeeded or Failed or Partial or Unknown or Expired;
}

public sealed record OperationTarget
{
    public string? ApplicationPoolName { get; init; }
    public string? SiteName { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceName { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApplicationPath { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VirtualDirectoryPath { get; init; }
}

public sealed record OperationRequest
{
    public Guid OperationId { get; init; }
    public string Type { get; init; } = "";
    public OperationTarget Target { get; init; } = new();
    public JsonElement Parameters { get; init; } = JsonSerializer.SerializeToElement(new { });
    public string? ExpectedFingerprint { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed record PoolParameters
{
    public string? ManagedRuntimeVersion { get; init; }
    public string? ManagedPipelineMode { get; init; }
    public bool? Enable32BitAppOnWin64 { get; init; }
    public bool? AutoStart { get; init; }
    public string? StartMode { get; init; }
    public int? IdleTimeoutMinutes { get; init; }
    public string? IdleTimeoutAction { get; init; }
}

public sealed record SiteParameters
{
    public string PhysicalPath { get; init; } = "";
    public string ApplicationPoolName { get; init; } = "";
    public BindingConfig[] Bindings { get; init; } = [];
}

public sealed record BindingConfig
{
    public string Protocol { get; init; } = "http";
    public string IpAddress { get; init; } = "*";
    public int Port { get; init; } = 80;
    public string HostName { get; init; } = "";
    public string? CertificateThumbprint { get; init; }
    public bool UseSni { get; init; }
}

public sealed record PoolConfig
{
    public string Name { get; init; } = "";
    public string State { get; init; } = "Unknown";
    public string ManagedRuntimeVersion { get; init; } = "";
    public string ManagedPipelineMode { get; init; } = "Integrated";
    public bool Enable32BitAppOnWin64 { get; init; }
    public bool AutoStart { get; init; }
    public string StartMode { get; init; } = "OnDemand";
    public int IdleTimeoutMinutes { get; init; } = 20;
    public string IdleTimeoutAction { get; init; } = "Terminate";
    public string IdentityType { get; init; } = "ApplicationPoolIdentity";
    public string[] Applications { get; init; } = [];
    public string Fingerprint { get; init; } = "";
}

public sealed record SiteConfig
{
    public string Name { get; init; } = "";
    public long Id { get; init; }
    public string State { get; init; } = "Unknown";
    public bool ServerAutoStart { get; init; }
    public string PhysicalPath { get; init; } = "";
    public string ApplicationPoolName { get; init; } = "";
    public BindingConfig[] Bindings { get; init; } = [];
}

public sealed record IisSnapshot(DateTimeOffset CollectedAt, PoolConfig[] ApplicationPools, SiteConfig[] Sites);
public sealed record AgentInfo(string ProtocolVersion, string AgentVersion, string MachineName, string Mode, string OsVersion, string IisVersion, bool SharedConfiguration, int[] ReservedPorts);
public sealed record CertificateInfo(string Thumbprint, string Subject, string FriendlyName, DateTimeOffset NotBefore, DateTimeOffset NotAfter, bool HasPrivateKey);
public sealed record ApiError(string Code, string Message);
public sealed record FieldChange(string Field, string Before, string After);
public sealed record ValidationResult(bool Valid, string Fingerprint, string[] AffectedApplications, FieldChange[] Changes, string[] Warnings, ApiError[] Errors, JsonElement? Before = null, JsonElement? Proposed = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? AffectedServices { get; init; }
}
public sealed record ExecutionResult(string State, JsonElement? After, ApiError? Error = null);

public sealed record OperationRecord
{
    public OperationRequest Request { get; init; } = new();
    public string RequestHash { get; init; } = "";
    public string State { get; init; } = OperationStates.Queued;
    public DateTimeOffset AcceptedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public JsonElement? Before { get; init; }
    public JsonElement? After { get; init; }
    public ApiError? Error { get; init; }
    public string? SourceIp { get; init; }
}
