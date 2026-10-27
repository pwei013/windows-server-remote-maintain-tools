using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using IISManager.Agent;
using IISManager.Contracts;

internal static class FrebDiagnosticsSelfTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private const string Id = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string Secret = "SYNTHETIC_PRIVATE_REQUEST_MARKER";
    private const string EventNs = "http://schemas.microsoft.com/win/2004/08/events/event";
    private const string FrebNs = "http://schemas.microsoft.com/win/2006/06/iis/freb";

    public static async Task<int> RunAsync()
    {
        var checks = 0; var failures = 0; var skipped = 0;
        async Task Check(string name, Func<Task> assertion, bool skip = false)
        {
            checks++;
            if (skip) { skipped++; System.Console.WriteLine("SKIP freb " + name + " platform=" + (OperatingSystem.IsWindows() ? "Windows" : "non-Windows")); return; }
            try { await assertion(); System.Console.WriteLine("PASS freb " + name); }
            catch (Exception exception) { failures++; System.Console.WriteLine("FAIL freb " + name + " type=" + exception.GetType().Name); }
        }
        Task Sync(Action action) { action(); return Task.CompletedTask; }

        await Check("Microsoft event schema projects status and failing module", () => Sync(() =>
        {
            var trace = Parse(Document()).Trace!;
            Require(trace is not null && trace.Status == 500 && trace.SubStatus == 19 && trace.Win32Status == 2147942487L);
            Require(trace!.TimeTakenMilliseconds == 123.5 && trace.Method == "GET" && trace.StartedAt == Now.AddSeconds(-2));
            Require(trace.FailureModule == "AspNetCoreModuleV2" && trace.FailureNotification == "EXECUTE_REQUEST_HANDLER");
            Require(trace.Events.Length == 3 && trace.Events[1].OffsetMilliseconds == 1000 && trace.Events[1].Sequence == 2);
        }));
        await Check("official https event namespace is also accepted", () => Sync(() =>
            Require(Parse(Document().Replace(EventNs, EventNs.Replace("http:", "https:"), StringComparison.Ordinal)).Trace?.Status == 500)));
        await Check("sensitive request attributes and data never leave the parser", () => Sync(() =>
        {
            var source = Document().Replace("<failedRequest ", "<failedRequest url='https://" + Secret + "/?token=" + Secret + "' userName='" + Secret + "' ", StringComparison.Ordinal)
                .Replace("</failedRequest>", Event("GENERAL_REQUEST_HEADERS", 0, "<Data Name='Headers'>Authorization: " + Secret + "</Data><Data Name='Body'>" + Secret + "</Data>") + "</failedRequest>", StringComparison.Ordinal);
            var result = Parse(source); var json = JsonSerializer.Serialize(result);
            Require(result.Trace is not null && !json.Contains(Secret, StringComparison.Ordinal) && !json.Contains("Authorization", StringComparison.Ordinal));
            Require(result.Trace!.Events.All(value => value.EventName != "GENERAL_REQUEST_HEADERS"));
        }));
        await Check("unknown text maps to fixed constants and cannot impersonate known modules", () => Sync(() =>
        {
            var result = Parse(Document().Replace("AspNetCoreModuleV2", Secret).Replace("EXECUTE_REQUEST_HANDLER", Secret).Replace("GENERAL_REQUEST_END", Secret));
            Require(result.Trace?.FailureModule == "OTHER" && result.Trace.FailureNotification == "OTHER");
            Require(result.Trace!.Events[^1].EventName == "OTHER" && !JsonSerializer.Serialize(result).Contains(Secret, StringComparison.Ordinal));
        }));
        await Check("header-only events are omitted and unknown data fields ignored", () => Sync(() =>
        {
            var xml = Document().Replace("</failedRequest>", Event("GENERAL_RESPONSE_ENTITY_BUFFER", 0, "<Data Name='ModuleName'>" + Secret + "</Data>") + "</failedRequest>", StringComparison.Ordinal);
            Require(Parse(xml).Trace?.Events.Length == 3);
        }));
        await Check("DTD and external entities are rejected without resolution", () => Sync(() =>
        {
            foreach (var dtd in new[] { "<!DOCTYPE failedRequest [<!ENTITY private SYSTEM 'file:///NONEXISTENT/" + Secret + "'>]>", "<!DOCTYPE failedRequest [<!ENTITY private '" + Secret + "'>]>" })
                Require(Parse(dtd + Document()).ErrorCode == "FREB_XML_INVALID");
        }));
        await Check("malformed and duplicate recognized event fields are rejected", () => Sync(() =>
        {
            Require(Parse("<failedRequest").Trace is null);
            Require(Parse(Document().Replace("<Data Name='HttpStatus'>500</Data>", "<Data Name='HttpStatus'>500</Data><Data Name='HttpStatus'>200</Data>", StringComparison.Ordinal)).ErrorCode == "FREB_XML_INVALID");
        }));
        await Check("root and event site ids enforce request scope", () => Sync(() =>
        {
            Require(Parse(Document(site: 2)).ErrorCode == "FREB_SCOPE_MISMATCH");
            Require(Parse(Document().Replace("<Data Name='SiteId'>1</Data>", "<Data Name='SiteId'>2</Data>", StringComparison.Ordinal)).ErrorCode == "FREB_SCOPE_MISMATCH");
            Require(FrebTraceReader.ParseXml(Bytes(Document()), 1, "not-opaque", Now).ErrorCode == "FREB_SCOPE_MISMATCH");
        }));
        await Check("invalid numbers cannot become misleading valid telemetry", () => Sync(() =>
        {
            var trace = Parse(Document().Replace("statusCode='500.19'", "statusCode='999.99999'", StringComparison.Ordinal)
                .Replace("timeTaken='123.5'", "timeTaken='NaN'", StringComparison.Ordinal).Replace(">500</Data>", ">-1</Data>", StringComparison.Ordinal)
                .Replace(">19</Data>", ">99999</Data>", StringComparison.Ordinal).Replace("0x80070057", "18446744073709551615")).Trace!;
            Require(trace.Status is null && trace.SubStatus is null && trace.Win32Status is null && trace.TimeTakenMilliseconds is null);
        }));
        await Check("24 hour window is inclusive and future traces excluded", () => Sync(() =>
        {
            Require(Parse(Document(start: Now.AddHours(-24))).Trace is not null);
            Require(Parse(Document(start: Now.AddHours(-24).AddTicks(-1))).Trace is null);
            Require(Parse(Document(start: Now.AddTicks(1))).Trace is null);
        }));
        await Check("slow successful requests remain valid FREB traces", () => Sync(() =>
        {
            var trace = Parse(Document().Replace("500.19", "200").Replace(">500</Data>", ">200</Data>", StringComparison.Ordinal).Replace("0x80070057", "0")).Trace!;
            Require(trace.Status == 200 && trace.FailureModule == "NONE" && trace.FailureNotification == "NONE");
        }));
        await Check("depth node and file bounds reject oversized XML", () => Sync(() =>
        {
            Require(Parse("<failedRequest siteId='1'>" + string.Concat(Enumerable.Repeat("<a>", 34)) + string.Concat(Enumerable.Repeat("</a>", 34)) + "</failedRequest>").ErrorCode == "FREB_XML_LIMIT_REACHED");
            Require(Parse("<failedRequest siteId='1'>" + string.Concat(Enumerable.Repeat("<a/>", 50001)) + "</failedRequest>").ErrorCode == "FREB_XML_LIMIT_REACHED");
            Require(FrebTraceReader.ParseXml(new byte[FrebTraceReader.MaximumFileBytes + 1], 1, Id, Now).ErrorCode == "FREB_FILE_TOO_LARGE");
        }));
        await Check("bounded timeline retains failure amid a long pipeline", () => Sync(() =>
        {
            var events = new List<string> { Event("GENERAL_REQUEST_START", -2) };
            events.Add(Event("MODULE_SET_RESPONSE_ERROR_STATUS", -1, FailureData()));
            events.AddRange(Enumerable.Range(0, 150).Select(_ => Event("NOTIFY_MODULE_END", 0)));
            var trace = Parse(Wrap(string.Concat(events))).Trace!;
            Require(trace.Truncated && trace.Events.Length == 96 && trace.Events.Any(value => value.Sequence == 2 && value.EventName == "MODULE_SET_RESPONSE_ERROR_STATUS"));
            Require(trace.Events[^1].Sequence == 152 && trace.FailureModule == "AspNetCoreModuleV2");
        }));
        await Check("zero event budget still exposes sanitized failure summary", () => Sync(() =>
        {
            var result = FrebTraceReader.ParseXml(Bytes(Document()), 1, Id, Now, eventLimit: 0);
            Require(result.Trace is { Events.Length: 0, Truncated: true, FailureModule: "AspNetCoreModuleV2" });
        }));
        await Check("parser honors cancellation", () => Sync(() =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Throws<OperationCanceledException>(() => FrebTraceReader.ParseXml(Bytes(Document()), 1, Id, Now, cancellation.Token));
        }));
        await Check("only configured top-level fr XML files are read and trace ids remain stable", () => Sync(() =>
        {
            using var fixture = new Fixture(); fixture.Write("fr000001.xml", Document()); fixture.Write("secret.xml", "private");
            Directory.CreateDirectory(Path.Combine(fixture.SiteDirectory, "nested"));
            File.WriteAllText(Path.Combine(fixture.SiteDirectory, "nested", "fr000002.xml"), Document());
            var reader = new FrebTraceReader(); var first = reader.ReadSite(fixture.Site, Now, new()); var second = reader.ReadSite(fixture.Site, Now, new());
            Require(first.State == "available" && first.Traces.Length == 1 && first.Traces[0].TraceId == second.Traces[0].TraceId);
            Require(first.Traces[0].TraceId.Length == 64 && first.Traces[0].TraceId.All(Uri.IsHexDigit));
            Require(!JsonSerializer.Serialize(first).Contains(fixture.Root, StringComparison.Ordinal));
        }));
        await Check("invalid file leaves valid traces available as partial", () => Sync(() =>
        {
            using var fixture = new Fixture(); fixture.Write("fr000001.xml", Document()); fixture.Write("fr000002.xml", "<failedRequest");
            var result = new FrebTraceReader().ReadSite(fixture.Site, Now, new());
            Require(result.State == "partial" && result.Traces.Length == 1 && result.ErrorCode == "FREB_XML_INVALID" && result.Truncated);
        }));
        await Check("old or missing event times do not manufacture recent traces", () => Sync(() =>
        {
            using var fixture = new Fixture(); fixture.Write("fr000001.xml", Document(start: Now.AddHours(-25)));
            var result = new FrebTraceReader().ReadSite(fixture.Site, Now, new());
            Require(result.State == "available" && result.ErrorCode == "FREB_NO_RECENT_TRACES" && result.Traces.Length == 0);
            Require(Parse(Document().Replace("SystemTime=", "UnrecognizedTime=", StringComparison.Ordinal)).ErrorCode == "FREB_XML_INVALID");
        }));
        await Check("byte and file budgets stop reads while oversized files are isolated", () => Sync(() =>
        {
            using var fixture = new Fixture(); fixture.Write("fr000001.xml", Document());
            var reader = new FrebTraceReader(); var zero = reader.ReadSite(fixture.Site, Now, new() { RemainingFiles = 0 });
            Require(zero.ErrorCode == "FREB_TRACE_LIMIT" && zero.Traces.Length == 0);
            var bytes = reader.ReadSite(fixture.Site, Now, new() { RemainingBytes = 1 });
            Require(bytes.ErrorCode == "FREB_BYTE_BUDGET_REACHED" && bytes.Truncated && bytes.Traces.Length == 0);
            fixture.Write("fr000002.xml", new string('x', FrebTraceReader.MaximumFileBytes + 1));
            var large = reader.ReadSite(fixture.Site, Now, new());
            Require(large.State == "partial" && large.Traces.Length == 1 && large.ErrorCode == "FREB_FILE_TOO_LARGE");
            var budget = new FrebReadBudget(); reader.ReadSite(fixture.Site, Now, budget);
            Require(budget.RemainingBytes == 8 * 1024 * 1024 - Bytes(Document()).Length && budget.RemainingFiles == 62);
        }));
        await Check("trace and directory scan counts are bounded", () => Sync(() =>
        {
            using var fixture = new Fixture();
            for (var index = 1; index <= 14; index++) fixture.Write("fr" + index.ToString("D6", CultureInfo.InvariantCulture) + ".xml", Document());
            var result = new FrebTraceReader().ReadSite(fixture.Site, Now, new());
            Require(result.Traces.Length == 12 && result.Truncated && result.ErrorCode == "FREB_TRACE_LIMIT");
            for (var index = 1; index <= 130; index++) fixture.Write("ignore" + index.ToString(CultureInfo.InvariantCulture), "x");
            Require(new FrebTraceReader().ReadSite(fixture.Site, Now, new()).Truncated);
        }));
        await Check("disabled tracing avoids filesystem access and paths are rejected safely", () => Sync(() =>
        {
            var reader = new FrebTraceReader();
            var disabled = reader.ReadSite(new(1, "Synthetic", false, "\\\\UNC\\private"), Now, new());
            Require(disabled.State == "disabled" && disabled.ErrorCode == "FREB_DISABLED");
            foreach (var path in new[] { "relative/private", "\\\\UNC\\private", "//UNC/private" })
                Require(reader.ReadSite(new(1, "Synthetic", true, path), Now, new()).ErrorCode == "FREB_PATH_REJECTED");
            if (OperatingSystem.IsWindows()) Require(reader.ReadSite(new(1, "Synthetic", true, "C:\\private:stream"), Now, new()).ErrorCode == "FREB_PATH_REJECTED");
        }));
        await Check("file symlinks directory links and hardlinks cannot expose outside traces", () => Sync(() =>
        {
            using var fixture = new Fixture(); var outside = Path.Combine(fixture.Root, "outside.xml"); File.WriteAllText(outside, Document()); File.SetLastWriteTimeUtc(outside, Now.UtcDateTime);
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(Path.Combine(fixture.SiteDirectory, "fr000001.xml"), outside);
                var symlink = new FrebTraceReader().ReadSite(fixture.Site, Now, new()); Require(symlink.Traces.Length == 0 && symlink.State == "unavailable");
                File.Delete(Path.Combine(fixture.SiteDirectory, "fr000001.xml"));
                Require(UnixLink(outside, Path.Combine(fixture.SiteDirectory, "fr000002.xml")) == 0);
            }
            else Require(CreateHardLink(Path.Combine(fixture.SiteDirectory, "fr000002.xml"), outside, IntPtr.Zero));
            var linked = new FrebTraceReader().ReadSite(fixture.Site, Now, new()); Require(linked.Traces.Length == 0 && linked.ErrorCode == "FREB_FILE_REJECTED");
            if (!OperatingSystem.IsWindows())
            {
                var alias = Path.Combine(fixture.Root, "alias"); Directory.CreateSymbolicLink(alias, fixture.LogRoot);
                var directory = new FrebTraceReader().ReadSite(fixture.Site with { Directory = alias }, Now, new());
                Require(directory.Traces.Length == 0 && directory.ErrorCode == "FREB_PATH_REJECTED");
            }
        }));
        await Check("pinned Unix directory remains scoped if its pathname is replaced", () => Sync(() =>
        {
            using var fixture = new Fixture(); fixture.Write("fr000001.xml", Document());
            var boundaryType = typeof(FrebTraceReader).Assembly.GetType("IISManager.Agent.FrebLogFileBoundary", true)!;
            using var boundary = (IDisposable)Activator.CreateInstance(boundaryType, BindingFlags.Instance | BindingFlags.NonPublic, null, [fixture.SiteDirectory], null)!;
            Directory.Move(fixture.SiteDirectory, fixture.SiteDirectory + "-pinned");
            var outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "fr000001.xml"), Document().Replace("500.19", "401"));
            Directory.CreateSymbolicLink(fixture.SiteDirectory, outside);
            using var pinned = (IDisposable)boundaryType.GetMethod("Open", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(boundary, ["fr000001.xml"])!;
            var stream = (FileStream)pinned.GetType().GetProperty("Stream", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pinned)!;
            using var text = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            Require(text.ReadToEnd().Contains("statusCode='500.19'", StringComparison.Ordinal));
            File.AppendAllText(Path.Combine(fixture.SiteDirectory + "-pinned", "fr000001.xml"), " ");
            var error = Throws<TargetInvocationException>(() => pinned.GetType().GetMethod("VerifyUnchanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pinned, null));
            Require(error.InnerException?.Message == "FREB_FILE_CHANGED");
        }), skip: OperatingSystem.IsWindows());
        await Check("Windows active writers cause safe rejection instead of a torn XML read", () => Sync(() =>
        {
            using var fixture = new Fixture(); fixture.Write("fr000001.xml", Document());
            using var writer = new FileStream(Path.Combine(fixture.SiteDirectory, "fr000001.xml"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            var result = new FrebTraceReader().ReadSite(fixture.Site, Now, new());
            Require(result.Traces.Length == 0 && result.ErrorCode == "FREB_FILE_REJECTED");
        }), skip: !OperatingSystem.IsWindows());
        await Check("simulation never calls a configuration source", async () =>
        {
            var source = new Source(_ => throw new InvalidOperationException(Secret));
            var result = await new FrebDiagnosticsReadService(new() { Mode = "Simulation" }, source).GetAsync();
            Require(result.State == "simulation" && result.ErrorCode == "FREB_SIMULATION" && result.Sites.Length == 0 && source.Reads == 0);
        });
        await Check("role absence and disabled tracing have explicit different states", async () =>
        {
            var site = new FrebSiteConfiguration(1, "Synthetic", false, "unused");
            var missing = await Service(new Source(_ => Task.FromResult(new FrebSiteConfigurationSnapshot(false, [site])))).GetAsync();
            Require(missing.State == "unavailable" && missing.ErrorCode == "FREB_ROLE_MISSING" && missing.Sites.Single().State == "unavailable");
            var disabled = await Service(new Source(_ => Task.FromResult(new FrebSiteConfigurationSnapshot(true, [site])))).GetAsync();
            Require(disabled.State == "disabled" && disabled.Sites.Single().ErrorCode == "FREB_DISABLED");
            var unsupported = await new FrebDiagnosticsReadService(new() { Mode = "Windows" }, null).GetAsync();
            Require(unsupported.ErrorCode == "FREB_WINDOWS_REQUIRED");
        });
        await Check("source errors are safe codes and never disclose exception text", async () =>
        {
            var result = await Service(new Source(_ => throw new IOException(Secret))).GetAsync();
            Require(result.ErrorCode == "FREB_CONFIGURATION_UNAVAILABLE" && !JsonSerializer.Serialize(result).Contains(Secret, StringComparison.Ordinal));
            var access = await Service(new Source(_ => throw new UnauthorizedAccessException(Secret))).GetAsync();
            Require(access.ErrorCode == "FREB_ACCESS_DENIED");
        });
        await Check("cache coalesces parallel collection and expires at ten seconds", async () =>
        {
            using var fixture = new Fixture(); fixture.Write("fr000001.xml", Document());
            var clock = new ManualTime(); var source = new Source(_ => Task.FromResult(new FrebSiteConfigurationSnapshot(true, [fixture.Site])));
            var service = Service(source, clock); var tasks = Enumerable.Range(0, 8).Select(_ => service.GetAsync()).ToArray(); await Task.WhenAll(tasks);
            Require(source.Reads == 1 && tasks.All(task => ReferenceEquals(task.Result, tasks[0].Result)));
            clock.Advance(TimeSpan.FromSeconds(9)); Require(ReferenceEquals(await service.GetAsync(), tasks[0].Result));
            clock.Advance(TimeSpan.FromSeconds(1)); var next = await service.GetAsync();
            Require(source.Reads == 2 && next.CollectedAt == Now.AddSeconds(10) && next.Sites[0].Traces[0].TraceId == tasks[0].Result.Sites[0].Traces[0].TraceId);
        });
        await Check("stalled source retains one collector after repeated timeouts", async () =>
        {
            var gate = new TaskCompletionSource<FrebSiteConfigurationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new Source(_ => { entered.TrySetResult(); return gate.Task; });
            var service = Service(source, deadline: TimeSpan.FromMilliseconds(60));
            try
            {
                var first = service.GetAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Require((await first).ErrorCode == "FREB_TIMEOUT");
                Require((await service.GetAsync()).ErrorCode == "FREB_TIMEOUT" && source.Reads == 1);
            }
            finally { gate.TrySetResult(new(true, [])); }
            await service.GetAsync(); Require(source.Reads == 1);
        });
        await Check("cancelled callers cannot start a new source read", async () =>
        {
            var source = new Source(_ => Task.FromResult(new FrebSiteConfigurationSnapshot(true, [])));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await Service(source).GetAsync(cancellation.Token); throw new InvalidOperationException(); }
            catch (OperationCanceledException) { Require(source.Reads == 0); }
        });
        await Check("site cap and per-site partial failures preserve usable traces", async () =>
        {
            using var fixture = new Fixture(); fixture.Write("fr000001.xml", Document()); fixture.Write("fr000002.xml", "invalid");
            var result = await Service(new Source(_ => Task.FromResult(new FrebSiteConfigurationSnapshot(true, [fixture.Site])))).GetAsync();
            Require(result.State == "partial" && result.Sites.Single().Traces.Length == 1);
            var rows = Enumerable.Range(1, 33).Select(index => new FrebSiteConfiguration(index, "Synthetic", false, "")).ToArray();
            var capped = await Service(new Source(_ => Task.FromResult(new FrebSiteConfigurationSnapshot(true, rows)))).GetAsync();
            Require(capped.Sites.Length == 32 && capped.State == "partial" && capped.ErrorCode == "FREB_SITE_LIMIT");
        });
        System.Console.WriteLine("FREB checks=" + checks.ToString(CultureInfo.InvariantCulture) + " passed=" + (checks - failures - skipped).ToString(CultureInfo.InvariantCulture) +
            " skipped=" + skipped.ToString(CultureInfo.InvariantCulture) + " failures=" + failures.ToString(CultureInfo.InvariantCulture));
        return failures;
    }

    private static FrebDiagnosticsReadService Service(Source source, TimeProvider? clock = null, TimeSpan? deadline = null) =>
        new(new AgentOptions { Mode = "Windows" }, source, clock ?? new ManualTime(), deadline);
    private static FrebTraceParseResult Parse(string xml) => FrebTraceReader.ParseXml(Bytes(xml), 1, Id, Now);
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static string Wrap(string events, long site = 1) => "<failedRequest siteId='" + site.ToString(CultureInfo.InvariantCulture) + "' statusCode='500.19' timeTaken='123.5' verb='GET'>" + events + "</failedRequest>";
    private static string Document(long site = 1, DateTimeOffset? start = null) => Wrap(
        Event("GENERAL_REQUEST_START", -2, "<Data Name='SiteId'>" + site.ToString(CultureInfo.InvariantCulture) + "</Data>", start) +
        Event("MODULE_SET_RESPONSE_ERROR_STATUS", -1, FailureData(), start?.AddSeconds(1)) + Event("GENERAL_REQUEST_END", 0, "", start?.AddSeconds(2)), site);
    private static string FailureData() => "<Data Name='ModuleName'>AspNetCoreModuleV2</Data><Data Name='HttpStatus'>500</Data><Data Name='HttpSubStatus'>19</Data><Data Name='ErrorCode'>0x80070057</Data>";
    private static string Event(string opcode, int seconds, string data = "", DateTimeOffset? at = null) =>
        "<Event xmlns='" + EventNs + "'><System><TimeCreated SystemTime='" + (at ?? Now.AddSeconds(seconds)).ToString("O", CultureInfo.InvariantCulture) + "'/></System><EventData>" + data +
        "</EventData><RenderingInfo><Opcode>" + opcode + "</Opcode><freb:Description xmlns:freb='" + FrebNs + "' Data='Notification'>EXECUTE_REQUEST_HANDLER</freb:Description></RenderingInfo></Event>";
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T exception) { return exception; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private sealed class Source(Func<CancellationToken, Task<FrebSiteConfigurationSnapshot>> read) : IFrebSiteConfigurationSource
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public Task<FrebSiteConfigurationSnapshot> ReadAsync(CancellationToken cancellation) { Interlocked.Increment(ref _reads); return read(cancellation); }
    }
    private sealed class ManualTime : TimeProvider
    {
        private TimeSpan _elapsed;
        public override DateTimeOffset GetUtcNow() => Now + _elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _elapsed.Ticks;
        public void Advance(TimeSpan duration) => _elapsed += duration;
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public string LogRoot => Path.Combine(Root, "logs");
        public string SiteDirectory => Path.Combine(LogRoot, "W3SVC1");
        public FrebSiteConfiguration Site => new(1, "Synthetic site", true, LogRoot);
        public Fixture()
        {
            var temp = Path.GetTempPath();
            // macOS /var is a system symlink: keep the production ancestor rejection intact.
            if (OperatingSystem.IsMacOS() && temp.StartsWith("/var/", StringComparison.Ordinal)) temp = "/private" + temp;
            if (OperatingSystem.IsMacOS() && temp == "/tmp/") temp = "/private/tmp/";
            Root = Path.Combine(temp, "iismanager-freb-synthetic-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(SiteDirectory);
        }
        public void Write(string name, string value)
        { var file = Path.Combine(SiteDirectory, name); File.WriteAllText(file, value, new UTF8Encoding(false)); File.SetLastWriteTimeUtc(file, Now.UtcDateTime); }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int UnixLink(string existing, string created);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string created, string existing, IntPtr security);
}
