using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using IISManager.Contracts;

namespace IISManager.Agent;

public sealed class FrebReadBudget
{
    public int RemainingBytes { get; set; } = 8 * 1024 * 1024;
    public int RemainingFiles { get; set; } = 64;
    public int RemainingTraces { get; set; } = 64;
    public int RemainingEvents { get; set; } = 1024;
}
public sealed record FrebTraceParseResult(FrebTraceSummary? Trace, string? ErrorCode, bool Truncated = false);

/// <summary>Reads bounded configured-site files. Only numeric values and exact mapped constants leave the XML parser.</summary>
public sealed class FrebTraceReader
{
    public const int MaximumFileBytes = 2 * 1024 * 1024;
    public const int MaximumEventsPerTrace = 96;
    public const int MaximumTracesPerSite = 12;
    private const int MaximumScannedFiles = 128;
    private const int MaximumXmlNodes = 50000;
    private const int MaximumScannedEvents = 4096;
    private const string FrebNamespace = "http://schemas.microsoft.com/win/2006/06/iis/freb";
    private readonly byte[] _identityKey = RandomNumberGenerator.GetBytes(32);
    private static readonly Regex FileName = new("^fr[0-9]{1,12}\\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] Modules = ["AnonymousAuthenticationModule", "WindowsAuthenticationModule", "BasicAuthenticationModule",
        "DigestAuthenticationModule", "UrlAuthorizationModule", "FileAuthorizationModule", "RequestFilteringModule", "StaticFileModule",
        "DefaultDocumentModule", "DirectoryListingModule", "HttpRedirectionModule", "RewriteModule", "ApplicationRequestRouting",
        "ApplicationRequestRoutingModule", "FastCgiModule", "CgiModule", "IsapiModule", "IsapiFilterModule", "ManagedPipelineHandler",
        "AspNetCoreModule", "AspNetCoreModuleV2", "ConfigurationValidationModule", "FailedRequestsTracingModule", "RequestMonitorModule",
        "HttpLoggingModule", "CustomLoggingModule", "CustomErrorModule", "ProtocolSupportModule", "WebSocketModule",
        "Session", "FormsAuthentication", "UrlAuthorization", "FileAuthorization", "OutputCache", "SessionStateModule"];
    private static readonly string[] Notifications = ["BEGIN_REQUEST", "AUTHENTICATE_REQUEST", "AUTHORIZE_REQUEST", "RESOLVE_REQUEST_CACHE",
        "MAP_REQUEST_HANDLER", "ACQUIRE_REQUEST_STATE", "PRE_EXECUTE_REQUEST_HANDLER", "EXECUTE_REQUEST_HANDLER", "RELEASE_REQUEST_STATE",
        "UPDATE_REQUEST_CACHE", "LOG_REQUEST", "END_REQUEST", "SEND_RESPONSE"];
    private static readonly string[] EventNames = ["GENERAL_REQUEST_START", "GENERAL_REQUEST_END", "NOTIFY_MODULE_START", "NOTIFY_MODULE_END",
        "MODULE_SET_RESPONSE_ERROR_STATUS", "PRE_BEGIN_REQUEST_START", "PRE_BEGIN_REQUEST_END", "HANDLER_CHANGED", "GENERAL_SET_RESPONSE_HEADER",
        "GENERAL_FLUSH_RESPONSE_START", "GENERAL_FLUSH_RESPONSE_END", "GENERAL_SEND_CUSTOM_ERROR", "GENERAL_REQUEST_DISCONNECT",
        "URL_REWRITE_START", "URL_REWRITE_END", "RULE_EVALUATION_START", "RULE_EVALUATION_END", "PATTERN_MATCH", "REWRITE_ACTION",
        "CONDITIONS_EVALUATION_START", "CONDITIONS_EVALUATION_END", "CONDITION_EVALUATION", "LOG_FILE_MAX_SIZE_TRUNCATE"];
    private static readonly HashSet<string> SensitiveEvents = new(["GENERAL_REQUEST_HEADERS", "GENERAL_RESPONSE_HEADERS",
        "GENERAL_RESPONSE_ENTITY_BUFFER", "GENERAL_REQUEST_ENTITY_BUFFER"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> DataFields = new(["ModuleName", "Notification", "HttpStatus", "HttpSubStatus", "ErrorCode",
        "Win32Status", "TimeTaken", "RequestVerb", "SiteId"], StringComparer.Ordinal);

    public FrebSiteDiagnostics ReadSite(FrebSiteConfiguration site, DateTimeOffset now, FrebReadBudget budget, CancellationToken cancellation = default)
    {
        if (!site.Enabled) return new(site.SiteId, site.SiteName, "disabled", "FREB_DISABLED", false, []);
        var traces = new List<FrebTraceSummary>();
        string? warning = null; var truncated = false;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (site.SiteId <= 0) throw new FrebReadException("FREB_PATH_REJECTED");
            if (budget.RemainingBytes <= 0) throw new FrebReadException("FREB_BYTE_BUDGET_REACHED");
            if (budget.RemainingFiles <= 0 || budget.RemainingTraces <= 0) throw new FrebReadException("FREB_TRACE_LIMIT");
            var root = FrebLogFileBoundary.LocalPath(site.Directory);
            var directory = FrebLogFileBoundary.LocalPath(Path.Combine(root, "W3SVC" + site.SiteId.ToString(CultureInfo.InvariantCulture)));
            using var boundary = new FrebLogFileBoundary(directory);
            var scanned = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Take(MaximumScannedFiles + 1).ToArray();
            if (scanned.Length > MaximumScannedFiles) { warning = "FREB_SCAN_LIMIT"; truncated = true; }
            var files = scanned.Take(MaximumScannedFiles).Where(path => FileName.IsMatch(Path.GetFileName(path)))
                .Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal).ToArray();
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                if (traces.Count == MaximumTracesPerSite || budget.RemainingFiles <= 0 || budget.RemainingTraces <= 0)
                { warning ??= "FREB_TRACE_LIMIT"; truncated = true; break; }
                // The XML's event time is independently checked below; mtime is only a cheap candidate filter.
                if (file.LastWriteTimeUtc < now.AddHours(-24).UtcDateTime) continue;
                budget.RemainingFiles--;
                try
                {
                    using var pinned = boundary.Open(file.Name);
                    var length = pinned.Stream.Length;
                    if (length is <= 0 or > MaximumFileBytes) throw new FrebReadException("FREB_FILE_TOO_LARGE");
                    if (length > budget.RemainingBytes) { warning ??= "FREB_BYTE_BUDGET_REACHED"; truncated = true; break; }
                    var bytes = new byte[checked((int)length)]; budget.RemainingBytes -= bytes.Length; pinned.Stream.ReadExactly(bytes);
                    cancellation.ThrowIfCancellationRequested(); pinned.VerifyUnchanged();
                    var identity = Encoding.UTF8.GetBytes(site.SiteId.ToString(CultureInfo.InvariantCulture) + "|" + file.Name + "|" +
                        pinned.Identity.Volume.ToString(CultureInfo.InvariantCulture) + "|" + pinned.Identity.Index.ToString(CultureInfo.InvariantCulture) + "|" + Convert.ToHexString(SHA256.HashData(bytes)));
                    var traceId = Convert.ToHexString(HMACSHA256.HashData(_identityKey, identity));
                    var parsed = ParseXml(bytes, site.SiteId, traceId, now, cancellation, Math.Min(MaximumEventsPerTrace, Math.Max(0, budget.RemainingEvents)));
                    if (parsed.ErrorCode is not null) { warning ??= parsed.ErrorCode; truncated = true; }
                    if (parsed.Trace is not null)
                    {
                        traces.Add(parsed.Trace); budget.RemainingTraces--; budget.RemainingEvents -= parsed.Trace.Events.Length;
                        truncated |= parsed.Trace.Truncated;
                    }
                }
                catch (FrebReadException exception) { warning ??= exception.Code; truncated = true; }
                catch (UnauthorizedAccessException) { warning ??= "FREB_ACCESS_DENIED"; truncated = true; }
                catch (IOException) { warning ??= "FREB_LOG_UNAVAILABLE"; truncated = true; }
            }
            var state = warning is null ? truncated ? "partial" : "available" : traces.Count > 0 ? "partial" : "unavailable";
            return new(site.SiteId, site.SiteName, state, warning ?? (truncated ? "FREB_TRACE_TRUNCATED" : traces.Count == 0 ? "FREB_NO_RECENT_TRACES" : null),
                truncated, traces.OrderByDescending(trace => trace.StartedAt).ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (FrebReadException exception) { return Failure(exception.Code); }
        catch (DirectoryNotFoundException) { return Failure("FREB_LOG_DIRECTORY_NOT_FOUND"); }
        catch (UnauthorizedAccessException) { return Failure("FREB_ACCESS_DENIED"); }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException or OverflowException)
        { return Failure("FREB_LOG_UNAVAILABLE"); }
        FrebSiteDiagnostics Failure(string code) => new(site.SiteId, site.SiteName, traces.Count > 0 ? "partial" : "unavailable", code,
            truncated || code is "FREB_BYTE_BUDGET_REACHED" or "FREB_TRACE_LIMIT", traces.ToArray());
    }

    public static FrebTraceParseResult ParseXml(byte[] bytes, long expectedSiteId, string traceId, DateTimeOffset now,
        CancellationToken cancellation = default, int eventLimit = MaximumEventsPerTrace)
    {
        if (bytes.Length is <= 0 or > MaximumFileBytes) return new(null, "FREB_FILE_TOO_LARGE", true);
        if (expectedSiteId <= 0 || traceId.Length != 64 || !traceId.All(Uri.IsHexDigit)) return new(null, "FREB_SCOPE_MISMATCH");
        eventLimit = Math.Clamp(eventLimit, 0, MaximumEventsPerTrace);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaximumFileBytes, MaxCharactersFromEntities = 0, IgnoreComments = true, IgnoreProcessingInstructions = true });
            reader.MoveToContent();
            if (reader.LocalName != "failedRequest" || reader.NamespaceURI is not ("" or FrebNamespace) ||
                !long.TryParse(reader.GetAttribute("siteId"), NumberStyles.None, CultureInfo.InvariantCulture, out var siteId) || siteId != expectedSiteId)
                return new(null, "FREB_SCOPE_MISMATCH");
            var status = HttpStatus(reader.GetAttribute("statusCode") ?? reader.GetAttribute("triggerStatusCode"));
            var timing = Milliseconds(reader.GetAttribute("timeTaken"));
            var method = Map(reader.GetAttribute("verb"), ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT"]);
            var first = new List<ParsedEvent>(); var tail = new Queue<ParsedEvent>();
            ParsedEvent? failure = null; DateTimeOffset? started = null, earliest = null; var eventCount = 0; var nodes = 1; var truncated = false;
            var prefixLimit = Math.Min(24, eventLimit / 2); var tailLimit = eventLimit - prefixLimit;
            while (reader.Read())
            {
                Check(reader, cancellation, ref nodes);
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1 || reader.LocalName != "Event" || !EventNamespace(reader.NamespaceURI)) continue;
                if (++eventCount > MaximumScannedEvents) throw new FrebReadException("FREB_XML_LIMIT_REACHED");
                using var subtree = reader.ReadSubtree();
                var value = ParseEvent(subtree, eventCount, cancellation, ref nodes);
                if (value.SiteId is not null && value.SiteId != expectedSiteId) return new(null, "FREB_SCOPE_MISMATCH");
                if (value.When is { } when && (earliest is null || when < earliest)) earliest = when;
                if (value.Name == "GENERAL_REQUEST_START") { started ??= value.When; if (method == "OTHER") method = value.Method; }
                if (value.Name == "LOG_FILE_MAX_SIZE_TRUNCATE") truncated = true;
                if (value.Name == "MODULE_SET_RESPONSE_ERROR_STATUS" && (value.Status is >= 400 || value.Win32 is > 0)) failure = value;
                if (SensitiveEvents.Contains(value.RawName)) continue;
                if (first.Count < prefixLimit) first.Add(value);
                else if (tailLimit > 0) { if (tail.Count == tailLimit) { tail.Dequeue(); truncated = true; } tail.Enqueue(value); }
                else truncated = true;
            }
            started ??= earliest;
            if (started is null) return new(null, "FREB_XML_INVALID");
            if (started < now.AddHours(-24) || started > now) return new(null, null);
            var stored = first.Concat(tail).ToList();
            if (failure is not null && eventLimit > 0 && !stored.Any(value => value.Sequence == failure.Sequence))
            { if (stored.Count == eventLimit) stored.RemoveAt(stored.Count - 1); stored.Add(failure); truncated = true; }
            var events = stored.OrderBy(value => value.Sequence).Select(value => new FrebPipelineEvent(value.Sequence,
                value.When is { } when && when >= started.Value && when <= now ? when : null,
                value.When is { } time && time >= started.Value && time <= now && time - started.Value <= TimeSpan.FromHours(24) ? Math.Round((time - started.Value).TotalMilliseconds, 3) : null,
                value.Name, value.Module, value.Notification, value.Status, value.SubStatus, value.Win32)).ToArray();
            return new(new(traceId, started.Value, status.Status ?? failure?.Status, status.SubStatus ?? failure?.SubStatus, failure?.Win32,
                timing, method, failure?.Module ?? "NONE", failure?.Notification ?? "NONE", truncated, events), null, truncated);
        }
        catch (OperationCanceledException) { throw; }
        catch (FrebReadException exception) { return new(null, exception.Code, true); }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException or ArgumentException or OverflowException)
        { return new(null, "FREB_XML_INVALID"); }
    }

    private sealed record ParsedEvent(int Sequence, DateTimeOffset? When, string Name, string RawName, string Module,
        string Notification, int? Status, int? SubStatus, long? Win32, long? SiteId, string Method);
    private static ParsedEvent ParseEvent(XmlReader reader, int sequence, CancellationToken cancellation, ref int nodes)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        DateTimeOffset? when = null; string? section = null, capture = null; var captureDepth = -1; var text = new StringBuilder(); var overflow = false;
        while (reader.Read())
        {
            Check(reader, cancellation, ref nodes);
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Depth == 1 && EventNamespace(reader.NamespaceURI)) section = reader.LocalName;
                if (reader.Depth != 2) continue;
                if (section == "System" && reader.LocalName == "TimeCreated" && EventNamespace(reader.NamespaceURI)) when = Timestamp(reader.GetAttribute("SystemTime"));
                var name = section == "EventData" && reader.LocalName == "Data" && EventNamespace(reader.NamespaceURI) && reader.GetAttribute("Name") is { } field && DataFields.Contains(field) ? field :
                    section == "RenderingInfo" && reader.LocalName == "Opcode" && EventNamespace(reader.NamespaceURI) ? "@opcode" :
                    section == "RenderingInfo" && reader.LocalName == "Description" && reader.NamespaceURI == FrebNamespace && reader.GetAttribute("Data") == "Notification" ? "@notification" : null;
                if (name is null) continue;
                if (values.ContainsKey(name)) throw new FrebReadException("FREB_XML_INVALID");
                if (reader.IsEmptyElement) { values[name] = ""; continue; }
                capture = name; captureDepth = reader.Depth; text.Clear(); overflow = false;
            }
            else if (capture is not null && reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
            {
                if (reader.Depth != captureDepth + 1) { overflow = true; continue; }
                if (text.Length + reader.Value.Length > 128) overflow = true;
                else if (!overflow) text.Append(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (capture is not null && reader.Depth == captureDepth) { values[capture] = overflow ? "" : text.ToString(); capture = null; }
                if (reader.Depth == 1) section = null;
            }
        }
        var raw = values.GetValueOrDefault("@opcode") ?? "";
        var notification = Map(values.GetValueOrDefault("@notification"), Notifications);
        if (notification == "OTHER") notification = values.GetValueOrDefault("Notification") switch { "1" => "BEGIN_REQUEST", "128" => "EXECUTE_REQUEST_HANDLER", _ => "OTHER" };
        var status = HttpStatus(values.GetValueOrDefault("HttpStatus"));
        var sub = Unsigned(values.GetValueOrDefault("HttpSubStatus"), 9999);
        return new(sequence, when, Map(raw, EventNames), raw, Map(values.GetValueOrDefault("ModuleName"), Modules), notification,
            status.Status, sub is null ? status.SubStatus : (int)sub, Unsigned(values.GetValueOrDefault("Win32Status") ?? values.GetValueOrDefault("ErrorCode"), uint.MaxValue),
            Unsigned(values.GetValueOrDefault("SiteId"), long.MaxValue), Map(values.GetValueOrDefault("RequestVerb"), ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT"]));
    }
    private static bool EventNamespace(string value) => value is "http://schemas.microsoft.com/win/2004/08/events/event" or "https://schemas.microsoft.com/win/2004/08/events/event";
    private static void Check(XmlReader reader, CancellationToken cancellation, ref int nodes)
    {
        cancellation.ThrowIfCancellationRequested();
        if (++nodes > MaximumXmlNodes || reader.Depth > 32) throw new FrebReadException("FREB_XML_LIMIT_REACHED");
    }
    private static string Map(string? input, string[] allowed) => input is { Length: > 0 and <= 128 }
        ? allowed.FirstOrDefault(value => string.Equals(value, input, StringComparison.OrdinalIgnoreCase)) ?? "OTHER" : "OTHER";
    private static (int? Status, int? SubStatus) HttpStatus(string? value)
    {
        if (value is not { Length: >= 3 and <= 8 }) return (null, null);
        var parts = value.Split('.');
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var status) || status is < 100 or > 599) return (null, null);
        if (parts.Length == 1) return (status, null);
        return int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sub) && sub is >= 0 and <= 9999 ? (status, sub) : (null, null);
    }
    private static long? Unsigned(string? value, long maximum)
    {
        if (value is not { Length: > 0 and <= 20 }) return null;
        var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return long.TryParse(hex ? value[2..] : value, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 0 && number <= maximum ? number : null;
    }
    private static double? Milliseconds(string? value) => value is { Length: > 0 and <= 32 } &&
        double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) && parsed is >= 0 and <= 86400000 ? parsed : null;
    private static DateTimeOffset? Timestamp(string? value)
    {
        if (value is not { Length: > 0 and <= 40 }) return null;
        try { return XmlConvert.ToDateTimeOffset(value).ToUniversalTime(); }
        catch (FormatException) { return null; }
    }
}
