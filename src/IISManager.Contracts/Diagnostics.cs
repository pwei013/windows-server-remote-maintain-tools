namespace IISManager.Contracts;

public sealed record DiagnosticModuleStatus(string Module, string State, string? ErrorCode = null, bool Truncated = false);
public sealed record DiskResourceUsage(string Name, long? TotalBytes, long? AvailableBytes, string? ErrorCode = null);
public sealed record ServerResourceMetrics(double? CpuPercent, long? TotalMemoryBytes, long? AvailableMemoryBytes,
    int LogicalProcessors, DiskResourceUsage[] Disks);
public sealed record ProcessDiagnostics(string Kind, string Name, string State, int? ProcessId, double? CpuPercent,
    long? WorkingSetBytes, long? PrivateMemoryBytes, DateTimeOffset? StartedAt, double? UptimeSeconds, string? ErrorCode = null);
public sealed record DiagnosticEvent(DateTimeOffset? TimeCreated, string LogName, string Provider, int EventId,
    int? Level, long? RecordId, string Summary);
public sealed record IisErrorSummary(int Status, int? SubStatus, long? Win32Status, int Count,
    double? AverageMilliseconds, long? MaximumMilliseconds);
public sealed record IisLogDiagnostics(string SiteName, string State, DateTimeOffset WindowStart, DateTimeOffset WindowEnd,
    int SampledFiles, int SampledLines, bool Truncated, string? ErrorCode, IisErrorSummary[] Errors);
public sealed record DiagnosticsSnapshot(DateTimeOffset CollectedAt, string Mode, int CacheSeconds,
    ServerResourceMetrics? Resources, ProcessDiagnostics[]? Processes, DiagnosticEvent[]? Events,
    IisLogDiagnostics[]? IisErrors, DiagnosticModuleStatus[] Modules);
