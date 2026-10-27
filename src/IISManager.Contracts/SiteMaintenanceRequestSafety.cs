using System.Text.Json;

namespace IISManager.Contracts;

public static class SiteMaintenanceRequestSafety
{
    public static bool ValidPath(string? path, bool root) => path is { Length: > 0 and <= 512 } && path[0] == '/' &&
        (path == "/" ? root : !path.EndsWith('/') && path.Split('/').Skip(1).All(segment => segment.Length > 0 && segment is not ("." or "..") &&
            segment == segment.Trim() && !segment.EndsWith('.') && !segment.Any(character => char.IsControl(character) || "%\\?#:;*\"<>|".Contains(character))));
    private static bool Name(string? value) => value is { Length: > 0 and <= 64 } && value == value.Trim() && !value.EndsWith('.') &&
        !value.Any(character => char.IsControl(character) || "/\\:*?\"<>|".Contains(character));
    public static bool IsValid(OperationRequest request)
    {
        var target = request.Target;
        if (!OperationTypes.IsSiteMaintenance(request.Type) || target is null || !Name(target.SiteName) ||
            target.ServiceName is not null || target.ApplicationPoolName is not null || request.Parameters.ValueKind != JsonValueKind.Object) return false;
        var directory = request.Type is OperationTypes.CreateVirtualDirectory or OperationTypes.UpdateVirtualDirectory;
        if (request.Type == OperationTypes.UpdateSite ? target.ApplicationPath is not null || target.VirtualDirectoryPath is not null :
            !ValidPath(target.ApplicationPath, directory) || (directory ? !ValidPath(target.VirtualDirectoryPath, false) : target.VirtualDirectoryPath is not null)) return false;
        var fields = request.Type == OperationTypes.UpdateSite ? new[] { "physicalPath", "applicationPoolName", "bindings" } :
            directory ? request.Type == OperationTypes.UpdateVirtualDirectory ? ["physicalPath", "ownerApplicationPoolName"] : ["physicalPath"]
                : new[] { "physicalPath", "applicationPoolName" };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in request.Parameters.EnumerateObject())
        {
            if (!fields.Contains(property.Name, StringComparer.OrdinalIgnoreCase) || !seen.Add(property.Name) || property.Value.ValueKind == JsonValueKind.Null) return false;
            if (property.Name.Equals("bindings", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind != JsonValueKind.Array) return false;
                foreach (var binding in property.Value.EnumerateArray())
                {
                    if (binding.ValueKind != JsonValueKind.Object) return false;
                    var bindingFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var field in binding.EnumerateObject())
                        if (!new[] { "protocol", "ipAddress", "port", "hostName", "certificateThumbprint", "useSni" }.Contains(field.Name, StringComparer.OrdinalIgnoreCase) ||
                            !bindingFields.Add(field.Name) || field.Value.ValueKind == JsonValueKind.Null && !field.Name.Equals("certificateThumbprint", StringComparison.OrdinalIgnoreCase)) return false;
                }
                continue;
            }
            if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not { Length: > 0 and <= 2048 } text || text.Any(char.IsControl)) return false;
        }
        if (seen.Count == 0 || request.Type == OperationTypes.CreateApplication && (!seen.Contains("physicalPath") || !seen.Contains("applicationPoolName"))) return false;
        try
        {
            if (request.Type == OperationTypes.UpdateSite)
            {
                var parameters = request.Parameters.Deserialize<SiteUpdateParameters>(Protocol.Json);
                return parameters is not null && (parameters.ApplicationPoolName is null || Name(parameters.ApplicationPoolName)) &&
                    (parameters.Bindings is null || parameters.Bindings.Length is > 0 and <= 16 && parameters.Bindings.All(binding => binding is not null && binding.Protocol is "http" or "https"));
            }
            if (directory)
            {
                var parameters = request.Parameters.Deserialize<VirtualDirectoryParameters>(Protocol.Json);
                return parameters is not null && (request.Type != OperationTypes.CreateVirtualDirectory || parameters.PhysicalPath is not null) &&
                    (parameters.OwnerApplicationPoolName is null || Name(parameters.OwnerApplicationPoolName));
            }
            var application = request.Parameters.Deserialize<ApplicationParameters>(Protocol.Json);
            return application is not null && (application.ApplicationPoolName is null || Name(application.ApplicationPoolName));
        }
        catch (JsonException) { return false; }
    }
}
