using System.Diagnostics;

namespace IISManager.Core;

public sealed record SiteRestartEvidence(long Id, string State, string ConfigurationFingerprint);

// A restart crosses two mutation boundaries. Failure after stop must never trigger a replay.
public static class SiteRestartSequence
{
    public static async Task ExecuteAsync(SiteRestartEvidence before, Action stop,
        Func<SiteRestartEvidence?> read, Action<SiteRestartEvidence> start, TimeSpan? stopTimeout = null)
    {
        if (before.State != "Started") throw new OperationException("site.notRunning", "只能重新启动正在运行的站点。");
        var timeout = stopTimeout ?? TimeSpan.FromSeconds(10);
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stopTimeout));
        var watch = Stopwatch.StartNew();
        stop();
        var remaining = timeout - watch.Elapsed;
        var stopped = await IisStatePolling.ReadUntilAsync(read, current =>
        {
            if (current is null || current.Id != before.Id)
                throw new OperationException("site.targetChanged", "停止后站点已消失或被替换，已暂停启动；请按原编号只读核对。");
            if (current.ConfigurationFingerprint != before.ConfigurationFingerprint)
                throw new OperationException("site.configurationChanged", "停止后站点配置发生变化，已暂停启动；请按原编号只读核对。");
            return current.State == "Stopped";
        }, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        if (stopped!.State != "Stopped")
            throw new OperationException("site.stopTimeout", "停止阶段等待超时，已暂停启动；请按原编号只读核对。");
        // The native start callback rechecks this evidence using its own fresh handle.
        start(stopped);
    }
}
