using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using IISManager.Contracts;
using IISManager.Core;
using Microsoft.Win32.SafeHandles;

namespace IISManager.Agent.Backends;

[SupportedOSPlatform("windows")]
internal sealed class WindowsServicePathLease : IDisposable
{
    private readonly List<SafeFileHandle> _handles = [];
    internal object[] Evidence { get; private set; } = [];
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdminSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier TrustedInstallerSid = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    internal static WindowsServicePathLease Acquire(string executable, string[] roots, string account)
    {
        if (!WindowsServiceRules.WindowsExecutableAllowed(executable, roots))
            throw new OperationException("service.pathNotAllowed", "服务程序须为允许根目录内的本机 Windows 绝对 EXE 路径。" );
        var lease = new WindowsServicePathLease();
        try
        {
            var full = Path.GetFullPath(executable);
            if (!File.Exists(full)) throw new OperationException("service.executableMissing", "服务 EXE 文件必须已由操作员准备。" );
            var accountSids = AccountSids(account);
            var chain = new List<string>();
            for (var directory = new FileInfo(full).Directory; directory is not null; directory = directory.Parent) chain.Add(directory.FullName);
            chain.Reverse(); chain.Add(full);
            var evidence = new List<object>();
            for (var i = 0; i < chain.Count; i++)
            {
                var file = i == chain.Count - 1;
                var path = chain[i];
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new OperationException("service.pathReparsePoint", "程序或父目录包含重解析点，拒绝注册。" );
                // Hold directories against deletion/rename and the EXE against writes/deletion until readback.
                var handle = CreateFile(path, 0x20080, file ? 1u : 3u, IntPtr.Zero, 3, file ? 0x00200000u : 0x02200000u, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new OperationException("service.pathUnverified", "无法锁定程序及父目录以核实身份和权限。" ); }
                lease._handles.Add(handle);
                if (!GetFileInformationByHandle(handle, out var identity) || (identity.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
                    file == ((identity.Attributes & (uint)FileAttributes.Directory) != 0))
                    throw new OperationException("service.pathUnverified", "程序或父目录身份发生变化。" );
                FileSystemSecurity acl = file ? new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access)
                    : new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
                var bytes = acl.GetSecurityDescriptorBinaryForm();
                ValidateSecurity(bytes, file || i == chain.Count - 2, file, accountSids);
                evidence.Add(new { path, identity.VolumeSerialNumber, identity.FileIndexHigh, identity.FileIndexLow,
                    identity.FileSizeHigh, identity.FileSizeLow, acl = Convert.ToHexString(SHA256.HashData(bytes)) });
            }
            // A normal console EXE is insufficient, but static PE validation can at least reject non-executables.
            using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var header = new byte[64];
                if (stream.Read(header, 0, header.Length) != header.Length || header[0] != 'M' || header[1] != 'Z')
                    throw new OperationException("service.executableInvalid", "程序不是可识别的 Windows PE 可执行文件。" );
                var peOffset = BitConverter.ToInt32(header, 60);
                if (peOffset < 64 || peOffset > stream.Length - 24) throw new OperationException("service.executableInvalid", "程序 PE 头无效。" );
                stream.Position = peOffset;
                var pe = new byte[24];
                if (stream.Read(pe, 0, pe.Length) != pe.Length || pe[0] != 'P' || pe[1] != 'E' || pe[2] != 0 || pe[3] != 0 || (BitConverter.ToUInt16(pe, 22) & 0x2000) != 0)
                    throw new OperationException("service.executableInvalid", "程序 PE 头无效或实际为 DLL。" );
                stream.Position = 0;
                evidence.Add(new { executableSha256 = Convert.ToHexString(SHA256.HashData(stream)) });
            }
            evidence.Add(new { accountSids = accountSids.Order(StringComparer.Ordinal).ToArray() });
            lease.Evidence = evidence.ToArray(); return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static HashSet<string> AccountSids(string account)
    {
        var sid = account.ToUpperInvariant() switch
        {
            "LOCALSYSTEM" => SystemSid,
            @"NT AUTHORITY\LOCALSERVICE" => new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
            @"NT AUTHORITY\NETWORKSERVICE" => new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null),
            // SCM service SIDs are deterministic: SHA1 of the uppercase UTF-16 service name,
            // expressed as the five little-endian DWORD subauthorities under S-1-5-80.
            _ when account.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase) => ServiceSid(account[11..]),
            _ => throw new OperationException("service.accountNotAllowed", "服务账号类型未受支持。" )
        };
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            sid.Value, new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.ServiceSid, null).Value
        };
        if (sid.Equals(SystemSid)) known.Add(AdminSid.Value);
        // Prove the common BUILTIN\Users membership from the actual localized local alias.
        // Merely assuming this group is always present would be unsafe on hardened servers.
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        string alias;
        try { alias = ((NTAccount)users.Translate(typeof(NTAccount))).Value.Split('\\')[^1]; }
        catch (IdentityNotMappedException) { return known; }
        UIntPtr resume = UIntPtr.Zero;
        for (var page = 0; page < 32; page++)
        {
            var code = NetLocalGroupGetMembers(null, alias, 0, out var buffer, -1, out var count, out _, ref resume);
            try
            {
                if (code is not (0 or 234)) break;
                for (var i = 0; i < count; i++)
                {
                    var member = new SecurityIdentifier(Marshal.ReadIntPtr(buffer, checked((int)i * IntPtr.Size)));
                    if (known.Contains(member.Value)) { known.Add(users.Value); return known; }
                }
                if (code == 0) break;
            }
            finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
        }
        return known;
    }

    private static SecurityIdentifier ServiceSid(string name)
    {
        var hash = SHA1.HashData(System.Text.Encoding.Unicode.GetBytes(name.ToUpperInvariant()));
        return new SecurityIdentifier("S-1-5-80-" + string.Join('-', Enumerable.Range(0, 5).Select(i =>
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(i * 4, 4)).ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    private static void ValidateSecurity(byte[] bytes, bool executableDirectory, bool file, HashSet<string> accountSids)
    {
        var descriptor = new RawSecurityDescriptor(bytes, 0);
        static bool Trusted(SecurityIdentifier? sid) => sid is not null && (sid.Equals(SystemSid) || sid.Equals(AdminSid) || sid.Equals(TrustedInstallerSid));
        if (!Trusted(descriptor.Owner) || descriptor.DiscretionaryAcl is null)
            throw new OperationException("service.pathUntrusted", "程序和父目录须由 SYSTEM、Administrators 或 TrustedInstaller 持有，并具有明确 ACL。" );
        // Ancestors may allow creating new sibling directories (as C:\ commonly does), but never replacing this chain.
        var unsafeRights = 0x10000u | 0x40000u | 0x80000u | 0x40u;
        if (executableDirectory) unsafeRights |= 0x2u | 0x4u | 0x10u | 0x100u;
        var required = file ? 0x001200A9u : 0x20u;
        uint allowed = 0;
        foreach (var entry in descriptor.DiscretionaryAcl)
        {
            if ((entry.AceFlags & AceFlags.InheritOnly) != 0) continue;
            if (entry is not CommonAce ace || ace.IsCallback)
                throw new OperationException("service.pathAclUnverified", "程序目录包含条件或扩展 ACL，无法保守核实。" );
            var mask = MapFileRights(unchecked((uint)ace.AccessMask));
            if (ace.AceQualifier == AceQualifier.AccessAllowed && !Trusted(ace.SecurityIdentifier) && (mask & unsafeRights) != 0)
                throw new OperationException("service.pathWritable", "程序或目录链允许非受信任身份写入或替换，拒绝创建高权限服务。" );
            if (ace.AceQualifier == AceQualifier.AccessDenied && (mask & required) != 0)
                throw new OperationException("service.accountAccessUnverified", "目录或程序存在涉及读/执行的拒绝权限，无法核实服务身份访问。" );
            var known = accountSids.Contains(ace.SecurityIdentifier.Value);
            if (known && ace.AceQualifier == AceQualifier.AccessAllowed) allowed |= mask;
        }
        if ((allowed & required) != required)
            throw new OperationException("service.accountAccessUnverified", "无法证明服务账号对程序及目录具备读/执行权限；Agent 不修改 ACL。" );
    }
    private static uint MapFileRights(uint mask)
    {
        if ((mask & 0x10000000) != 0) mask |= 0x001F01FF;
        if ((mask & 0x80000000) != 0) mask |= 0x00120089;
        if ((mask & 0x40000000) != 0) mask |= 0x00120116;
        if ((mask & 0x20000000) != 0) mask |= 0x001200A0;
        return mask & 0x0FFFFFFF;
    }
    public void Dispose() { foreach (var handle in _handles) handle.Dispose(); _handles.Clear(); }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        internal uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetLocalGroupGetMembers(string? server, string alias, int level, out IntPtr buffer, int preferredLength, out uint count, out uint total, ref UIntPtr resume);
    [DllImport("netapi32.dll")] private static extern int NetApiBufferFree(IntPtr buffer);
}
