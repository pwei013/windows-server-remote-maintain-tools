using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using IISManager.Contracts;

namespace IISManager.Console;

public sealed record SaveHealthCheckRequest
{
    public string? Id { get; init; }
    public string ServerId { get; init; } = "";
    public string SiteName { get; init; } = "";
    public string ApplicationPath { get; init; } = "/";
    public string Url { get; init; } = "";
    public int ExpectedStatusCode { get; init; } = 200;
    public int TimeoutSeconds { get; init; } = 5;
    public int IntervalSeconds { get; init; } = 60;
    public bool Enabled { get; init; }
    public string? Revision { get; init; }
    public HealthProbeRequest ToProbe() => new() { SiteName = SiteName, ApplicationPath = ApplicationPath,
        Url = Url, ExpectedStatusCode = ExpectedStatusCode, TimeoutSeconds = TimeoutSeconds };
}
public sealed record HealthCheckRevision(string Revision);
public sealed record HealthCheckView(string Id, string ServerId, string SiteName, string ApplicationPath, string Url,
    int ExpectedStatusCode, int TimeoutSeconds, int IntervalSeconds, bool Enabled, string Revision,
    HealthProbeResult? Result, DateTimeOffset? LastSuccessAt, DateTimeOffset? NextCheckAt);
public sealed record HealthCheckFile(int Version, HealthCheckView[] Checks);
public sealed class HealthCheckException(string code, string message, int statusCode = 400) : Exception(code)
{
    public string Code { get; } = code;
    public string SafeMessage { get; } = message;
    public int StatusCode { get; } = statusCode;
}

public sealed class HealthMonitoringService
{
    private readonly ConsoleOptions _options;
    private readonly IMonitoringAgentClient _agent;
    private readonly TimeProvider _time;
    private readonly string _file;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _probeLimit = new(4, 4);
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);
    private HealthCheckView[] _checks = [];
    private string? _diskHash;
    private bool _healthy = true;
    private static readonly JsonSerializerOptions Json = new(Protocol.Json);

    public HealthMonitoringService(ConsoleOptions options, IMonitoringAgentClient agent, string directory, TimeProvider? time = null)
    {
        _options = options; _agent = agent; _time = time ?? TimeProvider.System;
        _file = Path.Combine(directory, "health-checks.json");
        try
        {
            if (!File.Exists(_file)) return;
            if (new FileInfo(_file).Length > 2 * 1024 * 1024) throw new InvalidDataException();
            var bytes = File.ReadAllBytes(_file);
            var saved = JsonSerializer.Deserialize<HealthCheckFile>(bytes, Json) ?? throw new InvalidDataException();
            if (saved.Version != 1 || saved.Checks is null || saved.Checks.Length > 200 ||
                saved.Checks.Any(check => check is null || !ValidView(check)) ||
                saved.Checks.Select(check => check.Id).Distinct(StringComparer.Ordinal).Count() != saved.Checks.Length ||
                saved.Checks.Select(check => Scope(check.ServerId, check.SiteName, check.ApplicationPath)).Distinct(StringComparer.Ordinal).Count() != saved.Checks.Length)
                throw new InvalidDataException();
            _checks = saved.Checks;
            _diskHash = Hash(bytes);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { _healthy = false; }
    }

    public async Task<HealthCheckView[]> GetViewsAsync(CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try { RequireHealthy(); return _checks.OrderBy(check => check.ServerId).ThenBy(check => check.SiteName).ThenBy(check => check.ApplicationPath).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<HealthCheckView> SaveAsync(SaveHealthCheckRequest request, CancellationToken cancellation)
    {
        if (!ValidRequest(request))
            throw new HealthCheckException("HEALTH_CONFIG_INVALID", "请核对服务器、站点、应用、检查地址、期望状态码及检查间隔。地址不能包含账号、查询参数或片段。");
        bool disableOnly;
        await _gate.WaitAsync(cancellation);
        try
        {
            RequireHealthy();
            var previous = request.Id is null ? null : Find(request.Id);
            if (previous is not null)
            {
                RequireRevision(previous, request.Revision);
                RequireSameServer(previous, request);
            }
            disableOnly = previous is not null && !request.Enabled && SameTarget(previous, request);
        }
        finally { _gate.Release(); }
        if (!disableOnly && !_options.Servers.Any(server => server.Id == request.ServerId))
            throw new HealthCheckException("HEALTH_CONFIG_INVALID", "所选服务器不存在，请刷新后重新选择。");
        // Validation only reads the Agent topology; saving never sends a business HTTP request.
        try { if (!disableOnly) await _agent.ValidateAsync(request.ServerId, request.ToProbe(), cancellation); }
        catch (AgentCallException exception)
        {
            cancellation.ThrowIfCancellationRequested();
            throw AgentFailure(exception, validating: true);
        }
        await _gate.WaitAsync(cancellation);
        try
        {
            RequireHealthy();
            HealthCheckView? previous = null;
            if (request.Id is not null)
            {
                previous = Find(request.Id);
                RequireRevision(previous, request.Revision);
                RequireSameServer(previous, request);
            }
            else if (_checks.Length >= 200) throw new HealthCheckException("HEALTH_LIMIT_REACHED", "最多配置 200 项健康检查。");
            if (_checks.Any(check => check.Id != request.Id && Scope(check.ServerId, check.SiteName, check.ApplicationPath) ==
                    Scope(request.ServerId, request.SiteName, request.ApplicationPath)))
                throw new HealthCheckException("HEALTH_TARGET_DUPLICATED", "该站点或应用已有检查，请编辑已有配置。", 409);
            var view = new HealthCheckView(previous?.Id ?? Guid.NewGuid().ToString("D"), request.ServerId, request.SiteName,
                request.ApplicationPath, new Uri(request.Url).AbsoluteUri, request.ExpectedStatusCode, request.TimeoutSeconds,
                request.IntervalSeconds, request.Enabled, Guid.NewGuid().ToString("N"),
                previous is not null && SameTarget(previous, request) ? previous.Result : null,
                previous is not null && SameTarget(previous, request) ? previous.LastSuccessAt : null,
                request.Enabled ? _time.GetUtcNow() : null);
            var updated = _checks.Where(check => check.Id != view.Id).Append(view).ToArray();
            await PersistAsync(updated, cancellation);
            _checks = updated;
            return view;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string id, string? revision, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            RequireHealthy(); RequireRevision(Find(id), revision);
            if (_running.ContainsKey(id)) throw new HealthCheckException("HEALTH_CHECK_BUSY", "该检查正在执行，请结束后再删除。", 409);
            var updated = _checks.Where(check => check.Id != id).ToArray();
            await PersistAsync(updated, cancellation); _checks = updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<HealthCheckView> RunAsync(string id, CancellationToken cancellation, bool scheduled = false,
        string? expectedRevision = null)
    {
        if (!_running.TryAdd(id, 0)) throw new HealthCheckException("HEALTH_CHECK_BUSY", "该检查正在执行，请稍后查看结果。", 409);
        var acquired = false;
        try
        {
            await _probeLimit.WaitAsync(cancellation); acquired = true;
            HealthCheckView check;
            await _gate.WaitAsync(cancellation);
            try
            {
                RequireHealthy(); check = Find(id);
                if (!check.Enabled) throw new HealthCheckException("HEALTH_CHECK_DISABLED", "请先开启该检查。", 409);
                if (expectedRevision is not null) RequireRevision(check, expectedRevision);
                if (scheduled && check.NextCheckAt > _time.GetUtcNow()) return check;
            }
            finally { _gate.Release(); }
            var request = new HealthProbeRequest { SiteName = check.SiteName, ApplicationPath = check.ApplicationPath,
                Url = check.Url, ExpectedStatusCode = check.ExpectedStatusCode, TimeoutSeconds = check.TimeoutSeconds };
            HealthProbeResult result;
            try
            {
                result = await _agent.ProbeAsync(check.ServerId, request, cancellation);
                if (!ValidResult(result) || result.State == "healthy" && (result.StatusCode != check.ExpectedStatusCode || result.ErrorCode is not null) ||
                    result.State == "unhealthy" && (result.StatusCode is null || result.StatusCode == check.ExpectedStatusCode))
                    throw new AgentCallException("AGENT_RESPONSE_INVALID");
            }
            catch (AgentCallException exception)
            {
                cancellation.ThrowIfCancellationRequested();
                result = new HealthProbeResult { State = exception.Code == "AGENT_NOT_FOUND" ? "unsupported" : "unavailable",
                    CheckedAt = _time.GetUtcNow(), ErrorCode = exception.Code == "AGENT_NOT_FOUND" ? "HEALTH_AGENT_UPGRADE_REQUIRED" : exception.Code };
            }
            await _gate.WaitAsync(cancellation);
            try
            {
                RequireHealthy(); var current = Find(id);
                // An in-flight result must never overwrite edited/disabled configuration.
                if (current.Revision != check.Revision) return current;
                var updated = current with { Result = result,
                    LastSuccessAt = result.State == "healthy" ? result.CheckedAt : current.LastSuccessAt,
                    NextCheckAt = _time.GetUtcNow().AddSeconds(current.IntervalSeconds) };
                var next = _checks.Select(item => item.Id == id ? updated : item).ToArray();
                await PersistAsync(next, cancellation); _checks = next; return updated;
            }
            finally { _gate.Release(); }
        }
        finally { if (acquired) _probeLimit.Release(); _running.TryRemove(id, out _); }
    }

    public async Task RunDueAsync(CancellationToken cancellation)
    {
        var due = (await GetViewsAsync(cancellation)).Where(check => check.Enabled &&
            (check.NextCheckAt is null || check.NextCheckAt <= _time.GetUtcNow())).ToArray();
        using var limit = new SemaphoreSlim(4, 4);
        await Task.WhenAll(due.Select(async check =>
        {
            await limit.WaitAsync(cancellation);
            try
            {
                try { await RunAsync(check.Id, cancellation, scheduled: true); }
                catch (HealthCheckException exception) when (exception.Code is "HEALTH_CHECK_BUSY" or "HEALTH_CHECK_DISABLED" or "HEALTH_CHECK_NOT_FOUND") { }
            }
            finally { limit.Release(); }
        }));
    }

    private async Task PersistAsync(HealthCheckView[] checks, CancellationToken cancellation)
    {
        var temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var exists = File.Exists(_file);
            if (exists != (_diskHash is not null) || exists && Hash(await File.ReadAllBytesAsync(_file, cancellation)) != _diskHash)
            {
                _healthy = false;
                throw new HealthCheckException("HEALTH_STORE_CHANGED", "健康检查配置已被外部修改，请核对文件后重启控制台。", 503);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new HealthCheckFile(1, checks), Json);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await stream.WriteAsync(bytes, cancellation); await stream.FlushAsync(cancellation); stream.Flush(flushToDisk: true);
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, _file, overwrite: true);
            // After the atomic rename, always finish readback even if the browser disconnects.
            var persisted = await File.ReadAllBytesAsync(_file, CancellationToken.None);
            if (!persisted.AsSpan().SequenceEqual(bytes)) throw new IOException();
            _diskHash = Hash(persisted);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _healthy = false;
            throw new HealthCheckException("HEALTH_STORE_UNVERIFIED", "健康检查配置保存结果无法确认，请核对配置文件后重启；请勿重复提交。", 503);
        }
        finally { if (File.Exists(temporary)) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private HealthCheckView Find(string id) => _checks.FirstOrDefault(check => check.Id == id)
        ?? throw new HealthCheckException("HEALTH_CHECK_NOT_FOUND", "该健康检查不存在，请刷新列表。", 404);
    private void RequireHealthy()
    {
        if (!_healthy) throw new HealthCheckException("HEALTH_STORE_UNAVAILABLE", "健康检查配置无法可靠读取或保存，请核对配置文件后重启控制台。", 503);
    }
    private static void RequireRevision(HealthCheckView check, string? revision)
    {
        if (revision != check.Revision) throw new HealthCheckException("HEALTH_CONFIG_CONFLICT", "配置已发生变化，请刷新后重新编辑。", 409);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Scope(string serverId, string siteName, string applicationPath) =>
        serverId + "\0" + siteName.ToUpperInvariant() + "\0" + applicationPath.ToUpperInvariant();
    private static void RequireSameServer(HealthCheckView previous, SaveHealthCheckRequest request)
    {
        if (previous.ServerId != request.ServerId)
            throw new HealthCheckException("HEALTH_SERVER_IMMUTABLE", "已有检查不能迁移到其他服务器；请由有权限的操作员删除旧项后，在目标服务器新建。", 409);
    }
    private static bool SameTarget(HealthCheckView previous, SaveHealthCheckRequest request) =>
        previous.ServerId == request.ServerId && previous.SiteName == request.SiteName && previous.ApplicationPath == request.ApplicationPath &&
        previous.Url == request.Url && previous.ExpectedStatusCode == request.ExpectedStatusCode;
    private static HealthCheckException AgentFailure(AgentCallException exception, bool validating) => exception.Code switch
    {
        "AGENT_NOT_FOUND" => new("HEALTH_AGENT_UPGRADE_REQUIRED", "当前 Agent 不支持健康检查，请更新 Agent。", 501),
        "AGENT_REQUEST_REJECTED" => new("HEALTH_TARGET_REJECTED", "检查地址必须属于所选站点绑定和应用路径，且不能使用管理端口。", 400),
        _ => new("HEALTH_AGENT_UNAVAILABLE", validating ? "无法向 Agent 核对检查地址，请确认服务器连接后重试。" : "无法完成健康检查。", 503)
    };
    private static bool ValidRequest(SaveHealthCheckRequest request) =>
        request is not null && Regex.IsMatch(request.ServerId ?? "", "^[A-Za-z0-9_-]{1,64}$") &&
        request.SiteName is { Length: > 0 and <= 64 } && !request.SiteName.Any(char.IsControl) &&
        request.ApplicationPath is { Length: > 0 and <= 512 } && request.ApplicationPath.StartsWith('/') && !request.ApplicationPath.Any(char.IsControl) &&
        request.Url is { Length: > 0 and <= 2048 } && request.Url == request.Url.Trim() && !request.Url.Any(char.IsControl) &&
        Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        request.ExpectedStatusCode is >= 200 and <= 299 && request.TimeoutSeconds is >= 1 and <= 15 &&
        request.IntervalSeconds is >= 30 and <= 3600 &&
        (request.Id is null && request.Revision is null || Guid.TryParseExact(request.Id, "D", out _) && Guid.TryParseExact(request.Revision, "N", out _));
    private static bool ValidView(HealthCheckView view) => ValidRequest(new SaveHealthCheckRequest
    {
        Id = view.Id, ServerId = view.ServerId, SiteName = view.SiteName, ApplicationPath = view.ApplicationPath, Url = view.Url,
        ExpectedStatusCode = view.ExpectedStatusCode, TimeoutSeconds = view.TimeoutSeconds, IntervalSeconds = view.IntervalSeconds,
        Enabled = view.Enabled, Revision = view.Revision
    }) && (view.Result is null || ValidResult(view.Result));
    private static bool ValidResult(HealthProbeResult result) => result is not null &&
        result.State is "healthy" or "unhealthy" or "unavailable" or "simulation" or "unsupported" &&
        result.CheckedAt != default && (result.StatusCode is null or >= 100 and <= 599) && result.ElapsedMilliseconds is null or >= 0 &&
        (result.ErrorCode is null || Regex.IsMatch(result.ErrorCode, "^[A-Z][A-Z0-9_]{0,80}$"));
}

public sealed class HealthMonitoringWorker(HealthMonitoringService service, ILogger<HealthMonitoringWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var reportedFailure = false;
        do
        {
            try { await service.RunDueAsync(stoppingToken); reportedFailure = false; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (HealthCheckException) { if (!reportedFailure) logger.LogWarning("event=health_monitoring_unavailable"); reportedFailure = true; }
            catch (Exception exception)
            {
                if (!reportedFailure) logger.LogWarning("event=health_monitoring_failed exceptionType={ExceptionType}", exception.GetType().Name);
                reportedFailure = true;
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
