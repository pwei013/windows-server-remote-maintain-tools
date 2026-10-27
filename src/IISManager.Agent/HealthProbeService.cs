using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using IISManager.Contracts;
using IISManager.Core;

namespace IISManager.Agent;

/// <summary>Probes only a freshly verified IIS binding on this machine. No Agent credentials enter the probe.</summary>
public sealed class HealthProbeService : IDisposable
{
    private readonly AgentOptions _options;
    private readonly IIisTopologyBackend _topology;
    private readonly Func<HttpRequestMessage, IPAddress, CancellationToken, Task<int>> _send;
    private readonly Func<IPAddress[]> _localAddresses;
    private readonly SemaphoreSlim _gate = new(4, 4);

    public HealthProbeService(AgentOptions options, IIisTopologyBackend topology)
        : this(options, topology, SendLocalAsync, LocalAddresses) { }

    // Injection keeps tests off real IIS and the network, while exercising the same validation path.
    public HealthProbeService(AgentOptions options, IIisTopologyBackend topology,
        Func<HttpRequestMessage, IPAddress, CancellationToken, Task<int>> send,
        Func<IPAddress[]> localAddresses)
    {
        _options = options;
        _topology = topology;
        _send = send;
        _localAddresses = localAddresses;
    }

    public async Task ValidateAsync(HealthProbeRequest request, CancellationToken cancellationToken = default)
    {
        ValidateShape(request);
        if (!await _gate.WaitAsync(0, cancellationToken)) throw Invalid("HEALTH_BUSY", "健康检查繁忙，请稍后重试。", 429);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
            try { _ = await TargetAsync(request, deadline.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw Invalid("HEALTH_TIMEOUT", "健康检查目标验证超时。", 504); }
        }
        finally { _gate.Release(); }
    }

    public async Task<HealthProbeResult> ProbeAsync(HealthProbeRequest request, CancellationToken cancellationToken = default)
    {
        ValidateShape(request);
        var checkedAt = DateTimeOffset.UtcNow;
        var elapsed = Stopwatch.StartNew();
        if (!await _gate.WaitAsync(0, cancellationToken)) return Result("unavailable", "HEALTH_BUSY");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
            var target = await TargetAsync(request, deadline.Token);
            if (target.SiteState != "Started")
                return Result("unavailable", target.SiteState == "Stopped" ? "HEALTH_SITE_STOPPED" : "HEALTH_TARGET_NOT_RUNNING");
            if (_options.Mode == "Simulation") return Result("simulation", "HEALTH_SIMULATION");
            using var message = new HttpRequestMessage(HttpMethod.Get, target.Uri)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            message.Headers.Accept.ParseAdd("application/json, text/plain;q=0.9, */*;q=0.1");
            message.Headers.UserAgent.ParseAdd("IISManager-Health/1.0");
            var status = await _send(message, target.Address, deadline.Token);
            if (status is < 100 or > 599) return Result("unavailable", "HEALTH_RESPONSE_INVALID");
            return new HealthProbeResult
            {
                CheckedAt = checkedAt,
                State = status == request.ExpectedStatusCode ? "healthy" : "unhealthy",
                StatusCode = status,
                ElapsedMilliseconds = elapsed.ElapsedMilliseconds,
                ErrorCode = status == request.ExpectedStatusCode ? null : "HEALTH_STATUS_MISMATCH"
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Result("unavailable", "HEALTH_TIMEOUT"); }
        catch (OperationException exception) { return Result("unavailable", exception.Code); }
        catch (HttpRequestException exception)
        { return Result("unavailable", exception.HttpRequestError == HttpRequestError.SecureConnectionError ? "HEALTH_TLS_ERROR" : "HEALTH_CONNECTION_FAILED"); }
        catch (Exception exception) when (exception is SocketException or IOException)
        { return Result("unavailable", "HEALTH_CONNECTION_FAILED"); }
        finally { _gate.Release(); }

        HealthProbeResult Result(string state, string code) => new()
        {
            CheckedAt = checkedAt, State = state, ErrorCode = code, ElapsedMilliseconds = elapsed.ElapsedMilliseconds
        };
    }

    private async Task<ProbeTarget> TargetAsync(HealthProbeRequest request, CancellationToken cancellationToken)
    {
        SiteTopologySnapshot snapshot;
        try { snapshot = await _topology.GetSiteTopologyAsync(cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch { throw Invalid("HEALTH_TOPOLOGY_UNAVAILABLE", "无法读取当前站点与应用，请刷新后重试。", 503); }
        cancellationToken.ThrowIfCancellationRequested();
        var site = snapshot.Sites.SingleOrDefault(item => Equal(item.Name, request.SiteName));
        if (site is null) throw Invalid("HEALTH_SITE_NOT_FOUND", "健康检查站点已不存在，请刷新后重新选择。");
        var application = site.Applications.SingleOrDefault(item => Equal(item.Path, request.ApplicationPath));
        if (application is null) throw Invalid("HEALTH_APPLICATION_NOT_FOUND", "健康检查应用已不存在，请刷新后重新选择。");
        var uri = new Uri(request.Url, UriKind.Absolute);
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var owningApplication = site.Applications.Where(item => Within(path, item.Path))
            .OrderByDescending(item => item.Path.Length).FirstOrDefault();
        if (owningApplication is null || !Equal(owningApplication.Path, application.Path))
            throw Invalid("HEALTH_APPLICATION_PATH_MISMATCH", "检查地址必须属于所选应用。");
        if (_options.ReservedPorts.Contains(uri.Port) || uri.Port == _options.Port)
            throw Invalid("HEALTH_MANAGEMENT_PORT_DENIED", "检查地址不能使用管理通道端口。");
        IPAddress[] local;
        try { local = _localAddresses().Select(Normalize).Distinct().ToArray(); }
        catch { throw Invalid("HEALTH_LOCAL_ADDRESS_UNAVAILABLE", "无法核实本机网络地址。", 503); }
        var binding = site.Bindings.Where(item => Matches(item, uri, local))
            .OrderByDescending(item => !string.IsNullOrEmpty(item.HostName))
            .ThenByDescending(item => !Wildcard(item.IpAddress)).FirstOrDefault();
        if (binding is null) throw Invalid("HEALTH_BINDING_MISMATCH", "检查地址必须匹配所选站点的当前 HTTP 或 HTTPS 绑定。");
        // An IP URL must reach that exact local IP. Domain bindings retain their hostname for Host/TLS SNI.
        var address = IPAddress.TryParse(uri.Host.Trim('[', ']'), out var hostAddress) ? Normalize(hostAddress)
            : Wildcard(binding.IpAddress) ? binding.IpAddress == "::" ? IPAddress.IPv6Loopback : IPAddress.Loopback
            : Normalize(IPAddress.Parse(binding.IpAddress));
        if (snapshot.Sites.Where(other => !Equal(other.Name, site.Name)).Any(other => other.Bindings.Any(otherBinding =>
                RoutesTo(otherBinding, uri, address) && (string.IsNullOrEmpty(binding.HostName) || !string.IsNullOrEmpty(otherBinding.HostName)))))
            throw Invalid("HEALTH_BINDING_AMBIGUOUS", "该检查地址可能路由到其他站点，请使用明确的站点域名或绑定地址。");
        return new ProbeTarget(uri, address, site.State);
    }

    private static void ValidateShape(HealthProbeRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SiteName) || request.SiteName.Length > 256 ||
            request.SiteName != request.SiteName.Trim() || request.SiteName.Any(char.IsControl) ||
            string.IsNullOrEmpty(request.ApplicationPath) || request.ApplicationPath.Length > 1024 ||
            !CleanPath(request.ApplicationPath) || request.ApplicationPath[0] != '/' ||
            request.ApplicationPath.Length > 1 && request.ApplicationPath.EndsWith('/') ||
            request.ExpectedStatusCode is < 200 or > 299 || request.TimeoutSeconds is < 1 or > 15)
            throw Invalid("HEALTH_REQUEST_INVALID", "检查参数无效；期望状态码须为 200–299，超时须为 1–15 秒。");
        if (string.IsNullOrEmpty(request.Url) || request.Url.Length > 2048 || request.Url.Any(char.IsWhiteSpace) ||
            request.Url.Any(char.IsControl) || request.Url.Contains('\\') || request.Url.Contains('?') || request.Url.Contains('#') ||
            !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            string.IsNullOrEmpty(uri.Host) || uri.Port is < 1 or > 65535)
            throw Invalid("HEALTH_URL_INVALID", "检查地址须为不含凭据、查询参数或片段的完整 HTTP 或 HTTPS 地址。");
        var authorityStart = request.Url.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0) throw Invalid("HEALTH_URL_INVALID", "检查地址格式无效。");
        var pathStart = request.Url.IndexOf('/', authorityStart + 3);
        if (!CleanPath(pathStart < 0 ? "/" : request.Url[pathStart..]))
            throw Invalid("HEALTH_PATH_INVALID", "检查路径包含不明确的编码或路径段。");
    }

    private static bool CleanPath(string path)
    {
        if (path.Contains("//", StringComparison.Ordinal) || path.Contains('\\') || path.Any(char.IsControl)) return false;
        string decoded;
        try { decoded = Uri.UnescapeDataString(path); }
        catch (UriFormatException) { return false; }
        // Reject encoded separators, double encoding and dot segments before Uri normalization.
        if (decoded.Contains('%') || decoded.Contains('\\') || decoded.Contains('?') || decoded.Contains('#') ||
            decoded.Any(char.IsControl) || decoded.Contains("//", StringComparison.Ordinal) ||
            path.Contains("%2f", StringComparison.OrdinalIgnoreCase) || path.Contains("%5c", StringComparison.OrdinalIgnoreCase)) return false;
        return !decoded.Split('/').Any(segment => segment is "." or "..");
    }

    private static bool Matches(BindingConfig binding, Uri uri, IPAddress[] local)
    {
        if (!Equal(binding.Protocol, uri.Scheme) || binding.Port != uri.Port) return false;
        if (!Wildcard(binding.IpAddress) && (!IPAddress.TryParse(binding.IpAddress, out var address) ||
                !local.Contains(Normalize(address)))) return false;
        var uriHost = uri.Host.Trim('[', ']');
        var isIp = IPAddress.TryParse(uriHost, out var ip);
        if (isIp && (!local.Contains(Normalize(ip!)) || !AcceptsAddress(binding.IpAddress, Normalize(ip!)))) return false;
        if (!string.IsNullOrEmpty(binding.HostName)) return Equal(binding.HostName.TrimEnd('.'), uri.IdnHost.TrimEnd('.'));
        return isIp ||
            Equal(uri.IdnHost, "localhost") || Equal(uri.IdnHost.TrimEnd('.'), Environment.MachineName) ||
            Equal(uri.IdnHost.TrimEnd('.'), Dns.GetHostName());
    }

    private static bool RoutesTo(BindingConfig binding, Uri uri, IPAddress address) =>
        Equal(binding.Protocol, uri.Scheme) && binding.Port == uri.Port &&
        (string.IsNullOrEmpty(binding.HostName) || Equal(binding.HostName.TrimEnd('.'), uri.IdnHost.TrimEnd('.'))) &&
        AcceptsAddress(binding.IpAddress, address);

    private static bool AcceptsAddress(string bindingAddress, IPAddress address) => bindingAddress switch
    {
        "*" or "" => true,
        "0.0.0.0" => address.AddressFamily == AddressFamily.InterNetwork,
        "::" => address.AddressFamily == AddressFamily.InterNetworkV6,
        _ => IPAddress.TryParse(bindingAddress, out var ip) && Normalize(ip).Equals(address)
    };

    private static bool Within(string path, string application) => application == "/" ||
        Equal(path, application) || path.StartsWith(application + "/", StringComparison.OrdinalIgnoreCase);
    private static bool Equal(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool Wildcard(string value) => value is "*" or "0.0.0.0" or "::" or "";
    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    private static IPAddress[] LocalAddresses() =>
        [IPAddress.Loopback, IPAddress.IPv6Loopback, .. NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(item => item.GetIPProperties().UnicastAddresses).Select(item => item.Address)];

    private static async Task<int> SendLocalAsync(HttpRequestMessage request, IPAddress address, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            MaxConnectionsPerServer = 1, MaxResponseHeadersLength = 16,
            SslOptions = { CertificateRevocationCheckMode = X509RevocationMode.Online },
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return (int)response.StatusCode;
    }

    private static OperationException Invalid(string code, string message, int statusCode = 400) => new(code, message, statusCode);
    private sealed record ProbeTarget(Uri Uri, IPAddress Address, string SiteState);
    public void Dispose() => _gate.Dispose();
}
