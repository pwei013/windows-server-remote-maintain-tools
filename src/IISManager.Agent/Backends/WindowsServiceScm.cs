using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using IISManager.Contracts;
using IISManager.Core;
using Microsoft.Win32.SafeHandles;

namespace IISManager.Agent.Backends;

[SupportedOSPlatform("windows")]
internal static class WindowsServiceScm
{
    internal const uint QueryConfig = 1, ChangeConfig = 2, QueryStatus = 4, EnumDependents = 8, Start = 16, Stop = 32;
    private const uint NoChange = uint.MaxValue;

    internal sealed class Evidence
    {
        internal required WindowsServiceConfig Public { get; init; }
        internal required string ConfigurationHash { get; init; }
        internal required string ImmutableHash { get; init; }
        internal uint ServiceType { get; init; }
        internal uint Controls { get; init; }
    }

    internal static ServiceHandle OpenManager(uint access = 1)
    {
        var handle = OpenSCManager(null, null, access);
        if (handle.IsInvalid) { handle.Dispose(); throw Error("service.managerDenied", "无法取得服务管理器所需权限。" ); }
        return handle;
    }

    internal static ServiceHandle Open(string name, uint access)
    {
        using var scm = OpenManager();
        var handle = OpenService(scm, name, access);
        if (handle.IsInvalid) { handle.Dispose(); throw Error("service.accessDenied", "无法取得目标服务所需权限。" ); }
        return handle;
    }

    internal static bool CanCreate()
    {
        using var handle = OpenSCManager(null, null, 1 | 2);
        return !handle.IsInvalid;
    }

    internal static bool HasAccess(string name, string operation)
    {
        uint rights = operation switch
        {
            OperationTypes.ServiceStart => Start,
            OperationTypes.ServiceStop => Stop | EnumDependents,
            OperationTypes.ServiceRestart => Start | Stop | EnumDependents,
            OperationTypes.ServiceUpdateStartType => ChangeConfig,
            _ => 0
        };
        if (rights == 0) return false;
        using var scm = OpenSCManager(null, null, 1);
        if (scm.IsInvalid) return false;
        using var service = OpenService(scm, name, QueryConfig | QueryStatus | rights);
        if (service.IsInvalid) return false;
        var status = Status(service);
        return operation is not (OperationTypes.ServiceStop or OperationTypes.ServiceRestart) || status.State == 1 || (status.Controls & 1) != 0;
    }

    internal static string[] RunningDependents(string name)
    {
        using var service = Open(name, EnumDependents | QueryStatus);
        if (EnumDependentServices(service, 1, IntPtr.Zero, 0, out var needed, out _)) return [];
        if (Marshal.GetLastWin32Error() != 234 || needed is <= 0 or > 1_048_576)
            throw Error("service.dependentsUnverified", "无法核实全部正在运行的从属服务。" );
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!EnumDependentServices(service, 1, buffer, needed, out _, out var count)) throw Error("service.dependentsUnverified", "从属服务读回期间发生变化。" );
            var names = new List<string>();
            var size = Marshal.SizeOf<DependentEntry>();
            for (var i = 0; i < count; i++) names.Add(Marshal.PtrToStringUni(Marshal.PtrToStructure<DependentEntry>(IntPtr.Add(buffer, i * size)).Name) ?? "");
            return names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static Evidence[] Inventory(IEnumerable<string>? protectedNames = null)
    {
        using var scm = OpenManager(1 | 4);
        uint resume = 0;
        // SCM caps a single enumeration buffer at 256 KiB. ERROR_MORE_DATA still returns
        // a valid partial page; retain its entries and continue from the resume handle.
        const int capacity = 256 * 1024;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            var result = new List<Evidence>();
            var stride = Marshal.SizeOf<ServiceEntry>();
            for (var page = 0; page < 128; page++)
            {
                var previous = resume;
                var complete = EnumServicesStatusEx(scm, 0, 0x30, 3, buffer, capacity, out _, out var count, ref resume, null);
                if (!complete && Marshal.GetLastWin32Error() != 234 || count > capacity / stride)
                    throw Error("service.inventoryUnverified", "无法完整读取普通 Windows 服务清单。" );
                for (uint i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<ServiceEntry>(IntPtr.Add(buffer, checked((int)i * stride)));
                    var name = Marshal.PtrToStringUni(entry.Name) ?? "";
                    if (result.Any(x => BackendRules.Equal(x.Public.Name, name))) continue;
                    try { var value = Read(name); if (value is not null) result.Add(value); }
                    catch (OperationException)
                    {
                        result.Add(new()
                        {
                            Public = new() { Name = name, DisplayName = Marshal.PtrToStringUni(entry.DisplayName) ?? name, State = StateName(entry.Status.State), UnavailableReason = "服务配置查询权限不足或读取结果未能核实。" },
                            ConfigurationHash = "UNVERIFIED", ImmutableHash = "UNVERIFIED", ServiceType = entry.Status.Type, Controls = entry.Status.Controls
                        });
                    }
                }
                if (complete) break;
                if (resume == previous || page == 127) throw Error("service.inventoryUnverified", "服务清单分页未能完成，请刷新。" );
            }
            // Enum may omit protected services without QUERY_STATUS rights. Query them explicitly.
            foreach (var name in new[] { "IISManager.Agent", "IISManager.Console" }.Concat(protectedNames ?? []).Where(WindowsServiceRules.ValidName).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!result.Any(x => BackendRules.Equal(x.Public.Name, name)))
                {
                    try { var value = Read(name); if (value is not null) result.Add(value); }
                    catch (OperationException)
                    {
                        result.Add(new() { Public = new() { Name = name, DisplayName = name, UnavailableReason = "指定保护服务无法完整查询。" }, ConfigurationHash = "UNVERIFIED", ImmutableHash = "UNVERIFIED", ServiceType = 16 });
                    }
                }
            return result.OrderBy(x => x.Public.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static Evidence? Read(string name)
    {
        using var scm = OpenManager();
        using var service = OpenService(scm, name, QueryConfig | QueryStatus);
        if (service.IsInvalid)
        {
            if (Marshal.GetLastWin32Error() == 1060) return null;
            throw Error("service.queryDenied", "无法完整查询服务配置和状态。" );
        }
        return Read(service, name);
    }

    internal static Evidence Read(ServiceHandle service, string name)
    {
        QueryServiceConfig(service, IntPtr.Zero, 0, out var needed);
        if (Marshal.GetLastWin32Error() != 122 || needed is <= 0 or > 65536) throw Error("service.configUnverified", "服务配置未能核实。" );
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!QueryServiceConfig(service, buffer, needed, out _)) throw Error("service.configUnverified", "服务配置未能核实。" );
            var config = Marshal.PtrToStructure<Configuration>(buffer);
            var status = Status(service);
            var image = Marshal.PtrToStringUni(config.Binary) ?? "";
            var account = Marshal.PtrToStringUni(config.Account) ?? "";
            var displayName = Marshal.PtrToStringUni(config.DisplayName) ?? name;
            var dependencies = MultiString(config.Dependencies);
            var group = Marshal.PtrToStringUni(config.Group) ?? "";
            var description = ReadDescription(service);
            var delayed = ReadDelayed(service);
            var immutable = new { image, account, displayName, dependencies, group, config.Type, config.ErrorControl, config.Tag, description };
            return new()
            {
                Public = new()
                {
                    Name = name, DisplayName = displayName, Description = description, State = StateName(status.State),
                    StartType = StartName(config.Start, delayed), AccountName = account, ExecutablePath = SafeExecutable(image),
                    ProcessId = status.ProcessId, Dependencies = dependencies,
                    UnavailableReason = config.Type is not (16 or 32) ? "仅允许普通独立进程或共享进程服务；驱动和特殊服务只读。" : null
                },
                ConfigurationHash = BackendRules.Hash(new { immutable, config.Start, delayed }), ImmutableHash = BackendRules.Hash(immutable),
                ServiceType = config.Type, Controls = status.Controls
            };
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string ReadDescription(ServiceHandle service)
    {
        var buffer = ReadConfig2(service, 1);
        try { return Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer)) ?? ""; }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static bool ReadDelayed(ServiceHandle service)
    {
        var buffer = ReadConfig2(service, 3);
        try { return Marshal.ReadInt32(buffer) != 0; }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static IntPtr ReadConfig2(ServiceHandle service, int level)
    {
        QueryServiceConfig2(service, level, IntPtr.Zero, 0, out var needed);
        if (Marshal.GetLastWin32Error() != 122 || needed is <= 0 or > 65536) throw Error("service.configUnverified", "服务扩展配置未能核实。" );
        var buffer = Marshal.AllocHGlobal(needed);
        if (QueryServiceConfig2(service, level, buffer, needed, out _)) return buffer;
        Marshal.FreeHGlobal(buffer);
        throw Error("service.configUnverified", "服务扩展配置未能核实。" );
    }

    internal static void StartServiceChecked(ServiceHandle service)
    {
        if (!StartService(service, 0, IntPtr.Zero)) throw Error("service.startUnverified", "SCM 启动请求未能完成，请只读核实服务状态。" );
    }
    internal static void StopServiceChecked(ServiceHandle service)
    {
        if (!ControlService(service, 1, out _)) throw Error("service.stopUnverified", "SCM 停止请求未能完成，请只读核实服务状态。" );
    }

    internal static async Task WaitAsync(ServiceHandle service, uint desired, Stopwatch elapsed, TimeSpan timeout)
    {
        uint lastCheckpoint = 0;
        var lastProgress = elapsed.Elapsed;
        while (elapsed.Elapsed < timeout)
        {
            var value = Status(service);
            if (value.State == desired) return;
            if (desired == 4 && value.State == 1) throw Error("service.startFailed", "服务启动后回到停止状态，请检查服务自身运行条件。" );
            if (value.CheckPoint > lastCheckpoint) { lastCheckpoint = value.CheckPoint; lastProgress = elapsed.Elapsed; }
            if (value.WaitHint > 0 && elapsed.Elapsed - lastProgress > TimeSpan.FromMilliseconds(value.WaitHint))
                throw Error("service.waitNoProgress", "服务未在等待提示期限内报告进展，请只读核实。" );
            var interval = Math.Clamp(value.WaitHint / 10, 250u, 1000u);
            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(interval, remaining.TotalMilliseconds)));
        }
        throw Error("service.waitTimeout", "服务未在本次总体期限内达到预期状态，请只读核实；不会自动重放。" );
    }

    internal static ServiceHandle Create(string name, WindowsServiceCreateParameters p)
    {
        using var scm = OpenManager(1 | 2);
        var binary = Quote(p.ExecutablePath) + string.Concat(p.Arguments.Select(value => " " + Quote(value)));
        if (binary.Length > 32760) throw new OperationException("service.commandTooLong", "服务入口和参数总长度超过 Windows 命令行限制。" );
        var dependencies = p.Dependencies.Length == 0 ? null : string.Join('\0', p.Dependencies) + "\0\0";
        var account = WindowsServiceRules.ResolveAccount(name, p.AccountName);
        var handle = CreateService(scm, name, string.IsNullOrEmpty(p.DisplayName) ? name : p.DisplayName,
            QueryConfig | QueryStatus | ChangeConfig, 16, StartValue(p.StartType), 1, binary, null, IntPtr.Zero, dependencies,
            account == "LocalSystem" ? null : account, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw Error("service.createUnverified", "SCM 注册服务请求未能核实，请先只读查询服务是否存在。" ); }
        return handle;
    }

    internal static bool MatchesCreation(Evidence value, string name, WindowsServiceCreateParameters p)
    {
        var image = Quote(p.ExecutablePath) + string.Concat(p.Arguments.Select(argument => " " + Quote(argument)));
        var immutable = new { image, account = WindowsServiceRules.ResolveAccount(name, p.AccountName), displayName = string.IsNullOrEmpty(p.DisplayName) ? name : p.DisplayName,
            dependencies = p.Dependencies, group = "", Type = 16u, ErrorControl = 1u, Tag = 0u, description = p.Description };
        return value.ImmutableHash == BackendRules.Hash(immutable) && value.Public.StartType == p.StartType && value.Public.State == "Stopped";
    }

    internal static void CompleteCreation(ServiceHandle service, WindowsServiceCreateParameters p)
    {
        var description = Marshal.StringToHGlobalUni(p.Description);
        try
        {
            var info = new DescriptionInfo { Description = description };
            if (!ChangeDescription(service, 1, ref info)) throw Error("service.descriptionUnverified", "服务已注册，但描述写入未能核实。" );
        }
        finally { Marshal.FreeHGlobal(description); }
        var sid = new FlagInfo { Value = 1 };
        if (!ChangeFlag(service, 5, ref sid)) throw Error("service.sidUnverified", "服务已注册，但服务身份 SID 设置未能核实。" );
        SetDelayed(service, p.StartType == WindowsServiceStartTypes.AutomaticDelayed);
    }

    internal static void UpdateStartType(ServiceHandle service, string startType)
    {
        if (!ChangeServiceConfig(service, NoChange, StartValue(startType), NoChange, null, null, IntPtr.Zero, null, null, IntPtr.Zero, null))
            throw Error("service.startTypeUnverified", "启动类型写入未能核实，请先只读核实。" );
        SetDelayed(service, startType == WindowsServiceStartTypes.AutomaticDelayed);
    }
    private static void SetDelayed(ServiceHandle service, bool delayed)
    {
        var flag = new FlagInfo { Value = delayed ? 1u : 0u };
        if (!ChangeFlag(service, 3, ref flag)) throw Error("service.delayedStartUnverified", "延迟启动标志写入未能核实，请先只读核实。" );
    }

    internal static StatusInfo Status(ServiceHandle service)
    {
        if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<StatusInfo>(), out _)) throw Error("service.statusUnverified", "服务状态未能核实。" );
        return status;
    }
    private static string[] MultiString(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return [];
        var result = new List<string>();
        for (var offset = 0; offset < 32768;)
        {
            var value = Marshal.PtrToStringUni(IntPtr.Add(pointer, offset * 2)) ?? "";
            if (value.Length == 0) return result.ToArray();
            result.Add(value); offset += value.Length + 1;
        }
        throw Error("service.dependenciesUnverified", "服务依赖信息未能核实。" );
    }
    internal static string SafeExecutable(string image)
    {
        if (image.StartsWith('"'))
        {
            var end = image.IndexOf('"', 1);
            return end > 1 ? image[1..end] : "";
        }
        var exe = image.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe < 0) return "";
        var path = image[..(exe + 4)];
        return path.Any(char.IsWhiteSpace) ? "" : path;
    }
    internal static string Quote(string value)
    {
        var output = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            output.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); output.Append(c); slashes = 0;
        }
        output.Append('\\', slashes * 2).Append('"'); return output.ToString();
    }
    internal static string StateName(uint state) => state switch { 1 => "Stopped", 2 => "StartPending", 3 => "StopPending", 4 => "Running", 5 => "ContinuePending", 6 => "PausePending", 7 => "Paused", _ => "Unknown" };
    private static uint StartValue(string start) => start switch { WindowsServiceStartTypes.Automatic or WindowsServiceStartTypes.AutomaticDelayed => 2, WindowsServiceStartTypes.Manual => 3, WindowsServiceStartTypes.Disabled => 4, _ => throw new OperationException("service.startTypeInvalid", "启动类型无效。" ) };
    private static string StartName(uint start, bool delayed) => start switch { 2 => delayed ? WindowsServiceStartTypes.AutomaticDelayed : WindowsServiceStartTypes.Automatic, 3 => WindowsServiceStartTypes.Manual, 4 => WindowsServiceStartTypes.Disabled, _ => "Unknown" };
    private static OperationException Error(string code, string message) => new(code, message);

    internal sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Configuration
    {
        internal uint Type, Start, ErrorControl;
        internal IntPtr Binary, Group;
        internal uint Tag;
        internal IntPtr Dependencies, Account, DisplayName;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct StatusInfo
    {
        internal uint Type, State, Controls, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint, ProcessId, Flags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceEntry { internal IntPtr Name, DisplayName; internal StatusInfo Status; }
    [StructLayout(LayoutKind.Sequential)] private struct DependentEntry
    {
        internal IntPtr Name, DisplayName;
        internal uint Type, State, Controls, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint;
    }
    [StructLayout(LayoutKind.Sequential)] private struct DescriptionInfo { internal IntPtr Description; }
    [StructLayout(LayoutKind.Sequential)] private struct FlagInfo { internal uint Value; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenService(ServiceHandle scm, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumServicesStatusEx(ServiceHandle scm, int level, uint type, uint state, IntPtr buffer, uint size, out uint needed, out uint count, ref uint resume, string? group);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDependentServices(ServiceHandle service, uint state, IntPtr buffer, int size, out int needed, out int count);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfig(ServiceHandle service, IntPtr buffer, int size, out int needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfig2(ServiceHandle service, int level, IntPtr buffer, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out StatusInfo status, int size, out int needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartService(ServiceHandle service, int count, IntPtr arguments);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ControlService(ServiceHandle service, uint control, out BasicStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle CreateService(ServiceHandle scm, string name, string displayName, uint access, uint type, uint start, uint error, string binary, string? group, IntPtr tag, string? dependencies, string? account, IntPtr password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig(ServiceHandle service, uint type, uint start, uint error, string? binary, string? group, IntPtr tag, string? dependencies, string? account, IntPtr password, string? displayName);
    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeDescription(ServiceHandle service, int level, ref DescriptionInfo info);
    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeFlag(ServiceHandle service, int level, ref FlagInfo info);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)] private struct BasicStatus
    {
        internal uint Type, State, Controls, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint;
    }
}
