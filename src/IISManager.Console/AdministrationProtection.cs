using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace IISManager.Console;

public interface IAdministrationSecretProtector
{
    string Format { get; }
    byte[] Protect(byte[] clear);
    byte[] Unprotect(byte[] encrypted);
}

/// <summary>Explicit development-only storage. Never selected on a production or non-loopback listener.</summary>
public sealed class DevelopmentAdministrationProtector : IAdministrationSecretProtector
{
    public string Format => "development-plaintext-v1";
    public byte[] Protect(byte[] clear) => clear.ToArray();
    public byte[] Unprotect(byte[] encrypted) => encrypted.ToArray();
}

public sealed class WindowsAdministrationProtector(string purpose = "IISManager.Console/administration/v1") : IAdministrationSecretProtector
{
    public string Format => "windows-dpapi-current-user-v1";
    public byte[] Protect(byte[] clear) => Transform(clear, protecting: true);
    public byte[] Unprotect(byte[] encrypted) => Transform(encrypted, protecting: false);

    private byte[] Transform(byte[] input, bool protecting)
    {
        if (!OperatingSystem.IsWindows()) throw Unavailable();
        var entropy = Encoding.UTF8.GetBytes(purpose);
        var source = new Blob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        var entropyBlob = new Blob { Length = entropy.Length, Data = Marshal.AllocHGlobal(entropy.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(input, 0, source.Data, input.Length);
            Marshal.Copy(entropy, 0, entropyBlob.Data, entropy.Length);
            var success = protecting
                ? CryptProtectData(ref source, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref source, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success || output.Length is < 1 or > 4 * 1024 * 1024 || output.Data == IntPtr.Zero) throw Unavailable();
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        catch (AdministrationException) { throw; }
        catch (Exception exception) when (exception is CryptographicException or ExternalException or ArgumentException)
        { throw Unavailable(); }
        finally
        {
            if (source.Data != IntPtr.Zero) { Marshal.Copy(new byte[source.Length], 0, source.Data, source.Length); Marshal.FreeHGlobal(source.Data); }
            if (entropyBlob.Data != IntPtr.Zero) Marshal.FreeHGlobal(entropyBlob.Data);
            if (output.Data != IntPtr.Zero)
            {
                if (output.Length is > 0 and <= 4 * 1024 * 1024) Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length);
                LocalFree(output.Data);
            }
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private static AdministrationException Unavailable() => new("ADMIN_SECRET_PROTECTION_UNAVAILABLE",
        "无法使用当前 Console 服务身份保护或读取接入配置，请核对 Windows 身份及用户配置文件；未回退明文。", 503);
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob data, string? description,
        ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob result);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob data, IntPtr description,
        ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob result);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}

internal static class AdministrationFileSecurity
{
    internal static void CheckPath(string directory, string contentRoot, bool development)
    {
        var full = Path.GetFullPath(directory);
        var application = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot));
        if (!development && (string.Equals(full, application, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(application + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new AdministrationException("ADMIN_STORE_PATH_UNSAFE", "接入配置必须保存在程序和站点静态目录之外。", 503);
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new AdministrationException("ADMIN_STORE_PATH_UNSAFE", "接入配置路径包含符号链接或重解析目录。", 503);
    }

    internal static void PrepareDirectory(string directory, string contentRoot, bool development)
    {
        CheckPath(directory, contentRoot, development);
        if (!Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows()) CreateWindowsDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (OperatingSystem.IsWindows()) ValidateWindowsDirectory(directory);
        CheckPath(directory, contentRoot, development);
    }

    [SupportedOSPlatform("windows")]
    private static HashSet<SecurityIdentifier> AllowedIdentities()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is null) throw UnsafePermissions();
        return [identity.User, new(WellKnownSidType.LocalSystemSid, null), new(WellKnownSidType.BuiltinAdministratorsSid, null)];
    }
    [SupportedOSPlatform("windows")]
    private static void CreateWindowsDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in AllowedIdentities()) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }
    [SupportedOSPlatform("windows")]
    private static void ValidateWindowsDirectory(string directory)
    {
        var security = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
        var allowed = AllowedIdentities();
        if (!security.AreAccessRulesProtected || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !allowed.Contains(owner))
            throw UnsafePermissions();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !allowed.Contains((SecurityIdentifier)rule.IdentityReference)) throw UnsafePermissions();
    }
    private static AdministrationException UnsafePermissions() => new("ADMIN_STORE_PERMISSIONS_UNSAFE",
        "接入配置目录权限不符合受限存储要求，请由操作员核对当前服务身份、所有者和目录权限；程序未修改已有目录权限。", 503);

    internal static object? CaptureFilePermissions(string file)
    {
        if (!File.Exists(file)) return null;
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw UnsafePermissions();
        if (OperatingSystem.IsWindows())
        {
            var security = new FileInfo(file).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
            var allowed = AllowedIdentities();
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !allowed.Contains(owner)) throw UnsafePermissions();
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && !allowed.Contains((SecurityIdentifier)rule.IdentityReference)) throw UnsafePermissions();
            return security;
        }
        return File.GetUnixFileMode(file);
    }
    internal static void ApplyAndVerifyPermissions(string temporary, object? permissions)
    {
        if (OperatingSystem.IsWindows())
        {
            if (permissions is FileSecurity security)
            {
                new FileInfo(temporary).SetAccessControl(security);
                VerifyPermissions(temporary, permissions);
            }
        }
        else File.SetUnixFileMode(temporary, permissions is UnixFileMode mode ? mode : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    internal static void VerifyPermissions(string file, object? permissions)
    {
        if (OperatingSystem.IsWindows() && permissions is FileSecurity expected)
        {
            var sections = AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access;
            if (new FileInfo(file).GetAccessControl(sections).GetSecurityDescriptorSddlForm(sections) != expected.GetSecurityDescriptorSddlForm(sections))
                throw UnsafePermissions();
        }
        else if (!OperatingSystem.IsWindows() && permissions is UnixFileMode mode && File.GetUnixFileMode(file) != mode) throw UnsafePermissions();
    }
}
