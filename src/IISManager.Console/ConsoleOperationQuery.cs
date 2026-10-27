using System.Globalization;
using System.Text.RegularExpressions;
using IISManager.Contracts;

namespace IISManager.Console;

public sealed record ConsoleOperationSummary(Guid OperationId, string ServerId, string Type, string Target,
    string State, DateTimeOffset AcceptedAt, DateTimeOffset UpdatedAt, string? ErrorCode,
    string? OperatorName = null, string? PersistenceState = null);

public sealed record ConsoleOperationPage(ConsoleOperationSummary[] Items, int Total, int Page, int PageSize);

public sealed record ConsoleOperationQuery(string? ServerId, string? Type, string? State, string? Search,
    DateTimeOffset? From, DateTimeOffset? To, int Page, int PageSize)
{
    private static readonly string[] ParameterNames = ["serverId", "type", "state", "search", "from", "to", "page", "pageSize"];
    private const string TimestampPattern = @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})\z";

    public static bool TryParse(IQueryCollection parameters, out ConsoleOperationQuery? query)
    {
        query = null;
        if (parameters.Any(parameter => !ParameterNames.Contains(parameter.Key, StringComparer.Ordinal) || parameter.Value.Count != 1))
            return false;
        string? Filter(string name) => parameters.TryGetValue(name, out var value) && value[0]?.Length > 0 ? value[0] : null;
        var serverId = Filter("serverId");
        var type = Filter("type");
        var state = Filter("state");
        var search = Filter("search")?.Trim();
        if (serverId is not null && !Regex.IsMatch(serverId, @"^[A-Za-z0-9_-]{1,64}\z") ||
            type is not null && !OperationTypes.All.Contains(type, StringComparer.Ordinal) ||
            state is not null && state is not (OperationStates.Queued or OperationStates.Running or OperationStates.Succeeded or
                OperationStates.Failed or OperationStates.Partial or OperationStates.Unknown or OperationStates.Expired) ||
            Filter("search") is { } rawSearch && (rawSearch.Length > 128 || rawSearch.Any(char.IsControl)))
            return false;
        if (!TryTimestamp(Filter("from"), out var from) || !TryTimestamp(Filter("to"), out var to) || from > to)
            return false;
        var page = 1;
        var pageSize = 20;
        if (parameters.TryGetValue("page", out var pageValue) &&
            (!int.TryParse(pageValue[0], NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1) ||
            parameters.TryGetValue("pageSize", out var sizeValue) &&
            (!int.TryParse(sizeValue[0], NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize is not (10 or 20 or 50)))
            return false;
        query = new ConsoleOperationQuery(serverId, type, state, string.IsNullOrEmpty(search) ? null : search, from, to, page, pageSize);
        return true;
    }

    private static bool TryTimestamp(string? value, out DateTimeOffset? timestamp)
    {
        timestamp = null;
        if (value is null) return true;
        if (value.Length > 40 || !Regex.IsMatch(value, TimestampPattern) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        timestamp = parsed;
        return true;
    }
}

public sealed class ConsoleLedgerUnavailableException : Exception
{
    public ConsoleLedgerUnavailableException() : base("CONSOLE_LEDGER_UNHEALTHY") { }
}
