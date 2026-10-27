using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using IISManager.Core;
using Microsoft.Win32.SafeHandles;

namespace IISManager.Agent.Backends;

[SupportedOSPlatform("windows")]
internal sealed class WindowsDirectoryLease : IDisposable
{
    private readonly List<SafeFileHandle> _handles = [];
    internal object[] Evidence { get; private set; } = [];

    internal static WindowsDirectoryLease Acquire(string path, string[] allowedRoots, string identity)
    {
        var lease = new WindowsDirectoryLease();
        try
        {
            var chain = SitePathGuard.Validate(path, allowedRoots);
            SecurityIdentifier sid;
            try { sid = (SecurityIdentifier)new NTAccount(identity).Translate(typeof(SecurityIdentifier)); }
            catch (IdentityNotMappedException) { throw new OperationException("site.identityUnverified", "无法解析实际应用池身份，拒绝创建站点。"); }
            var evidence = new List<object>();
            for (var i = 0; i < chain.Length; i++)
            {
                // No FILE_SHARE_DELETE: ancestors cannot be renamed/replaced while a site is committed.
                var handle = CreateFile(chain[i], 0x00020080, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new OperationException("site.directoryUnverified", "无法锁定目录链并验证安全描述符。"); }
                lease._handles.Add(handle);
                if (!GetFileInformationByHandle(handle, out var info)) throw new OperationException("site.directoryUnverified", "无法读回目录身份。");
                if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (info.Attributes & (uint)FileAttributes.Directory) == 0)
                    throw new OperationException("site.pathReparsePoint", "目录链发生变化或包含重解析点。");
                var acl = new DirectoryInfo(chain[i]).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
                var bytes = acl.GetSecurityDescriptorBinaryForm();
                ProveAccess(bytes, sid, i == chain.Length - 1 ? (uint)FileSystemRights.ReadAndExecute : (uint)FileSystemRights.Traverse);
                evidence.Add(new
                {
                    path = chain[i], info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow,
                    identity = sid.Value, acl = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                });
            }
            lease.Evidence = evidence.ToArray();
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static void ProveAccess(byte[] descriptor, SecurityIdentifier identity, uint required)
    {
        var sd = new RawSecurityDescriptor(descriptor, 0);
        // A null DACL or a conditional/object ACE needs a real token evaluation; do not guess.
        if (sd.DiscretionaryAcl is null) throw new OperationException("site.identityUnverified", "目录权限不能用首版的保守身份检查核实。");
        uint granted = 0;
        foreach (var entry in sd.DiscretionaryAcl)
        {
            if ((entry.AceFlags & AceFlags.InheritOnly) != 0) continue;
            if (entry is not CommonAce ace || ace.IsCallback)
                throw new OperationException("site.identityUnverified", "目录包含条件或扩展 ACL，需先由操作员核实真实池身份访问权限。");
            var mask = MapFileRights(unchecked((uint)ace.AccessMask));
            // Unknown group membership cannot prove a deny is irrelevant. Reject conservatively.
            if (ace.AceQualifier == AceQualifier.AccessDenied && (mask & required) != 0)
                throw new OperationException("site.identityUnverified", "目录存在涉及所需权限的拒绝 ACE，不能证明应用池身份可访问。");
            var known = ace.SecurityIdentifier.Equals(identity) || ace.SecurityIdentifier.IsWellKnown(WellKnownSidType.WorldSid) ||
                ace.SecurityIdentifier.IsWellKnown(WellKnownSidType.AuthenticatedUserSid);
            if (known && ace.AceQualifier == AceQualifier.AccessAllowed) granted |= mask;
        }
        if ((granted & required) != required)
            throw new OperationException("site.identityAccessDenied", "无法证明实际池身份拥有目录读/执行权限；仅通过组授权的目录需操作员先核实并明确授权。Agent 不修改 ACL。");
    }

    private static uint MapFileRights(uint mask)
    {
        if ((mask & 0x10000000) != 0) mask |= 0x001F01FF;
        if ((mask & 0x80000000) != 0) mask |= 0x00120089;
        if ((mask & 0x40000000) != 0) mask |= 0x00120116;
        if ((mask & 0x20000000) != 0) mask |= 0x001200A0;
        return mask & 0x0FFFFFFF;
    }
    public void Dispose()
    {
        foreach (var handle in _handles) handle.Dispose();
        _handles.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        internal uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
