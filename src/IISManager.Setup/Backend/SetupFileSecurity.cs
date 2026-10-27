using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using IISManager.Setup.Core;
using Microsoft.Win32.SafeHandles;

namespace IISManager.Setup.Backend;

internal sealed record AccessEvidence(string Path, string BeforeSddl, string AfterSddl, bool ReadBackVerified = false);

internal static class SetupFileSecurity
{
    internal const string RestrictedSddl = "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
    internal const FileSystemRights KeyRead = FileSystemRights.Read | FileSystemRights.Synchronize;
    internal static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    internal static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);

    internal static string CanonicalPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal) ||
            value.Length > 220 || value.Contains('"') || value.AsSpan(2).Contains(':') || value.Any(char.IsControl))
            throw new SetupException("PATH_UNSUPPORTED", "路径须为本机 Windows 绝对路径，不能使用 UNC、设备路径或备用数据流。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (full.Length <= 3 || !full.Equals(value.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new SetupException("PATH_NOT_CANONICAL", "请填写无相对片段、尾部分隔符或路径别名的组件绝对目录。");
        if (new DriveInfo(Path.GetPathRoot(full)!).DriveType is DriveType.Network or DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown)
            throw new SetupException("PATH_VOLUME_UNSUPPORTED", "组件和数据须位于可证明的本机磁盘卷。");
        return full;
    }
    internal static bool Under(string path, string root) => SetupPathBoundary.IsUnder(path: path, root: root);
    internal static string ResolveRelative(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.StartsWith('/') ||
            relative.Split('/').Any(x => x.Length == 0 || x is "." or ".." || x.EndsWith(' ') || x.EndsWith('.') || x.Any(c => char.IsControl(c) || ":*?\"<>|".Contains(c))))
            throw new SetupException("PAYLOAD_PATH_INVALID", "载荷相对路径不符合安全契约。");
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!Under(full, root)) throw new SetupException("PAYLOAD_PATH_ESCAPE", "载荷路径逃出提取根目录。");
        return full;
    }
    internal static void RejectReparseChain(string path, bool finalMayBeAbsent)
    {
        var full = Path.GetFullPath(path);
        for (var info = new DirectoryInfo(Directory.Exists(full) ? full : Path.GetDirectoryName(full)!); info is not null; info = info.Parent)
        {
            if (!info.Exists) continue;
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
                throw new SetupException("PATH_REPARSE_POINT", "路径链包含重解析点、联接或符号链接，拒绝操作。");
        }
        if (File.Exists(full))
        {
            var file = new FileInfo(full);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.LinkTarget is not null)
                throw new SetupException("PATH_REPARSE_POINT", "目标文件为重解析点或符号链接，拒绝操作。");
        }
        else if (!Directory.Exists(full) && !finalMayBeAbsent)
            throw new SetupException("PATH_MISSING", "需要核验的目录或文件不存在。");
    }
    internal static void RequireTrustedAncestors(string requested, bool includeExistingDirectory = true)
    {
        RejectReparseChain(requested, true);
        SecurityIdentifier installer;
        try { installer = (SecurityIdentifier)new NTAccount(@"NT SERVICE\TrustedInstaller").Translate(typeof(SecurityIdentifier)); }
        catch (IdentityNotMappedException) { throw new SetupException("TRUSTED_OWNER_UNVERIFIED", "无法核实 Windows 受信任安装身份，拒绝猜测祖先权限。"); }
        static bool Same(SecurityIdentifier? first, SecurityIdentifier second) => first is not null && first.Equals(second);
        bool Trusted(SecurityIdentifier? sid) => Same(sid, Administrators) || Same(sid, System) || Same(sid, installer);
        var start = includeExistingDirectory && Directory.Exists(requested) ? requested : Path.GetDirectoryName(requested)!;
        // Root Users CreateDirectories is not a replacement grant. InheritOnly does not apply to this existing object;
        // every new installer directory instead receives an explicit protected descriptor and cannot inherit Creator Owner.
        const uint replacement = 0x00010000 | 0x00040000 | 0x00080000 | 0x00000040; // DELETE, WRITE_DAC, WRITE_OWNER, DELETE_CHILD
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (!directory.Exists) continue;
            var descriptor = new RawSecurityDescriptor(ReadSddl(directory.FullName));
            if (!Trusted(descriptor.Owner) || descriptor.DiscretionaryAcl is null)
                throw new SetupException("PATH_ANCESTOR_UNTRUSTED", "程序、回执或数据祖先须由 Admin/System/TrustedInstaller 持有；不修改父目录权限。");
            foreach (var entry in descriptor.DiscretionaryAcl)
            {
                if ((entry.AceFlags & AceFlags.InheritOnly) != 0) continue;
                if (entry is not CommonAce ace || ace.IsCallback)
                    throw new SetupException("PATH_ANCESTOR_ACL_UNVERIFIED", "祖先存在复杂 ACL，无法排除目录置换，拒绝安装。");
                var prohibited = replacement | (includeExistingDirectory && directory.FullName.Equals(requested, StringComparison.OrdinalIgnoreCase) ? 0x00000116u : 0u);
                if (ace.AceQualifier == AceQualifier.AccessAllowed && !Trusted(ace.SecurityIdentifier) && (Expand(unchecked((uint)ace.AccessMask)) & prohibited) != 0)
                    throw new SetupException("PATH_ANCESTOR_REPLACEMENT_ALLOWED", "祖先给其他身份删除、删除子项或改写 Owner/DACL 的权限，可置换服务入口；请选择受信任路径，不修改父 ACL。");
            }
        }
    }
    internal static void RequireTrustedProgramFile(string path)
    {
        RejectReparseChain(path, false);
        if (!File.Exists(path)) throw new SetupException("PROGRAM_FILE_MISSING", "需要核验的程序或配置文件不存在。");
        var installer = (SecurityIdentifier)new NTAccount(@"NT SERVICE\TrustedInstaller").Translate(typeof(SecurityIdentifier));
        bool Trusted(SecurityIdentifier? sid) => sid is not null && (sid.Equals(Administrators) || sid.Equals(System) || sid.Equals(installer));
        var descriptor = new RawSecurityDescriptor(ReadSddl(path));
        if (!Trusted(descriptor.Owner) || descriptor.DiscretionaryAcl is null)
            throw new SetupException("PROGRAM_FILE_OWNER_UNTRUSTED", "程序、配置或归属文件 Owner/DACL 不受信任；拒绝继承可劫持的权限，不重置既有 ACL。");
        const uint writeOrReplace = 0x00010000 | 0x00040000 | 0x00080000 | 0x00000116; // DELETE, WRITE_DAC, WRITE_OWNER, data/append/EA/attributes
        foreach (var entry in descriptor.DiscretionaryAcl)
        {
            if ((entry.AceFlags & AceFlags.InheritOnly) != 0) continue;
            if (entry is not CommonAce ace || ace.IsCallback)
                throw new SetupException("PROGRAM_FILE_ACL_UNVERIFIED", "程序或配置文件存在复杂 ACE，无法排除改写，拒绝继续。");
            if (ace.AceQualifier == AceQualifier.AccessAllowed && !Trusted(ace.SecurityIdentifier) && (Expand(unchecked((uint)ace.AccessMask)) & writeOrReplace) != 0)
                throw new SetupException("PROGRAM_FILE_WRITABLE_UNTRUSTED", "其他身份可写入、置换或改写程序/配置权限，请操作员先准备安全权限；安装器不收紧既有 ACL。");
        }
    }
    internal static void SetNewProgramFileOwner(string path)
    {
        // Called only for a CreateNew file owned by this execution, never for an existing target/configuration.
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
        security.SetOwner(Administrators); security.SetGroup(Administrators);
        var expected = security.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
        new FileInfo(path).SetAccessControl(security);
        RequireSameDescriptor(expected, ReadSddl(path));
        RequireTrustedProgramFile(path);
    }

    internal static string[] CreateRestrictedTree(string requested)
    {
        RejectReparseChain(requested, true);
        var absent = new Stack<string>();
        for (var current = requested; !Directory.Exists(current); current = Path.GetDirectoryName(current) ?? throw new SetupException("PATH_PARENT_MISSING", "未找到已存在父目录。"))
        {
            if (File.Exists(current)) throw new SetupException("PATH_OCCUPIED", "目录路径被文件占用。");
            absent.Push(current);
        }
        var created = new List<string>();
        while (absent.Count > 0)
        {
            var next = absent.Pop();
            CreateDirectoryWithDescriptor(next, RestrictedSddl);
            RequireRestricted(next);
            created.Add(next);
        }
        return created.ToArray();
    }

    internal static void CreateDirectoryWithDescriptor(string path, string sddl)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
            throw new SetupException("ACL_DESCRIPTOR_INVALID", "无法创建受限权限描述符。");
        try
        {
            var attributes = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            if (!CreateDirectory(path, ref attributes)) throw new SetupException("DIRECTORY_CREATE_FAILED", "受限目录创建未能核实，拒绝继续写入。");
        }
        finally { LocalFree(descriptor); }
    }
    internal static string ReadSddl(string path)
    {
        var sections = AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access;
        FileSystemSecurity security = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl(sections) : new FileInfo(path).GetAccessControl(sections);
        return security.GetSecurityDescriptorSddlForm(sections);
    }
    internal static void RequireRestricted(string path, SecurityIdentifier? service = null)
    {
        RejectReparseChain(path, false);
        var descriptor = new RawSecurityDescriptor(ReadSddl(path));
        if (descriptor.Owner is null || (!descriptor.Owner.Equals(Administrators) && !descriptor.Owner.Equals(System)) ||
            descriptor.Group is null || descriptor.DiscretionaryAcl is null || (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0)
            throw new SetupException("DIRECTORY_ACL_UNVERIFIED", "受限目录所有者、继承或 DACL 无法核实，不修改既有权限。");
        foreach (var entry in descriptor.DiscretionaryAcl)
            if (entry is not CommonAce ace || ace.IsCallback || ace.AceQualifier != AceQualifier.AccessAllowed ||
                (!ace.SecurityIdentifier.Equals(Administrators) && !ace.SecurityIdentifier.Equals(System) && (service is null || !ace.SecurityIdentifier.Equals(service))))
                throw new SetupException("DIRECTORY_ACL_TOO_BROAD", "目录存在未授权身份或复杂 ACE，不重置既有权限；请操作员先准备受限目录。");
    }
    internal static void ProveAccess(string path, SecurityIdentifier identity, FileSystemRights required)
    {
        RejectReparseChain(path, false);
        var descriptor = new RawSecurityDescriptor(ReadSddl(path));
        if (descriptor.DiscretionaryAcl is null) throw new SetupException("IDENTITY_ACCESS_UNVERIFIED", "空 DACL 不能用于证明精确身份访问权限。");
        uint allowed = 0;
        var needed = unchecked((uint)required);
        foreach (var entry in descriptor.DiscretionaryAcl)
        {
            if ((entry.AceFlags & AceFlags.InheritOnly) != 0) continue;
            if (entry is not CommonAce ace || ace.IsCallback) throw new SetupException("IDENTITY_ACCESS_UNVERIFIED", "条件或扩展 ACL 无法证明身份权限。");
            var mask = Expand(unchecked((uint)ace.AccessMask));
            if (ace.AceQualifier == AceQualifier.AccessDenied && (mask & needed) != 0)
                throw new SetupException("IDENTITY_ACCESS_UNVERIFIED", "相关拒绝 ACE 无法排除对实际服务身份的影响。");
            // Only an exact SID grant is used. Group membership is not guessed from the elevated installer token.
            if (ace.AceQualifier == AceQualifier.AccessAllowed && ace.SecurityIdentifier.Equals(identity)) allowed |= mask;
        }
        if ((allowed & needed) != needed) throw new SetupException("IDENTITY_ACCESS_UNVERIFIED", "未能证明实际服务身份拥有所需直接权限，请由操作员准备；不自动扩大权限。");
    }
    internal static AccessEvidence GrantNewDirectory(string path, SecurityIdentifier identity, FileSystemRights rights, Action<AccessEvidence> beforeWrite)
    {
        RequireRestricted(path);
        var before = ReadSddl(path);
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(identity, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var expected = security.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
        beforeWrite(new(path, before, expected));
        new DirectoryInfo(path).SetAccessControl(security);
        RequireSameDescriptor(expected, ReadSddl(path));
        RequireRestricted(path, identity);
        ProveAccess(path, identity, rights);
        return new(path, before, ReadSddl(path), true);
    }
    internal static AccessEvidence GrantExactKeyRead(string path, SecurityIdentifier identity, Action<AccessEvidence> beforeWrite)
    {
        RejectReparseChain(path, false);
        using var pin = PathLease.Acquire(path, includeFile: true);
        var before = ReadSddl(path);
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
        PreflightKeyGrant(path);
        var raw = new RawSecurityDescriptor(before);
        var acl = raw.DiscretionaryAcl ?? throw new SetupException("KEY_ACL_UNSUPPORTED", "私钥 DACL 无法核实。");
        // Retain every original ACE and its order; add only one explicit read entry before inherited ACEs.
        var insertion = acl.Count;
        for (var i = 0; i < acl.Count; i++) if ((acl[i].AceFlags & AceFlags.Inherited) != 0) { insertion = i; break; }
        acl.InsertAce(insertion, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, (int)KeyRead, identity, false, null));
        var bytes = new byte[raw.BinaryLength]; raw.GetBinaryForm(bytes, 0);
        security.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access);
        var expected = raw.GetSddlForm(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
        beforeWrite(new(path, before, expected));
        new FileInfo(path).SetAccessControl(security); // Only DACL; no owner/group, parent or SACL change.
        var after = ReadSddl(path);
        RequireSameDescriptor(expected, after);
        ProveAccess(path, identity, KeyRead);
        return new(path, before, after, true);
    }
    internal static void PreflightKeyGrant(string path)
    {
        RejectReparseChain(path, false);
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
        if (!security.AreAccessRulesCanonical) throw new SetupException("KEY_ACL_UNSUPPORTED", "私钥文件存在非标准 ACL，拒绝自动授权。");
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access));
        if (raw.DiscretionaryAcl is null || raw.Owner is null || raw.Group is null) throw new SetupException("KEY_ACL_UNSUPPORTED", "私钥 Owner/Group/DACL 记录不完整。");
        foreach (var entry in raw.DiscretionaryAcl)
            if (entry is not CommonAce ace || ace.IsCallback || (ace.AceQualifier == AceQualifier.AccessDenied && (Expand(unchecked((uint)ace.AccessMask)) & (uint)KeyRead) != 0))
                throw new SetupException("KEY_ACL_UNSUPPORTED", "私钥 ACL 包含复杂或相关拒绝规则，拒绝自动修改。");
    }
    internal static void RequireSameDescriptor(string expected, string actual)
    {
        var left = new RawSecurityDescriptor(expected); var right = new RawSecurityDescriptor(actual);
        if (!Equals(left.Owner, right.Owner) || !Equals(left.Group, right.Group) ||
            (left.ControlFlags & ControlFlags.DiscretionaryAclProtected) != (right.ControlFlags & ControlFlags.DiscretionaryAclProtected) ||
            !AclBytes(left.DiscretionaryAcl).SequenceEqual(AclBytes(right.DiscretionaryAcl)))
            throw new SetupException("ACL_READBACK_MISMATCH", "真实 Owner/Group/DACL 与记录不一致，停止相关写入；未恢复或改写 SACL。");
    }
    private static byte[] AclBytes(RawAcl? acl) { if (acl is null) return []; var bytes = new byte[acl.BinaryLength]; acl.GetBinaryForm(bytes, 0); return bytes; }
    private static uint Expand(uint mask)
    {
        if ((mask & 0x10000000) != 0) mask |= 0x1F01FF;
        if ((mask & 0x80000000) != 0) mask |= 0x120089;
        if ((mask & 0x40000000) != 0) mask |= 0x120116;
        if ((mask & 0x20000000) != 0) mask |= 0x1200A0;
        return mask & 0x0FFFFFFF;
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { internal int Size; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor, uint revision, out IntPtr security, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectory(string path, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}

internal sealed class PathLease : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    internal string LeafIdentity { get; private set; } = "";
    internal static PathLease Acquire(string path, bool includeFile = false, bool guardDirectories = false)
    {
        var lease = new PathLease();
        try
        {
            SetupFileSecurity.RejectReparseChain(path, true);
            var file = includeFile && File.Exists(path);
            var start = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
            var chain = new Stack<string>();
            for (var current = new DirectoryInfo(start); current is not null; current = current.Parent) if (current.Exists) chain.Push(current.FullName);
            foreach (var item in chain) lease.Pin(item, true, guardDirectories);
            if (file) lease.Pin(path, false, guardDirectories: false);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
    private void Pin(string path, bool directory, bool guardDirectories)
    {
        // Metadata-only handles do not participate in all sharing checks. Uninstall directory guards
        // include FILE_LIST_DIRECTORY, while the live Agent's exclusively opened owner.lock is only
        // sampled for identity here and receives a content lock after the service has stopped.
        var access = 0x20080u | (directory && guardDirectories ? 0x1u : 0u);
        var handle = CreateFile(path, access, 3, IntPtr.Zero, 3, 0x00200000 | (directory ? 0x02000000u : 0), IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new SetupException("PATH_LOCK_FAILED", "无法固定路径链，拒绝操作。"); }
        handles.Add(handle);
        if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
            (((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory))
            throw new SetupException("PATH_CHANGED", "路径身份或重解析状态发生变化，拒绝操作。");
        var final = new StringBuilder(4096);
        var length = GetFinalPathNameByHandle(handle, final, final.Capacity, 0);
        var actual = final.ToString();
        if (length == 0 || length >= final.Capacity || !actual.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            !Path.TrimEndingDirectorySeparator(actual[4..]).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase))
            throw new SetupException("PATH_ALIAS_UNVERIFIED", "真实长名称与指定路径不符，拒绝 DOS 别名或未知路径映射。");
        LeafIdentity = $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}";
    }
    public void Dispose() { foreach (var handle in handles) handle.Dispose(); handles.Clear(); }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder name, int size, uint flags);
}
