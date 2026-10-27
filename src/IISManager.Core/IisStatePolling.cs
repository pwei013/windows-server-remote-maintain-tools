using System.Diagnostics;

namespace IISManager.Core;

public static class IisStatePolling
{
    // After a mutation, a new handle can still report the previous stable state. Poll reads only;
    // neither request cancellation nor a delayed status must cause the mutation to be replayed.
    public static Task<T> ReadUntilAsync<T>(Func<T> read, Func<T, bool> complete,
        TimeSpan? timeout = null, TimeSpan? pollInterval = null) => ReadUntilAsync(read, complete, timeout, pollInterval, null);

    public static async Task<T> ReadUntilAsync<T>(Func<T> read, Func<T, bool> complete,
        TimeSpan? timeout, TimeSpan? pollInterval, Func<Exception, bool>? retryRead)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(200);
        if (limit < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (interval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        var watch = Stopwatch.StartNew();
        while (true)
        {
            T current;
            try { current = read(); }
            catch (Exception exception) when (retryRead?.Invoke(exception) == true && watch.Elapsed < limit)
            {
                // Only explicitly allowed read failures may be transient. At the deadline the
                // actual exception escapes; never invent a successful observation or retry a write.
                await Task.Delay(interval);
                continue;
            }
            if (complete(current) || watch.Elapsed >= limit) return current;
            await Task.Delay(interval);
        }
    }
}
