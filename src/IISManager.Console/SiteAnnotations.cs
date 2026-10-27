using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IISManager.Contracts;

namespace IISManager.Console;

public sealed record SiteAnnotationEntry(long SiteId, string SiteName, string Alias, string[] Tags, string Version,
    string ApplicationPath = "");
public sealed record SiteAnnotationList(string ServerId, SiteAnnotationEntry[] Entries);
public sealed record SaveSiteAnnotationRequest
{
    [JsonRequired] public long SiteId { get; init; }
    [JsonRequired] public string SiteName { get; init; } = "";
    public string ApplicationPath { get; init; } = "";
    [JsonRequired] public string Alias { get; init; } = "";
    [JsonRequired] public string[] Tags { get; init; } = [];
    [JsonRequired] public string ExpectedVersion { get; init; } = "";
}

public sealed class SiteAnnotationException(string code, int statusCode = 503) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public string SafeMessage => Code switch
    {
        "SITE_ANNOTATIONS_NOT_CONFIGURED" => "站点标注存储尚未配置，请先配置零代码调用连接。",
        "SITE_ANNOTATION_INVALID" => "别名或标签不合法：别名最多100字，最多20个标签，每个最多40字。",
        "SITE_ANNOTATION_CONFLICT" => "别名与标签已被修改，请读取最新版本后再保存。",
        "SITE_TARGET_CHANGED" => "原 IIS 站点或应用程序不存在或身份已改变，请刷新站点列表。",
        "SITE_ANNOTATION_OUTCOME_UNKNOWN" => "保存结果尚未确认，请只读核对；请勿直接重复提交。",
        "SERVER_NOT_FOUND" => "服务器不存在。",
        _ => "暂时无法读取零代码站点标注，请检查调用配置及平台连接。"
    };
}

public sealed record StoredSiteAnnotation(string PrimaryId, string SiteKey, string ServerId, long SiteId,
    string SiteName, string Alias, string[] Tags, string ModifiedAt = "");

public interface ISiteAnnotationRepository
{
    Task<StoredSiteAnnotation[]> QueryAsync(string serverId, string? siteKey, CancellationToken cancellation);
    Task CreateAsync(StoredSiteAnnotation entry, CancellationToken cancellation);
    Task UpdateAsync(StoredSiteAnnotation entry, CancellationToken cancellation);
}

public static class SiteAnnotationRules
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string SiteKey(string serverId, long siteId, string siteName, string applicationPath = "")
    {
        var siteKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new object[] { serverId, siteId, siteName }, Json))));
        // The existing unique field supports 128 characters. Keep site keys unchanged;
        // application keys add a hash of the full identity without adding platform fields.
        return applicationPath.Length == 0 ? siteKey : siteKey + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new object[] { serverId, siteId, siteName, applicationPath }, Json))));
    }

    public static bool ValidApplicationPath(string? path) => path is not null && (path.Length == 0 ||
        path.Length is > 1 and <= 2048 && path[0] == '/' && path == path.Trim() && !path.Any(char.IsControl) &&
        !path.Contains('\\') && !path.Contains('?') && !path.Contains('#') &&
        path.Split('/').Skip(1).All(segment => segment.Length > 0 && segment is not "." and not ".."));

    public static bool TryNormalize(SaveSiteAnnotationRequest request, out SaveSiteAnnotationRequest normalized)
    {
        normalized = request;
        if (request.SiteId <= 0 || string.IsNullOrWhiteSpace(request.SiteName) || request.SiteName.Length > 255 ||
            request.SiteName.Any(char.IsControl) || !ValidApplicationPath(request.ApplicationPath) || request.Alias is null || request.Tags is null ||
            request.ExpectedVersion is null || request.ExpectedVersion.Length != 0 &&
            (request.ExpectedVersion.Length != 64 || !request.ExpectedVersion.All(Uri.IsHexDigit)) ||
            request.Alias.Any(char.IsControl) || request.Alias.Trim().Length > 100 || request.Tags.Length > 20 ||
            request.Tags.Any(tag => tag is null || tag.Any(char.IsControl) || tag.Trim().Length > 40)) return false;
        normalized = request with
        {
            Alias = request.Alias.Trim(),
            Tags = request.Tags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
        return JsonSerializer.Serialize(normalized.Tags, Json).Length <= 4000;
    }

    public static SiteAnnotationEntry Entry(StoredSiteAnnotation stored, string applicationPath = "") => new(stored.SiteId, stored.SiteName,
        stored.Alias, stored.Tags, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(stored, Json)))), applicationPath);

    public static bool SameContent(StoredSiteAnnotation? actual, StoredSiteAnnotation expected) => actual is not null &&
        actual.SiteKey == expected.SiteKey && actual.ServerId == expected.ServerId && actual.SiteId == expected.SiteId &&
        actual.SiteName == expected.SiteName && actual.Alias == expected.Alias && actual.Tags.SequenceEqual(expected.Tags);
}

public sealed class SiteAnnotationService(ISiteAnnotationRepository repository,
    Func<string, bool> exists, Func<string, CancellationToken, Task<IisSnapshot>> snapshot,
    Func<string, CancellationToken, Task<SiteTopologySnapshot>>? topology = null)
{
    // One Console owns saves; independent platform editors are detected by the read-before-write version.
    private readonly Dictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private SemaphoreSlim Gate(string serverId)
    {
        if (!exists(serverId)) throw new SiteAnnotationException("SERVER_NOT_FOUND", 404);
        lock (_locks)
        {
            if (!_locks.TryGetValue(serverId, out var gate)) _locks[serverId] = gate = new SemaphoreSlim(1, 1);
            return gate;
        }
    }

    public async Task<SiteAnnotationList> GetAsync(string serverId, CancellationToken cancellation)
    {
        var gate = Gate(serverId);
        await gate.WaitAsync(cancellation);
        try
        {
            var records = await repository.QueryAsync(serverId, null, cancellation);
            var current = await snapshot(serverId, cancellation);
            var targets = current.Sites.ToDictionary(site => SiteAnnotationRules.SiteKey(serverId, site.Id, site.Name),
                site => (site.Id, site.Name, Path: ""), StringComparer.Ordinal);
            if (records.Any(record => record.SiteKey.Length == 128))
            {
                if (topology is null) throw new SiteAnnotationException("SITE_ANNOTATIONS_UNAVAILABLE");
                var detail = await topology(serverId, cancellation);
                foreach (var site in detail.Sites.Where(site => current.Sites.Any(candidate => candidate.Id == site.Id && candidate.Name == site.Name)))
                    foreach (var application in site.Applications.Where(application => application.Path.Length > 0 && SiteAnnotationRules.ValidApplicationPath(application.Path)))
                        targets[SiteAnnotationRules.SiteKey(serverId, site.Id, site.Name, application.Path)] = (site.Id, site.Name, application.Path);
            }
            var valid = records.Where(record => record.ServerId == serverId && targets.TryGetValue(record.SiteKey, out var target) &&
                record.SiteId == target.Id && record.SiteName == target.Name).ToArray();
            if (valid.Select(record => record.SiteKey).Distinct().Count() != valid.Length)
                throw new SiteAnnotationException("SITE_ANNOTATIONS_UNAVAILABLE");
            return new SiteAnnotationList(serverId, valid.Select(record => SiteAnnotationRules.Entry(record, targets[record.SiteKey].Path)).ToArray());
        }
        finally { gate.Release(); }
    }

    public async Task<SiteAnnotationEntry> SaveAsync(string serverId, SaveSiteAnnotationRequest request, CancellationToken cancellation)
    {
        if (!SiteAnnotationRules.TryNormalize(request, out var normalized))
            throw new SiteAnnotationException("SITE_ANNOTATION_INVALID", 400);
        var gate = Gate(serverId);
        await gate.WaitAsync(cancellation);
        try
        {
            var current = await snapshot(serverId, cancellation);
            if (!current.Sites.Any(site => site.Id == normalized.SiteId && site.Name == normalized.SiteName))
                throw new SiteAnnotationException("SITE_TARGET_CHANGED", 409);
            if (normalized.ApplicationPath.Length > 0)
            {
                if (topology is null) throw new SiteAnnotationException("SITE_ANNOTATIONS_UNAVAILABLE");
                var detail = await topology(serverId, cancellation);
                if (!detail.Sites.Any(site => site.Id == normalized.SiteId && site.Name == normalized.SiteName &&
                    site.Applications.Any(application => application.Path == normalized.ApplicationPath)))
                    throw new SiteAnnotationException("SITE_TARGET_CHANGED", 409);
            }
            var key = SiteAnnotationRules.SiteKey(serverId, normalized.SiteId, normalized.SiteName, normalized.ApplicationPath);
            var before = await ReadOneAsync(serverId, key, cancellation);
            if ((before is null ? "" : SiteAnnotationRules.Entry(before).Version) != normalized.ExpectedVersion)
                throw new SiteAnnotationException("SITE_ANNOTATION_CONFLICT", 409);
            var proposed = new StoredSiteAnnotation(before?.PrimaryId ?? "", key, serverId, normalized.SiteId,
                normalized.SiteName, normalized.Alias, normalized.Tags);
            if (SiteAnnotationRules.SameContent(before, proposed)) return SiteAnnotationRules.Entry(before!, normalized.ApplicationPath);
            // Exactly one mutation attempt. Disconnects/timeouts never cause an automatic replay.
            try
            {
                if (before is null) await repository.CreateAsync(proposed, CancellationToken.None);
                else await repository.UpdateAsync(proposed, CancellationToken.None);
            }
            catch (SiteAnnotationException exception) when (exception.Code != "SITE_ANNOTATIONS_NOT_CONFIGURED")
            {
                var recovered = await TryReadAfterWriteAsync(serverId, key);
                if (SiteAnnotationRules.SameContent(recovered, proposed)) return SiteAnnotationRules.Entry(recovered!, normalized.ApplicationPath);
                throw new SiteAnnotationException("SITE_ANNOTATION_OUTCOME_UNKNOWN", 502);
            }
            var after = await TryReadAfterWriteAsync(serverId, key);
            if (!SiteAnnotationRules.SameContent(after, proposed))
                throw new SiteAnnotationException("SITE_ANNOTATION_OUTCOME_UNKNOWN", 502);
            return SiteAnnotationRules.Entry(after!, normalized.ApplicationPath);
        }
        finally { gate.Release(); }
    }

    private async Task<StoredSiteAnnotation?> ReadOneAsync(string serverId, string key, CancellationToken cancellation)
    {
        var records = await repository.QueryAsync(serverId, key, cancellation);
        var matching = records.Where(record => record.ServerId == serverId && record.SiteKey == key).ToArray();
        if (matching.Length > 1) throw new SiteAnnotationException("SITE_ANNOTATIONS_UNAVAILABLE");
        return matching.SingleOrDefault();
    }

    private async Task<StoredSiteAnnotation?> TryReadAfterWriteAsync(string serverId, string key)
    {
        try { return await ReadOneAsync(serverId, key, CancellationToken.None); }
        catch (SiteAnnotationException) { return null; }
        catch (AppUserAuthenticationException) { return null; } // A committed write must still report an unknown outcome.
    }
}
