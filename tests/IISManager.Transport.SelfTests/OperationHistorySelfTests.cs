using System.Text.Json;
using IISManager.Console;
using IISManager.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

internal static class OperationHistorySelfTests
{
    private const string Token = "SYNTHETIC-OPERATOR-SESSION";

    public static async Task<int> RunAsync()
    {
        var checks = 0; var failures = 0;
        async Task Check(string name, Func<Task> assertion)
        {
            checks++;
            try { await assertion(); System.Console.WriteLine("PASS " + name); }
            catch (Exception exception) { failures++; System.Console.WriteLine("FAIL " + name + " type=" + exception.GetType().Name); }
        }

        await Check("first cloud write persists its attempt before mutation and requires independent readback", async () =>
        {
            using var fixture = new Fixture(); var task = TaskRecord(); await fixture.Store.SaveAsync(task);
            fixture.Repository.BeforeWrite = async snapshot =>
            {
                var receipt = await fixture.Store.GetAsync(snapshot.OperationId) ?? throw new InvalidOperationException("TEST_RECEIPT_MISSING");
                Require(receipt.PersistenceAttemptHash == snapshot.Hash() && receipt.PersistenceState == "unknown");
                Require(receipt.OperatorUserId == task.OperatorUserId && receipt.OperatorName == task.OperatorName);
            };
            var saved = await fixture.SyncAsync(task);
            Require(fixture.Repository.Events.SequenceEqual(["find", "create", "find"]));
            Require(saved.PersistenceState == "synced" && saved.PersistenceAttemptHash is null && saved.PersistenceErrorCode is null);
            Require(saved.PersistencePrimaryId == fixture.Repository.Row!.PrimaryId);
            var durable = await fixture.Store.GetAsync(task.Request.OperationId) ?? throw new InvalidOperationException("TEST_RECORD_MISSING");
            Require(durable.PersistenceSyncedHash == OperationHistorySnapshot.FromTask(task).Hash());
            Require(durable.OperatorUserId == task.OperatorUserId && durable.OperatorName == task.OperatorName);
        });

        await Check("an unchanged verified snapshot never repeats a cloud mutation", async () =>
        {
            using var fixture = new Fixture(); var task = await fixture.SaveAndSyncAsync(TaskRecord());
            var writes = fixture.Repository.Writes; var reads = fixture.Repository.Reads;
            for (var repeat = 0; repeat < 3; repeat++) task = await fixture.SyncAsync(task);
            Require(task.PersistenceState == "synced" && fixture.Repository.Writes == writes && fixture.Repository.Reads == reads);
        });

        await Check("status updates use the original cloud row and retain the submitting operator", async () =>
        {
            using var fixture = new Fixture(); var first = await fixture.SaveAndSyncAsync(TaskRecord());
            var latest = Later(first); await fixture.Store.SaveAsync(latest);
            var saved = await fixture.SyncAsync(latest); var row = fixture.Repository.Row!;
            Require(fixture.Repository.Creates == 1 && fixture.Repository.Updates == 1 && saved.PersistenceState == "synced");
            Require(row.PrimaryId == first.PersistencePrimaryId && row.Snapshot.State == OperationStates.Succeeded);
            Require(row.Snapshot.OperatorUserId == first.OperatorUserId && row.Snapshot.OperatorName == first.OperatorName && row.Snapshot.AcceptedAt == first.AcceptedAt);
            Require((await fixture.Store.GetAsync(first.Request.OperationId))?.PersistenceSyncedHash == row.Snapshot.Hash());
        });

        await Check("a lost cloud write reply succeeds only after matching independent readback", async () =>
        {
            using var fixture = new Fixture(); fixture.Repository.LoseWriteReply = true;
            var task = await fixture.SaveAndSyncAsync(TaskRecord());
            Require(task.PersistenceState == "synced" && task.PersistenceAttemptHash is null && fixture.Repository.Reads == 2);
            task = await fixture.SyncAsync(task);
            Require(fixture.Repository.Creates == 1 && fixture.Repository.Updates == 0 && task.PersistenceState == "synced");
        });

        await Check("an uncertain create with an empty readback is never replayed, even for a newer status", async () =>
        {
            using var fixture = new Fixture(); fixture.Repository.CommitWrites = false; fixture.Repository.LoseWriteReply = true;
            var task = await fixture.SaveAndSyncAsync(TaskRecord()); var attempted = task.PersistenceAttemptHash;
            Require(task.PersistenceState == "unknown" && attempted is not null && fixture.Repository.Row is null);
            fixture.Repository.CommitWrites = true; fixture.Repository.LoseWriteReply = false;
            task = await fixture.SyncAsync(task); task = Later(task); await fixture.Store.SaveAsync(task); task = await fixture.SyncAsync(task);
            Require(task.PersistenceState == "unknown" && task.PersistenceAttemptHash == attempted && task.PersistenceErrorCode == "OPERATION_HISTORY_WRITE_UNKNOWN");
            Require(fixture.Repository.Creates == 1 && fixture.Repository.Updates == 0 && fixture.Repository.Row is null);
        });

        await Check("a successful write reply with different readback stays unknown and cannot overwrite it", async () =>
        {
            using var fixture = new Fixture(); fixture.Repository.WriteTransform = snapshot => snapshot with { State = OperationStates.Failed };
            var task = await fixture.SaveAndSyncAsync(TaskRecord()); var remote = fixture.Repository.Row!;
            Require(task.PersistenceState == "unknown" && task.PersistenceAttemptHash is not null);
            fixture.Repository.WriteTransform = null; task = await fixture.SyncAsync(task);
            Require(task.PersistenceState == "unknown" && fixture.Repository.Writes == 1 && fixture.Repository.Row == remote);
        });

        await Check("an uncertain update cannot be replayed over its last verified remote snapshot", async () =>
        {
            using var fixture = new Fixture(); var task = await fixture.SaveAndSyncAsync(TaskRecord());
            var verified = fixture.Repository.Row; var syncedHash = task.PersistenceSyncedHash;
            fixture.Repository.CommitWrites = false; fixture.Repository.LoseWriteReply = true;
            task = Later(task); await fixture.Store.SaveAsync(task); task = await fixture.SyncAsync(task);
            var attempted = task.PersistenceAttemptHash;
            Require(task.PersistenceState == "unknown" && attempted is not null && attempted != syncedHash);
            fixture.Repository.CommitWrites = true; fixture.Repository.LoseWriteReply = false;
            for (var repeat = 0; repeat < 2; repeat++) task = await fixture.SyncAsync(task);
            Require(fixture.Repository.Creates == 1 && fixture.Repository.Updates == 1 && fixture.Repository.Row == verified);
            Require(task.PersistenceState == "unknown" && task.PersistenceAttemptHash == attempted && task.PersistenceSyncedHash == syncedHash);
        });

        await Check("restart preserves an uncertain attempt and does not create a missing cloud record again", async () =>
        {
            using var fixture = new Fixture(); fixture.Repository.CommitWrites = false; fixture.Repository.LoseWriteReply = true;
            var task = await fixture.SaveAndSyncAsync(TaskRecord()); var hash = task.PersistenceAttemptHash;
            fixture.Restart(); var restored = await fixture.Store.GetAsync(task.Request.OperationId) ?? throw new InvalidOperationException("TEST_RECORD_MISSING");
            Require(fixture.Store.Healthy && restored.PersistenceAttemptHash == hash && restored.PersistenceState == "unknown");
            fixture.Repository.CommitWrites = true; fixture.Repository.LoseWriteReply = false;
            restored = await fixture.SyncAsync(restored);
            Require(restored.PersistenceState == "unknown" && restored.PersistenceAttemptHash == hash && fixture.Repository.Writes == 1);
        });

        await Check("restart reconciles a committed uncertain snapshot before applying a newer status once", async () =>
        {
            using var fixture = new Fixture(); fixture.Repository.FailReads.Add(2);
            var task = await fixture.SaveAndSyncAsync(TaskRecord()); var attempted = task.PersistenceAttemptHash;
            Require(task.PersistenceState == "unknown" && attempted == fixture.Repository.Row!.Snapshot.Hash());
            fixture.Restart(); task = Later((await fixture.Store.GetAsync(task.Request.OperationId))!); await fixture.Store.SaveAsync(task);
            var saved = await fixture.SyncAsync(task);
            Require(saved.PersistenceState == "synced" && saved.PersistenceAttemptHash is null && fixture.Repository.Creates == 1 && fixture.Repository.Updates == 1);
            var remote = fixture.Repository.Row!;
            Require(remote.Snapshot.State == OperationStates.Succeeded && remote.Snapshot.OperatorUserId == task.OperatorUserId);
        });

        await Check("remote operation identity drift blocks updates without changing local ownership", async () =>
        {
            foreach (var change in new Func<OperationHistorySnapshot, OperationHistorySnapshot>[]
            {
                snapshot => snapshot with { OperatorUserId = "synthetic-other-user" },
                snapshot => snapshot with { OperatorName = "另一名操作人" },
                snapshot => snapshot with { ServerId = "other-server" },
                snapshot => snapshot with { RequestHash = new string('B', 64) },
                snapshot => snapshot with { AcceptedAt = snapshot.AcceptedAt.AddSeconds(1) }
            })
            {
                using var fixture = new Fixture(); var original = await fixture.SaveAndSyncAsync(TaskRecord());
                var originalRow = fixture.Repository.Row!;
                fixture.Repository.Row = originalRow with { Snapshot = change(originalRow.Snapshot) };
                var remote = fixture.Repository.Row; var latest = Later(original); await fixture.Store.SaveAsync(latest);
                var blocked = await fixture.SyncAsync(latest);
                Require(blocked.PersistenceState == "unknown" && blocked.PersistenceErrorCode == "OPERATION_HISTORY_CONFLICT");
                Require(fixture.Repository.Writes == 1 && fixture.Repository.Row == remote);
                Require(blocked.OperatorUserId == original.OperatorUserId && blocked.OperatorName == original.OperatorName);
            }
        });

        await Check("remote primary key replacement blocks updating a reconstructed row", async () =>
        {
            using var fixture = new Fixture(); var task = await fixture.SaveAndSyncAsync(TaskRecord());
            fixture.Repository.Row = fixture.Repository.Row! with { PrimaryId = "replacement" };
            task = Later(task); await fixture.Store.SaveAsync(task); task = await fixture.SyncAsync(task);
            Require(task.PersistenceState == "unknown" && task.PersistenceErrorCode == "OPERATION_HISTORY_CONFLICT" &&
                task.PersistencePrimaryId != "replacement" && fixture.Repository.Writes == 1);
        });
        await Check("a prior verified hash cannot clear a pending or unknown remote conflict without reading", async () =>
        {
            foreach (var state in new[] { "pending", "unknown" })
            {
                using var fixture = new Fixture(); var original = await fixture.SaveAndSyncAsync(TaskRecord());
                fixture.Repository.Row = fixture.Repository.Row! with
                { Snapshot = fixture.Repository.Row!.Snapshot with { State = OperationStates.Failed, ErrorCode = "REMOTE_CHANGE" } };
                var task = original with { PersistenceState = state, PersistenceErrorCode = "OPERATION_HISTORY_CONFLICT" };
                await fixture.Store.SaveAsync(task); var reads = fixture.Repository.Reads;
                task = await fixture.SyncAsync(task);
                Require(fixture.Repository.Reads == reads + 1 && fixture.Repository.Writes == 1 &&
                    task.PersistenceState == "unknown" && task.PersistenceErrorCode == "OPERATION_HISTORY_CONFLICT");
            }
        });
        await Check("primary key replacement during update stays unknown despite equal readback values", async () =>
        {
            using var fixture = new Fixture(); var task = await fixture.SaveAndSyncAsync(TaskRecord());
            var primary = task.PersistencePrimaryId;
            fixture.Repository.AfterWrite = () => fixture.Repository.Row = fixture.Repository.Row! with { PrimaryId = "replacement" };
            task = Later(task); await fixture.Store.SaveAsync(task); task = await fixture.SyncAsync(task);
            Require(task.PersistenceState == "unknown" && task.PersistenceErrorCode == "OPERATION_HISTORY_CONFLICT" &&
                task.PersistenceAttemptHash is not null && task.PersistencePrimaryId == primary && fixture.Repository.Writes == 2);
            task = await fixture.SyncAsync(task); Require(task.PersistenceState == "unknown" && fixture.Repository.Writes == 2);
        });
        await Check("drift in previously synced mutable fields is a conflict rather than an overwrite", async () =>
        {
            using var fixture = new Fixture(); var task = await fixture.SaveAndSyncAsync(TaskRecord());
            var originalRow = fixture.Repository.Row!;
            fixture.Repository.Row = originalRow with { Snapshot = originalRow.Snapshot with { State = OperationStates.Failed, ErrorCode = "REMOTE_CHANGE" } };
            var remote = fixture.Repository.Row; task = Later(task); await fixture.Store.SaveAsync(task);
            task = await fixture.SyncAsync(task);
            Require(task.PersistenceState == "unknown" && task.PersistenceErrorCode == "OPERATION_HISTORY_CONFLICT");
            Require(fixture.Repository.Writes == 1 && fixture.Repository.Row == remote);
        });

        await Check("a previously synced cloud row disappearing is not automatically recreated", async () =>
        {
            using var fixture = new Fixture(); var task = await fixture.SaveAndSyncAsync(TaskRecord());
            fixture.Repository.Row = null; task = Later(task); await fixture.Store.SaveAsync(task); task = await fixture.SyncAsync(task);
            Require(task.PersistenceState == "unknown" && task.PersistenceErrorCode == "OPERATION_HISTORY_RECORD_MISSING");
            Require(fixture.Repository.Creates == 1 && fixture.Repository.Updates == 0);
        });

        await Check("a query failure before mutation remains pending and can retry only the read safely", async () =>
        {
            using var fixture = new Fixture(); fixture.Repository.FailReads.Add(1);
            var task = await fixture.SaveAndSyncAsync(TaskRecord());
            Require(task.PersistenceState == "pending" && task.PersistenceAttemptHash is null && fixture.Repository.Writes == 0);
            Require((await fixture.Store.GetAsync(task.Request.OperationId))?.PersistenceState == "pending");
            task = await fixture.SyncAsync(task);
            Require(task.PersistenceState == "synced" && fixture.Repository.Creates == 1 && fixture.Repository.Reads == 3);
        });

        await Check("failed readback retains an uncertain receipt until a read proves the committed result", async () =>
        {
            using var fixture = new Fixture(); fixture.Repository.FailReads.Add(2);
            var task = await fixture.SaveAndSyncAsync(TaskRecord());
            Require(task.PersistenceState == "unknown" && task.PersistenceAttemptHash is not null && fixture.Repository.Writes == 1);
            task = await fixture.SyncAsync(task);
            Require(task.PersistenceState == "synced" && task.PersistenceAttemptHash is null && fixture.Repository.Writes == 1 && fixture.Repository.Reads == 3);
        });

        await Check("a matching existing cloud snapshot is adopted without creating or updating", async () =>
        {
            using var fixture = new Fixture(); var task = TaskRecord();
            fixture.Repository.Row = new StoredOperationHistory("existing-row", OperationHistorySnapshot.FromTask(task));
            var adopted = await fixture.SaveAndSyncAsync(task);
            Require(adopted.PersistenceState == "synced" && adopted.PersistencePrimaryId == "existing-row" && fixture.Repository.Writes == 0 && fixture.Repository.Reads == 1);
        });

        await Check("legacy ledger entries remain readable and are never assigned a synthetic operator", async () =>
        {
            using var fixture = new Fixture(); var task = TaskRecord();
            var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(task, Protocol.Json))!;
            foreach (var key in fields.Keys.Where(key => key.StartsWith("operator", StringComparison.Ordinal) || key.StartsWith("persistence", StringComparison.Ordinal)).ToArray()) fields.Remove(key);
            await File.WriteAllTextAsync(Path.Combine(fixture.DirectoryPath, task.Request.OperationId + ".json"), JsonSerializer.Serialize(fields, Protocol.Json));
            fixture.Restart(); var legacy = await fixture.Store.GetAsync(task.Request.OperationId) ?? throw new InvalidOperationException("TEST_LEGACY_MISSING");
            Require(fixture.Store.Healthy && legacy.OperatorUserId is null && legacy.OperatorName is null && legacy.PersistenceState is null);
            legacy = await fixture.SyncAsync(legacy);
            Require(legacy.OperatorName is null && legacy.PersistenceState is null && fixture.Repository.Events.Count == 0);
            Require((await fixture.Store.PendingHistoryIdsAsync(default)).Length == 0);
            var page = await fixture.Store.QueryAsync(Query());
            Require(page.Total == 1 && page.Items.Single().OperatorName is null && page.Items.Single().PersistenceState is null);
        });

        await Check("history searches operator names and exposes saved status without session or request secrets", async () =>
        {
            using var fixture = new Fixture(); var named = await fixture.SaveAndSyncAsync(TaskRecord());
            var other = TaskRecord() with { OperatorUserId = "synthetic-other", OperatorName = "陈工" }; await fixture.Store.SaveAsync(other);
            var page = await fixture.Store.QueryAsync(Query(("search", "王工")));
            Require(page.Total == 1 && page.Items.Single().OperationId == named.Request.OperationId && page.Items.Single().OperatorName == "王工");
            Require(page.Items.Single().PersistenceState == "synced");
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(page.Items.Single(), Protocol.Json));
            Require(!json.RootElement.TryGetProperty("operatorUserId", out _) && !json.RootElement.TryGetProperty("request", out _) && !json.RootElement.TryGetProperty("sourceIp", out _));
            Require(!json.RootElement.ToString().Contains(Token, StringComparison.Ordinal));
            Require((await fixture.Store.QueryAsync(Query(("search", "nobody")))).Total == 0);
        });

        await Check("disabled persistence and explicitly unconfigured records perform no cloud calls", async () =>
        {
            using var fixture = new Fixture(); fixture.Repository.Enabled = false;
            var task = TaskRecord(); await fixture.Store.SaveAsync(task); var untouched = await fixture.SyncAsync(task);
            Require(untouched == task && fixture.Repository.Events.Count == 0);
            fixture.Repository.Enabled = true; task = task with { PersistenceState = "notConfigured" }; await fixture.Store.SaveAsync(task);
            untouched = await fixture.SyncAsync(task);
            Require(untouched == task && fixture.Repository.Events.Count == 0);
        });

        System.Console.WriteLine($"Operation history self-tests: {checks} checks, {failures} failures.");
        return failures == 0 ? 0 : 1;
    }

    private static ConsoleTaskRecord TaskRecord()
    {
        var accepted = new DateTimeOffset(2026, 10, 13, 1, 0, 0, TimeSpan.Zero);
        var request = new OperationRequest
        {
            OperationId = Guid.NewGuid(), Type = OperationTypes.Start,
            Target = new OperationTarget { ApplicationPoolName = "SyntheticPool" }, Parameters = JsonSerializer.SerializeToElement(new { }),
            ExpectedFingerprint = new string('A', 64), ExpiresAt = accepted.AddMinutes(5)
        };
        return new ConsoleTaskRecord
        {
            ServerId = "synthetic-server", Request = request, RequestHash = ConsoleTaskStore.RequestHash("synthetic-server", request),
            AcceptedAt = accepted, UpdatedAt = accepted, State = OperationStates.Running, SourceIp = "127.0.0.1",
            OperatorUserId = "synthetic-user-a", OperatorName = "王工", PersistenceState = "pending"
        };
    }

    private static ConsoleTaskRecord Later(ConsoleTaskRecord task) => task with
    { State = OperationStates.Succeeded, UpdatedAt = task.UpdatedAt.AddSeconds(1), PersistenceState = "pending" };

    private static ConsoleOperationQuery Query(params (string Key, string Value)[] pairs)
    {
        Require(ConsoleOperationQuery.TryParse(new QueryCollection(pairs.ToDictionary(pair => pair.Key, pair => new StringValues(pair.Value))), out var query));
        return query!;
    }

    private static void Require(bool result) { if (!result) throw new InvalidOperationException("TEST_ASSERTION_FAILED"); }

    private sealed class Fixture : IDisposable
    {
        public readonly string DirectoryPath = Path.Combine(Directory.GetCurrentDirectory(), "work", "operation-history-tests-" + Guid.NewGuid().ToString("N"));
        public readonly MemoryRepository Repository = new();
        public ConsoleTaskStore Store { get; private set; }
        public Fixture() { Store = new ConsoleTaskStore(DirectoryPath); }
        public void Restart() { Store.Dispose(); Store = new ConsoleTaskStore(DirectoryPath); }
        public async Task<ConsoleTaskRecord> SaveAndSyncAsync(ConsoleTaskRecord task)
        { await Store.SaveAsync(task); return await SyncAsync(task); }
        public async Task<ConsoleTaskRecord> SyncAsync(ConsoleTaskRecord task)
        {
            var gate = Store.Lock(task.Request.OperationId); await gate.WaitAsync();
            try { return await new OperationHistorySynchronizer(Repository, Store).SyncAsync(task, Token, default); }
            finally { gate.Release(); }
        }
        public void Dispose() { Store.Dispose(); Directory.Delete(DirectoryPath, recursive: true); }
    }

    private sealed class MemoryRepository : IOperationHistoryRepository
    {
        public bool Enabled { get; set; } = true;
        public StoredOperationHistory? Row;
        public readonly List<string> Events = [];
        public readonly HashSet<int> FailReads = [];
        public int Reads, Creates, Updates;
        public int Writes => Creates + Updates;
        public bool CommitWrites = true, LoseWriteReply;
        public Func<OperationHistorySnapshot, Task>? BeforeWrite;
        public Func<OperationHistorySnapshot, OperationHistorySnapshot>? WriteTransform;
        public Action? AfterWrite;
        public Task<StoredOperationHistory?> FindAsync(Guid operationId, string token, CancellationToken cancellation)
        {
            Require(token == Token); cancellation.ThrowIfCancellationRequested(); Events.Add("find"); Reads++;
            if (FailReads.Contains(Reads)) throw new OperationHistoryException("OPERATION_HISTORY_UNAVAILABLE");
            return Task.FromResult(Row?.Snapshot.OperationId == operationId ? Row : null);
        }
        public async Task CreateAsync(OperationHistorySnapshot snapshot, string token, CancellationToken cancellation)
        {
            Require(token == Token); cancellation.ThrowIfCancellationRequested(); Events.Add("create"); Creates++;
            if (BeforeWrite is not null) await BeforeWrite(snapshot);
            if (CommitWrites) Row = new StoredOperationHistory("synthetic-row-1", WriteTransform?.Invoke(snapshot) ?? snapshot);
            AfterWrite?.Invoke();
            if (LoseWriteReply) throw new OperationHistoryException("OPERATION_HISTORY_WRITE_UNKNOWN");
        }
        public async Task UpdateAsync(string primaryId, OperationHistorySnapshot snapshot, string token, CancellationToken cancellation)
        {
            Require(token == Token && Row?.PrimaryId == primaryId); cancellation.ThrowIfCancellationRequested(); Events.Add("update"); Updates++;
            if (BeforeWrite is not null) await BeforeWrite(snapshot);
            if (CommitWrites) Row = new StoredOperationHistory(primaryId, WriteTransform?.Invoke(snapshot) ?? snapshot);
            AfterWrite?.Invoke();
            if (LoseWriteReply) throw new OperationHistoryException("OPERATION_HISTORY_WRITE_UNKNOWN");
        }
    }
}
