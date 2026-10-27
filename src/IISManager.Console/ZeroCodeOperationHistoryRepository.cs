using System.Globalization;
using System.Text.Json;
using IISManager.Contracts;

namespace IISManager.Console;

/// <summary>Field and method codes independently read back from application 1001's operation log.</summary>
public sealed class ZeroCodeOperationHistoryRepository : IOperationHistoryRepository, IDisposable
{
    public const string QueryMethod = "QueryOperationHistory";
    public const string CreateMethod = "CreateOperationHistory";
    public const string UpdateMethod = "UpdateOperationHistory";
    public const string OperationField = "operationNumber";
    public const string ServerField = "fUWUQIBIANHAO";
    public const string TypeField = "cAOZUOLEIXING";
    public const string TargetField = "cAOZUOMUBIAO";
    public const string StateField = "operatingStatus";
    public const string OperatorIdField = "cAOZUORENBIANHAO";
    public const string OperatorNameField = "cAOZUORENXINGMING";
    public const string SourceIpField = "lAIYUANIP";
    public const string AcceptedField = "submissionTime";
    public const string UpdatedField = "gENGXINSHIJIAN";
    public const string HashField = "qINGQIUZHIWEN";
    public const string ErrorField = "cUOWUDAIMA";
    private readonly ZeroCodeSiteAnnotationRepository _client;
    private readonly TimeSpan _timeout;
    public bool Enabled { get; }

    public ZeroCodeOperationHistoryRepository(ConsoleOptions options, bool authenticationConfigured,
        HttpMessageHandler? handler = null)
    {
        Enabled = options.OperationHistory.Enabled && options.UserAuthentication.Enabled && authenticationConfigured;
        var source = options.SiteAnnotations;
        _timeout = TimeSpan.FromSeconds(source.TimeoutSeconds);
        // Authentication and both repositories use the same application connection; no new secrets are configured.
        _client = new ZeroCodeSiteAnnotationRepository(new SiteAnnotationZeroCodeOptions
        {
            Enabled = Enabled, ApplicationId = source.ApplicationId, OpenApiBaseUrl = source.OpenApiBaseUrl,
            OpenApiAppId = source.OpenApiAppId, PublicKeyBase64 = source.PublicKeyBase64, TimeoutSeconds = source.TimeoutSeconds
        }, handler);
    }

    public async Task<StoredOperationHistory?> FindAsync(Guid operationId, string token, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(_timeout);
        var matches = new List<StoredOperationHistory>();
        var primaries = new HashSet<string>(StringComparer.Ordinal);
        int? expectedTotal = null; var received = 0;
        for (var page = 1; page <= 50; page++)
        {
            using var response = await CallAsync(QueryMethod, new Dictionary<string, object?>
            { [OperationField] = operationId.ToString("D"), ["pageNumber"] = page, ["pageSize"] = 200 }, token, deadline.Token);
            var root = response.RootElement;
            if (!root.TryGetProperty("ReturnValue", out var rows) || rows.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("TotalCount", out var total) ||
                !int.TryParse(total.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                count is < 0 or > 10000 || rows.GetArrayLength() != Math.Min(200, count - received)) throw Unavailable();
            expectedTotal ??= count;
            if (count != expectedTotal) throw Unavailable();
            foreach (var row in rows.EnumerateArray())
            {
                var primaryId = Text(row, "primaryId", 128);
                primaries.Add(primaryId);
                if (Text(row, OperationField, 36) != operationId.ToString("D")) continue;
                var state = Text(row, StateField, 32);
                var hash = Text(row, HashField, 64);
                if (state is not (OperationStates.Queued or OperationStates.Running or OperationStates.Succeeded or
                        OperationStates.Failed or OperationStates.Partial or OperationStates.Unknown or OperationStates.Expired) ||
                    hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw Unavailable();
                matches.Add(new StoredOperationHistory(primaryId, new OperationHistorySnapshot(operationId,
                    Text(row, ServerField, 64), Text(row, TypeField, 64), Text(row, TargetField, 256), state,
                    Optional(row, OperatorIdField, 128), Optional(row, OperatorNameField, 256), Optional(row, SourceIpField, 64),
                    Timestamp(row, AcceptedField), Timestamp(row, UpdatedField), hash, Optional(row, ErrorField, 128))));
            }
            received += rows.GetArrayLength();
            if (matches.Count > 1) throw new OperationHistoryException("OPERATION_HISTORY_DUPLICATE");
            if (received == count)
            {
                if (primaries.Count != count) throw Unavailable();
                return matches.SingleOrDefault();
            }
        }
        throw Unavailable();
    }

    public async Task CreateAsync(OperationHistorySnapshot snapshot, string token, CancellationToken cancellation)
    {
        using var response = await CallAsync(CreateMethod, Fields(snapshot), token, cancellation);
        // The mutation response is only transport/business evidence. The synchronizer always queries exact values.
    }

    public async Task UpdateAsync(string primaryId, OperationHistorySnapshot snapshot, string token, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(primaryId) || primaryId.Length > 128 || primaryId.Any(char.IsControl))
            throw new OperationHistoryException("OPERATION_HISTORY_PRIMARY_ID_INVALID");
        using var response = await CallAsync(UpdateMethod, new Dictionary<string, object?>
        {
            ["primaryId"] = primaryId, [StateField] = snapshot.State,
            [UpdatedField] = snapshot.UpdatedAt.ToString("O", CultureInfo.InvariantCulture), [ErrorField] = snapshot.ErrorCode ?? ""
        }, token, cancellation);
    }

    private async Task<JsonDocument> CallAsync(string method, Dictionary<string, object?> parameters, string token,
        CancellationToken cancellation)
    {
        if (!Enabled) throw new OperationHistoryException("OPERATION_HISTORY_NOT_CONFIGURED");
        if (string.IsNullOrEmpty(token)) throw new OperationHistoryException("APP_USER_SESSION_EXPIRED");
        try { return await _client.CallAsync(method, parameters, cancellation, token); }
        catch (AppUserAuthenticationException exception) { throw new OperationHistoryException(exception.Code); }
        catch (SiteAnnotationException) { throw Unavailable(); }
    }

    public static Dictionary<string, object?> Fields(OperationHistorySnapshot snapshot) => new()
    {
        [OperationField] = snapshot.OperationId.ToString("D"), [ServerField] = snapshot.ServerId,
        [TypeField] = snapshot.Type, [TargetField] = snapshot.Target, [StateField] = snapshot.State,
        [OperatorIdField] = snapshot.OperatorUserId ?? "", [OperatorNameField] = snapshot.OperatorName ?? "",
        [SourceIpField] = snapshot.SourceIp ?? "", [AcceptedField] = snapshot.AcceptedAt.ToString("O", CultureInfo.InvariantCulture),
        [UpdatedField] = snapshot.UpdatedAt.ToString("O", CultureInfo.InvariantCulture), [HashField] = snapshot.RequestHash,
        [ErrorField] = snapshot.ErrorCode ?? ""
    };

    private static string Text(JsonElement row, string field, int maximum)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(field, out var value) ||
            value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) throw Unavailable();
        var text = value.ToString();
        if (text.Length is 0 || text.Length > maximum || text.Any(char.IsControl)) throw Unavailable();
        return text;
    }

    private static string? Optional(JsonElement row, string field, int maximum)
    {
        if (!row.TryGetProperty(field, out var value)) throw Unavailable();
        if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.String && value.GetString() == "") return null;
        return Text(row, field, maximum);
    }

    private static DateTimeOffset Timestamp(JsonElement row, string field)
    {
        var text = Text(row, field, 64);
        if (!DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            throw Unavailable();
        return timestamp;
    }

    private static OperationHistoryException Unavailable() => new("OPERATION_HISTORY_UNAVAILABLE");
    public void Dispose() => _client.Dispose();
}
