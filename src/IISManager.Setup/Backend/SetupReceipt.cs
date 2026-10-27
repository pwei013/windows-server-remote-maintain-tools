using System.Security.Cryptography;
using System.Text.Json;
using IISManager.Setup.Core;

namespace IISManager.Setup.Backend;

internal sealed record TemporaryPreservationEvidence(FileEvidence Source, string Destination, FileEvidence? Archived, bool OriginalAbsent);

internal sealed class SetupReceipt : IDisposable
{
    internal string DirectoryPath { get; }
    internal string ReceiptPath => Path.Combine(DirectoryPath, "receipt.json");
    internal string StageDirectory => Path.Combine(DirectoryPath, "stage");
    internal string BackupDirectory => Path.Combine(DirectoryPath, "runtime-backup");
    internal Guid Id { get; } = Guid.NewGuid();
    private readonly SetupComponent component;
    private readonly SetupMode mode;
    private readonly string transportMode;
    private readonly string sourceAccessMode;
    private readonly string procedure;
    private readonly string operationMode;
    private readonly Guid? recoveryOf;
    private string stage = "Prepared", state = "Preparing", code = "";
    private string? failureStage;
    private string? runtimeFile, runtimeStep;
    private string? exceptionType; private int? hresult;
    private SafeExceptionDetail[] exceptionChain = [];
    private bool exceptionChainTruncated;
    private ServiceEvidence? beforeService, afterService;
    private readonly List<FileEvidence> files = [];
    private readonly List<FileEvidence> preservedFiles = [];
    private readonly List<AccessEvidence> access = [];
    private readonly List<TemporaryPreservationEvidence> temporaryPreservations = [];
    private FirewallEvidence? firewall;
    private PathLease? pin;
    internal SetupReceipt(SetupPlan plan, string procedure = "InstallOrUpdate", Guid? recoveryOf = null, string? operationMode = null)
    {
        if (operationMode is not null && (operationMode != "Uninstall" || procedure != "UninstallAgent" || plan.Component != SetupComponent.Agent))
            throw new SetupException("RECEIPT_OPERATION_INVALID", "卸载回执只能记录固定 Agent 卸载过程。");
        component = plan.Component; mode = plan.Mode; transportMode = plan.TransportMode; sourceAccessMode = plan.SourceAccessMode;
        this.operationMode = operationMode ?? mode.ToString();
        this.procedure = procedure; this.recoveryOf = recoveryOf;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IISManager.Setup", "receipts");
        SetupFileSecurity.RequireTrustedAncestors(root);
        using var rootParentPin = PathLease.Acquire(root);
        SetupFileSecurity.CreateRestrictedTree(root); SetupFileSecurity.RequireRestricted(root);
        DirectoryPath = Path.Combine(root, Id.ToString("N"));
        SetupFileSecurity.CreateDirectoryWithDescriptor(DirectoryPath, SetupFileSecurity.RestrictedSddl);
        SetupFileSecurity.RequireRestricted(DirectoryPath);
        pin = PathLease.Acquire(DirectoryPath);
        try
        {
            SetupFileSecurity.CreateDirectoryWithDescriptor(StageDirectory, SetupFileSecurity.RestrictedSddl);
            SetupFileSecurity.CreateDirectoryWithDescriptor(BackupDirectory, SetupFileSecurity.RestrictedSddl);
            Save();
        }
        catch { Dispose(); throw; }
    }
    internal void Advance(string next, string nextState = "Preparing")
    {
        stage = next; state = nextState; runtimeFile = null; runtimeStep = null; Save();
    }
    internal void RecordRuntimeStep(string relativePath, string step)
    {
        if (!PayloadArchive.IsSafeRelativePath(relativePath) || string.IsNullOrWhiteSpace(step) ||
            step.Length > 64 || step.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new SetupException("RUNTIME_STEP_INVALID", "运行文件步骤记录须使用规范相对路径及安全步骤标识。");
        runtimeFile = relativePath; runtimeStep = step; Save();
    }
    internal void RecordService(ServiceEvidence? value, bool after = false) { if (after) afterService = value; else beforeService = value; Save(); }
    internal void RecordFiles(IEnumerable<FileEvidence> value) { files.AddRange(value); Save(); }
    internal void RecordPreservedFiles(IEnumerable<FileEvidence> value) { preservedFiles.AddRange(value); Save(); }
    internal void RecordAccess(AccessEvidence value) { access.Add(value); Save(); }
    internal void RecordTemporaryPreservation(FileEvidence source, string destination, FileEvidence? archived = null, bool originalAbsent = false)
    {
        if (!Path.GetDirectoryName(destination)!.Equals(BackupDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(destination).Equals("preserved-" + Path.GetFileName(source.Path), StringComparison.Ordinal) ||
            archived is not null && !archived.Path.Equals(destination, StringComparison.OrdinalIgnoreCase) || originalAbsent && archived is null)
            throw new SetupException("UI_RECOVERY_TEMP_RECORD_INVALID", "临时产物保留记录的来源、目的及阶段不符。");
        temporaryPreservations.RemoveAll(value => value.Source.Path.Equals(source.Path, StringComparison.OrdinalIgnoreCase));
        temporaryPreservations.Add(new(source, destination, archived, originalAbsent)); Save();
    }
    internal void RecordFirewall(FirewallEvidence value) { firewall = value; Save(); }
    internal void Finish(string finalState, string finalCode, Exception? failure = null)
    {
        failureStage = failure is null ? null : stage;
        var summary = failure is null ? null : SafeFailureSummary.From(failure);
        state = finalState; code = finalCode; stage = "Final";
        exceptionType = summary?.LeafExceptionType; hresult = summary?.LeafHResult;
        exceptionChain = summary?.ExceptionChain ?? []; exceptionChainTruncated = summary?.ChainTruncated ?? false;
        Save();
    }
    private void Save()
    {
        SetupFileSecurity.RequireRestricted(DirectoryPath);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, id = Id, component = component.ToString(), mode = operationMode, transportMode, sourceAccessMode,
            procedure, recoveryOf, stage, failureStage, runtimeFile, runtimeStep, state, code, exceptionType, hresult, exceptionChain, exceptionChainTruncated, observedAt = DateTimeOffset.UtcNow, beforeService, afterService,
            runtimeBackups = files, preservedFiles, permissions = access, temporaryPreservations, firewall, automaticRollback = false, saclRecorded = false }, PayloadArchive.JsonOptions);
        DurableJson(ReceiptPath, bytes);
    }
    internal static void DurableJson(string target, byte[] bytes)
    {
        SetupFileSecurity.RejectReparseChain(target, true);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, "." + Guid.NewGuid().ToString("N") + ".setup-tmp");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
        { stream.Write(bytes); stream.Flush(flushToDisk: true); }
        SetupFileSecurity.SetNewProgramFileOwner(temporary);
        // Both files belong to the newly created restricted installer directory. Existing configuration is never passed here.
        File.Move(temporary, target, overwrite: true);
        SetupFileSecurity.RequireTrustedProgramFile(target);
        using var read = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (read.Length != bytes.Length || !SHA256.HashData(read).SequenceEqual(SHA256.HashData(bytes)))
            throw new SetupException("RECEIPT_READBACK_MISMATCH", "受限回执或归属记录未能持久化核验，停止相关写入。");
    }
    public void Dispose() { pin?.Dispose(); pin = null; }
}
