namespace IISManager.Contracts;

public sealed record SiteTopologySnapshot(DateTimeOffset CollectedAt, SiteTopology[] Sites, PoolConfig[] ApplicationPools);

public sealed record SiteTopology
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string State { get; init; } = "Unknown";
    public BindingConfig[] Bindings { get; init; } = [];
    public ApplicationTopology[] Applications { get; init; } = [];
}

public sealed record ApplicationTopology
{
    public string Path { get; init; } = "/";
    public string ApplicationPoolName { get; init; } = "";
    public string PhysicalPath { get; init; } = "";
    public string EnabledProtocols { get; init; } = "";
    public VirtualDirectoryTopology[] VirtualDirectories { get; init; } = [];
}

public sealed record VirtualDirectoryTopology
{
    public string Path { get; init; } = "/";
    public string PhysicalPath { get; init; } = "";
}
