using System.ComponentModel;

namespace IISManager.Setup.Core;

public sealed record SafeExceptionDetail(string ExceptionType, int HResult, int? NativeErrorCode);

// Exception messages, stack traces and Data can contain configuration or credentials.
public sealed record SafeFailureSummary(string LeafExceptionType, int LeafHResult,
    SafeExceptionDetail[] ExceptionChain, bool ChainTruncated)
{
    public static SafeFailureSummary From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var chain = new List<SafeExceptionDetail>();
        var current = exception;
        while (true)
        {
            chain.Add(new(current.GetType().Name, current.HResult,
                current is Win32Exception native ? native.NativeErrorCode : null));
            if (current.InnerException is null || chain.Count == 16) break;
            current = current.InnerException;
        }
        var leaf = chain[^1];
        return new(leaf.ExceptionType, leaf.HResult, chain.ToArray(), current.InnerException is not null);
    }
}
