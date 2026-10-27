namespace IISManager.Setup.Core;

public static class ConsoleUiUpdatePolicy
{
    private const string EndpointsPath = "IISManager.Console.staticwebassets.endpoints.json";
    private static readonly HashSet<string> ResourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".css", ".js", ".mjs", ".svg", ".png", ".jpg", ".jpeg", ".webp", ".gif", ".ico",
        ".woff", ".woff2", ".ttf", ".otf", ".map", ".json", ".txt", ".md"
    };
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "config", "configuration", "data", "logs", "state", "secrets", "secret", "credentials", "credential",
        "keys", "key", "certificates", "certificate", "certs", "cert", "tokens", "token", "passwords", "password"
    };

    // This only establishes the permitted manifest delta. The caller must independently verify
    // staged bytes, installed bytes, recorded permissions and the stopped service before writing.
    public static PayloadFile[] GetPendingScope(PayloadFile[] oldRuntime, PayloadManifest newManifest)
    {
        if (oldRuntime is null || newManifest is null) throw Error("UI_UPDATE_MANIFEST_INVALID");
        PayloadArchive.ValidateManifest(newManifest, SetupComponent.Console);
        // The existing archive validator checks kind, size, hashes, protected paths and duplicate
        // Windows names for the old list as a runtime-only legacy manifest.
        PayloadArchive.ValidateManifest(newManifest with { LegacyRuntimeFiles = oldRuntime }, SetupComponent.Console);
        var nextRuntime = newManifest.Files.Where(file => file.Kind == PayloadFileKind.Runtime).ToArray();
        RequireUnambiguousPaths(oldRuntime.Concat(newManifest.Files));
        var oldByPath = oldRuntime.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var nextByPath = nextRuntime.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var oldFile in oldRuntime)
        {
            if (!nextByPath.TryGetValue(oldFile.Path, out var next)) throw Error("UI_UPDATE_RUNTIME_REMOVED");
            if (next.Path != oldFile.Path) throw Error("UI_UPDATE_PATH_AMBIGUOUS");
        }

        var pending = new List<PayloadFile>();
        foreach (var file in nextRuntime)
        {
            if (oldByPath.TryGetValue(file.Path, out var oldFile) && oldFile.Size == file.Size &&
                oldFile.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsUiResource(file.Path)) throw Error("UI_UPDATE_SCOPE_FORBIDDEN");
            pending.Add(file);
        }
        if (pending.Count == 0) throw Error("UI_UPDATE_HAS_NO_CHANGES");
        return pending.ToArray();
    }

    private static void RequireUnambiguousPaths(IEnumerable<PayloadFile> files)
    {
        var paths = files.Select(file => file.Path).Distinct(StringComparer.Ordinal).ToArray();
        var fileNames = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var parts = path.Split('/');
            var prefix = "";
            for (var index = 0; index < parts.Length; index++)
            {
                prefix = index == 0 ? parts[index] : prefix + "/" + parts[index];
                if (names.TryGetValue(prefix, out var existing) && existing != prefix)
                    throw Error("UI_UPDATE_PATH_AMBIGUOUS");
                names[prefix] = prefix;
                if (index < parts.Length - 1 && fileNames.Contains(prefix)) throw Error("UI_UPDATE_PATH_AMBIGUOUS");
            }
        }
    }

    private static bool IsUiResource(string path)
    {
        if (path == EndpointsPath) return true;
        if (!path.StartsWith("wwwroot/", StringComparison.Ordinal)) return false;
        var parts = path.Split('/');
        if (parts.Skip(1).Any(part => part.StartsWith('.') || ProtectedNames.Contains(part))) return false;
        var name = parts[^1];
        if (name.EndsWith(".br", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            name = name[..^3];
        var extension = Path.GetExtension(name);
        var baseName = Path.GetFileNameWithoutExtension(name);
        return ResourceExtensions.Contains(extension) && !ProtectedNames.Contains(baseName) &&
            !name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("NLog.", StringComparison.OrdinalIgnoreCase) &&
            !baseName.Split('.', '-', '_').Any(part => ProtectedNames.Contains(part));
    }

    private static SetupException Error(string code) => new(code,
        "仅允许补全已核实的 Console 界面更新，运行清单不符合该范围（" + code + "）。");
}
