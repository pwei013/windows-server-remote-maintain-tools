namespace IISManager.Contracts;

// FREB is projected into a numeric/constant-only trace view. Raw XML and request data are never returned.
public sealed record FrebDiagnosticsSnapshot(DateTimeOffset CollectedAt, string Mode, int CacheSeconds,
    string State, string? ErrorCode, FrebSiteDiagnostics[] Sites);
public sealed record FrebSiteDiagnostics(long SiteId, string SiteName, string State, string? ErrorCode,
    bool Truncated, FrebTraceSummary[] Traces);
public sealed record FrebTraceSummary(string TraceId, DateTimeOffset StartedAt, int? Status, int? SubStatus,
    long? Win32Status, double? TimeTakenMilliseconds, string Method, string FailureModule,
    string FailureNotification, bool Truncated, FrebPipelineEvent[] Events);
public sealed record FrebPipelineEvent(int Sequence, DateTimeOffset? TimeCreated, double? OffsetMilliseconds,
    string EventName, string Module, string Notification, int? Status, int? SubStatus, long? Win32Status);
