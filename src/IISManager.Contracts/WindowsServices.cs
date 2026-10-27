namespace IISManager.Contracts;

public static class WindowsServiceStartTypes
{
    public const string Automatic = "Automatic";
    public const string AutomaticDelayed = "AutomaticDelayed";
    public const string Manual = "Manual";
    public const string Disabled = "Disabled";
    public static readonly string[] All = [Automatic, AutomaticDelayed, Manual, Disabled];
}

public sealed record WindowsServiceConfig
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string State { get; init; } = "Unknown";
    public string StartType { get; init; } = "Unknown";
    public string AccountName { get; init; } = "";
    // Only the executable is exposed. Existing command-line arguments may contain secrets.
    public string ExecutablePath { get; init; } = "";
    public uint ProcessId { get; init; }
    public string[] Dependencies { get; init; } = [];
    public string[] DependentServices { get; init; } = [];
    public bool Managed { get; init; }
    public bool CanStart { get; init; }
    public bool CanStop { get; init; }
    public bool CanRestart { get; init; }
    public bool CanUpdateStartType { get; init; }
    public string? UnavailableReason { get; init; }
}

public sealed record WindowsServiceSnapshot(DateTimeOffset CollectedAt, WindowsServiceConfig[] Services,
    bool CanCreate, string? CreationUnavailableReason, string[] AllowedAccounts);

public sealed record WindowsServiceCreateParameters
{
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public string[] Arguments { get; init; } = [];
    public string StartType { get; init; } = WindowsServiceStartTypes.Manual;
    public string AccountName { get; init; } = "VirtualAccount";
    public string[] Dependencies { get; init; } = [];
}

public sealed record WindowsServiceStartTypeParameters
{
    public string StartType { get; init; } = "";
}
