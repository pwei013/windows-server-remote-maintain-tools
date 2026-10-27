using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using IISManager.Contracts;
using IISManager.Core;

namespace IISManager.Console;

public sealed class AgentCallException(string code, int statusCode = 502) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class AgentClient : IDisposable
{
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
    private readonly X509Certificate2? _clientCertificate;
    private readonly TimeSpan _timeout;

    public AgentClient(ConsoleOptions options)
    {
        _timeout = TimeSpan.FromSeconds(options.AgentTimeoutSeconds);
        foreach (var server in options.Servers)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, CheckCertificateRevocationList = true };
            if (new Uri(server.AgentUrl).Scheme == "https")
            {
                _clientCertificate ??= MachineCertificates.Load(options.AgentClientCertificateThumbprint, "1.3.6.1.5.5.7.3.2");
                handler.ClientCertificates.Add(_clientCertificate);
                var expected = MachineCertificates.Normalize(server.ServerCertificateThumbprint);
                handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                    errors == SslPolicyErrors.None && certificate is not null &&
                    string.Equals(MachineCertificates.Normalize(certificate.Thumbprint), expected, StringComparison.Ordinal) &&
                    certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(extension =>
                        extension.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1"));
            }
            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(server.AgentUrl),
                Timeout = TimeSpan.FromSeconds(options.AgentTimeoutSeconds)
            };
            if (client.BaseAddress.Scheme == "http" && !server.Simulation)
            {
                if (options.TransportMode != IntranetTransportSecurity.HttpToken ||
                    !IntranetTransportSecurity.IsValidAccessToken(server.AccessToken))
                    throw new InvalidOperationException("AGENT_HTTP_TOKEN_REQUIRED");
                // This secret belongs only to the server-side outbound client, never to browser DTOs.
                client.DefaultRequestHeaders.Add(IntranetTransportSecurity.TokenHeader, server.AccessToken);
            }
            _clients.Add(server.Id, client);
        }
    }

    public bool Exists(string id) => _clients.ContainsKey(id);

    public async Task<T> GetAsync<T>(string serverId, string route, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "agent/v1/" + route);
        return await SendAsync<T>(serverId, request, cancellationToken);
    }

    public async Task<T> PostAsync<T>(string serverId, string route, OperationRequest body, string sourceIp, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "agent/v1/" + route);
        request.Headers.Add("X-IISManager-Source-IP", sourceIp);
        request.Content = JsonContent.Create(body, options: Protocol.Json);
        return await SendAsync<T>(serverId, request, cancellationToken);
    }

    public async Task<T> PostHealthAsync<T>(string serverId, string route, HealthProbeRequest body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "agent/v1/" + route);
        request.Content = JsonContent.Create(body, options: Protocol.Json);
        return await SendAsync<T>(serverId, request, cancellationToken);
    }

    private async Task<T> SendAsync<T>(string serverId, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_clients.TryGetValue(serverId, out var client)) throw new AgentCallException("SERVER_NOT_FOUND", 404);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeout);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                // Error responses and exception text are never relayed or logged verbatim.
                throw new AgentCallException(response.StatusCode switch
                {
                    HttpStatusCode.NotFound => "AGENT_NOT_FOUND",
                    HttpStatusCode.Conflict => "AGENT_CONFLICT",
                    HttpStatusCode.BadRequest => "AGENT_REQUEST_REJECTED",
                    HttpStatusCode.Forbidden => "AGENT_ACCESS_DENIED",
                    HttpStatusCode.Unauthorized => "AGENT_AUTHENTICATION_REJECTED",
                    _ => "AGENT_HTTP_ERROR"
                }, (int)response.StatusCode is 400 or 404 or 409 ? (int)response.StatusCode : 502);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            return await JsonSerializer.DeserializeAsync<T>(stream, Protocol.Json, deadline.Token)
                   ?? throw new AgentCallException("AGENT_RESPONSE_INVALID");
        }
        catch (OperationCanceledException) { throw new AgentCallException("AGENT_TIMEOUT", 504); }
        catch (HttpRequestException exception)
        {
            throw new AgentCallException(exception.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "AGENT_DNS_FAILED",
                HttpRequestError.SecureConnectionError => "AGENT_TLS_FAILED",
                _ => "AGENT_UNREACHABLE"
            });
        }
        catch (JsonException) { throw new AgentCallException("AGENT_RESPONSE_INVALID"); }
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values) client.Dispose();
        _clientCertificate?.Dispose();
    }
}
