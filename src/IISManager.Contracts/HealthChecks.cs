namespace IISManager.Contracts;

public sealed record HealthProbeRequest
{
    public string SiteName { get; init; } = "";
    public string ApplicationPath { get; init; } = "/";
    public string Url { get; init; } = "";
    public int ExpectedStatusCode { get; init; } = 200;
    public int TimeoutSeconds { get; init; } = 5;
}

public sealed record HealthProbeResult
{
    public string State { get; init; } = "unavailable";
    public DateTimeOffset CheckedAt { get; init; }
    public int? StatusCode { get; init; }
    public long? ElapsedMilliseconds { get; init; }
    public string? ErrorCode { get; init; }
}
