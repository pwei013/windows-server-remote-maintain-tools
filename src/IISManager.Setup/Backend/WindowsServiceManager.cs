using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using IISManager.Setup.Core;
using Microsoft.Win32.SafeHandles;

namespace IISManager.Setup.Backend;

internal sealed record ServiceEvidence(string Name, string BinaryPath, string Account, uint StartType, uint ServiceType,
    uint State, uint ProcessId, uint Win32ExitCode, uint ServiceExitCode)
{
    internal string StateName => State switch { 1 => "Stopped", 2 => "StartPending", 3 => "StopPending", 4 => "Running", 5 => "ContinuePending", 6 => "PausePending", 7 => "Paused", _ => "Unknown" };
}

internal static class WindowsServiceManager
{
    internal static ServiceEvidence? Read(string name)
    {
        using var scm = OpenSCManager(null, null, 1);
        if (scm.IsInvalid) throw Failure("SERVICE_MANAGER_UNAVAILABLE");
        using var service = OpenService(scm, name, 1 | 4);
        if (service.IsInvalid)
        {
            if (Marshal.GetLastWin32Error() == 1060) return null;
            if (Marshal.GetLastWin32Error() == 1072) throw Failure("SERVICE_MARKED_FOR_DELETE");
            throw Failure("SERVICE_QUERY_DENIED");
        }
        return Read(service, name);
    }

    private static ServiceEvidence Read(ServiceHandle service, string name)
    {
        QueryServiceConfig(service, IntPtr.Zero, 0, out var length);
        if (Marshal.GetLastWin32Error() != 122 || length is < 1 or > 65536) throw Failure("SERVICE_CONFIG_UNVERIFIED");
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!QueryServiceConfig(service, buffer, length, out _)) throw Failure("SERVICE_CONFIG_UNVERIFIED");
            var config = Marshal.PtrToStructure<ServiceConfiguration>(buffer);
            var status = Status(service);
            return new(name, Marshal.PtrToStringUni(config.BinaryPath) ?? "", Marshal.PtrToStringUni(config.Account) ?? "",
                config.StartType, config.ServiceType, status.State, status.ProcessId, status.Win32ExitCode, status.ServiceExitCode);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static async Task DeleteStoppedAgentAsync(ServiceEvidence expected)
    {
        if (expected.Name != AgentUninstallPolicy.ServiceName || expected.State != 1 || expected.ProcessId != 0)
            throw new SetupException("UNINSTALL_SERVICE_SCOPE_INVALID", "只允许删除已核实停止的固定 Agent 服务。");
        using (var scm = OpenSCManager(null, null, 1))
        {
            if (scm.IsInvalid) throw Failure("SERVICE_MANAGER_UNAVAILABLE");
            using var service = OpenService(scm, expected.Name, 1 | 4 | 0x00010000);
            if (service.IsInvalid) throw Failure(Marshal.GetLastWin32Error() == 1072 ? "SERVICE_MARKED_FOR_DELETE" : "SERVICE_DELETE_OPEN_FAILED");
            var actual = Read(service, expected.Name);
            if (actual != expected) throw Failure("UNINSTALL_SERVICE_DRIFT");
            if (!DeleteService(service)) throw Failure(Marshal.GetLastWin32Error() == 1072 ? "SERVICE_MARKED_FOR_DELETE" : "SERVICE_DELETE_FAILED");
        }
        // All local service handles are closed before polling: an open handle itself postpones deletion.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ServiceEvidence? current;
            try { current = Read(expected.Name); }
            catch (SetupException exception) when (exception.Code == "SERVICE_MARKED_FOR_DELETE")
            { await Task.Delay(500); continue; }
            if (current is null) return;
            if (current != expected) throw Failure("SERVICE_DELETE_READBACK_MISMATCH");
            await Task.Delay(500);
        }
        throw Failure("SERVICE_DELETE_UNVERIFIED");
    }

    internal static async Task StopReviewedAgentAsync(ServiceEvidence expected)
    {
        if (expected.Name != AgentUninstallPolicy.ServiceName || expected.State is not (1 or 4))
            throw new SetupException("UNINSTALL_SERVICE_SCOPE_INVALID", "只能停止已审阅的固定 Agent 服务。");
        using var scm = OpenSCManager(null, null, 1);
        if (scm.IsInvalid) throw Failure("SERVICE_MANAGER_UNAVAILABLE");
        using var service = OpenService(scm, expected.Name, 1 | 4 | 32);
        if (service.IsInvalid) throw Failure("SERVICE_STOP_DENIED");
        // Check identity and entry on the same handle that receives Stop; do not reopen by name for this write.
        if (Read(service, expected.Name) != expected) throw Failure("UNINSTALL_SERVICE_DRIFT");
        if (expected.State == 4)
        {
            if (!ControlService(service, 1, out _)) throw Failure("SERVICE_STOP_FAILED");
            await Wait(service, 1, "SERVICE_STOP_UNVERIFIED");
        }
        if (Read(service, expected.Name) != (expected with { State = 1, ProcessId = 0 }))
            throw Failure("SERVICE_STOP_READBACK_FAILED");
    }

    internal static string BinaryPath(SetupPlan plan) => $"\"{Path.Combine(plan.InstallDirectory, plan.ExecutableName)}\" --contentRoot \"{plan.InstallDirectory}\" --environment Production";

    internal static void Create(SetupPlan plan, SecureString? password)
    {
        using var scm = OpenSCManager(null, null, 1 | 2);
        if (scm.IsInvalid) throw Failure("SERVICE_CREATE_DENIED");
        var pointer = IntPtr.Zero;
        try
        {
            if (plan.IdentityKind == ServiceIdentityKind.ExistingAccount)
            {
                if (password is null || password.Length == 0) throw new SetupException("ACCOUNT_PASSWORD_REQUIRED", "已有账号需要本次内存输入的密码；不创建账号或分配登录特权。");
                pointer = Marshal.SecureStringToGlobalAllocUnicode(password);
            }
            using var service = CreateService(scm, plan.ServiceName, plan.ServiceName, 1 | 2 | 4 | 16 | 32,
                16, 2, 1, BinaryPath(plan), null, IntPtr.Zero, null,
                plan.IdentityKind == ServiceIdentityKind.LocalSystem ? null : plan.ServiceAccount, pointer);
            if (service.IsInvalid) throw Failure("SERVICE_CREATE_FAILED");
            // An unrestricted service SID grants only permissions explicitly applied to this exact SID.
            var sid = new ServiceSidInfo { SidType = 1 };
            if (!ChangeServiceConfig2(service, 5, ref sid)) throw Failure("SERVICE_SID_CONFIG_FAILED");
            var read = Read(plan.ServiceName) ?? throw Failure("SERVICE_CREATE_READBACK_FAILED");
            if (read.BinaryPath != BinaryPath(plan) || read.ServiceType != 16 || read.StartType != 2 || !SameAccount(read.Account, plan.ServiceAccount))
                throw Failure("SERVICE_CREATE_READBACK_FAILED");
        }
        finally { if (pointer != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(pointer); }
    }

    internal static SecurityIdentifier ResolveIdentity(string account)
    {
        if (SameAccount(account, "LocalSystem")) return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        try { return (SecurityIdentifier)new NTAccount(account.StartsWith(@".\", StringComparison.Ordinal) ? Environment.MachineName + account[1..] : account).Translate(typeof(SecurityIdentifier)); }
        catch (IdentityNotMappedException) { throw new SetupException("SERVICE_IDENTITY_UNVERIFIED", "无法解析指定实际服务身份；请先由操作员准备账号。"); }
    }
    internal static void ValidateExistingAccount(SetupPlan plan, SecureString? password)
    {
        if (plan.IdentityKind != ServiceIdentityKind.ExistingAccount) return;
        if (password is null || password.Length == 0) throw new SetupException("ACCOUNT_PASSWORD_REQUIRED", "已有账号须使用本次安全输入的密码证明服务登录；不分配登录特权。");
        var pointer = Marshal.SecureStringToGlobalAllocUnicode(password);
        try
        {
            var parts = plan.AccountName.Split('\\');
            var user = parts.Length == 2 ? parts[1] : plan.AccountName;
            var domain = parts.Length == 2 ? parts[0] : null;
            if (parts.Length > 2 || !LogonUser(user, domain, pointer, 5, 0, out var token))
                throw new SetupException("ACCOUNT_SERVICE_LOGON_UNVERIFIED", "无法证明已有账号具备服务登录权限，请由操作员准备；不创建账号或更改安全策略。");
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            {
                if (identity.User is null || !identity.User.Equals(ResolveIdentity(plan.ServiceAccount)) ||
                    plan.Component == SetupComponent.Agent && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new SetupException("ACCOUNT_IIS_AUTHORITY_UNVERIFIED", "无法证明指定 Agent 账号的管理员权限，首版不扩大账号权限。");
            }
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(pointer); }
    }
    internal static bool SameAccount(string first, string second)
    {
        static string Normalize(string value) => value.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) || value.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase) ? "S-1-5-18" : value;
        return Normalize(first).Equals(Normalize(second), StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task StopAsync(string name)
    {
        using var scm = OpenSCManager(null, null, 1);
        if (scm.IsInvalid) throw Failure("SERVICE_MANAGER_UNAVAILABLE");
        using var service = OpenService(scm, name, 4 | 32);
        if (service.IsInvalid) throw Failure("SERVICE_STOP_DENIED");
        var status = Status(service);
        if (status.State == 1) return;
        if (status.State != 4) throw Failure("SERVICE_STATE_UNSTABLE");
        if (!ControlService(service, 1, out _)) throw Failure("SERVICE_STOP_FAILED");
        await Wait(service, 1, "SERVICE_STOP_UNVERIFIED");
        if (Read(name)?.State != 1) throw Failure("SERVICE_STOP_READBACK_FAILED");
    }
    internal static async Task StartAsync(string name)
    {
        using var scm = OpenSCManager(null, null, 1);
        if (scm.IsInvalid) throw Failure("SERVICE_MANAGER_UNAVAILABLE");
        using var service = OpenService(scm, name, 4 | 16);
        if (service.IsInvalid) throw Failure("SERVICE_START_DENIED");
        var state = Status(service).State;
        if (state == 4) return;
        if (state != 1) throw Failure("SERVICE_STATE_UNSTABLE");
        if (!StartService(service, 0, IntPtr.Zero)) throw Failure("SERVICE_START_FAILED");
        await Wait(service, 4, "SERVICE_START_UNVERIFIED");
        if (Read(name)?.State != 4) throw Failure("SERVICE_START_READBACK_FAILED");
    }
    private static async Task Wait(ServiceHandle service, uint expected, string code)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = Status(service);
            if (status.State == expected) return;
            if (expected == 4 && status.State == 1) throw Failure(code);
            await Task.Delay(500); // No cancellation after SCM may have received a mutation.
        }
        throw Failure(code);
    }
    private static ServiceStatus Status(ServiceHandle service)
    {
        if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _)) throw Failure("SERVICE_STATUS_UNVERIFIED");
        return status;
    }
    private static SetupException Failure(string code) => new(code, "Windows 服务操作未能核实，请先只读检查原服务状态；不自动重放或回滚。");
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceConfiguration
    {
        internal uint ServiceType, StartType, ErrorControl;
        internal IntPtr BinaryPath, LoadOrderGroup;
        internal uint TagId;
        internal IntPtr Dependencies, Account, DisplayName;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    {
        internal uint ServiceType, State, ControlsAccepted, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint, ProcessId, Flags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceSidInfo { internal uint SidType; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenService(ServiceHandle scm, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle CreateService(ServiceHandle scm, string name, string displayName, uint access, uint type, uint start, uint error, string binary, string? group, IntPtr tag, string? dependencies, string? account, IntPtr password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfig(ServiceHandle service, IntPtr config, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartService(ServiceHandle service, int count, IntPtr arguments);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ControlService(ServiceHandle service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteService(ServiceHandle service);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig2(ServiceHandle service, int level, ref ServiceSidInfo info);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LogonUser(string user, string? domain, IntPtr password, int type, int provider, out SafeAccessTokenHandle token);
}
