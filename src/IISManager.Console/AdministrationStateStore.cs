using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IISManager.Contracts;

namespace IISManager.Console;

public static class AdministrationBootstrap
{
    public static AdministrationStateStore Load(ConsoleOptions settings, string contentRoot, bool development)
    {
        settings.Administration.Validate(development, settings.ListenAddress);
        var allowDevelopment = development && settings.Administration.AllowDevelopmentPlaintext &&
            IPAddress.TryParse(settings.ListenAddress, out var address) && IPAddress.IsLoopback(address);
        var instance = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(contentRoot).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..20];
        IAdministrationSecretProtector? protector = OperatingSystem.IsWindows() ? new WindowsAdministrationProtector("IISManager.Console/administration/v1/" + instance) :
            allowDevelopment ? new DevelopmentAdministrationProtector() : null;
        string directory;
        if (!string.IsNullOrWhiteSpace(settings.Administration.DataDirectory)) directory = Path.GetFullPath(settings.Administration.DataDirectory, contentRoot);
        else if (OperatingSystem.IsWindows())
        {
            directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MKY", "IISManagerTool", "Console", instance, "administration");
        }
        else
        {
            var tasks = Path.GetFullPath(settings.TaskDirectory, contentRoot);
            directory = Path.Combine(Path.GetDirectoryName(tasks)!, "administration");
        }
        var store = new AdministrationStateStore(settings, contentRoot, directory, development, protector);
        settings.Servers = store.ActiveServers;
        return store;
    }

    public static void Register(IServiceCollection services, AdministrationStateStore store)
    {
        services.AddSingleton(store);
        services.AddSingleton<ConsoleAccessService>();
        services.AddSingleton<IAdministrationConnectionTester, AdministrationConnectionTester>();
        services.AddSingleton<AdministrationService>();
    }
}

public sealed class AdministrationStateStore : IDisposable
{
    private readonly ConsoleOptions _options;
    private readonly string _contentRoot;
    private readonly string _directory;
    private readonly string _file;
    private readonly bool _development;
    private readonly IAdministrationSecretProtector? _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ServerOptions[] _baseServers;
    private AdministrationDocument _document;
    private string? _diskHash;
    private volatile bool _healthy = true;
    private FileStream? _processLock;
    public ServerOptions[] ActiveServers { get; }
    public bool DevelopmentBypass { get; }
    public bool DevelopmentAdministration { get; }
    internal bool IsDevelopment => _development;
    public bool CanPersist => _healthy && _protector is not null;
    public string Protection => _protector?.Format ?? "unavailable";
    public string DirectoryPath => _directory;
    public string Revision { get { RequireHealthy(); return Volatile.Read(ref _document).Revision; } }
    internal ConsoleAuthorizationPolicy Policy { get { RequireHealthy(); return Volatile.Read(ref _document).Policy; } }

    public AdministrationStateStore(ConsoleOptions options, string contentRoot, string directory, bool development,
        IAdministrationSecretProtector? protector)
    {
        _options = options; _contentRoot = Path.GetFullPath(contentRoot); _directory = Path.GetFullPath(directory);
        _file = Path.Combine(_directory, "administration-state.json"); _development = development; _protector = protector;
        _baseServers = options.Servers.Select(AdministrationRules.Clone).ToArray();
        DevelopmentBypass = development && !options.UserAuthentication.Enabled &&
            IPAddress.TryParse(options.ListenAddress, out var address) && IPAddress.IsLoopback(address);
        DevelopmentAdministration = DevelopmentBypass && options.Administration.AllowDevelopmentPlaintext;
        _document = new AdministrationDocument { Policy = new() { Administrators = options.Administration.BootstrapAdministrators.ToArray() } };
        try
        {
            AdministrationFileSecurity.CheckPath(_directory, _contentRoot, development);
            if (File.Exists(_file))
            {
                if (_protector is null) throw new InvalidDataException();
                AdministrationFileSecurity.PrepareDirectory(_directory, _contentRoot, development);
                _ = AdministrationFileSecurity.CaptureFilePermissions(_file);
                if (new FileInfo(_file).Length > 4 * 1024 * 1024) throw new InvalidDataException();
                var bytes = File.ReadAllBytes(_file);
                _document = Decode(bytes); _diskHash = Hash(bytes);
            }
            ValidateDocument(_document);
            ActiveServers = AdministrationRules.Merge(_baseServers, _document.ServerOverrides);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
            JsonException or FormatException or CryptographicException or AdministrationException or InvalidOperationException)
        {
            // A missing/corrupt policy must never silently turn strict authorization back into legacy access.
            throw new InvalidOperationException("ADMINISTRATION_STARTUP_STATE_UNAVAILABLE");
        }
    }

    internal ServerOptions[] PendingServers
    {
        get { RequireHealthy(); return AdministrationRules.Merge(_baseServers, Volatile.Read(ref _document).ServerOverrides); }
    }

    internal async Task<T> ChangeAsync<T>(string revision, AppUserIdentity? actor,
        Func<AdministrationDocument, (AdministrationDocument Document, T Result)> change, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            RequireHealthy(); RequireAdministrator(actor);
            var current = Volatile.Read(ref _document);
            if (current.Revision != revision) throw Conflict();
            if (_protector is null) throw new AdministrationException("ADMIN_SECRET_PROTECTION_UNAVAILABLE",
                "当前运行环境没有可用的安全接入配置存储；生产不会回退明文。", 503);
            var next = change(current);
            next.Document.Revision = Guid.NewGuid().ToString("N");
            ValidateDocument(next.Document);
            await PersistAsync(next.Document, cancellation);
            Volatile.Write(ref _document, next.Document);
            return next.Result;
        }
        finally { _gate.Release(); }
    }

    internal void RequireRevision(string revision)
    {
        if (Revision != revision) throw Conflict();
    }
    internal void RequireAdministrator(AppUserIdentity? actor)
    {
        if (!AdministrationRules.IsAdministrator(Policy, actor, DevelopmentAdministration))
            throw new AdministrationException("ADMINISTRATION_DENIED", "当前账号没有接入与权限管理权限。", 403);
    }
    private static AdministrationException Conflict() => new("ADMINISTRATION_CONFLICT", "配置已变化，请重新读取并预览，勿重复提交。", 409);
    private void RequireHealthy()
    {
        if (!_healthy) throw new AdministrationException("ADMINISTRATION_STORE_UNAVAILABLE", "接入或权限配置存储不可用，请先只读核对文件、权限和保护身份后重启 Console。", 503);
    }

    private void ValidateDocument(AdministrationDocument document)
    {
        if (document.Version != 1 || !Guid.TryParseExact(document.Revision, "N", out _) ||
            document.ServerOverrides is null || document.ServerOverrides.Length > 500 || document.Policy is null ||
            document.Policy.Administrators is null || document.Policy.Grants is null ||
            document.ServerOverrides.Any(server => server is null) ||
            document.ServerOverrides.Select(server => server.Id).Distinct(StringComparer.Ordinal).Count() != document.ServerOverrides.Length)
            throw new InvalidDataException();
        foreach (var server in document.ServerOverrides) _options.ValidateServer(server, _development);
        var ids = AdministrationRules.Merge(_baseServers, document.ServerOverrides).Select(server => server.Id);
        // Persisted policies require structural validity. Self-lockout is checked at each explicit policy save.
        var validator = document.Policy.Administrators.FirstOrDefault();
        var actor = validator is null ? null : new AppUserIdentity(validator.AppId, validator.UserId, "", "");
        if (validator is null && (document.Policy.Enabled || document.Policy.Grants.Length > 0)) throw new InvalidDataException();
        AdministrationRules.ValidatePolicy(document.Policy, ids, actor, validator is null);
    }

    private AdministrationDocument Decode(byte[] bytes)
    {
        var envelope = JsonSerializer.Deserialize<AdministrationEnvelope>(bytes, Protocol.Json) ?? throw new InvalidDataException();
        if (envelope.Version != 1 || envelope.Protection != _protector!.Format || !Guid.TryParseExact(envelope.Revision, "N", out _))
            throw new InvalidDataException();
        var protectedBytes = Convert.FromBase64String(envelope.Payload);
        byte[]? clear = null;
        try
        {
            clear = _protector.Unprotect(protectedBytes);
            var result = JsonSerializer.Deserialize<AdministrationDocument>(clear, Protocol.Json) ?? throw new InvalidDataException();
            if (result.Revision != envelope.Revision) throw new InvalidDataException();
            ValidateDocument(result); return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
        }
    }

    private byte[] Encode(AdministrationDocument document)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(document, Protocol.Json);
        byte[]? encrypted = null;
        try
        {
            encrypted = _protector!.Protect(clear);
            return JsonSerializer.SerializeToUtf8Bytes(new AdministrationEnvelope(1, _protector.Format, document.Revision,
                Convert.ToBase64String(encrypted)), Protocol.Json);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private async Task PersistAsync(AdministrationDocument document, CancellationToken cancellation)
    {
        string? temporary = null;
        var replaced = false;
        try
        {
            AdministrationFileSecurity.PrepareDirectory(_directory, _contentRoot, _development);
            _processLock ??= new FileStream(Path.Combine(_directory, ".administration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(_file) != (_diskHash is not null) || _diskHash is not null && Hash(await File.ReadAllBytesAsync(_file, cancellation)) != _diskHash)
            {
                _healthy = false;
                throw new AdministrationException("ADMINISTRATION_STORE_CHANGED", "接入或权限文件已被外部更改；未覆盖，请只读核对后重启 Console。", 503);
            }
            var permissions = AdministrationFileSecurity.CaptureFilePermissions(_file);
            var bytes = Encode(document);
            temporary = Path.Combine(_directory, ".administration-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                AdministrationFileSecurity.ApplyAndVerifyPermissions(temporary, permissions);
                await stream.WriteAsync(bytes, cancellation); await stream.FlushAsync(cancellation); stream.Flush(flushToDisk: true);
            }
            AdministrationFileSecurity.CheckPath(_directory, _contentRoot, _development);
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, _file, overwrite: true); replaced = true;
            var persisted = await File.ReadAllBytesAsync(_file, CancellationToken.None);
            if (!persisted.AsSpan().SequenceEqual(bytes)) throw new IOException();
            AdministrationFileSecurity.VerifyPermissions(_file, permissions);
            var decoded = Decode(persisted);
            if (decoded.Revision != document.Revision) throw new InvalidDataException();
            _diskHash = Hash(persisted);
        }
        catch (OperationCanceledException) when (!replaced) { throw; }
        catch (AdministrationException) when (!replaced) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
            JsonException or CryptographicException or AdministrationException or InvalidOperationException)
        {
            _healthy = false;
            throw new AdministrationException("ADMINISTRATION_STORE_UNVERIFIED", "保存结果无法确认，已停止相关写入；请只读核对实际文件、权限和保护身份，勿重复提交。", 503);
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary))
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public void Dispose() { _processLock?.Dispose(); _gate.Dispose(); }
}
