using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using IISManager.Contracts;
using Microsoft.Win32.SafeHandles;

namespace IISManager.Agent;

/// <summary>Bounded local W3C tail sampling. No request identifiers or raw log text leave this reader.</summary>
public static class DiagnosticsIisLogReader
{
    private const int FileLimit = 2;
    private const int TailLimit = 512 * 1024;
    private const int HeaderLimit = 32 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static IisLogDiagnostics Read(string siteName, string configuredRoot, long siteId, DateTimeOffset now,
        ref int byteBudget, CancellationToken cancellation = default)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (siteId <= 0) throw new LogBoundaryException("DIAGNOSTICS_IIS_LOG_PATH_REJECTED");
            var root = LocalPath(configuredRoot);
            var directory = LocalPath(Path.Combine(root, "W3SVC" + siteId.ToString(CultureInfo.InvariantCulture)));
            using var lease = PinDirectories(directory);
            var candidates = Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly).Take(129).ToArray();
            var files = candidates.Where(file => Regex.IsMatch(Path.GetFileName(file), "^u_ex[0-9_]+\\.log$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .Select(file => new FileInfo(file)).OrderByDescending(file => file.LastWriteTimeUtc).Take(FileLimit).ToArray();
            if (files.Length == 0) return Failure("DIAGNOSTICS_IIS_LOG_NOT_FOUND");
            var truncated = candidates.Length > FileLimit;
            var texts = new List<string>();
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                if (byteBudget <= HeaderLimit) { truncated = true; break; }
                using var stream = OpenPinned(file.FullName);
                var length = stream.Length;
                var allowance = Math.Min(TailLimit, byteBudget - HeaderLimit);
                if (length <= allowance)
                {
                    var bytes = new byte[checked((int)length)]; stream.ReadExactly(bytes);
                    byteBudget -= bytes.Length; texts.Add(Utf8.GetString(bytes));
                }
                else
                {
                    truncated = true;
                    var header = new byte[(int)Math.Min(HeaderLimit, length)]; stream.ReadExactly(header);
                    byteBudget -= header.Length;
                    var headerText = Utf8.GetString(header.AsSpan(0, LastNewLine(header)));
                    var fields = headerText.Split('\n').LastOrDefault(line => line.StartsWith("#Fields:", StringComparison.Ordinal));
                    stream.Position = length - allowance;
                    var tail = new byte[allowance]; stream.ReadExactly(tail); byteBudget -= tail.Length;
                    var skip = Array.IndexOf(tail, (byte)'\n');
                    // Discard the first incomplete tail line before decoding UTF-8.
                    texts.Add((fields is null ? "" : fields + "\n") + (skip < 0 ? "" : Utf8.GetString(tail.AsSpan(skip + 1))));
                }
            }
            if (texts.Count == 0) return Failure("DIAGNOSTICS_IIS_LOG_BUDGET_REACHED");
            var parsed = ParseW3c(siteName, texts, now, cancellation);
            return parsed with { Truncated = truncated || parsed.Truncated, SampledFiles = texts.Count };
        }
        catch (OperationCanceledException) { throw; }
        catch (LogBoundaryException exception) { return Failure(exception.Code); }
        catch (UnauthorizedAccessException) { return Failure("DIAGNOSTICS_ACCESS_DENIED"); }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        { return Failure("DIAGNOSTICS_IIS_LOG_UNAVAILABLE"); }
        IisLogDiagnostics Failure(string code) => new(siteName, "unavailable", now.AddHours(-24), now, 0, 0, false, code, []);
    }

    public static IisLogDiagnostics ParseW3c(string siteName, IEnumerable<string> samples, DateTimeOffset now, CancellationToken cancellation = default)
    {
        var rows = new Dictionary<(int Status, int? Sub, long? Win32), (int Count, long Total, int Timed, long? Maximum)>();
        var recent = new PriorityQueue<(int Status, int? Sub, long? Win32, long? Timing), long>();
        var files = 0; var validFields = false; var truncated = false; var malformed = false;
        foreach (var sample in samples.Take(FileLimit))
        {
            files++; string[]? fields = null;
            using var reader = new StringReader(sample);
            while (reader.ReadLine() is { } line)
            {
                cancellation.ThrowIfCancellationRequested();
                if (line.Length > 16384) { malformed = true; continue; }
                if (line.StartsWith("#Fields:", StringComparison.Ordinal))
                {
                    fields = line[8..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    validFields |= fields.Contains("date") && fields.Contains("time") && fields.Contains("sc-status");
                    continue;
                }
                if (line.Length == 0 || line[0] == '#') continue;
                if (fields is null) { malformed = true; continue; }
                var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (values.Length != fields.Length) { malformed = true; continue; }
                string? Value(string name) { var index = Array.IndexOf(fields, name); return index < 0 ? null : values[index]; }
                int? Number(string name) => int.TryParse(Value(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;
                if (!DateTimeOffset.TryParseExact(Value("date") + " " + Value("time"), "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
                { malformed = true; continue; }
                if (when < now.AddHours(-24) || when > now) continue;
                var status = Number("sc-status");
                if (status is null or < 100 or > 599) { malformed = true; continue; }
                long? win32 = uint.TryParse(Value("sc-win32-status"), NumberStyles.None, CultureInfo.InvariantCulture, out var win32Status) ? win32Status : null;
                var timing = long.TryParse(Value("time-taken"), NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) && milliseconds is >= 0 and <= int.MaxValue
                    ? milliseconds : (long?)null;
                var entry = (status.Value, Number("sc-substatus"), win32, timing);
                if (recent.Count == 20000) { recent.EnqueueDequeue(entry, when.UtcDateTime.Ticks); truncated = true; }
                else recent.Enqueue(entry, when.UtcDateTime.Ticks);
            }
        }
        foreach (var item in recent.UnorderedItems)
        {
            cancellation.ThrowIfCancellationRequested();
            var entry = item.Element;
            if (entry.Status < 400) continue;
            var key = (entry.Status, entry.Sub, entry.Win32);
            rows.TryGetValue(key, out var old);
            rows[key] = (old.Count + 1, old.Total + (entry.Timing ?? 0), old.Timed + (entry.Timing is null ? 0 : 1),
                entry.Timing is null ? old.Maximum : Math.Max(old.Maximum ?? 0, entry.Timing.Value));
        }
        var errors = rows.OrderByDescending(row => row.Value.Count).ThenBy(row => row.Key.Status).Take(100)
            .Select(row => new IisErrorSummary(row.Key.Status, row.Key.Sub, row.Key.Win32, row.Value.Count,
                row.Value.Timed == 0 ? null : Math.Round((double)row.Value.Total / row.Value.Timed, 1), row.Value.Maximum)).ToArray();
        return new(siteName, !validFields ? "unavailable" : malformed ? "partial" : "available", now.AddHours(-24), now,
            files, recent.Count, truncated || rows.Count > 100,
            !validFields ? "DIAGNOSTICS_IIS_FIELDS_UNAVAILABLE" : malformed ? "DIAGNOSTICS_IIS_LINES_SKIPPED" : null, errors);
    }

    private static int LastNewLine(byte[] bytes)
    {
        var index = Array.LastIndexOf(bytes, (byte)'\n'); return index < 0 ? 0 : index + 1;
    }

    private static string LocalPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal) || value.Any(char.IsControl) ||
            OperatingSystem.IsWindows() && (value.Length < 3 || value[1] != ':' || value.AsSpan(2).Contains(':')))
            throw new LogBoundaryException("DIAGNOSTICS_IIS_LOG_PATH_REJECTED");
        var full = Path.GetFullPath(value);
        if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(full)!).DriveType != DriveType.Fixed)
            throw new LogBoundaryException("DIAGNOSTICS_IIS_LOG_PATH_REJECTED");
        return full;
    }

    private static IDisposable PinDirectories(string path)
    {
        var directories = new List<string>();
        for (var item = new DirectoryInfo(path); item is not null; item = item.Parent) directories.Add(item.FullName);
        directories.Reverse(); var leases = new Leases();
        try
        {
            foreach (var directory in directories)
            {
                var info = new DirectoryInfo(directory);
                if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new LogBoundaryException("DIAGNOSTICS_IIS_LOG_PATH_REJECTED");
                if (!OperatingSystem.IsWindows()) continue;
                var handle = CreateFile(directory, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new LogBoundaryException("DIAGNOSTICS_IIS_LOG_PATH_REJECTED"); }
                leases.Handles.Add(handle);
                if (!GetFileInformationByHandle(handle, out var state) ||
                    (state.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (state.Attributes & (uint)FileAttributes.Directory) == 0)
                    throw new LogBoundaryException("DIAGNOSTICS_IIS_LOG_PATH_REJECTED");
            }
            return leases;
        }
        catch { leases.Dispose(); throw; }
    }

    private static FileStream OpenPinned(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new LogBoundaryException("DIAGNOSTICS_IIS_LOG_PATH_REJECTED");
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        var handle = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new LogBoundaryException("DIAGNOSTICS_IIS_LOG_PATH_REJECTED"); }
        if (!GetFileInformationByHandle(handle, out var state) || state.Links != 1 ||
            (state.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory)) != 0)
        { handle.Dispose(); throw new LogBoundaryException("DIAGNOSTICS_IIS_LOG_PATH_REJECTED"); }
        return new FileStream(handle, FileAccess.Read);
    }
    private sealed class LogBoundaryException(string code) : Exception(code) { internal string Code { get; } = code; }
    private sealed class Leases : IDisposable
    {
        internal readonly List<SafeFileHandle> Handles = [];
        public void Dispose() { foreach (var handle in Handles) handle.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
}
