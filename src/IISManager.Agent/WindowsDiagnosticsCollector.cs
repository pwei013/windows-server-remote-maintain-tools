using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml;
using System.Xml.Linq;
using IISManager.Contracts;
using IISManager.Core;
using Microsoft.Web.Administration;
using Microsoft.Win32.SafeHandles;

namespace IISManager.Agent;

[SupportedOSPlatform("windows")]
internal sealed class WindowsDiagnosticsCollector(IWindowsServiceBackend services) : IDiagnosticsCollector
{
    public Task<(DiagnosticCpuSample Cpu, long TotalMemory, long AvailableMemory, DiskResourceUsage[] Disks)> ReadResourcesAsync(CancellationToken cancellation) => Task.Run(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var disks = new List<DiskResourceUsage>();
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed).Take(32))
        {
            cancellation.ThrowIfCancellationRequested();
            try { disks.Add(new(drive.Name, drive.TotalSize, drive.AvailableFreeSpace)); }
            catch { disks.Add(new(drive.Name, null, null, "DIAGNOSTICS_DISK_UNAVAILABLE")); }
        }
        return (new DiagnosticCpuSample(Ticks(idle), Ticks(kernel), Ticks(user), GetActiveProcessorGroupCount() == 1), checked((long)memory.TotalPhysical),
            checked((long)memory.AvailablePhysical), disks.ToArray());
    }, cancellation);

    public Task<DiagnosticProcessSample[]> ReadProcessesAsync(CancellationToken cancellation) => Task.Run(async () =>
    {
        var samples = new List<DiagnosticProcessSample>();
        try
        {
            using var manager = new ServerManager();
            foreach (var pool in manager.ApplicationPools.Take(200))
            {
                cancellation.ThrowIfCancellationRequested();
                var workers = manager.WorkerProcesses.Where(worker => worker.AppPoolName.Equals(pool.Name, StringComparison.OrdinalIgnoreCase)).Take(32).ToArray();
                if (workers.Length == 0) samples.Add(new("applicationPool", pool.Name, pool.State.ToString(), null, null, null, null, null));
                foreach (var worker in workers) samples.Add(ReadProcess("applicationPool", pool.Name, pool.State.ToString(), worker.ProcessId, expectedProcess: "w3wp"));
                if (samples.Count >= 200) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { samples.Add(new("applicationPool", "应用程序池", "Unknown", null, null, null, null, null, "DIAGNOSTICS_IIS_PROCESS_UNAVAILABLE")); }
        try
        {
            var snapshot = await services.GetSnapshotAsync(cancellation);
            foreach (var service in snapshot.Services.Where(service => service.Managed).Take(200))
            {
                cancellation.ThrowIfCancellationRequested();
                samples.Add(service.ProcessId is > 0 and <= int.MaxValue
                    ? ReadProcess("windowsService", service.Name, service.State, (int)service.ProcessId)
                    : new("windowsService", service.Name, service.State, null, null, null, null, null));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { samples.Add(new("windowsService", "Windows 服务", "Unknown", null, null, null, null, null, "DIAGNOSTICS_SERVICE_PROCESS_UNAVAILABLE")); }
        return samples.ToArray();
    }, cancellation);

    private static DiagnosticProcessSample ReadProcess(string kind, string name, string state, int pid, string? expectedProcess = null)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var start = new DateTimeOffset(process.StartTime.ToUniversalTime());
            if (expectedProcess is not null && !process.ProcessName.Equals(expectedProcess, StringComparison.OrdinalIgnoreCase))
                return new(kind, name, state, pid, null, null, null, null, "DIAGNOSTICS_PID_CHANGED");
            var cpu = process.TotalProcessorTime.Ticks;
            var working = process.WorkingSet64; var privateBytes = process.PrivateMemorySize64;
            process.Refresh();
            if (process.HasExited || new DateTimeOffset(process.StartTime.ToUniversalTime()) != start)
                return new(kind, name, state, pid, null, null, null, null, "DIAGNOSTICS_PID_CHANGED");
            return new(kind, name, state, pid, start, cpu, working, privateBytes);
        }
        catch { return new(kind, name, state, pid, null, null, null, null, "DIAGNOSTICS_PROCESS_UNAVAILABLE"); }
    }

    public Task<DiagnosticEvent[]> ReadEventsAsync(CancellationToken cancellation) => Task.Run(() =>
    {
        var rows = new List<DiagnosticEvent>();
        foreach (var channel in new[] { "System", "Application" })
        {
            cancellation.ThrowIfCancellationRequested();
            var providers = DiagnosticEventSanitizer.AllowedProviders.Select(provider => "Provider[@Name='" + provider + "']");
            var query = "*[System[TimeCreated[timediff(@SystemTime) >= 0 and timediff(@SystemTime) <= 86400000] and ((Level >= 1 and Level <= 3) or (Provider[@Name='Service Control Manager'] and EventID=7036)) and (" + string.Join(" or ", providers) + ")]]";
            using var handle = EvtQuery(IntPtr.Zero, channel, query, 1 | 512);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            for (var index = 0; index < 51; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                var events = new IntPtr[1];
                if (!EvtNext(handle, 1, events, 250, 0, out var returned))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }
                if (returned != 1) break;
                using var record = new EventHandle(events[0]);
                _ = EvtRender(IntPtr.Zero, record, 1, 0, IntPtr.Zero, out var required, out _);
                if (required is <= 0 or > 65536) continue;
                var buffer = Marshal.AllocHGlobal(required);
                try
                {
                    if (!EvtRender(IntPtr.Zero, record, 1, required, buffer, out _, out _)) continue;
                    var xml = Marshal.PtrToStringUni(buffer);
                    if (xml is not null && DiagnosticEventSanitizer.FromXml(channel, xml) is { } safe) rows.Add(safe);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        return rows.OrderByDescending(row => row.TimeCreated).Take(51).ToArray();
    }, cancellation);

    public Task<IisLogDiagnostics[]> ReadIisLogsAsync(CancellationToken cancellation) => Task.Run(() =>
    {
        using var manager = new ServerManager();
        var log = manager.GetApplicationHostConfiguration().GetSection("system.applicationHost/log");
        var mode = Convert.ToString(log["centralLogFileMode"], CultureInfo.InvariantCulture);
        if (!string.Equals(mode, "Site", StringComparison.OrdinalIgnoreCase) && mode != "0")
            throw new InvalidOperationException("DIAGNOSTICS_IIS_LOG_MODE_UNSUPPORTED");
        var budget = 8 * 1024 * 1024;
        var rows = new List<IisLogDiagnostics>();
        foreach (var site in manager.Sites.Take(33))
        {
            cancellation.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            if (rows.Count == 32)
            {
                rows.Add(new(site.Name, "unavailable", now.AddHours(-24), now, 0, 0, true, "DIAGNOSTICS_LIMIT_REACHED", [])); break;
            }
            if (!site.LogFile.Enabled || site.LogFile.LogFormat != LogFormat.W3c)
            {
                rows.Add(new(site.Name, "unavailable", now.AddHours(-24), now, 0, 0, false,
                    "DIAGNOSTICS_IIS_LOG_DISABLED_OR_UNSUPPORTED", [])); continue;
            }
            var root = Environment.ExpandEnvironmentVariables(site.LogFile.Directory);
            rows.Add(DiagnosticsIisLogReader.Read(site.Name, root, site.Id, now, ref budget, cancellation));
        }
        return rows.ToArray();
    }, cancellation);

    private static ulong Ticks(System.Runtime.InteropServices.ComTypes.FILETIME time) => ((ulong)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        internal uint Length, Load;
        internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idle,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernel, out System.Runtime.InteropServices.ComTypes.FILETIME user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")] private static extern ushort GetActiveProcessorGroupCount();
    private sealed class EventHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public EventHandle() : base(true) { }
        public EventHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => EvtClose(handle);
    }
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern EventHandle EvtQuery(IntPtr session, string path, string query, int flags);
    [DllImport("wevtapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtNext(EventHandle handle, int count, [Out] IntPtr[] events, int timeout, int flags, out int returned);
    [DllImport("wevtapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtRender(IntPtr context, EventHandle handle, int flags, int size, IntPtr buffer, out int used, out int count);
    [DllImport("wevtapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EvtClose(IntPtr handle);
}

public static class DiagnosticEventSanitizer
{
    public static readonly string[] AllowedProviders = ["Microsoft-Windows-WAS", "Microsoft-Windows-IIS-W3SVC", "Microsoft-Windows-IIS-W3SVC-WP",
        "Service Control Manager", "Application Error", ".NET Runtime", "Windows Error Reporting"];

    public static DiagnosticEvent? FromXml(string channel, string xml)
    {
        if (channel is not ("System" or "Application") || xml.Length > 32768) return null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var system = XDocument.Load(reader).Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "System");
            XElement? Field(string name) => system?.Elements().FirstOrDefault(element => element.Name.LocalName == name);
            var provider = Field("Provider")?.Attribute("Name")?.Value;
            if (provider is null || !AllowedProviders.Contains(provider, StringComparer.Ordinal) ||
                !int.TryParse(Field("EventID")?.Value, out var id) || id is < 0 or > 65535) return null;
            int? level = int.TryParse(Field("Level")?.Value, out var parsedLevel) ? parsedLevel : null;
            if (level is not (>= 1 and <= 3) && !(provider == "Service Control Manager" && id == 7036)) return null;
            DateTimeOffset? when = DateTimeOffset.TryParse(Field("TimeCreated")?.Attribute("SystemTime")?.Value,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) ? timestamp : null;
            long? record = long.TryParse(Field("EventRecordID")?.Value, out var recordId) ? recordId : null;
            var summary = provider switch
            {
                "Service Control Manager" => "Windows 服务控制事件；请按时间与事件编号在服务器核对。",
                "Application Error" or ".NET Runtime" or "Windows Error Reporting" => "应用或运行时关键事件；请按事件编号在服务器核对。",
                _ => "IIS/WAS 关键事件；请按事件编号在服务器核对。"
            };
            return new(when, channel, provider, id, level, record, summary);
        }
        catch (XmlException) { return null; }
    }
}
