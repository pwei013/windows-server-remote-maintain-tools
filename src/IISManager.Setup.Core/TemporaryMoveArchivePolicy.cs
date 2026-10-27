using System.Globalization;

namespace IISManager.Setup.Core;

public sealed record ArchiveAce(byte AceType, byte AceFlags, uint AccessMask, string Sid,
    bool IsCallback = false, int OpaqueLength = 0);

public sealed record ArchiveSecurityDescriptor(string OwnerSid, string GroupSid, bool DaclProtected,
    byte Revision, ArchiveAce[] Dacl);

public enum TemporaryArchiveAclShape { Unchanged, ServiceReadExecuteMadeExplicit }

public static class TemporaryMoveArchivePolicy
{
    private const string AdministratorsSid = "S-1-5-32-544";
    private const string SystemSid = "S-1-5-18";
    private const uint FullControl = 0x001F01FF;
    private const uint ReadExecute = 0x001200A9;
    private const byte AccessAllowedType = 0;
    private const byte InheritedFlag = 16;

    // This whitelist applies only to a separately verified archived setup temporary file.
    // It does not establish ACL equivalence for program files, configuration or directories,
    // and it authorizes neither restoring permissions nor moving or deleting the archive.
    public static TemporaryArchiveAclShape RequireAcceptedArchive(ArchiveSecurityDescriptor original,
        ArchiveSecurityDescriptor archived, string actualServiceSid)
    {
        if (!IsServiceSid(actualServiceSid) || !HasRequiredDescriptor(original) || !HasRequiredDescriptor(archived))
            throw Rejected();
        if (!IsAce(original.Dacl[0], SystemSid, FullControl, InheritedFlag) ||
            !IsAce(original.Dacl[1], AdministratorsSid, FullControl, InheritedFlag) ||
            !IsAce(original.Dacl[2], actualServiceSid, ReadExecute, InheritedFlag)) throw Rejected();

        if (archived.Dacl.SequenceEqual(original.Dacl)) return TemporaryArchiveAclShape.Unchanged;
        if (IsAce(archived.Dacl[0], actualServiceSid, ReadExecute, 0) &&
            IsAce(archived.Dacl[1], SystemSid, FullControl, InheritedFlag) &&
            IsAce(archived.Dacl[2], AdministratorsSid, FullControl, InheritedFlag))
            return TemporaryArchiveAclShape.ServiceReadExecuteMadeExplicit;
        throw Rejected();
    }

    private static bool HasRequiredDescriptor(ArchiveSecurityDescriptor? descriptor) => descriptor is not null &&
        descriptor.OwnerSid == AdministratorsSid && descriptor.GroupSid == AdministratorsSid &&
        !descriptor.DaclProtected && descriptor.Revision == 2 && descriptor.Dacl is { Length: 3 };

    private static bool IsAce(ArchiveAce? ace, string sid, uint mask, byte flags) => ace is not null &&
        ace.AceType == AccessAllowedType && ace.AceFlags == flags && ace.AccessMask == mask &&
        ace.Sid == sid && !ace.IsCallback && ace.OpaqueLength == 0;

    private static bool IsServiceSid(string? sid)
    {
        if (sid is null || !sid.StartsWith("S-1-5-80-", StringComparison.Ordinal)) return false;
        var parts = sid.Split('-');
        if (parts.Length != 9) return false;
        foreach (var part in parts.Skip(4))
        {
            if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                part != value.ToString(CultureInfo.InvariantCulture)) return false;
        }
        return true;
    }

    private static SetupException Rejected() => new("ARCHIVED_TEMP_ACL_SHAPE_REJECTED",
        "归档临时文件的权限形状不符合精确白名单；保留现场并停止收尾，不修改原程序或配置权限。");
}
