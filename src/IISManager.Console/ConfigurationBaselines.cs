using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IISManager.Contracts;
using IISManager.Core;

namespace IISManager.Console;

public sealed record ConfigurationBaseline(string ServerId, string Revision, string Label, DateTimeOffset SavedAt,
    string? SavedBy, string Fingerprint, JsonElement Configuration);
public sealed record BaselineChange(string Path, string? Before, string? After);
public sealed record BaselineComparison(ConfigurationBaseline? Baseline, string CurrentFingerprint,
    JsonElement CurrentConfiguration, BaselineChange[] Differences, int DifferenceCount, bool Matches);
public sealed record SaveBaselineRequest(string Label, string? Revision, string CurrentFingerprint);

public sealed class ConfigurationBaselineService
{
    private readonly IMonitoringAgentClient _agent;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _hashes = new(StringComparer.Ordinal);
    private bool _healthy = true;

    public ConfigurationBaselineService(IMonitoringAgentClient agent, string directory)
    {
        _agent = agent; _directory = Path.GetFullPath(directory);
        try
        {
            if (!Directory.Exists(_directory)) return;
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                if (new FileInfo(file).Length > 8 * 1024 * 1024 || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
                var bytes = File.ReadAllBytes(file);
                var id = JsonSerializer.Deserialize<ConfigurationBaseline>(bytes, Protocol.Json)?.ServerId;
                if (id is null || !ValidId(id) || Path.GetFileName(file) != FileName(id)) throw new InvalidDataException();
                _ = Parse(bytes, id); _hashes[id] = Hash(bytes);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
        { _healthy = false; }
    }

    public async Task<BaselineComparison> CompareAsync(string id, CancellationToken cancellation)
    {
        if (!ValidId(id)) throw Failure("BASELINE_SERVER_INVALID", "服务器编号无效。", 400);
        var current = await ReadCurrentAsync(id, cancellation);
        await _gate.WaitAsync(cancellation);
        try
        {
            RequireHealthy(); var baseline = await ReadStoredAsync(id, cancellation);
            var changes = baseline is null ? [] : Differences(baseline.Configuration, current);
            return new(baseline, CanonicalJson.Hash(current), current, changes.Take(500).ToArray(), changes.Length,
                baseline is not null && changes.Length == 0);
        }
        finally { _gate.Release(); }
    }

    public async Task<ConfigurationBaseline> SaveAsync(string id, SaveBaselineRequest request, string? user,
        CancellationToken cancellation)
    {
        if (!ValidId(id) || request.Label is null || request.Label.Length > 100 || request.Label.Any(char.IsControl) ||
            request.CurrentFingerprint is not { Length: 64 } || !request.CurrentFingerprint.All(Uri.IsHexDigit))
            throw Failure("BASELINE_REQUEST_INVALID", "请核对基线说明和当前配置指纹。", 400);
        var current = await ReadCurrentAsync(id, cancellation);
        var fingerprint = CanonicalJson.Hash(current);
        if (!fingerprint.Equals(request.CurrentFingerprint, StringComparison.OrdinalIgnoreCase))
            throw Failure("BASELINE_CONFIGURATION_CHANGED", "当前配置已变化，请重新巡检并确认。", 409);
        await _gate.WaitAsync(cancellation);
        try
        {
            RequireHealthy(); var previous = await ReadStoredAsync(id, cancellation);
            if (previous?.Revision != request.Revision)
                throw Failure("BASELINE_REVISION_CONFLICT", "基线已更新，请重新读取后确认。", 409);
            var saved = new ConfigurationBaseline(id, Guid.NewGuid().ToString("N"), request.Label.Trim(), DateTimeOffset.UtcNow,
                user, fingerprint, current);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(saved, Protocol.Json);
            if (bytes.Length > 8 * 1024 * 1024)
                throw Failure("BASELINE_TOO_LARGE", "配置超过基线存储上限；未覆盖原基线，请缩小受管配置范围。", 400);
            _ = Parse(bytes, id);
            Directory.CreateDirectory(_directory);
            var file = Path.Combine(_directory, FileName(id));
            var temporary = Path.Combine(_directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    await stream.WriteAsync(bytes, cancellation); await stream.FlushAsync(cancellation); stream.Flush(true);
                }
                cancellation.ThrowIfCancellationRequested();
                _ = await ReadStoredAsync(id, cancellation);
                File.Move(temporary, file, overwrite: true);
                var readback = await File.ReadAllBytesAsync(file, CancellationToken.None);
                if (!readback.AsSpan().SequenceEqual(bytes)) throw new IOException();
                _hashes[id] = Hash(readback);
                return Parse(readback, id);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            { _healthy = false; throw Failure("BASELINE_STORE_UNVERIFIED", "无法确认基线保存结果，请先只读核对文件，勿重复提交。", 503); }
            finally { if (File.Exists(temporary)) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        finally { _gate.Release(); }
    }

    private async Task<JsonElement> ReadCurrentAsync(string id, CancellationToken cancellation)
    {
        try { return Normalize(await _agent.GetAsync<SiteTopologySnapshot>(id, "site-topology", cancellation)); }
        catch (AgentCallException exception) when (exception.Code == "AGENT_NOT_FOUND")
        { throw Failure("BASELINE_AGENT_UNSUPPORTED", "当前 Agent 不支持应用拓扑，请更新 Agent。", 501); }
        catch (InvalidDataException) { throw Failure("BASELINE_RESPONSE_INVALID", "当前配置无法可靠读取，请先核对 Agent。", 503); }
    }

    private async Task<ConfigurationBaseline?> ReadStoredAsync(string id, CancellationToken cancellation)
    {
        var file = Path.Combine(_directory, FileName(id));
        try
        {
            if (!File.Exists(file))
            {
                if (_hashes.ContainsKey(id)) throw new InvalidDataException();
                return null;
            }
            if (new FileInfo(file).Length > 8 * 1024 * 1024 || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            var bytes = await File.ReadAllBytesAsync(file, cancellation);
            if (!_hashes.TryGetValue(id, out var expected) || Hash(bytes) != expected) throw new InvalidDataException();
            return Parse(bytes, id);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { _healthy = false; throw Failure("BASELINE_STORE_CHANGED", "基线文件损坏或被外部修改，请保留文件并只读核对后重启 Console。", 503); }
    }

    public static JsonElement Normalize(SiteTopologySnapshot topology)
    {
        if (topology?.Sites is null || topology.ApplicationPools is null) throw new InvalidDataException();
        try
        {
            var pools = topology.ApplicationPools.OrderBy(pool => pool.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(pool => pool.Name, pool => new
            {
                pool.ManagedRuntimeVersion, pool.ManagedPipelineMode, pool.Enable32BitAppOnWin64, pool.AutoStart, pool.StartMode,
                pool.IdleTimeoutMinutes, pool.IdleTimeoutAction, pool.IdentityType
            }, StringComparer.OrdinalIgnoreCase);
            var sites = topology.Sites.OrderBy(site => site.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(site => site.Name, site => new
            {
                site.Id,
                Bindings = site.Bindings.OrderBy(binding => binding.Protocol).ThenBy(binding => binding.IpAddress)
                    .ThenBy(binding => binding.Port).ThenBy(binding => binding.HostName).Select(binding => new
                    { binding.Protocol, binding.IpAddress, binding.Port, binding.HostName, binding.CertificateThumbprint, binding.UseSni }).ToArray(),
                Applications = site.Applications.OrderBy(application => application.Path, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(application => application.Path, application => new
                    {
                        application.ApplicationPoolName, application.PhysicalPath, application.EnabledProtocols,
                        VirtualDirectories = application.VirtualDirectories.OrderBy(directory => directory.Path, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(directory => directory.Path, directory => directory.PhysicalPath, StringComparer.OrdinalIgnoreCase)
                    }, StringComparer.OrdinalIgnoreCase)
            }, StringComparer.OrdinalIgnoreCase);
            var result = JsonSerializer.SerializeToElement(new { Pools = pools, Sites = sites }, Protocol.Json);
            if (!SafeConfiguration(result)) throw new InvalidDataException();
            return result;
        }
        catch (Exception exception) when (exception is ArgumentException or NullReferenceException) { throw new InvalidDataException(); }
    }

    public static BaselineChange[] Differences(JsonElement before, JsonElement after)
    {
        var changes = new List<BaselineChange>();
        void Compare(string path, JsonElement oldValue, JsonElement newValue)
        {
            if (oldValue.ValueKind == JsonValueKind.Object && newValue.ValueKind == JsonValueKind.Object)
            {
                var oldProperties = oldValue.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
                var newProperties = newValue.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
                foreach (var name in oldProperties.Keys.Union(newProperties.Keys).Order(StringComparer.Ordinal))
                    Compare(path + "/" + name.Replace("~", "~0").Replace("/", "~1"), oldProperties.GetValueOrDefault(name), newProperties.GetValueOrDefault(name));
            }
            else if (CanonicalJson.Hash(oldValue.ValueKind == JsonValueKind.Undefined ? (object?)null : oldValue) !=
                     CanonicalJson.Hash(newValue.ValueKind == JsonValueKind.Undefined ? (object?)null : newValue))
                changes.Add(new(path, oldValue.ValueKind == JsonValueKind.Undefined ? null : oldValue.GetRawText(),
                    newValue.ValueKind == JsonValueKind.Undefined ? null : newValue.GetRawText()));
        }
        Compare("", before, after);
        return changes.ToArray();
    }

    private static ConfigurationBaseline Parse(byte[] bytes, string id)
    {
        if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException();
        var value = JsonSerializer.Deserialize<ConfigurationBaseline>(bytes, Protocol.Json) ?? throw new InvalidDataException();
        if (value.ServerId != id || !Guid.TryParseExact(value.Revision, "N", out _) || value.SavedAt == default ||
            !SafeConfiguration(value.Configuration) || value.Fingerprint != CanonicalJson.Hash(value.Configuration))
            throw new InvalidDataException();
        return value;
    }
    private static bool SafeConfiguration(JsonElement value)
    {
        static bool Fields(JsonElement element, params string[] fields) => element.ValueKind == JsonValueKind.Object &&
            element.EnumerateObject().Count() == fields.Length && element.EnumerateObject().All(property => fields.Contains(property.Name, StringComparer.Ordinal)) &&
            element.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() == fields.Length;
        static bool Text(JsonElement element) => element.ValueKind is JsonValueKind.String or JsonValueKind.Null;
        static bool Boolean(JsonElement element) => element.ValueKind is JsonValueKind.True or JsonValueKind.False;
        static bool Map(JsonElement element, Func<JsonElement, bool> valid) => element.ValueKind == JsonValueKind.Object &&
            element.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == element.EnumerateObject().Count() &&
            element.EnumerateObject().All(property => property.Name.Length is > 0 and <= 1024 && !property.Name.Any(char.IsControl) && valid(property.Value));
        static bool Binding(JsonElement element) => Fields(element, "protocol", "ipAddress", "port", "hostName", "certificateThumbprint", "useSni") &&
            new[] { "protocol", "ipAddress", "hostName", "certificateThumbprint" }.All(field => Text(element.GetProperty(field))) &&
            element.GetProperty("port").ValueKind == JsonValueKind.Number && element.GetProperty("port").TryGetInt32(out _) && Boolean(element.GetProperty("useSni"));
        static bool Pool(JsonElement element) => Fields(element, "managedRuntimeVersion", "managedPipelineMode", "enable32BitAppOnWin64", "autoStart", "startMode", "idleTimeoutMinutes", "idleTimeoutAction", "identityType") &&
            new[] { "managedRuntimeVersion", "managedPipelineMode", "startMode", "idleTimeoutAction", "identityType" }.All(field => Text(element.GetProperty(field))) &&
            Boolean(element.GetProperty("enable32BitAppOnWin64")) && Boolean(element.GetProperty("autoStart")) && element.GetProperty("idleTimeoutMinutes").ValueKind == JsonValueKind.Number && element.GetProperty("idleTimeoutMinutes").TryGetInt32(out _);
        static bool Application(JsonElement element) => Fields(element, "applicationPoolName", "physicalPath", "enabledProtocols", "virtualDirectories") &&
            new[] { "applicationPoolName", "physicalPath", "enabledProtocols" }.All(field => Text(element.GetProperty(field))) && Map(element.GetProperty("virtualDirectories"), Text);
        static bool Site(JsonElement element) => Fields(element, "id", "bindings", "applications") && element.GetProperty("id").ValueKind == JsonValueKind.Number && element.GetProperty("id").TryGetInt64(out _) &&
            element.GetProperty("bindings").ValueKind == JsonValueKind.Array && element.GetProperty("bindings").EnumerateArray().All(Binding) && Map(element.GetProperty("applications"), Application);
        return Fields(value, "pools", "sites") && Map(value.GetProperty("pools"), Pool) && Map(value.GetProperty("sites"), Site);
    }
    private static bool ValidId(string id) => Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,64}$");
    private static string FileName(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".json";
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private void RequireHealthy() { if (!_healthy) throw Failure("BASELINE_STORE_UNAVAILABLE", "配置基线存储不可用，请保留文件并只读核对。", 503); }
    private static HealthCheckException Failure(string code, string message, int status) => new(code, message, status);
}

public static class ConfigurationBaselineEndpoints
{
    public static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/servers/{id}/configuration-baseline", async (string id, ConfigurationBaselineService service, CancellationToken cancellation) =>
            Results.Ok(await service.CompareAsync(id, cancellation)));
        app.MapPost("/api/v1/servers/{id}/configuration-baseline", async (string id, SaveBaselineRequest request,
            ConfigurationBaselineService service, AppUserAuthenticationService authentication, HttpContext context, CancellationToken cancellation) =>
            Results.Ok(await service.SaveAsync(id, request, authentication.CurrentUser(context)?.UserId, cancellation)));
    }
}
