using System.Text.Json;
using IISManager.Agent;
using IISManager.Contracts;

internal static class DiagnosticsSelfTests
{
    public static async Task<int> RunAsync()
    {
        var checks = 0; var failures = 0;
        async Task Check(string name, Func<Task> assertion)
        {
            checks++;
            try { await assertion(); System.Console.WriteLine("PASS " + name); }
            catch (Exception exception) { failures++; System.Console.WriteLine("FAIL " + name + " type=" + exception.GetType().Name); }
        }
        await Check("diagnostics first CPU sample is unknown rather than zero", async () =>
        {
            var collector = new Collector(); var time = new Clock();
            var service = new DiagnosticsReadService(new AgentOptions(), collector, time, 2);
            var first = await service.GetAsync();
            Require(first.Resources!.CpuPercent is null && first.Processes![0].CpuPercent is null);
            Require(first.Resources!.TotalMemoryBytes == 1000 && first.Processes![0].WorkingSetBytes == 100);
        });
        await Check("diagnostics CPU deltas normalize process usage across logical processors", async () =>
        {
            var collector = new Collector(); var time = new Clock();
            var service = new DiagnosticsReadService(new AgentOptions(), collector, time, 2);
            await service.GetAsync(); time.Advance(20);
            collector.Cpu = new(160, 180, 160); collector.ProcessCpu = TimeSpan.FromSeconds(20).Ticks;
            var next = await service.GetAsync();
            Require(next.Resources!.CpuPercent == 50 && next.Processes![0].CpuPercent == 50);
        });
        await Check("diagnostics reused PID cannot reuse the previous CPU baseline", async () =>
        {
            var collector = new Collector(); var time = new Clock();
            var service = new DiagnosticsReadService(new AgentOptions(), collector, time, 2);
            await service.GetAsync(); time.Advance(20); collector.Start = collector.Start.AddSeconds(1);
            collector.ProcessCpu = 999;
            Require((await service.GetAsync()).Processes![0].CpuPercent is null);
        });
        await Check("diagnostics unsupported processor groups keep CPU unknown while memory remains available", async () =>
        {
            var collector = new Collector { Cpu = new(100, 120, 100, false) };
            var result = await new DiagnosticsReadService(new AgentOptions(), collector).GetAsync();
            Require(result.Resources is { CpuPercent: null, TotalMemoryBytes: 1000 } &&
                result.Modules.Any(module => module.ErrorCode == "DIAGNOSTICS_CPU_GROUP_UNSUPPORTED"));
        });
        await Check("diagnostics cache avoids repeated native reads", async () =>
        {
            var collector = new Collector(); var service = new DiagnosticsReadService(new AgentOptions(), collector);
            var first = await service.GetAsync(); var second = await service.GetAsync();
            Require(ReferenceEquals(first, second) && collector.ResourceReads == 1);
        });
        await Check("diagnostics module failures retain other results and never return exception messages", async () =>
        {
            var collector = new Collector { ResourcesDenied = true };
            var result = await new DiagnosticsReadService(new AgentOptions(), collector).GetAsync();
            Require(result.Resources is null && result.Processes is not null && result.Events is not null);
            Require(result.Modules.Any(module => module.Module == "resources" && module.State == "unavailable" && module.ErrorCode == "DIAGNOSTICS_ACCESS_DENIED"));
            Require(!JsonSerializer.Serialize(result, Protocol.Json).Contains("SYNTHETIC_SECRET", StringComparison.Ordinal));
        });
        await Check("diagnostics simulation never invokes real collectors", async () =>
        {
            var collector = new Collector { ResourcesDenied = true };
            var result = await new DiagnosticsReadService(new AgentOptions { Mode = "Simulation" }, collector).GetAsync();
            Require(result.Mode == "Simulation" && result.Resources is null && result.Modules.All(module => module.State == "simulation") && collector.ResourceReads == 0);
        });
        await Check("diagnostics caps process rows and reports truncation", async () =>
        {
            var collector = new Collector { ProcessCount = 250 };
            var result = await new DiagnosticsReadService(new AgentOptions(), collector).GetAsync();
            Require(result.Processes!.Length == 100 && result.Modules.Any(module => module.Truncated));
        });
        await Check("diagnostics reserves bounded rows for both pools and managed services", async () =>
        {
            var collector = new Collector { ProcessCount = 150, ServiceCount = 150 };
            var result = await new DiagnosticsReadService(new AgentOptions(), collector).GetAsync();
            Require(result.Processes!.Count(row => row.Kind == "applicationPool") == 100 &&
                result.Processes!.Count(row => row.Kind == "windowsService") == 100);
        });
        await Check("diagnostics timeout is bounded and does not start duplicate collections", async () =>
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var collector = new Collector { ResourceBlock = release.Task };
            var service = new DiagnosticsReadService(new AgentOptions(), collector, deadline: TimeSpan.FromMilliseconds(30));
            try
            {
                var first = await service.GetAsync(); var second = await service.GetAsync();
                Require(first.Resources is null && second.Resources is null && collector.ResourceReads == 1);
            }
            finally { release.TrySetResult(); }
        });
        await Check("diagnostics propagates caller cancellation", async () =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await new DiagnosticsReadService(new AgentOptions(), new Collector()).GetAsync(cancellation.Token); }
            catch (OperationCanceledException) { return; }
            throw new InvalidOperationException("EXPECTED_CANCELLATION");
        });

        await Check("diagnostic events contain only whitelisted fields and fixed summaries", () =>
        {
            var safe = DiagnosticEventSanitizer.FromXml("Application", EventXml(".NET Runtime", 1026, 2));
            Require(safe is not null && safe.EventId == 1026 && safe.RecordId == 42);
            var json = JsonSerializer.Serialize(safe, Protocol.Json);
            Require(!json.Contains("SYNTHETIC_SECRET", StringComparison.Ordinal) && !json.Contains("username", StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        });
        await Check("diagnostic events reject unknown provider level channel and XML entities", () =>
        {
            Require(DiagnosticEventSanitizer.FromXml("Application", EventXml("Untrusted", 1026, 2)) is null);
            Require(DiagnosticEventSanitizer.FromXml("Application", EventXml(".NET Runtime", 1026, 4)) is null);
            Require(DiagnosticEventSanitizer.FromXml("Security", EventXml(".NET Runtime", 1026, 2)) is null);
            Require(DiagnosticEventSanitizer.FromXml("Application", "<!DOCTYPE Event [<!ENTITY leak SYSTEM 'file:///SYNTHETIC_SECRET'>]><Event>&leak;</Event>") is null);
            return Task.CompletedTask;
        });
        await Check("diagnostic service-stop informational event is explicitly supported", () =>
        {
            Require(DiagnosticEventSanitizer.FromXml("System", EventXml("Service Control Manager", 7036, 4)) is not null);
            return Task.CompletedTask;
        });
        await Check("IIS summaries aggregate codes and timing without request or credential fields", () =>
        {
            var result = DiagnosticsIisLogReader.ParseW3c("Demo", [Log()], Now);
            Require(result.State == "available" && result.Errors.Length == 2);
            var error = result.Errors.Single(item => item.Status == 500);
            Require(error.Count == 2 && error.SubStatus == 19 && error.Win32Status == 5 && error.AverageMilliseconds == 150 && error.MaximumMilliseconds == 200);
            var json = JsonSerializer.Serialize(result, Protocol.Json);
            Require(!json.Contains("SYNTHETIC_SECRET", StringComparison.Ordinal) && !json.Contains("query", StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        });
        await Check("IIS optional numeric fields stay unknown when they were not logged", () =>
        {
            var result = DiagnosticsIisLogReader.ParseW3c("Demo", ["#Fields: date time sc-status\n2026-10-25 11:00:00 503\n"], Now);
            Require(result.Errors.Single() is { SubStatus: null, Win32Status: null, AverageMilliseconds: null, MaximumMilliseconds: null });
            return Task.CompletedTask;
        });
        await Check("IIS Win32 error codes retain unsigned 32-bit values", () =>
        {
            var result = DiagnosticsIisLogReader.ParseW3c("Demo", ["#Fields: date time sc-status sc-win32-status\n2026-10-25 11:00:00 500 2148074254\n"], Now);
            Require(result.Errors.Single().Win32Status == 2148074254L);
            return Task.CompletedTask;
        });
        await Check("IIS missing fields are unavailable rather than a clean error count", () =>
        {
            var result = DiagnosticsIisLogReader.ParseW3c("Demo", ["#Fields: date time cs-uri-query\n2026-10-25 11:00:00 SYNTHETIC_SECRET\n"], Now);
            Require(result.State == "unavailable" && result.ErrorCode == "DIAGNOSTICS_IIS_FIELDS_UNAVAILABLE");
            return Task.CompletedTask;
        });
        await Check("IIS malformed rows are explicit partial results and old/future samples are excluded", () =>
        {
            var result = DiagnosticsIisLogReader.ParseW3c("Demo", [Log() + "2026-10-20 11:00:00 /x synthetic 500 0 0 100\n2026-10-26 11:00:00 /x synthetic 500 0 0 100\nbad record\n"], Now);
            Require(result.State == "partial" && result.Errors.Single(item => item.Status == 500).Count == 2);
            return Task.CompletedTask;
        });
        await Check("IIS reader reads only configured site's bounded W3C tail", () =>
        {
            using var fixture = new LogFixture();
            File.WriteAllText(Path.Combine(fixture.Site, "u_ex261025.log"), Log());
            File.WriteAllText(Path.Combine(fixture.Site, "other.log"), "SYNTHETIC_SECRET");
            var budget = 1024 * 1024;
            var result = DiagnosticsIisLogReader.Read("Demo", fixture.Root, 1, Now, ref budget);
            Require(result.State == "available" && result.SampledFiles == 1 && result.Errors.Length == 2 && budget < 1024 * 1024);
            return Task.CompletedTask;
        });
        await Check("IIS reader rejects UNC relative paths and absent configured directories", () =>
        {
            foreach (var root in new[] { "\\\\server\\share", "//server/share", "relative/path" })
            {
                var budget = 1024;
                var result = DiagnosticsIisLogReader.Read("Demo", root, 1, Now, ref budget);
                Require(result.State == "unavailable" && result.ErrorCode == "DIAGNOSTICS_IIS_LOG_PATH_REJECTED");
            }
            return Task.CompletedTask;
        });
        await Check("IIS reader rejects a symlink directory instead of following logs outside its boundary", () =>
        {
            if (OperatingSystem.IsWindows()) return Task.CompletedTask;
            using var fixture = new LogFixture(); Directory.Delete(fixture.Site);
            var outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "u_ex261025.log"), Log()); Directory.CreateSymbolicLink(fixture.Site, outside);
            var budget = 1024 * 1024;
            Require(DiagnosticsIisLogReader.Read("Demo", fixture.Root, 1, Now, ref budget).State == "unavailable");
            return Task.CompletedTask;
        });
        await Check("IIS reader signals the shared byte budget instead of silently returning no errors", () =>
        {
            using var fixture = new LogFixture(); File.WriteAllText(Path.Combine(fixture.Site, "u_ex261025.log"), Log());
            var budget = 0; var result = DiagnosticsIisLogReader.Read("Demo", fixture.Root, 1, Now, ref budget);
            Require(result.State == "unavailable" && result.ErrorCode == "DIAGNOSTICS_IIS_LOG_BUDGET_REACHED");
            return Task.CompletedTask;
        });
        await Check("IIS reader bounds large files to a tail sample and signals truncation", () =>
        {
            using var fixture = new LogFixture();
            var header = "#Fields: date time sc-status\n";
            File.WriteAllText(Path.Combine(fixture.Site, "u_ex261025.log"), header +
                string.Concat(Enumerable.Repeat("2026-10-25 11:00:00 200\n", 40000)) + "2026-10-25 11:00:01 503\n");
            var budget = 1024 * 1024; var before = budget;
            var result = DiagnosticsIisLogReader.Read("Demo", fixture.Root, 1, Now, ref budget);
            Require(result.Truncated && before - budget <= 512 * 1024 + 32 * 1024 && result.SampledLines <= 20000 &&
                result.Errors.Any(error => error.Status == 503));
            return Task.CompletedTask;
        });
        await Check("IIS reader refuses a linked log file", () =>
        {
            if (OperatingSystem.IsWindows()) return Task.CompletedTask;
            using var fixture = new LogFixture(); var target = Path.Combine(fixture.Root, "outside.log"); File.WriteAllText(target, Log());
            File.CreateSymbolicLink(Path.Combine(fixture.Site, "u_ex261025.log"), target);
            var budget = 1024 * 1024;
            Require(DiagnosticsIisLogReader.Read("Demo", fixture.Root, 1, Now, ref budget).State == "unavailable");
            return Task.CompletedTask;
        });
        System.Console.WriteLine($"Diagnostics checks: {checks}, failures: {failures}");
        return failures;
    }
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
    private static string Log() => "#Fields: date time cs-uri-stem cs-uri-query sc-status sc-substatus sc-win32-status time-taken\n" +
        "2026-10-25 11:00:00 /SYNTHETIC_SECRET token=SYNTHETIC_SECRET 500 19 5 100\n" +
        "2026-10-25 11:00:01 /SYNTHETIC_SECRET token=SYNTHETIC_SECRET 500 19 5 200\n" +
        "2026-10-25 11:00:02 /SYNTHETIC_SECRET token=SYNTHETIC_SECRET 503 0 0 300\n" +
        "2026-10-25 11:00:03 /SYNTHETIC_SECRET token=SYNTHETIC_SECRET 200 0 0 10\n";
    private static string EventXml(string provider, int id, int level) =>
        $"<Event><System><Provider Name='{provider}'/><EventID>{id}</EventID><Level>{level}</Level><TimeCreated SystemTime='2026-10-25T11:00:00Z'/><EventRecordID>42</EventRecordID><Security UserID='SYNTHETIC_SECRET'/></System><EventData><Data Name='username'>SYNTHETIC_SECRET</Data></EventData></Event>";
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("TEST_ASSERTION_FAILED"); }
    private sealed class LogFixture : IDisposable
    {
        public string Root = Path.Combine(Directory.GetCurrentDirectory(), "work", "diagnostics-tests-" + Guid.NewGuid().ToString("N"));
        public string Site => Path.Combine(Root, "W3SVC1");
        public LogFixture() => Directory.CreateDirectory(Site);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => Now.AddTicks(_ticks);
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(int seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    private sealed class Collector : IDiagnosticsCollector
    {
        public int ResourceReads; public bool ResourcesDenied; public int ProcessCount = 1; public int ServiceCount;
        public DiagnosticCpuSample Cpu = new(100, 120, 100);
        public long ProcessCpu; public DateTimeOffset Start = Now.AddHours(-1);
        public Task? ResourceBlock;
        public async Task<(DiagnosticCpuSample Cpu, long TotalMemory, long AvailableMemory, DiskResourceUsage[] Disks)> ReadResourcesAsync(CancellationToken cancellation)
        {
            Interlocked.Increment(ref ResourceReads);
            if (ResourcesDenied) throw new UnauthorizedAccessException("SYNTHETIC_SECRET");
            if (ResourceBlock is not null) await ResourceBlock;
            return (Cpu, 1000, 400, [new("C:\\", 2000, 500)]);
        }
        public Task<DiagnosticProcessSample[]> ReadProcessesAsync(CancellationToken cancellation) => Task.FromResult(Enumerable.Range(0, ProcessCount)
            .Select(index => new DiagnosticProcessSample("applicationPool", "Demo", "Started", 100 + index, Start, ProcessCpu, 100, 200))
            .Concat(Enumerable.Range(0, ServiceCount).Select(index => new DiagnosticProcessSample("windowsService", "Service", "Running", 10000 + index, Start, ProcessCpu, 100, 200))).ToArray());
        public Task<DiagnosticEvent[]> ReadEventsAsync(CancellationToken cancellation) => Task.FromResult<DiagnosticEvent[]>([]);
        public Task<IisLogDiagnostics[]> ReadIisLogsAsync(CancellationToken cancellation) => Task.FromResult<IisLogDiagnostics[]>([]);
    }
}
