using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IISManager.Agent;

internal sealed class FrebReadException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

/// <summary>Windows directories and complete traces permit read sharing only; Unix test reads use openat/O_NOFOLLOW.</summary>
internal sealed class FrebLogFileBoundary : IDisposable
{
    private readonly List<SafeFileHandle> _directories = [];
    private readonly bool _windows = OperatingSystem.IsWindows();
    internal string DirectoryPath { get; }

    internal FrebLogFileBoundary(string directory)
    {
        DirectoryPath = LocalPath(directory);
        try
        {
            if (_windows) PinWindows();
            else PinUnix();
        }
        catch { Dispose(); throw; }
    }

    internal static string LocalPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal) || value.Any(char.IsControl) ||
            OperatingSystem.IsWindows() && (value.Length < 3 || value[1] != ':' || value.AsSpan(2).Contains(':')))
            throw new FrebReadException("FREB_PATH_REJECTED");
        var full = Path.GetFullPath(value);
        if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(full)!).DriveType != DriveType.Fixed)
            throw new FrebReadException("FREB_PATH_REJECTED");
        return full;
    }

    private void PinWindows()
    {
        var chain = new List<string>();
        for (var directory = new DirectoryInfo(DirectoryPath); directory is not null; directory = directory.Parent) chain.Add(directory.FullName);
        chain.Reverse();
        foreach (var directory in chain)
        {
            var info = new DirectoryInfo(directory);
            if (!info.Exists) throw new FrebReadException("FREB_LOG_DIRECTORY_NOT_FOUND");
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new FrebReadException("FREB_PATH_REJECTED");
            // Denying write/delete sharing also blocks an in-place reparse-point change while child paths are opened.
            var handle = CreateFile(directory, 0x80, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw NativeFailure(); }
            _directories.Add(handle);
            var state = WindowsIdentity(handle);
            if ((state.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (state.Attributes & (uint)FileAttributes.Directory) == 0)
                throw new FrebReadException("FREB_PATH_REJECTED");
        }
    }

    private void PinUnix()
    {
        RequireUnix();
        var root = UnixOpen("/", UnixFlags(directory: true), 0);
        if (root < 0) throw NativeFailure();
        _directories.Add(new SafeFileHandle((IntPtr)root, true));
        foreach (var part in DirectoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = UnixOpenAt(_directories[^1].DangerousGetHandle().ToInt32(), part, UnixFlags(directory: true), 0);
            if (next < 0) throw NativeFailure(path: true);
            var handle = new SafeFileHandle((IntPtr)next, true); _directories.Add(handle);
            if ((UnixIdentity(handle).Attributes & 0xF000) != 0x4000) throw new FrebReadException("FREB_PATH_REJECTED");
        }
    }

    internal PinnedFile Open(string fileName)
    {
        if (Path.GetFileName(fileName) != fileName || fileName.IndexOfAny(['/', '\\', ':']) >= 0) throw new FrebReadException("FREB_FILE_REJECTED");
        SafeFileHandle handle;
        if (_windows)
        {
            // Active writers cannot coexist: mtime alone does not establish that a trace was read atomically.
            handle = CreateFile(Path.Combine(DirectoryPath, fileName), 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw NativeFailure(); }
        }
        else
        {
            var descriptor = UnixOpenAt(_directories[^1].DangerousGetHandle().ToInt32(), fileName, UnixFlags(directory: false), 0);
            if (descriptor < 0) throw NativeFailure(path: true);
            handle = new SafeFileHandle((IntPtr)descriptor, true);
        }
        try
        {
            var identity = Identity(handle);
            if (identity.Links != 1 || (_windows
                    ? (identity.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory)) != 0
                    : (identity.Attributes & 0xF000) != 0x8000))
                throw new FrebReadException("FREB_FILE_REJECTED");
            return new PinnedFile(new FileStream(handle, FileAccess.Read), identity, _windows);
        }
        catch { handle.Dispose(); throw; }
    }

    private FileIdentity Identity(SafeFileHandle handle) => _windows ? WindowsIdentity(handle) : UnixIdentity(handle);
    internal readonly record struct FileIdentity(uint Attributes, ulong Volume, ulong Index, ulong Links, ulong Size, long WriteSeconds, long WriteNanoseconds);
    internal sealed class PinnedFile(FileStream stream, FileIdentity identity, bool windows) : IDisposable
    {
        internal FileStream Stream { get; } = stream;
        internal FileIdentity Identity { get; } = identity;
        internal void VerifyUnchanged()
        {
            var current = windows ? WindowsIdentity(Stream.SafeFileHandle) : UnixIdentity(Stream.SafeFileHandle);
            if (current != Identity || current.Links != 1) throw new FrebReadException("FREB_FILE_CHANGED");
        }
        public void Dispose() => Stream.Dispose();
    }

    private static FileIdentity WindowsIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var value)) throw NativeFailure();
        return new(value.Attributes, value.Volume, ((ulong)value.IndexHigh << 32) | value.IndexLow, value.Links,
            ((ulong)value.SizeHigh << 32) | value.SizeLow, unchecked((long)(((ulong)(uint)value.Written.dwHighDateTime << 32) | (uint)value.Written.dwLowDateTime)), 0);
    }

    private static FileIdentity UnixIdentity(SafeFileHandle handle)
    {
        RequireUnix();
        var memory = Marshal.AllocHGlobal(256);
        try
        {
            // Intel macOS retains the legacy 32-bit-inode fstat symbol; explicitly select its 64-bit layout.
            var result = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? MacFstat64(handle, memory) : UnixFstat(handle, memory);
            if (result != 0) throw NativeFailure();
            if (OperatingSystem.IsMacOS())
                return new(unchecked((ushort)Marshal.ReadInt16(memory, 4)), unchecked((uint)Marshal.ReadInt32(memory)),
                    unchecked((ulong)Marshal.ReadInt64(memory, 8)), unchecked((ushort)Marshal.ReadInt16(memory, 6)),
                    checked((ulong)Marshal.ReadInt64(memory, 96)), Marshal.ReadInt64(memory, 48), Marshal.ReadInt64(memory, 56));
            var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            return new(unchecked((uint)Marshal.ReadInt32(memory, arm ? 16 : 24)), unchecked((ulong)Marshal.ReadInt64(memory)),
                unchecked((ulong)Marshal.ReadInt64(memory, 8)), arm ? unchecked((uint)Marshal.ReadInt32(memory, 20)) : unchecked((ulong)Marshal.ReadInt64(memory, 16)),
                checked((ulong)Marshal.ReadInt64(memory, 48)), Marshal.ReadInt64(memory, 88), Marshal.ReadInt64(memory, 96));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static int UnixFlags(bool directory) => OperatingSystem.IsMacOS()
        ? 0x100 | 0x1000000 | (directory ? 0x100000 : 0x4)
        : 0x20000 | 0x80000 | (directory ? 0x10000 : 0x800);
    private static void RequireUnix()
    {
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64) ||
            IntPtr.Size != 8) throw new FrebReadException("FREB_PATH_REJECTED");
    }
    private static FrebReadException NativeFailure(bool path = false) => Marshal.GetLastWin32Error() switch
    {
        2 or 3 => new("FREB_LOG_DIRECTORY_NOT_FOUND"),
        5 when OperatingSystem.IsWindows() => new("FREB_ACCESS_DENIED"),
        13 when !OperatingSystem.IsWindows() => new("FREB_ACCESS_DENIED"),
        _ => new(path ? "FREB_PATH_REJECTED" : "FREB_FILE_REJECTED")
    };
    public void Dispose() { for (var index = _directories.Count - 1; index >= 0; index--) _directories[index].Dispose(); _directories.Clear(); }
    [StructLayout(LayoutKind.Sequential)] private struct WindowsFileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out WindowsFileInformation value);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int UnixOpen(string path, int flags, int mode);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)] private static extern int UnixOpenAt(int directory, string name, int flags, int mode);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] private static extern int UnixFstat(SafeFileHandle descriptor, IntPtr value);
    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)] private static extern int MacFstat64(SafeFileHandle descriptor, IntPtr value);
}
