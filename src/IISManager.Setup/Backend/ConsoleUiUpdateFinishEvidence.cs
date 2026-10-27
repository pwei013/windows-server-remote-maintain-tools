using System.Security.AccessControl;
using System.Text.Json;
using System.Text.RegularExpressions;
using IISManager.Setup.Core;

namespace IISManager.Setup.Backend;

public sealed partial class ConsoleUiUpdateRecoveryBackend
{
    // This is a new reviewed operation, linked to the failed completion. Its original claim is never removed.
    private sealed record InterruptedUiCompletion(Guid Id, string ReceiptPath, string DirectoryPath,
        Guid OriginalId, string OriginalReceiptPath, JsonElement Value, FileEvidence Snapshot,
        FileEvidence? OriginalClaim = null, FileEvidence? ArchivedTemporary = null)
    {
        internal string StageDirectory => Path.Combine(DirectoryPath, "stage");
        internal string BackupDirectory => Path.Combine(DirectoryPath, "runtime-backup");
        internal string GuardPath => Path.Combine(DirectoryPath, "console-ui-update-finish-guard.json");
    }

    private static InterruptedUiCompletion ReadInterruptedCompletion(string receiptPath)
    {
        var full = Path.GetFullPath(receiptPath);
        var directory = SetupFileSecurity.CanonicalPath(Path.GetDirectoryName(full)!);
        var receipts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IISManager.Setup", "receipts");
        var name = Path.GetFileName(directory);
        if (receiptPath != full || Path.GetFileName(full) != "receipt.json" ||
            !Path.GetDirectoryName(directory)!.Equals(receipts, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(name, "N", out var id) || id == Guid.Empty || name != id.ToString("N"))
            throw new SetupException("UI_FINISH_RECEIPT_PATH_INVALID", "此入口须填写受限目录中归档阶段失败的关联回执。");
        SetupFileSecurity.RejectReparseChain(full, false); SetupFileSecurity.RequireTrustedAncestors(directory);
        SetupFileSecurity.RequireRestricted(directory); SetupFileSecurity.RequireTrustedProgramFile(full);
        var snapshot = SetupInventory.Snapshot(full);
        using var json = ReadJson(full);
        var value = json.RootElement;
        if (value.GetProperty("version").GetInt32() != 1 || value.GetProperty("id").GetGuid() != id ||
            Text(value, "component") != "Console" || Text(value, "mode") != "Update" ||
            Text(value, "transportMode") != "HttpToken" || Text(value, "sourceAccessMode") != "PrivateNetwork" ||
            Text(value, "procedure") != "CompleteConsoleUiUpdate" || Text(value, "state") != "PartialOrUnknown" ||
            Text(value, "code") != "ACL_READBACK_MISMATCH" || Text(value, "failureStage") != "PreserveTemporary" ||
            Text(value, "exceptionType") != "SetupException" || value.GetProperty("hresult").GetInt32() != unchecked((int)0x80131500) ||
            !EmptyOptional(value, "runtimeFile") || !EmptyOptional(value, "runtimeStep") ||
            value.TryGetProperty("temporaryPreservations", out var actions) && actions.GetArrayLength() != 0)
            throw new SetupException("UI_FINISH_RECEIPT_UNSUPPORTED", "关联回执不是支持的、尚未替换界面文件的旧版归档权限读回失败。");
        var originalId = value.GetProperty("recoveryOf").GetGuid();
        if (originalId == Guid.Empty || originalId == id)
            throw new SetupException("UI_FINISH_LINK_INVALID", "关联回执缺少规范的原更新关联。");
        SetupInventory.VerifySnapshots([snapshot]);
        return new(id, full, directory, originalId, Path.Combine(receipts, originalId.ToString("N"), "receipt.json"),
            value.Clone(), snapshot);
    }

    private static bool EmptyOptional(JsonElement value, string key) => !value.TryGetProperty(key, out var field) ||
        field.ValueKind == JsonValueKind.Null || field.ValueKind == JsonValueKind.String && field.GetString() == "";

    private static FileEvidence VerifyOriginalClaim(VerifiedPartialConsoleUiReceipt original, InterruptedUiCompletion interrupted)
    {
        if (original.Id != interrupted.OriginalId || !original.ReceiptPath.Equals(interrupted.OriginalReceiptPath, StringComparison.OrdinalIgnoreCase))
            throw new SetupException("UI_FINISH_LINK_INVALID", "原更新与关联回执不匹配。");
        SetupFileSecurity.RequireTrustedProgramFile(original.GuardPath);
        var snapshot = SetupInventory.Snapshot(original.GuardPath);
        using var json = ReadJson(original.GuardPath);
        var value = json.RootElement;
        if (value.GetProperty("version").GetInt32() != 1 || Text(value, "procedure") != "CompleteConsoleUiUpdate" ||
            value.GetProperty("originalReceiptId").GetGuid() != original.Id ||
            value.GetProperty("recoveryReceiptId").GetGuid() != interrupted.Id ||
            !string.Equals(Text(value, "receiptPath"), interrupted.ReceiptPath, StringComparison.OrdinalIgnoreCase))
            throw new SetupException("UI_FINISH_CLAIM_LINK_INVALID", "原一次性标记不属于该关联回执，拒绝接续。");
        SetupInventory.VerifySnapshots([snapshot]);
        return snapshot;
    }

    private static void RequireUnclaimed(Evidence proof)
    {
        if (proof.Interrupted is null) { proof.Original.RequireUnclaimed(); return; }
        SetupFileSecurity.RejectReparseChain(proof.Interrupted.GuardPath, true);
        if (File.Exists(proof.Interrupted.GuardPath) || Directory.Exists(proof.Interrupted.GuardPath))
            throw new SetupException("UI_FINISH_ALREADY_CLAIMED", "此关联回执已提交过独立完成操作；请只读核对，不能再次提交。");
    }

    private static Evidence VerifyInterruptedCompletion(Evidence proof, PayloadManifest manifest)
    {
        var interrupted = proof.Interrupted!;
        var value = interrupted.Value;
        var before = value.GetProperty("beforeService").Deserialize<ServiceEvidence>(PayloadArchive.JsonOptions);
        var after = value.GetProperty("afterService").Deserialize<ServiceEvidence>(PayloadArchive.JsonOptions);
        WindowsSetupBackend.RequireSameService(proof.Original.BeforeService, before, proof.Original.BeforeService.State);
        WindowsSetupBackend.RequireSameService(proof.Original.BeforeService, after, 1);
        if (after!.ProcessId != 0 || !WindowsServiceManager.SameAccount(proof.Existing.Plan.ServiceAccount, @"NT SERVICE\IISManager.Console") ||
            proof.Original.TemporaryFiles.Length != 0)
            throw new SetupException("UI_FINISH_SCENE_UNSUPPORTED", "此入口仅支持固定 Console 虚拟服务已停止、原临时文件已归档的现场。");

        var originals = ReadArray<FileEvidence>(value, "runtimeBackups", 4096);
        RequireUniquePaths(originals.Select(f => f.Path));
        RequireFileSet(proof.Existing.Plan.InstallDirectory, originals.Select(f => f.Path), proof.Targets.Select(f => f.Path));
        SetupInventory.VerifySnapshots(originals); // Every byte, owner/group/DACL and parent must still match the failed operation.
        var preserved = ReadArray<FileEvidence>(value, "preservedFiles", 4096);
        RequireUniquePaths(preserved.Select(f => f.Path));
        var expectedPreserved = proof.Existing.ConfigurationFiles.Select(f => f.Path).Concat([
            proof.Original.ReceiptPath, proof.Original.BackupMarkerSnapshot.Path, proof.Existing.MarkerEvidence!.Path]);
        RequireFileSet(interrupted.DirectoryPath, preserved.Select(f => f.Path), expectedPreserved);
        SetupInventory.VerifySnapshots(preserved);
        var permissions = ReadArray<AccessEvidence>(value, "permissions", 16384);
        RequireUniquePaths(permissions.Select(p => p.Path));
        RequireFileSet(interrupted.DirectoryPath, permissions.Select(p => p.Path), proof.Directories.Select(p => p.Path));
        foreach (var access in permissions)
        {
            if (!access.ReadBackVerified) throw new SetupException("UI_FINISH_PERMISSION_RECORD_INVALID", "关联回执含未完成或额外的权限写入记录。");
            SetupFileSecurity.RequireSameDescriptor(access.BeforeSddl, access.AfterSddl);
        }
        VerifyDirectories(permissions);

        foreach (var directory in new[] { interrupted.StageDirectory, interrupted.BackupDirectory })
        { SetupFileSecurity.RequireTrustedAncestors(directory); SetupFileSecurity.RequireRestricted(directory); }
        var stageFiles = WalkFiles(interrupted.StageDirectory);
        RequireFileSet(interrupted.StageDirectory, stageFiles,
            manifest.Files.Select(f => SetupFileSecurity.ResolveRelative(interrupted.StageDirectory, f.Path)));
        foreach (var file in manifest.Files)
        {
            var path = SetupFileSecurity.ResolveRelative(interrupted.StageDirectory, file.Path);
            // Legacy ExtractTo did not set a program-file owner. This is corroborating evidence, never the copy source.
            SetupInventory.VerifyFile(path, file);
        }
        var backupFiles = WalkFiles(interrupted.BackupDirectory);
        var archivedPaths = backupFiles.Where(p => Path.GetDirectoryName(p)!.Equals(interrupted.BackupDirectory, StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(Path.GetFileName(p), @"^preserved-\.[0-9a-f]{32}\.setup-tmp$", RegexOptions.CultureInvariant)).ToArray();
        if (archivedPaths.Length != 1)
            throw new SetupException("UI_FINISH_ARCHIVE_SET_INVALID", "须保留唯一已核对的归档临时文件，不能有遗漏或额外产物。");
        var archived = SetupInventory.Snapshot(archivedPaths[0]);
        SetupFileSecurity.RequireTrustedProgramFile(archived.Path);
        SetupFileSecurity.RequireSameDescriptor(SetupFileSecurity.RestrictedSddl, archived.ParentSddl);
        var originalTemporaryName = Path.GetFileName(archived.Path)["preserved-".Length..];
        var originalTemporaryPath = Path.Combine(proof.Existing.Plan.InstallDirectory, originalTemporaryName);
        SetupFileSecurity.RejectReparseChain(originalTemporaryPath, true);
        if (File.Exists(originalTemporaryPath) || Directory.Exists(originalTemporaryPath))
            throw new SetupException("UI_FINISH_TEMP_SOURCE_PRESENT", "已归档临时文件的原路径仍被占用，拒绝完成。");
        var unfinished = proof.Original.Permissions.Last();
        var originalFile = proof.Original.Originals.SingleOrDefault(f => f.Path.Equals(unfinished.Path, StringComparison.OrdinalIgnoreCase));
        if (unfinished.ReadBackVerified || originalFile is null ||
            !Path.GetDirectoryName(originalFile.Path)!.Equals(proof.Existing.Plan.InstallDirectory, StringComparison.OrdinalIgnoreCase))
            throw new SetupException("UI_FINISH_TEMP_ORIGIN_INVALID", "归档临时文件缺少原最后未完成替换记录的依据。");
        var runtime = manifest.Files.Single(f => f.Path == Relative(proof.Existing.Plan.InstallDirectory, originalFile.Path));
        if (archived.Size != originalFile.Size || archived.Sha256 != originalFile.Sha256 ||
            archived.Size != runtime.Size || archived.Sha256 != runtime.Sha256)
            throw new SetupException("UI_FINISH_TEMP_BYTES_INVALID", "归档临时文件须同时匹配未变化原运行文件及完整新载荷。");
        var identity = WindowsServiceManager.ResolveIdentity(proof.Existing.Plan.ServiceAccount);
        TemporaryMoveArchivePolicy.RequireAcceptedArchive(ToArchiveDescriptor(originalFile.Sddl), ToArchiveDescriptor(archived.Sddl), identity.Value);
        // Capture its actual archival descriptor; never restore the former inherited runtime descriptor.
        var expectedBackups = originals.Select(f => SetupFileSecurity.ResolveRelative(interrupted.BackupDirectory,
            Relative(proof.Existing.Plan.InstallDirectory, f.Path))).Append(Path.Combine(interrupted.BackupDirectory, SetupInventory.MarkerName)).Append(archived.Path);
        RequireFileSet(interrupted.BackupDirectory, backupFiles, expectedBackups);
        foreach (var file in originals)
        {
            var path = SetupFileSecurity.ResolveRelative(interrupted.BackupDirectory, Relative(proof.Existing.Plan.InstallDirectory, file.Path));
            SetupFileSecurity.RequireTrustedProgramFile(path);
            SetupInventory.VerifyFile(path, new(Relative(proof.Existing.Plan.InstallDirectory, file.Path), file.Size, file.Sha256, PayloadFileKind.Runtime));
        }
        var backupMarkerPath = Path.Combine(interrupted.BackupDirectory, SetupInventory.MarkerName);
        SetupFileSecurity.RequireTrustedProgramFile(backupMarkerPath);
        var backupMarker = SetupInventory.Snapshot(backupMarkerPath);
        if (backupMarker.Size != proof.Existing.MarkerEvidence.Size || backupMarker.Sha256 != proof.Existing.MarkerEvidence.Sha256)
            throw new SetupException("UI_FINISH_BACKUP_MARKER_DRIFT", "关联备份的 Updating 归属记录与当前现场不符。");
        var claim = VerifyOriginalClaim(proof.Original, interrupted);
        var known = proof.FrozenFiles.Concat([interrupted.Snapshot, archived, claim, backupMarker]).ToArray();
        SetupInventory.VerifySnapshots(known); SetupInventory.VerifySnapshots(originals); SetupInventory.VerifySnapshots(preserved);
        var extra = manifest.Files.Select(file => CaptureVerifiedFile(
                SetupFileSecurity.ResolveRelative(interrupted.StageDirectory, file.Path), file.Size, file.Sha256))
            .Concat(originals.Select(file => CaptureVerifiedFile(SetupFileSecurity.ResolveRelative(interrupted.BackupDirectory,
                Relative(proof.Existing.Plan.InstallDirectory, file.Path)), file.Size, file.Sha256))).ToArray();
        var files = known.Concat(extra).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        var directories = proof.Directories.Concat(WalkDirectories(interrupted.StageDirectory).Concat(WalkDirectories(interrupted.BackupDirectory))
            .Append(interrupted.DirectoryPath).Select(p => new AccessEvidence(p, SetupFileSecurity.ReadSddl(p), SetupFileSecurity.ReadSddl(p), true)))
            .DistinctBy(p => p.Path, StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Path, StringComparer.Ordinal).ToArray();
        return proof with { FrozenFiles = files, Directories = directories,
            Interrupted = interrupted with { OriginalClaim = claim, ArchivedTemporary = archived } };
    }

    private static FileEvidence CaptureVerifiedFile(string path, long size, string sha256)
    {
        var snapshot = SetupInventory.Snapshot(path);
        if (snapshot.Size != size || snapshot.Sha256 != sha256)
            throw new SetupException("UI_FINISH_EVIDENCE_BYTES_DRIFT", "捕获归档证据时字节与已核对载荷或记录不符，拒绝提交。");
        return snapshot;
    }

    private static ArchiveSecurityDescriptor ToArchiveDescriptor(string sddl)
    {
        var raw = new RawSecurityDescriptor(sddl);
        if (raw.Owner is null || raw.Group is null || raw.DiscretionaryAcl is null)
            throw new SetupException("UI_FINISH_TEMP_ACL_INVALID", "归档临时文件的描述符不完整。");
        var aces = new List<ArchiveAce>();
        foreach (var entry in raw.DiscretionaryAcl)
        {
            if (entry is not CommonAce ace)
                throw new SetupException("UI_FINISH_TEMP_ACL_INVALID", "归档临时文件包含不受支持的 ACE。");
            aces.Add(new((byte)ace.AceType, (byte)ace.AceFlags, unchecked((uint)ace.AccessMask), ace.SecurityIdentifier.Value,
                ace.IsCallback, ace.OpaqueLength));
        }
        return new(raw.Owner.Value, raw.Group.Value, (raw.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0,
            raw.DiscretionaryAcl.Revision, aces.ToArray());
    }

    private static void RequireUniquePaths(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (path != Path.GetFullPath(path) || !seen.Add(path))
                throw new SetupException("UI_FINISH_RECORD_PATH_INVALID", "关联回执路径含重复或非规范记录。");
        }
    }

    private static void VerifyInterruptedEvidenceSets(Evidence proof)
    {
        if (proof.Interrupted is null) return;
        foreach (var directory in new[] { proof.Interrupted.StageDirectory, proof.Interrupted.BackupDirectory })
        {
            var actual = Walk(directory);
            RequireFileSet(directory, actual.Files, proof.FrozenFiles.Where(f => SetupFileSecurity.Under(path: f.Path, root: directory)).Select(f => f.Path));
            RequireFileSet(directory, actual.Directories, proof.Directories.Where(d => SetupFileSecurity.Under(path: d.Path, root: directory)).Select(d => d.Path));
        }
    }
}
