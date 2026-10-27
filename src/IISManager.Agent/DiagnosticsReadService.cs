using System.Collections.Concurrent;
using IISManager.Contracts;
using IISManager.Core;

namespace IISManager.Agent;

public sealed record DiagnosticCpuSample(ulong IdleTicks, ulong KernelTicks, ulong UserTicks, bool Supported = true);
public sealed record DiagnosticProcessSample(string Kind, string Name, string State, int? ProcessId,
    DateTimeOffset? StartedAt, long? CpuTicks, long? WorkingSetBytes, long? PrivateMemoryBytes, string? ErrorCode = null);

/// <summary>Injected sources allow tests to exercise diagnostics without native calls or real servers.</summary>
public interface IDiagnosticsCollector
{
    Task<(DiagnosticCpuSample Cpu, long TotalMemory, long AvailableMemory, DiskResourceUsage[] Disks)> ReadResourcesAsync(CancellationToken cancellation);
    Task<DiagnosticProcessSample[]> ReadProcessesAsync(CancellationToken cancellation);
    Task<DiagnosticEvent[]> ReadEventsAsync(CancellationToken cancellation);
    Task<IisLogDiagnostics[]> ReadIisLogsAsync(CancellationToken cancellation);
}

public sealed class DiagnosticsReadService
{
    private const int CacheSeconds = 10;
    private readonly string _mode;
    private readonly IDiagnosticsCollector? _collector;
    private readonly TimeProvider _time;
    private readonly int _processors;
    private readonly TimeSpan _deadline;
    private readonly object _gate = new();
    private Task<DiagnosticsSnapshot>? _inflight;
    private DiagnosticsSnapshot? _cached;
    private long _cachedAt;
    private DiagnosticCpuSample? _cpu;
    private readonly Dictionary<(int Pid, DateTimeOffset Start), (long Cpu, long Timestamp)> _processSamples = [];

    public DiagnosticsReadService(AgentOptions options, IWindowsServiceBackend services)
        : this(options, OperatingSystem.IsWindows() ? new WindowsDiagnosticsCollector(services) : null) { }

    public DiagnosticsReadService(AgentOptions options, IDiagnosticsCollector? collector, TimeProvider? time = null,
        int? logicalProcessors = null, TimeSpan? deadline = null)
    {
        _mode = options.Mode == "Simulation" ? "Simulation" : "Windows";
        _collector = collector;
        _time = time ?? TimeProvider.System;
        _processors = Math.Max(1, logicalProcessors ?? Environment.ProcessorCount);
        _deadline = deadline ?? TimeSpan.FromSeconds(10);
    }

    public async Task<DiagnosticsSnapshot> GetAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (_mode == "Simulation") return Empty("simulation", "DIAGNOSTICS_SIMULATION");
        if (_collector is null) return Empty("unavailable", "DIAGNOSTICS_WINDOWS_REQUIRED");
        Task<DiagnosticsSnapshot> collection;
        lock (_gate)
        {
            if (_cached is not null && _time.GetElapsedTime(_cachedAt) < TimeSpan.FromSeconds(CacheSeconds)) return _cached;
            // Keep one native collection alive if a platform API stalls; repeated browser refreshes cannot spawn unbounded work.
            collection = _inflight ??= Task.Run(CollectAsync);
        }
        DiagnosticsSnapshot result;
        try { result = await collection.WaitAsync(_deadline, cancellation); }
        catch (TimeoutException) { return Empty("unavailable", "DIAGNOSTICS_TIMEOUT"); }
        lock (_gate)
        {
            if (ReferenceEquals(_inflight, collection))
            {
                _cached = result; _cachedAt = _time.GetTimestamp(); _inflight = null;
            }
        }
        return result;
    }

    private async Task<DiagnosticsSnapshot> CollectAsync()
    {
        using var timeout = new CancellationTokenSource(_deadline);
        var modules = new ConcurrentBag<DiagnosticModuleStatus>();
        var now = _time.GetUtcNow();
        async Task<T?> Read<T>(string module, Func<CancellationToken, Task<T>> read) where T : class
        {
            try
            {
                var data = await read(timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                modules.Add(new(module, "available")); return data;
            }
            catch (Exception exception)
            {
                modules.Add(new(module, "unavailable", SafeError(exception)));
                return null;
            }
        }
        var resources = Read("resources", async cancellation =>
        {
            var sample = await _collector!.ReadResourcesAsync(cancellation);
            if (sample.TotalMemory <= 0 || sample.AvailableMemory < 0 || sample.AvailableMemory > sample.TotalMemory)
                throw new InvalidDataException();
            double? cpu = null;
            if (sample.Cpu.Supported && _cpu is { Supported: true } old && sample.Cpu.KernelTicks >= old.KernelTicks && sample.Cpu.UserTicks >= old.UserTicks && sample.Cpu.IdleTicks >= old.IdleTicks)
            {
                var total = sample.Cpu.KernelTicks - old.KernelTicks + sample.Cpu.UserTicks - old.UserTicks;
                var idle = sample.Cpu.IdleTicks - old.IdleTicks;
                if (total > 0 && idle <= total) cpu = Math.Round(100d * (total - idle) / total, 1);
            }
            _cpu = sample.Cpu;
            if (!sample.Cpu.Supported) modules.Add(new("resources.cpu", "unavailable", "DIAGNOSTICS_CPU_GROUP_UNSUPPORTED"));
            if (sample.Disks.Any(disk => disk.ErrorCode is not null))
                modules.Add(new("resources.disks", "partial", "DIAGNOSTICS_DISK_UNAVAILABLE"));
            return new ServerResourceMetrics(cpu, sample.TotalMemory, sample.AvailableMemory, _processors, sample.Disks);
        });
        var processes = Read("processes", async cancellation =>
        {
            var samples = await _collector!.ReadProcessesAsync(cancellation);
            var selected = samples.Where(sample => sample.Kind == "applicationPool").Take(100)
                .Concat(samples.Where(sample => sample.Kind == "windowsService").Take(100)).ToArray();
            var timestamp = _time.GetTimestamp();
            var next = new Dictionary<(int, DateTimeOffset), (long, long)>();
            var rows = selected.Select(sample =>
            {
                double? cpu = null;
                if (sample.ProcessId is { } pid && sample.StartedAt is { } started && sample.CpuTicks is { } ticks)
                {
                    var key = (pid, started);
                    if (_processSamples.TryGetValue(key, out var old) && ticks >= old.Cpu)
                    {
                        var elapsed = _time.GetElapsedTime(old.Timestamp, timestamp).TotalSeconds;
                        if (elapsed > 0) cpu = Math.Round(Math.Clamp(100d * TimeSpan.FromTicks(ticks - old.Cpu).TotalSeconds / elapsed / _processors, 0, 100), 1);
                    }
                    next[key] = (ticks, timestamp);
                }
                return new ProcessDiagnostics(sample.Kind, sample.Name, sample.State, sample.ProcessId, cpu,
                    sample.WorkingSetBytes, sample.PrivateMemoryBytes, sample.StartedAt,
                    sample.StartedAt is { } start ? Math.Max(0, (now - start).TotalSeconds) : null, sample.ErrorCode);
            }).ToArray();
            _processSamples.Clear(); foreach (var item in next) _processSamples[item.Key] = item.Value;
            if (samples.Length > selected.Length) modules.Add(new("processes.limit", "partial", "DIAGNOSTICS_LIMIT_REACHED", true));
            if (rows.Any(row => row.ErrorCode is not null)) modules.Add(new("processes.items", "partial", "DIAGNOSTICS_PROCESS_PARTIAL"));
            return rows;
        });
        var events = Read("events", async cancellation =>
        {
            var rows = await _collector!.ReadEventsAsync(cancellation);
            if (rows.Length > 50) modules.Add(new("events.limit", "partial", "DIAGNOSTICS_LIMIT_REACHED", true));
            return rows.Take(50).ToArray();
        });
        var logs = Read("iisLogs", async cancellation =>
        {
            var all = await _collector!.ReadIisLogsAsync(cancellation);
            if (all.Length > 32) modules.Add(new("iisLogs.limit", "partial", "DIAGNOSTICS_LIMIT_REACHED", true));
            var rows = all.Take(32).ToArray();
            if (rows.Any(row => row.State != "available" || row.Truncated))
                modules.Add(new("iisLogs.items", "partial", "DIAGNOSTICS_IIS_LOG_PARTIAL", true));
            return rows;
        });
        await Task.WhenAll(resources, processes, events, logs);
        return new(now, _mode, CacheSeconds, await resources, await processes, await events, await logs,
            modules.OrderBy(module => module.Module, StringComparer.Ordinal).ToArray());
    }

    private DiagnosticsSnapshot Empty(string state, string code) => new(_time.GetUtcNow(), _mode, CacheSeconds,
        null, null, null, null, new[] { "resources", "processes", "events", "iisLogs" }.Select(module => new DiagnosticModuleStatus(module, state, code)).ToArray());
    private static string SafeError(Exception exception) => exception switch
    {
        OperationCanceledException => "DIAGNOSTICS_TIMEOUT",
        UnauthorizedAccessException => "DIAGNOSTICS_ACCESS_DENIED",
        System.ComponentModel.Win32Exception { NativeErrorCode: 5 } => "DIAGNOSTICS_ACCESS_DENIED",
        System.ComponentModel.Win32Exception => "DIAGNOSTICS_NATIVE_UNAVAILABLE",
        _ => "DIAGNOSTICS_READ_UNAVAILABLE"
    };
}
