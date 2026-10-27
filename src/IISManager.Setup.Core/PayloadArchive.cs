using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IISManager.Setup.Core;

public sealed class PayloadArchive : ISetupPayload
{
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _entries;
    public PayloadManifest Manifest { get; }
    public const string ManifestName = "payload-manifest.json";
    public static JsonSerializerOptions JsonOptions => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public PayloadArchive(Stream stream, SetupComponent expectedComponent, bool leaveOpen = false)
    {
        _archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen);
        try
        {
            if (!Enum.IsDefined(expectedComponent)) throw Error("PAYLOAD_COMPONENT_INVALID");
            if (_archive.Entries.Count is < 2 or > 4097) throw Error("PAYLOAD_ENTRY_COUNT_INVALID");
            _entries = new(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _archive.Entries)
            {
                if (!IsSafeRelativePath(entry.FullName) || !_entries.TryAdd(entry.FullName, entry)) throw Error("PAYLOAD_PATH_INVALID");
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0)
                    throw Error("PAYLOAD_LINK_FORBIDDEN");
            }
            if (!_entries.TryGetValue(ManifestName, out var manifestEntry) || manifestEntry.Length > 2 * 1024 * 1024)
                throw Error("PAYLOAD_MANIFEST_MISSING");
            using (var content = manifestEntry.Open())
                Manifest = JsonSerializer.Deserialize<PayloadManifest>(content, JsonOptions) ?? throw Error("PAYLOAD_MANIFEST_INVALID");
            ValidateManifest(Manifest, expectedComponent);
            var expected = Manifest.Files.Select(f => f.Path).Append(ManifestName).ToHashSet(StringComparer.Ordinal);
            if (!expected.SetEquals(_entries.Keys)) throw Error("PAYLOAD_FILE_SET_MISMATCH");
            foreach (var file in Manifest.Files)
                if (_entries[file.Path].Length != file.Size) throw Error("PAYLOAD_SIZE_MISMATCH");
        }
        catch
        {
            _archive.Dispose();
            throw;
        }
    }

    public static bool IsSafeRelativePath(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length < 240 &&
        !path.Contains('\\') && !path.StartsWith('/') && path.Split('/').All(SetupPlanValidator.IsSafeSegment);

    public static void ValidateManifest(PayloadManifest manifest, SetupComponent expectedComponent)
    {
        if (manifest.Component != expectedComponent.ToString() || manifest.TargetFramework != "net10.0" ||
            manifest.RuntimeIdentifier != "win-x64" || !manifest.SelfContained) throw Error("PAYLOAD_TARGET_INVALID");
        ValidateFiles(manifest.Files, false);
        if (manifest.LegacyRuntimeFiles is { } legacy) ValidateFiles(legacy, true);
        var app = "IISManager." + expectedComponent;
        foreach (var required in new[] { app + ".exe", app + ".dll", app + ".deps.json", app + ".runtimeconfig.json", "hostfxr.dll", "hostpolicy.dll", "coreclr.dll" })
            if (!manifest.Files.Any(f => f.Kind == PayloadFileKind.Runtime && f.Path == required)) throw Error("PAYLOAD_RUNTIME_MISSING");
        foreach (var required in new[] { "appsettings.json", "NLog.config" })
            if (!manifest.Files.Any(f => f.Kind == PayloadFileKind.DefaultConfiguration && f.Path == required)) throw Error("PAYLOAD_DEFAULT_CONFIG_MISSING");
        if (expectedComponent == SetupComponent.Console && !manifest.Files.Any(f => f.Path == "wwwroot/index.html"))
            throw Error("PAYLOAD_UI_MISSING");
    }

    private static void ValidateFiles(PayloadFile[]? files, bool legacy)
    {
        if (files is null || files.Length is < 1 or > 4096) throw Error("PAYLOAD_MANIFEST_INVALID");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in files)
        {
            if (file is null || !IsSafeRelativePath(file.Path) || !names.Add(file.Path) || file.Path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) ||
                file.Size < 0 || file.Size > 256L * 1024 * 1024 || !Enum.IsDefined(file.Kind) ||
                file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)) throw Error("PAYLOAD_MANIFEST_INVALID");
            total += file.Size;
            if (total > 2L * 1024 * 1024 * 1024) throw Error("PAYLOAD_TOO_LARGE");
            if (file.Kind == PayloadFileKind.DefaultConfiguration)
            {
                if (legacy || file.Path is not ("appsettings.json" or "NLog.config")) throw Error("PAYLOAD_CONFIG_INVALID");
            }
            else
            {
                var parts = file.Path.Split('/');
                var name = parts[^1];
                if (parts.Any(p => p.Equals("data", StringComparison.OrdinalIgnoreCase) || p.Equals("logs", StringComparison.OrdinalIgnoreCase)) ||
                    name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) || name.Equals("NLog.config", StringComparison.OrdinalIgnoreCase) ||
                    new[] { ".pfx", ".p12", ".key", ".pem", ".snk" }.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                    throw Error("PAYLOAD_PROTECTED_FILE");
            }
        }
    }

    public void ExtractTo(string directory)
    {
        var root = Path.GetFullPath(directory);
        AssertNoReparseAncestors(root);
        Directory.CreateDirectory(root);
        if (Directory.EnumerateFileSystemEntries(root).Any()) throw Error("PAYLOAD_STAGE_NOT_EMPTY");
        foreach (var file in Manifest.Files)
        {
            var destination = Path.GetFullPath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw Error("PAYLOAD_PATH_INVALID");
            var parent = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(parent);
            AssertNoReparseAncestors(parent);
            using var source = _entries[file.Path].Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long written = 0;
            int length;
            while ((length = source.Read(buffer)) != 0)
            {
                written += length;
                if (written > file.Size) throw Error("PAYLOAD_SIZE_MISMATCH");
                hash.AppendData(buffer, 0, length);
                output.Write(buffer, 0, length);
            }
            output.Flush(flushToDisk: true);
            if (written != file.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw Error("PAYLOAD_HASH_MISMATCH");
        }
    }

    public static void AssertNoReparseAncestors(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw Error("PATH_REPARSE_POINT_FORBIDDEN");
    }
    private static SetupException Error(string code) => new(code, "安装资源或路径校验失败（" + code + "），请使用完整的原始安装包。");
    public void Dispose() => _archive.Dispose();
}
