using System.Text.Json;
using IISManager.Contracts;

namespace IISManager.Core;

// One process owns the ledger. Temp files are never treated as accepted tasks.
public sealed class FileOperationStore : IDisposable
{
    private readonly string directory;
    private readonly FileStream processLock;
    private readonly SemaphoreSlim gate = new(1, 1);

    public FileOperationStore(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
        processLock = new FileStream(Path.Combine(this.directory, ".owner.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    public async Task<OperationRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await ReadFileAsync(PathFor(id), cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task<OperationRecord[]> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var records = new List<OperationRecord>();
            foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
            {
                var record = await ReadFileAsync(file, cancellationToken)
                    ?? throw new IOException("LEDGER_RECORD_MISSING");
                if (!string.Equals(Path.GetFileName(file), record.Request.OperationId.ToString("D") + ".json", StringComparison.Ordinal))
                    throw new IOException("LEDGER_ID_MISMATCH");
                records.Add(record);
            }
            return records.ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(OperationRecord record, CancellationToken cancellationToken = default)
    {
        if (record.Request.OperationId == Guid.Empty) throw new IOException("LEDGER_ID_INVALID");
        await gate.WaitAsync(cancellationToken);
        var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(file, record, Protocol.Json, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, PathFor(record.Request.OperationId), overwrite: true);
            // Independent read-back is required before the executor may cross the write boundary.
            var saved = await ReadFileAsync(PathFor(record.Request.OperationId), cancellationToken);
            if (saved is null || CanonicalJson.Hash(saved) != CanonicalJson.Hash(record))
                throw new IOException("LEDGER_READBACK_MISMATCH");
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            gate.Release();
        }
    }

    private string PathFor(Guid id) => Path.Combine(directory, id.ToString("D") + ".json");

    private static async Task<OperationRecord?> ReadFileAsync(string file, CancellationToken cancellationToken)
    {
        if (!File.Exists(file)) return null;
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var record = await JsonSerializer.DeserializeAsync<OperationRecord>(stream, Protocol.Json, cancellationToken)
            ?? throw new IOException("LEDGER_RECORD_INVALID");
        if (record.Request is null || record.Request.Target is null ||
            record.Request.Parameters.ValueKind != JsonValueKind.Object || !OperationTypes.All.Contains(record.Request.Type) ||
            record.Request.ExpiresAt == default || record.AcceptedAt == default || record.Request.OperationId == Guid.Empty ||
            !string.Equals(Path.GetFileName(file), record.Request.OperationId.ToString("D") + ".json", StringComparison.Ordinal) ||
            record.RequestHash != CanonicalJson.Hash(record.Request) ||
            (record.State is not OperationStates.Queued and not OperationStates.Running && !OperationStates.IsFinal(record.State)))
            throw new IOException("LEDGER_RECORD_INVALID");
        return record;
    }

    public void Dispose()
    {
        processLock.Dispose();
        gate.Dispose();
    }
}
