using IISManager.Contracts;

namespace IISManager.Core;

public interface IIisBackend : IOperationBackend
{
    Task<AgentInfo> GetInfoAsync(CancellationToken cancellationToken = default);
    Task<IisSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<PoolConfig?> GetPoolAsync(string name, CancellationToken cancellationToken = default);
    Task<CertificateInfo[]> GetCertificatesAsync(CancellationToken cancellationToken = default);
}

public sealed class OperationException(string code, string message, int statusCode = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
