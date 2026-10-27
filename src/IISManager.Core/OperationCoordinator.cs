using System.Threading.Channels;
using IISManager.Contracts;
using Microsoft.Extensions.Logging;

namespace IISManager.Core;

public sealed class OperationCoordinator(IOperationBackend backend, FileOperationStore store,
    ILogger<OperationCoordinator> logger, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim acceptanceGate = new(1, 1);
    private readonly Channel<Guid> queue = Channel.CreateBounded<Guid>(new BoundedChannelOptions(256)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait
    });
    private volatile bool ready;
    private volatile bool storageHealthy = true;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var record in await store.ReadAllAsync(cancellationToken))
            {
                if (OperationStates.IsFinal(record.State)) continue;
                // No automatic replay, even when the last persisted state says queued.
                await store.SaveAsync(record with
                {
                    State = OperationStates.Unknown,
                    CompletedAt = clock.GetUtcNow(),
                    Error = new("AGENT_RESTARTED", "Agent 已重启，需独立核实现场；未自动重放。")
                }, cancellationToken);
                logger.LogWarning("operation.recovery id={OperationId} state=unknown", record.Request.OperationId);
            }
            ready = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            storageHealthy = false;
            throw new OperationException("STORAGE_UNAVAILABLE", "任务存储无法可靠读取，已停止受理操作。", 503);
        }
    }

    public async Task<ValidationResult> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        CheckEnvelope(request, requireFingerprint: false);
        return await backend.ValidateAsync(request, cancellationToken);
    }

    public async Task<OperationRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            var record = await store.GetAsync(id, cancellationToken);
            return record is not null && !storageHealthy && !OperationStates.IsFinal(record.State)
                ? record with { State = OperationStates.Unknown, Error = new("STORAGE_UNAVAILABLE", "状态存储故障，需核实现场，禁止自动重放。") }
                : record;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            storageHealthy = false;
            throw new OperationException("STORAGE_UNAVAILABLE", "任务存储无法可靠读取，已停止受理新操作。", 503);
        }
    }

    public async Task<OperationRecord> SubmitAsync(OperationRequest request, string? sourceIp = null,
        CancellationToken cancellationToken = default)
    {
        CheckServiceSafety(request);
        await acceptanceGate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            var hash = CanonicalJson.Hash(request);
            var existing = await store.GetAsync(request.OperationId, cancellationToken);
            if (existing is not null)
            {
                if (existing.RequestHash != hash)
                    throw new OperationException("OPERATION_ID_CONFLICT", "同一操作编号对应不同请求。", 409);
                return existing;
            }
            CheckEnvelope(request, requireFingerprint: true);
            var validation = await backend.ValidateAsync(request, cancellationToken);
            if (!string.Equals(validation.Fingerprint, request.ExpectedFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new OperationException("CONFIGURATION_CONFLICT", "配置或关联资源已变化，请重新预检查。", 409);
            if (!validation.Valid) throw new OperationException(validation.Errors.FirstOrDefault()?.Code ?? "VALIDATION_FAILED",
                validation.Errors.FirstOrDefault()?.Message ?? "操作预检查失败。", 422);
            var record = new OperationRecord
            {
                Request = request, RequestHash = hash, AcceptedAt = clock.GetUtcNow(),
                Before = validation.Before, SourceIp = sourceIp
            };
            await PersistAsync(record, cancellationToken);
            if (!queue.Writer.TryWrite(request.OperationId))
            {
                record = record with { State = OperationStates.Failed, CompletedAt = clock.GetUtcNow(),
                    Error = new("QUEUE_FULL", "执行队列已满，本操作未执行。") };
                await PersistAsync(record, CancellationToken.None);
            }
            logger.LogInformation("operation.accepted id={OperationId} type={Type} pool={Pool} site={Site} service={Service} source={SourceIp} state={State}",
                request.OperationId, request.Type, request.Target.ApplicationPoolName, request.Target.SiteName, request.Target.ServiceName, sourceIp, record.State);
            return record;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            storageHealthy = false;
            throw new OperationException("STORAGE_UNAVAILABLE", "任务存储失败，已停止受理新操作。", 503);
        }
        finally { acceptanceGate.Release(); }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var id in queue.Reader.ReadAllAsync(cancellationToken))
        {
            if (!storageHealthy) continue;
            await ExecuteOneAsync(id, cancellationToken);
        }
    }

    private async Task ExecuteOneAsync(Guid id, CancellationToken cancellationToken)
    {
        var mayHaveWritten = false;
        OperationRecord? record = null;
        try
        {
            record = await store.GetAsync(id, cancellationToken)
                ?? throw new IOException("LEDGER_RECORD_MISSING");
            if (record.State != OperationStates.Queued) return;
            if (record.Request.ExpiresAt <= clock.GetUtcNow())
            {
                await PersistAsync(record with { State = OperationStates.Expired, CompletedAt = clock.GetUtcNow(),
                    Error = new("OPERATION_EXPIRED", "操作开始前已过期，未执行。") }, cancellationToken);
                logger.LogInformation("operation.rejected id={OperationId} state=expired code=OPERATION_EXPIRED", id);
                return;
            }
            var validation = await backend.ValidateAsync(record.Request, cancellationToken);
            if (!validation.Valid || !string.Equals(validation.Fingerprint, record.Request.ExpectedFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                await PersistAsync(record with { State = OperationStates.Failed, CompletedAt = clock.GetUtcNow(),
                    Error = !validation.Valid ? validation.Errors.FirstOrDefault() ?? new("VALIDATION_FAILED", "执行前校验失败，未执行。")
                    : new("CONFIGURATION_CONFLICT", "执行前配置发生变化，未执行。") }, cancellationToken);
                logger.LogInformation("operation.rejected id={OperationId} state=failed code=PRE_EXECUTION_CONFLICT", id);
                return;
            }
            record = record with { State = OperationStates.Running, StartedAt = clock.GetUtcNow(), Before = validation.Before };
            await PersistAsync(record, cancellationToken);
            if (record.Request.ExpiresAt <= clock.GetUtcNow())
            {
                await PersistAsync(record with { State = OperationStates.Expired, CompletedAt = clock.GetUtcNow(),
                    Error = new("OPERATION_EXPIRED", "持久化期间已过期，未执行。") }, CancellationToken.None);
                logger.LogInformation("operation.rejected id={OperationId} state=expired code=OPERATION_EXPIRED", id);
                return;
            }
            // Do not cancel the operation halfway merely because the HTTP client disconnected.
            mayHaveWritten = true;
            var result = await backend.ExecuteAsync(record.Request, CancellationToken.None);
            if (!OperationStates.IsFinal(result.State))
                throw new InvalidOperationException("BACKEND_STATE_INVALID");
            record = record with { State = result.State, After = result.After, Error = result.Error, CompletedAt = clock.GetUtcNow() };
            await PersistAsync(record, CancellationToken.None);
            foreach (var change in validation.Changes)
                logger.LogInformation("operation.change id={OperationId} field={Field} before={Before} after={After} state={State}",
                    id, change.Field, change.Before, change.After, record.State);
            logger.LogInformation("operation.completed id={OperationId} type={Type} pool={Pool} site={Site} service={Service} source={SourceIp} state={State} code={Code}",
                id, record.Request.Type, record.Request.Target.ApplicationPoolName, record.Request.Target.SiteName, record.Request.Target.ServiceName, record.SourceIp, record.State, record.Error?.Code);
        }
        catch (Exception exception)
        {
            if (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) storageHealthy = false;
            logger.LogError("operation.interrupted id={OperationId} exceptionType={ExceptionType} hresult={HResult} mayHaveWritten={MayHaveWritten}",
                id, exception.GetType().Name, exception.HResult, mayHaveWritten);
            if (record is null || !storageHealthy) return;
            try
            {
                await PersistAsync(record with
                {
                    State = mayHaveWritten ? OperationStates.Unknown : OperationStates.Failed,
                    CompletedAt = clock.GetUtcNow(),
                    Error = new(mayHaveWritten ? "EXECUTION_UNKNOWN" : "EXECUTION_REJECTED",
                        mayHaveWritten ? "无法确认执行结果，请核实现场，禁止自动重放。" : "执行前发生异常，本操作未执行。")
                }, CancellationToken.None);
            }
            catch (Exception persistenceError)
            {
                storageHealthy = false;
                logger.LogError("operation.persistenceFailed id={OperationId} exceptionType={ExceptionType}", id, persistenceError.GetType().Name);
            }
        }
    }

    private async Task PersistAsync(OperationRecord record, CancellationToken cancellationToken)
    {
        try { await store.SaveAsync(record, cancellationToken); }
        catch { storageHealthy = false; throw; }
    }

    private void EnsureReady()
    {
        if (!ready || !storageHealthy) throw new OperationException("STORAGE_UNAVAILABLE", "Agent 任务存储尚未就绪或故障，拒绝写操作。", 503);
    }

    private void CheckEnvelope(OperationRequest request, bool requireFingerprint)
    {
        CheckServiceSafety(request);
        if (request.OperationId == Guid.Empty || !OperationTypes.All.Contains(request.Type) || request.Target is null ||
            request.Parameters.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new OperationException("REQUEST_INVALID", "操作编号、类型、目标或参数格式无效。");
        if (requireFingerprint && (request.ExpectedFingerprint?.Length != 64 ||
            !request.ExpectedFingerprint.All(Uri.IsHexDigit)))
            throw new OperationException("FINGERPRINT_REQUIRED", "请先预检查并提交有效配置指纹。");
        if (request.ExpiresAt <= clock.GetUtcNow()) throw new OperationException("OPERATION_EXPIRED", "操作已过期。", 410);
        if (request.ExpiresAt > clock.GetUtcNow().AddMinutes(15))
            throw new OperationException("EXPIRY_INVALID", "操作有效期不能超过 15 分钟。");
    }

    private static void CheckServiceSafety(OperationRequest request)
    {
        if (OperationTypes.WindowsServices.Contains(request.Type, StringComparer.Ordinal) && !WindowsServiceRequestSafety.IsValid(request))
            throw new OperationException("SERVICE_REQUEST_INVALID", "服务目标或参数无效；启动参数不得包含密码、令牌或连接凭据。");
    }
}
