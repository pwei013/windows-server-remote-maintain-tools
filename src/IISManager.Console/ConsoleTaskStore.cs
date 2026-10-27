using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IISManager.Contracts;

namespace IISManager.Console;

public sealed record ConsoleTaskRecord
{
    public static ConsoleTaskRecord CreateIntent(string serverId, OperationRequest request, string? sourceIp,
        AppUserIdentity? user, bool historyEnabled)
    {
        var now = DateTimeOffset.UtcNow;
        return new ConsoleTaskRecord
        {
            ServerId = serverId, Request = request, RequestHash = ConsoleTaskStore.RequestHash(serverId, request),
            SourceIp = sourceIp, AcceptedAt = now, UpdatedAt = now, State = OperationStates.Unknown,
            OperatorUserId = user?.UserId,
            OperatorName = user is null ? null : string.IsNullOrWhiteSpace(user.DisplayName) ? user.Account : user.DisplayName,
            PersistenceState = historyEnabled ? "pending" : "notConfigured",
            HealthFollowUp = OperationHealthFollowUpRules.Eligible(request.Type) ? new() { RequestedAt = now } : null,
            Error = new ApiError("DISPATCH_PENDING", "已持久化操作意向；需要核对 Agent 是否接收。")
        };
    }

    public string ServerId { get; init; } = "";
    public OperationRequest Request { get; init; } = new();
    public string RequestHash { get; init; } = "";
    public string State { get; init; } = OperationStates.Unknown;
    public DateTimeOffset AcceptedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? SourceIp { get; init; }
    public string? OperatorUserId { get; init; }
    public string? OperatorName { get; init; }
    public string? PersistenceState { get; init; }
    public string? PersistenceErrorCode { get; init; }
    public string? PersistencePrimaryId { get; init; }
    public string? PersistenceSyncedHash { get; init; }
    public string? PersistenceAttemptHash { get; init; }
    public OperationRecord? AgentRecord { get; init; }
    public ApiError? Error { get; init; }
    public OperationHealthFollowUp? HealthFollowUp { get; init; }
}

public sealed class ConsoleTaskStore : IDisposable
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private readonly string _directory;
    private readonly FileStream _processLock;
    private readonly Dictionary<Guid, (string Hash, ConsoleOperationSummary Summary)> _summaryCache = new();
    private readonly HashSet<Guid> _healthFollowUpIds = [];
    private long _summaryCacheHits;
    public long SummaryCacheHits => Interlocked.Read(ref _summaryCacheHits);
    private volatile bool _healthy = true;
    public bool Healthy => _healthy;

    public ConsoleTaskStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        _processLock = new FileStream(Path.Combine(_directory, ".console.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!Guid.TryParseExact(name, "D", out var id) || name != id.ToString("D")) throw new InvalidDataException();
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
                _ = Summary(File.ReadAllBytes(file), id);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            _healthy = false;
        }
    }

    public SemaphoreSlim Lock(Guid id) => _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    public async Task<ConsoleTaskRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _persistenceGate.WaitAsync(cancellationToken);
        try { return await ReadAsync(id, cancellationToken); }
        finally { _persistenceGate.Release(); }
    }

    private async Task<ConsoleTaskRecord?> ReadAsync(Guid id, CancellationToken cancellationToken)
    {
        var file = Path.Combine(_directory, id.ToString("D") + ".json");
        if (!File.Exists(file))
        {
            if (_summaryCache.ContainsKey(id)) { _healthy = false; throw new InvalidDataException("CONSOLE_TASK_MISSING"); }
            return null;
        }
        try
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("CONSOLE_TASK_INVALID");
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
            var record = JsonSerializer.Deserialize<ConsoleTaskRecord>(bytes, Protocol.Json);
            if (record is null || !IsValid(record, id)) throw new InvalidDataException("CONSOLE_TASK_INVALID");
            TrackHealthFollowUp(record);
            _summaryCache[id] = (Convert.ToHexString(SHA256.HashData(bytes)), Summarize(record));
            return record;
        }
        catch (OperationCanceledException) { throw; }
        catch { _healthy = false; throw; }
    }

    public async Task SaveAsync(ConsoleTaskRecord record, CancellationToken cancellationToken = default)
    {
        await _persistenceGate.WaitAsync(cancellationToken);
        try { await SaveCoreAsync(record, cancellationToken); }
        finally { _persistenceGate.Release(); }
    }

    private async Task SaveCoreAsync(ConsoleTaskRecord record, CancellationToken cancellationToken)
    {
        if (!_healthy) throw new InvalidOperationException("CONSOLE_LEDGER_UNHEALTHY");
        if (!IsValid(record, record.Request.OperationId)) { _healthy = false; throw new InvalidDataException("CONSOLE_TASK_INVALID"); }
        var file = Path.Combine(_directory, record.Request.OperationId.ToString("D") + ".json");
        var temporary = Path.Combine(_directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, record, Protocol.Json, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, file, overwrite: true);
            var persisted = await ReadAsync(record.Request.OperationId, cancellationToken);
            if (persisted is null || JsonSerializer.Serialize(record, Protocol.Json) != JsonSerializer.Serialize(persisted, Protocol.Json))
                throw new InvalidDataException("CONSOLE_TASK_READBACK_MISMATCH");
        }
        catch { _healthy = false; throw; }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<ConsoleOperationPage> QueryAsync(ConsoleOperationQuery query, CancellationToken cancellationToken = default,
        IReadOnlySet<string>? allowedServerIds = null)
    {
        await _persistenceGate.WaitAsync(cancellationToken);
        try
        {
            if (!_healthy) throw new ConsoleLedgerUnavailableException();
            var summaries = await ReadSummariesAsync(query, allowedServerIds, cancellationToken);
            var offset = ((long)query.Page - 1) * query.PageSize;
            var items = offset >= summaries.Count ? [] : summaries.OrderByDescending(record => record.AcceptedAt)
                .ThenBy(record => record.OperationId.ToString("D"), StringComparer.Ordinal)
                .Skip((int)offset).Take(query.PageSize).ToArray();
            return new ConsoleOperationPage(items, summaries.Count, query.Page, query.PageSize);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            _healthy = false;
            throw new ConsoleLedgerUnavailableException();
        }
        finally { _persistenceGate.Release(); }
    }

    public async Task<ConsoleOperationSummary[]> ExportAsync(ConsoleOperationQuery query, IReadOnlySet<string>? allowedServerIds,
        CancellationToken cancellationToken)
    {
        await _persistenceGate.WaitAsync(cancellationToken);
        try
        {
            if (!_healthy) throw new ConsoleLedgerUnavailableException();
            var rows = await ReadSummariesAsync(query, allowedServerIds, cancellationToken);
            if (rows.Count > 5000) throw new HealthCheckException("OPERATION_EXPORT_TOO_LARGE", "最多导出 5000 条，请缩小时间或服务器范围。", 400);
            return rows.OrderByDescending(row => row.AcceptedAt).ThenBy(row => row.OperationId).ToArray();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { _healthy = false; throw new ConsoleLedgerUnavailableException(); }
        finally { _persistenceGate.Release(); }
    }

    private async Task<List<ConsoleOperationSummary>> ReadSummariesAsync(ConsoleOperationQuery query,
        IReadOnlySet<string>? allowedServerIds, CancellationToken cancellation)
    {
        var rows = new List<ConsoleOperationSummary>(); var seen = new HashSet<Guid>();
        // Always hash the entire ledger: cached summaries must never hide corrupt records or external deletions.
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellation.ThrowIfCancellationRequested(); var name = Path.GetFileNameWithoutExtension(file);
            if (!Guid.TryParseExact(name, "D", out var id) || name != id.ToString("D") ||
                (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || !seen.Add(id)) throw new InvalidDataException();
            var row = Summary(await File.ReadAllBytesAsync(file, cancellation), id);
            if (allowedServerIds is not null && !allowedServerIds.Contains(row.ServerId) ||
                query.ServerId is not null && row.ServerId != query.ServerId || query.Type is not null && row.Type != query.Type ||
                query.State is not null && row.State != query.State || query.From is not null && row.AcceptedAt < query.From ||
                query.To is not null && row.AcceptedAt > query.To || query.Search is not null &&
                !row.Target.Contains(query.Search, StringComparison.OrdinalIgnoreCase) &&
                !(row.OperatorName?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ?? false) &&
                !row.OperationId.ToString("D").Contains(query.Search, StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add(row);
        }
        if (_summaryCache.Keys.Any(id => !seen.Contains(id))) throw new InvalidDataException("CONSOLE_TASK_MISSING");
        return rows;
    }

    private ConsoleOperationSummary Summary(byte[] bytes, Guid id)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (_summaryCache.TryGetValue(id, out var cached) && cached.Hash == hash)
        { Interlocked.Increment(ref _summaryCacheHits); return cached.Summary; }
        var record = JsonSerializer.Deserialize<ConsoleTaskRecord>(bytes, Protocol.Json);
        if (record is null || !IsValid(record, id)) throw new InvalidDataException("CONSOLE_TASK_INVALID");
        TrackHealthFollowUp(record);
        var summary = Summarize(record); _summaryCache[id] = (hash, summary); return summary;
    }

    private static ConsoleOperationSummary Summarize(ConsoleTaskRecord record)
    {
        var target = record.Request.Target;
        var label = target.ServiceName ?? (target.SiteName is not null ? target.SiteName + target.ApplicationPath + target.VirtualDirectoryPath : target.ApplicationPoolName) ?? "";
        return new(record.Request.OperationId, record.ServerId, record.Request.Type, label, record.State,
            record.AcceptedAt, record.UpdatedAt, record.Error?.Code, record.OperatorName, record.PersistenceState);
    }

    public async Task<Guid[]> PendingHistoryIdsAsync(CancellationToken cancellation)
    {
        await _persistenceGate.WaitAsync(cancellation);
        try
        {
            if (!_healthy) throw new ConsoleLedgerUnavailableException();
            var ids = new List<Guid>();
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "D", out var id))
                    throw new InvalidDataException();
                var task = await ReadAsync(id, cancellation) ?? throw new InvalidDataException();
                if (task.OperatorUserId is not null && task.PersistenceState is not (null or "notConfigured") &&
                    (task.State is OperationStates.Queued or OperationStates.Running or OperationStates.Unknown ||
                     task.PersistenceState != "synced")) ids.Add(id);
            }
            return ids.ToArray();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { _healthy = false; throw new ConsoleLedgerUnavailableException(); }
        finally { _persistenceGate.Release(); }
    }

    // The startup ledger pass populates this index. Timer ticks do not rescan the whole ledger.
    public async Task<Guid[]> PendingHealthFollowUpIdsAsync(CancellationToken cancellation = default)
    {
        await _persistenceGate.WaitAsync(cancellation);
        try
        {
            if (!_healthy) throw new ConsoleLedgerUnavailableException();
            return _healthFollowUpIds.ToArray();
        }
        finally { _persistenceGate.Release(); }
    }
    private void TrackHealthFollowUp(ConsoleTaskRecord record)
    {
        if (record.HealthFollowUp?.State is "pending" or "running") _healthFollowUpIds.Add(record.Request.OperationId);
        else _healthFollowUpIds.Remove(record.Request.OperationId);
    }

    private static bool IsValid(ConsoleTaskRecord record, Guid id) => id != Guid.Empty && record.Request is not null &&
        record.Request.OperationId == id && record.AcceptedAt != default && record.UpdatedAt != default &&
        record.ServerId is not null && System.Text.RegularExpressions.Regex.IsMatch(record.ServerId, @"^[A-Za-z0-9_-]{1,64}\z") &&
        record.Request.Target is not null &&
        ValidText(record.OperatorUserId, 128) && ValidText(record.OperatorName, 256) &&
        (record.PersistenceState is null or "pending" or "synced" or "unknown" or "notConfigured") &&
        ValidText(record.PersistencePrimaryId, 128) && ValidText(record.PersistenceErrorCode, 128) &&
        ValidHash(record.PersistenceSyncedHash) && ValidHash(record.PersistenceAttemptHash) &&
        record.Request.Parameters.ValueKind == JsonValueKind.Object &&
        OperationTypes.All.Contains(record.Request.Type, StringComparer.Ordinal) &&
        (record.State is OperationStates.Queued or OperationStates.Running or OperationStates.Succeeded or
            OperationStates.Failed or OperationStates.Partial or OperationStates.Unknown or OperationStates.Expired) &&
        record.RequestHash == RequestHash(record.ServerId, record.Request) &&
        OperationHealthFollowUpRules.Valid(record.HealthFollowUp, record.Request.Type);

    private static bool ValidText(string? text, int maximum) => text is null || text.Length <= maximum && !text.Any(char.IsControl);
    private static bool ValidHash(string? hash) => hash is null || hash.Length == 64 && hash.All(Uri.IsHexDigit);

    public void Dispose()
    {
        _processLock.Dispose();
        _persistenceGate.Dispose();
        foreach (var gate in _locks.Values) gate.Dispose();
    }

    public static string RequestHash(string serverId, OperationRequest request)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request, Protocol.Json));
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(writer, document.RootElement);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serverId + "\n").Concat(buffer.ToArray()).ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var child in element.EnumerateArray()) WriteCanonical(writer, child);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
