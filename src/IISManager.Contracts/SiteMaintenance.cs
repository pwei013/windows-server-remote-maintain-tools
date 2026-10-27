namespace IISManager.Contracts;

// Missing fields preserve the current value. Explicit nulls are rejected by the validator.
public sealed record SiteUpdateParameters
{
    public string? PhysicalPath { get; init; }
    public string? ApplicationPoolName { get; init; }
    public BindingConfig[]? Bindings { get; init; }
}

public sealed record ApplicationParameters
{
    public string? PhysicalPath { get; init; }
    public string? ApplicationPoolName { get; init; }
}

public sealed record VirtualDirectoryParameters
{
    public string? PhysicalPath { get; init; }
    // Update only: a virtual directory inherits its owner's pool. This changes that
    // application and therefore all directories owned by it, not an independent pool.
    public string? OwnerApplicationPoolName { get; init; }
}
