using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IISManager.Console;

public sealed class SiteAnnotationZeroCodeOptions
{
    public bool Enabled { get; set; }
    public int ApplicationId { get; set; } = 1001;
    public string OpenApiBaseUrl { get; set; } = "";
    public string OpenApiAppId { get; set; } = "";
    public string PublicKeyBase64 { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;

    public void Validate()
    {
        if (!Enabled) return;
        if (ApplicationId != 1001 || TimeoutSeconds is < 1 or > 120 ||
            !Uri.TryCreate(OpenApiBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            string.IsNullOrWhiteSpace(OpenApiAppId) || OpenApiAppId.Length > 256 ||
            OpenApiAppId.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
            string.IsNullOrWhiteSpace(PublicKeyBase64) || PublicKeyBase64.Length > 8192)
            throw new InvalidOperationException("SITE_ANNOTATIONS_CONFIGURATION_INVALID");
    }
}

/// <summary>Application 1001's published field codes; these are read back from the platform, not inferred.</summary>
public sealed class ZeroCodeSiteAnnotationRepository : ISiteAnnotationRepository, IDisposable
{
    public const string QueryMethod = "QuerySiteAnnotations";
    public const string CreateMethod = "CreateSiteAnnotation";
    public const string UpdateMethod = "UpdateSiteAnnotation";
    public const string KeyField = "onlyOneClickOnSite";
    public const string ServerField = "fUWUQIBIANHAO";
    public const string SiteIdField = "zHANDIANBIANHAO";
    public const string SiteNameField = "iISZHANDIANMINGCHENG";
    public const string AliasField = "siteAlias";
    public const string TagsField = "bIAOQIANJIHE";
    private const int PageSize = 200;
    private const int MaxEntries = 10000;
    private readonly SiteAnnotationZeroCodeOptions _options;
    private readonly HttpClient _http;
    private readonly string _publicKey;
    private readonly IAppUserTokenAccessor? _authentication;

    public ZeroCodeSiteAnnotationRepository(ConsoleOptions options) : this(options.SiteAnnotations) { }

    public ZeroCodeSiteAnnotationRepository(SiteAnnotationZeroCodeOptions options, HttpMessageHandler? handler = null,
        IAppUserTokenAccessor? authentication = null)
    {
        options.Validate();
        _options = options;
        _authentication = authentication;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
        };
        _publicKey = "";
        if (!options.Enabled) return;
        try
        {
            _publicKey = Convert.ToBase64String(Convert.FromBase64String(options.PublicKeyBase64));
            using var rsa = ImportPublicKey(_publicKey);
        }
        catch
        {
            _http.Dispose();
            throw new InvalidOperationException("SITE_ANNOTATIONS_PUBLIC_KEY_INVALID");
        }
    }

    public async Task<StoredSiteAnnotation[]> QueryAsync(string serverId, string? siteKey, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        var all = new List<StoredSiteAnnotation>();
        int? total = null;
        for (var page = 1; page <= MaxEntries / PageSize + 1; page++)
        {
            var parameters = new Dictionary<string, object?>
            {
                [ServerField] = serverId, ["pageNumber"] = page, ["pageSize"] = PageSize
            };
            if (siteKey is not null) parameters[KeyField] = siteKey;
            using var response = await CallAsync(QueryMethod, parameters, deadline.Token);
            var root = response.RootElement;
            if (!root.TryGetProperty("ReturnValue", out var rows) || rows.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("TotalCount", out var count) || !TryLong(count, out var countValue) ||
                countValue is < 0 or > MaxEntries || rows.GetArrayLength() > PageSize)
                throw Unavailable();
            total ??= (int)countValue;
            if (total != countValue) throw Unavailable(); // Refuse to present a truncated or drifting page set.
            foreach (var row in rows.EnumerateArray()) all.Add(ReadRecord(row));
            if (all.Count >= total)
            {
                if (all.Count != total || all.Select(row => row.PrimaryId).Distinct(StringComparer.Ordinal).Count() != all.Count)
                    throw Unavailable();
                // Text-query operators can be broad on the platform: keep exact identity boundaries here.
                return all.Where(row => row.ServerId == serverId && (siteKey is null || row.SiteKey == siteKey)).ToArray();
            }
            if (rows.GetArrayLength() == 0) throw Unavailable();
        }
        throw Unavailable();
    }

    public async Task CreateAsync(StoredSiteAnnotation entry, CancellationToken cancellation)
    {
        using var response = await CallAsync(CreateMethod, new Dictionary<string, object?>
        {
            [KeyField] = entry.SiteKey, [ServerField] = entry.ServerId, [SiteIdField] = entry.SiteId,
            [SiteNameField] = entry.SiteName, [AliasField] = entry.Alias,
            [TagsField] = JsonSerializer.Serialize(entry.Tags, SiteAnnotationRules.Json)
        }, cancellation);
        CheckMutation(response.RootElement);
    }

    public async Task UpdateAsync(StoredSiteAnnotation entry, CancellationToken cancellation)
    {
        using var response = await CallAsync(UpdateMethod, new Dictionary<string, object?>
        {
            ["primaryId"] = entry.PrimaryId, [AliasField] = entry.Alias,
            [TagsField] = JsonSerializer.Serialize(entry.Tags, SiteAnnotationRules.Json)
        }, cancellation);
        CheckMutation(response.RootElement);
    }

    internal async Task<JsonDocument> CallAsync(string method, Dictionary<string, object?> parameters, CancellationToken cancellation,
        string? accessToken = null)
    {
        if (!_options.Enabled) throw new SiteAnnotationException("SITE_ANNOTATIONS_NOT_CONFIGURED");
        var endpoint = new Uri(_options.OpenApiBaseUrl.TrimEnd('/') + "/" + method + "/" + Uri.EscapeDataString(_options.OpenApiAppId));
        // Platform OpenAPI contract: seconds, uppercase MD5, UTF-8 segmented PKCS#1 v1.5, uppercase hex JSON string.
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        parameters["timeStamp"] = timestamp;
        parameters["sign"] = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(
            _options.OpenApiAppId + method + timestamp.ToString(CultureInfo.InvariantCulture))));
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            var encrypted = Encrypt(JsonSerializer.Serialize(parameters, SiteAnnotationRules.Json), _publicKey);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(encrypted), Encoding.UTF8, "application/json")
            };
            if (accessToken is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            else if (_authentication?.Required == true)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authentication.GetAccessToken());
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw SessionExpired();
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 4 * 1024 * 1024)
                throw Unavailable();
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (bytes.Length + read > 4 * 1024 * 1024) throw Unavailable();
                bytes.Write(buffer, 0, read);
            }
            var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("ResultCode", out var authenticationCode) && authenticationCode.ToString() == "401")
            {
                document.Dispose();
                throw SessionExpired();
            }
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                Text(document.RootElement, "ResultCode") != "200")
            {
                document.Dispose();
                throw Unavailable();
            }
            return document;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or CryptographicException or IOException)
        {
            throw Unavailable(); // Never propagate remote response, request, URL or key in error/log text.
        }
    }

    private AppUserAuthenticationException SessionExpired()
    {
        _authentication?.Invalidate();
        return new AppUserAuthenticationException("APP_USER_SESSION_EXPIRED", 401);
    }

    private static StoredSiteAnnotation ReadRecord(JsonElement row)
    {
        try
        {
            if (!row.TryGetProperty(SiteIdField, out var idValue) || !TryLong(idValue, out var id)) throw Unavailable();
            var primary = Text(row, "primaryId");
            var key = Text(row, KeyField);
            var server = Text(row, ServerField);
            var name = Text(row, SiteNameField);
            var alias = OptionalText(row, AliasField);
            var rawTags = OptionalText(row, TagsField);
            var tags = rawTags.Length == 0 ? [] : JsonSerializer.Deserialize<string[]>(rawTags);
            var candidate = new SaveSiteAnnotationRequest { SiteId = id, SiteName = name, Alias = alias, Tags = tags! };
            if (primary.Length == 0 || primary.Length > 128 || !SiteAnnotationRules.TryNormalize(candidate, out var normalized) ||
                !(key == SiteAnnotationRules.SiteKey(server, id, name) ||
                  key.Length == 128 && key.StartsWith(SiteAnnotationRules.SiteKey(server, id, name), StringComparison.Ordinal) &&
                  key.All(Uri.IsHexDigit))) throw Unavailable();
            return new StoredSiteAnnotation(primary, key, server, id, name, normalized.Alias, normalized.Tags,
                OptionalText(row, "lastModifiedAt"));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        { throw Unavailable(); }
    }

    private static void CheckMutation(JsonElement root)
    {
        if (!root.TryGetProperty("ReturnValue", out var result) || result.ValueKind != JsonValueKind.Object ||
            Text(result, "primaryId").Length == 0) throw Unavailable();
    }

    private static string Text(JsonElement row, string field) => row.TryGetProperty(field, out var value) &&
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : throw Unavailable();
    private static string OptionalText(JsonElement row, string field) => row.TryGetProperty(field, out var value) &&
        value.ValueKind != JsonValueKind.Null ? Text(row, field) : "";
    private static bool TryLong(JsonElement value, out long result) => value.ValueKind == JsonValueKind.Number
        ? value.TryGetInt64(out result) : long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    private static SiteAnnotationException Unavailable() => new("SITE_ANNOTATIONS_UNAVAILABLE");

    public static string Encrypt(string plainText, string publicKey)
    {
        using var rsa = ImportPublicKey(publicKey);
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var blockSize = rsa.KeySize / 8 - 11;
        using var encrypted = new MemoryStream();
        for (var offset = 0; offset < bytes.Length;)
        {
            var length = Math.Min(blockSize, bytes.Length - offset);
            if (offset + length < bytes.Length)
                while (length > 0 && (bytes[offset + length] & 0xC0) == 0x80) length--;
            var block = rsa.Encrypt(bytes.AsSpan(offset, length).ToArray(), RSAEncryptionPadding.Pkcs1);
            encrypted.Write(block);
            offset += length;
        }
        return Convert.ToHexString(encrypted.ToArray());
    }

    private static RSA ImportPublicKey(string key)
    {
        var rsa = RSA.Create();
        try
        {
            if (key.Contains("PRIVATE", StringComparison.Ordinal)) throw new CryptographicException();
            if (key.Contains("-----BEGIN", StringComparison.Ordinal)) rsa.ImportFromPem(key);
            else
            {
                var encoded = Convert.FromBase64String(key);
                int bytesRead;
                try { rsa.ImportSubjectPublicKeyInfo(encoded, out bytesRead); }
                catch (CryptographicException) { rsa.ImportRSAPublicKey(encoded, out bytesRead); }
                if (bytesRead != encoded.Length) throw new CryptographicException();
            }
            if (rsa.KeySize < 1024) throw new CryptographicException();
            return rsa;
        }
        catch { rsa.Dispose(); throw; }
    }

    public void Dispose() => _http.Dispose();
}
