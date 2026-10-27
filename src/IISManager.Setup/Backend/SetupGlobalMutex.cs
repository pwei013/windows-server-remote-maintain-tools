using System.Runtime.InteropServices;
using IISManager.Setup.Core;
using Microsoft.Win32.SafeHandles;

namespace IISManager.Setup.Backend;

// Native mutex access is restricted to elevated administrators and SYSTEM. It spans both components.
internal sealed class SetupGlobalMutex : IDisposable
{
    private readonly MutexHandle handle;
    private bool owned;
    private SetupGlobalMutex(MutexHandle value) { handle = value; }
    internal static SetupGlobalMutex Acquire()
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:P(A;;GA;;;SY)(A;;GA;;;BA)", 1, out var descriptor, out _))
            throw new SetupException("INSTALL_MUTEX_FAILED", "无法准备安装互斥权限。");
        try
        {
            var attributes = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var native = CreateMutex(ref attributes, false, @"Global\IISManager.Setup.v1");
            if (native.IsInvalid) { native.Dispose(); throw new SetupException("INSTALL_MUTEX_FAILED", "无法取得全局安装互斥。"); }
            var result = new SetupGlobalMutex(native);
            var state = WaitForSingleObject(native, 0);
            if (state == 0) { result.owned = true; return result; }
            if (state == 0x80)
            {
                result.owned = true; result.Dispose();
                throw new SetupException("PREVIOUS_INSTALL_UNKNOWN", "前次安装互斥已被遗弃，请先只读核实服务、受限回执和文件；不自动接续。");
            }
            result.Dispose();
            throw new SetupException(state == 0x102 ? "INSTALL_BUSY" : "INSTALL_MUTEX_FAILED", "另一个安装正在进行或互斥结果未能核实。");
        }
        finally { LocalFree(descriptor); }
    }
    public void Dispose() { if (owned) { ReleaseMutex(handle); owned = false; } handle.Dispose(); }
    private sealed class MutexHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private MutexHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { internal int Size; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor, uint revision, out IntPtr security, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern MutexHandle CreateMutex(ref SecurityAttributes attributes, [MarshalAs(UnmanagedType.Bool)] bool owner, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(MutexHandle handle, uint timeout);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReleaseMutex(MutexHandle handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
