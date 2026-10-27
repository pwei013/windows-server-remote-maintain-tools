using System.Text.Json;
using System.Text.RegularExpressions;

namespace IISManager.Contracts;

// Shared admission checks run before either durable ledger receives a service request.
public static class WindowsServiceRequestSafety
{
    private static readonly Regex Sensitive = new(
        @"(?i)password|passwd|(?:^|[-/])pwd(?:$|[\s=:])|token|secret|credential|private[\s_-]*key|connection[\s_-]*string|(?:api|access)[\s_-]*key|authorization|bearer\s|-----BEGIN|://[^\s/]+@|[?&](?:key|sig|signature|auth|code)\s*=",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly string[] CreateFields = ["displayName", "description", "executablePath", "arguments", "startType", "accountName", "dependencies"];

    public static bool ValidName(string? name) => name is { Length: > 0 and <= 128 } && name == name.Trim() &&
        !name.EndsWith('.') && !name.Any(c => char.IsControl(c) || "/\\:*?\"<>|".Contains(c));

    public static bool ContainsSensitiveArguments(string[]? arguments)
    {
        if (arguments is null) return true;
        try { return arguments.Any(argument => argument is null || Sensitive.IsMatch(argument)); }
        catch (RegexMatchTimeoutException) { return true; }
    }

    public static bool IsValid(OperationRequest request)
    {
        if (!OperationTypes.WindowsServices.Contains(request.Type, StringComparer.Ordinal) || request.Target is null ||
            !ValidName(request.Target.ServiceName) || request.Target.ApplicationPoolName is not null || request.Target.SiteName is not null ||
            request.Target.ApplicationPath is not null || request.Target.VirtualDirectoryPath is not null ||
            request.Parameters.ValueKind != JsonValueKind.Object) return false;
        var allowed = request.Type == OperationTypes.ServiceCreate ? CreateFields
            : request.Type == OperationTypes.ServiceUpdateStartType ? ["startType"] : Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in request.Parameters.EnumerateObject())
            if (!allowed.Contains(field.Name, StringComparer.OrdinalIgnoreCase) || !seen.Add(field.Name) || field.Value.ValueKind == JsonValueKind.Null)
                return false;
        try
        {
            if (request.Type == OperationTypes.ServiceUpdateStartType)
                return request.Parameters.Deserialize<WindowsServiceStartTypeParameters>(Protocol.Json) is { } update &&
                    WindowsServiceStartTypes.All.Contains(update.StartType, StringComparer.Ordinal);
            if (request.Type != OperationTypes.ServiceCreate) return true;
            var p = request.Parameters.Deserialize<WindowsServiceCreateParameters>(Protocol.Json);
            return p is not null && p.DisplayName is { Length: <= 256 } && !p.DisplayName.Any(char.IsControl) &&
                p.Description is { Length: <= 1024 } && !p.Description.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t') &&
                p.ExecutablePath is { Length: > 0 and <= 2048 } && !p.ExecutablePath.Any(char.IsControl) &&
                p.Arguments is { Length: <= 32 } && p.Arguments.All(a => a is { Length: <= 2048 } && !a.Any(char.IsControl)) &&
                !ContainsSensitiveArguments(p.Arguments) &&
                QuotedLength(p.ExecutablePath) + p.Arguments.Sum(a => 1 + QuotedLength(a)) <= 32760 &&
                WindowsServiceStartTypes.All.Contains(p.StartType, StringComparer.Ordinal) &&
                p.AccountName is "VirtualAccount" or "LocalService" or "NetworkService" or "LocalSystem" &&
                p.Dependencies is { Length: <= 32 } && p.Dependencies.All(ValidName) &&
                p.Dependencies.Distinct(StringComparer.OrdinalIgnoreCase).Count() == p.Dependencies.Length;
        }
        catch (JsonException) { return false; }
    }

    // Account for Windows quoting before durable admission and before any SCM write.
    private static int QuotedLength(string value)
    {
        var length = 2;
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            length += character == '"' ? slashes * 2 + 2 : slashes + 1;
            slashes = 0;
        }
        return length + slashes * 2;
    }
}
