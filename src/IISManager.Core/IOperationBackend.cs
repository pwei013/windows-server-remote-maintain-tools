using IISManager.Contracts;

namespace IISManager.Core;

public interface IOperationBackend
{
    Task<ValidationResult> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default);
    // Writes are serialized and revalidated by the coordinator. Once a mutation may have
    // occurred, adapters return partial/unknown rather than claiming nothing happened.
    Task<ExecutionResult> ExecuteAsync(OperationRequest request, CancellationToken cancellationToken = default);
}

public interface IWindowsServiceBackend : IOperationBackend
{
    Task<WindowsServiceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<WindowsServiceConfig?> GetServiceAsync(string name, CancellationToken cancellationToken = default);
}

public sealed class OperationBackendRouter(IIisBackend iis, IWindowsServiceBackend services) : IOperationBackend
{
    private IOperationBackend Resolve(string type) => OperationTypes.Iis.Contains(type, StringComparer.Ordinal) ? iis
        : OperationTypes.WindowsServices.Contains(type, StringComparer.Ordinal) ? services
        : throw new OperationException("OPERATION_UNSUPPORTED", "不支持此操作类型。");

    public Task<ValidationResult> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default) =>
        Resolve(request.Type).ValidateAsync(request, cancellationToken);
    public Task<ExecutionResult> ExecuteAsync(OperationRequest request, CancellationToken cancellationToken = default) =>
        Resolve(request.Type).ExecuteAsync(request, cancellationToken);
}
