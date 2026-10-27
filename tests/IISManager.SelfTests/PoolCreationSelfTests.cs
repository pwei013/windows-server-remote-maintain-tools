using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using IISManager.Agent.Backends;
using IISManager.Contracts;
using IISManager.Core;

internal static class PoolCreationSelfTests
{
    internal static readonly (string Name, Func<Task> Test)[] Cases =
    [
        ("pool creation precheck follows the automatic start selection", CreationPrecheck),
        ("created pool waits for the selected state through runtime registration delays", RuntimeRegistrationDelay),
        ("created pool read deadline preserves the native failure", RegistrationTimeout),
        ("persistent runtime registration failure stops retrying at the deadline", PersistentRegistrationFailure),
        ("created pool permission and unexpected read failures are not retried", PermanentReadFailures),
        ("IIS polling only retries reads when explicitly allowed", PollingScope),
        ("IIS native error exposes HRESULT and stage without exception text", SafeNativeError),
        ("created pool readback requires selected state and matching configuration", CreationReadback),
        ("pool creation persists auto-start and its corresponding runtime state", CreationContract),
        ("updating auto-start preserves the current runtime state", AutoStartUpdate)
    ];

    private static readonly Type NativeBackend = typeof(SimulationIisBackend).Assembly
        .GetType("IISManager.Agent.Backends.WindowsIisBackend", throwOnError: true)!;
    private static bool RetryRead(Exception exception) => (bool)NativeBackend
        .GetMethod("PoolRuntimeNotReady", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [exception])!;
    private static Task<string?> ReadState(Func<string?> read, string expectedState, TimeSpan? timeout = null, TimeSpan? pollInterval = null) =>
        IisStatePolling.ReadUntilAsync(read, state => state == expectedState,
            timeout ?? TimeSpan.FromSeconds(1), pollInterval ?? TimeSpan.Zero, RetryRead);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new TestFailure(message);
    }
    private static OperationRequest Request(bool? autoStart) => new()
    {
        OperationId = Guid.NewGuid(), Type = OperationTypes.CreatePool,
        Target = new() { ApplicationPoolName = "CreationPool" },
        Parameters = autoStart is null ? JsonSerializer.SerializeToElement(new { }, Protocol.Json)
            : JsonSerializer.SerializeToElement(new { autoStart = autoStart.Value }, Protocol.Json),
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2)
    };

    private static async Task CreationPrecheck()
    {
        foreach (var autoStart in new bool?[] { false, true, null })
        {
            using var fixture = new SimulationFixture();
            var before = await fixture.Backend.GetSnapshotAsync();
            var validation = await fixture.Backend.ValidateAsync(Request(autoStart));
            Require(validation.Valid, "Creation precheck must validate.");
            var expected = validation.Proposed!.Value.Deserialize<PoolConfig>(Protocol.Json)!;
            var state = autoStart == true ? "Started" : "Stopped";
            Require(expected.State == state && expected.AutoStart == (autoStart ?? false), "Precheck must predict the selected AutoStart state, with manual startup when omitted.");
            Require(validation.Changes.Any(change => change.Field == "state" && change.After == state), "The expected runtime state must appear in the reviewed differences.");
            Require(validation.Warnings.Any(warning => warning.Contains(autoStart == true ? "AutoStart=true" : "AutoStart=false")), "Warnings must describe the selected startup behavior.");
            Require(CanonicalJson.Hash(before.ApplicationPools) == CanonicalJson.Hash((await fixture.Backend.GetSnapshotAsync()).ApplicationPools), "Prechecks cannot start, stop or create pools.");
        }
    }

    private static async Task RuntimeRegistrationDelay()
    {
        foreach (var hresult in new[] { unchecked((int)0x800710D8), unchecked((int)0x80070490) })
        foreach (var expected in new[] { "Started", "Stopped" })
        {
            var reads = 0;
            var observed = await ReadState(() => ++reads switch
            {
                1 or 2 => throw new COMException("private native detail", hresult),
                3 => expected == "Started" ? "Stopped" : "Started",
                4 => expected == "Started" ? "Starting" : "Stopping",
                _ => expected
            }, expected);
            Require(reads == 5 && observed == expected, "Only actual selected-state evidence may complete readback; no lifecycle write is needed.");
        }
    }

    private static async Task RegistrationTimeout()
    {
        var original = new COMException("private native detail", unchecked((int)0x800710D8));
        var reads = 0;
        try
        {
            await ReadState(() => { reads++; throw original; }, "Started", TimeSpan.Zero);
            throw new TestFailure("A runtime registration timeout must fail verification.");
        }
        catch (COMException exception) { Require(ReferenceEquals(exception, original), "The original HRESULT must survive the deadline."); }
        Require(reads == 1, "A zero deadline cannot retry.");
        foreach (var state in new string?[] { null, "Unknown", "Starting", "Stopping", "Stopped" })
            Require(await ReadState(() => state, "Started", TimeSpan.Zero) != "Started", "A deadline must not invent the requested runtime state.");
    }

    private static async Task PermanentReadFailures()
    {
        foreach (var original in new Exception[]
        {
            new COMException("private native detail", unchecked((int)0x80070005)),
            new COMException("private native detail", unchecked((int)0x80070057)),
            new UnauthorizedAccessException("private native detail"),
            new InvalidOperationException("private native detail")
        })
        {
            var reads = 0;
            try
            {
                await ReadState(() => { reads++; throw original; }, "Started");
                throw new TestFailure("Permanent read errors must escape immediately.");
            }
            catch (Exception exception) when (ReferenceEquals(exception, original)) { }
            Require(reads == 1, "Only the two registration HRESULTs may trigger read retries.");
        }
    }

    private static async Task PersistentRegistrationFailure()
    {
        var original = new COMException("private native detail", unchecked((int)0x80070490));
        var reads = 0;
        try
        {
            await ReadState(() => { reads++; throw original; }, "Started", TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(1));
            throw new TestFailure("A persistent registration failure cannot be declared successful.");
        }
        catch (COMException exception) { Require(ReferenceEquals(exception, original), "The deadline must preserve the last read error."); }
        Require(reads > 1, "The bounded wait must retry transient reads.");
    }

    private static async Task PollingScope()
    {
        var reads = 0;
        try
        {
            await IisStatePolling.ReadUntilAsync<string>(() => { reads++; throw new IOException(); }, _ => true);
            throw new TestFailure("Existing polling callers must not retry failures by default.");
        }
        catch (IOException) { Require(reads == 1, "Default read error behavior must remain unchanged."); }
        reads = 0;
        try
        {
            await IisStatePolling.ReadUntilAsync(() => { reads++; return "Stopped"; },
                _ => throw new IOException(), TimeSpan.FromSeconds(1), TimeSpan.Zero, exception => exception is IOException);
            throw new TestFailure("A completion callback error must not be treated as a transient read.");
        }
        catch (IOException) { Require(reads == 1, "Only the read callback may be retried."); }
    }

    private static Task SafeNativeError()
    {
        const string privateText = "credential-value-must-stay-private";
        var method = NativeBackend.GetMethod("SafeError", BindingFlags.NonPublic | BindingFlags.Static)!;
        var error = (ApiError)method.Invoke(null, [new COMException(privateText, unchecked((int)0x800710D8)), "独立读回 IIS 结果"])!;
        Require(error.Code == "iis.nativeFailure" && error.Message.Contains("0x800710D8") && error.Message.Contains("独立读回 IIS 结果"),
            "Safe diagnostics must identify the native code and execution stage.");
        Require(!error.Message.Contains(privateText), "Native exception text must stay out of public results.");
        return Task.CompletedTask;
    }

    private static Task CreationReadback()
    {
        var method = NativeBackend.GetMethod("ReadbackError", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var autoStart in new[] { false, true })
        {
            var expected = new PoolConfig { Name = "CreationPool", State = autoStart ? "Started" : "Stopped", AutoStart = autoStart };
            var proposed = JsonSerializer.SerializeToElement(expected, Protocol.Json);
            ApiError? Compare(PoolConfig actual, JsonElement? plan) => (ApiError?)method.Invoke(null, [Request(autoStart), actual, plan, true]);
            Require(Compare(expected, proposed) is null, "Both automatic and manual startup must accept matching independent evidence.");
            foreach (var actual in new[] { expected with { State = autoStart ? "Stopped" : "Started" }, expected with { State = "Unknown" },
                expected with { State = "Starting" }, expected with { AutoStart = !autoStart }, expected with { IdentityType = "LocalSystem" },
                expected with { IdleTimeoutAction = "Suspend" } })
                Require(Compare(actual, proposed)?.Code == "iis.readbackMismatch", "State and configuration drift must remain unverified.");
            Require(Compare(expected, null)?.Code == "iis.readbackMismatch", "A missing plan must not pass readback.");
            var inconsistent = expected with { State = autoStart ? "Stopped" : "Started" };
            Require(Compare(inconsistent, JsonSerializer.SerializeToElement(inconsistent, Protocol.Json))?.Code == "iis.readbackMismatch",
                "An old or inconsistent plan cannot require a state contrary to AutoStart.");
        }
        return Task.CompletedTask;
    }

    private static async Task CreationContract()
    {
        foreach (var autoStart in new bool?[] { false, true, null })
        {
            using var fixture = new SimulationFixture();
            var request = Request(autoStart);
            var validation = await fixture.Backend.ValidateAsync(request);
            Require(validation.Valid, "Creation contract must validate.");
            var result = await fixture.Backend.ExecuteAsync(request with { ExpectedFingerprint = validation.Fingerprint });
            var actual = await fixture.Backend.GetPoolAsync("CreationPool");
            Require(result.State == OperationStates.Succeeded && actual?.State == (autoStart == true ? "Started" : "Stopped") &&
                actual.IdentityType == "ApplicationPoolIdentity" && actual.AutoStart == (autoStart ?? false),
                "Creation must persist the selected configuration and state without forcing Stop.");
        }
    }

    private static async Task AutoStartUpdate()
    {
        using var fixture = new SimulationFixture();
        foreach (var state in new[] { "Stopped", "Started" })
        {
            if (state == "Started") await Execute(OperationTypes.Start, new { });
            foreach (var autoStart in new[] { true, false })
            {
                await Execute(OperationTypes.UpdatePool, new { autoStart });
                var actual = await fixture.Backend.GetPoolAsync("SIMULATION-DefaultPool");
                Require(actual?.State == state && actual.AutoStart == autoStart, "Editing the startup configuration must preserve the current state.");
            }
        }
        async Task Execute(string type, object parameters)
        {
            var request = Request(null) with { Type = type, Target = new() { ApplicationPoolName = "SIMULATION-DefaultPool" },
                Parameters = JsonSerializer.SerializeToElement(parameters, Protocol.Json) };
            var validation = await fixture.Backend.ValidateAsync(request);
            Require(validation.Valid, "Update or explicit start must validate.");
            Require((await fixture.Backend.ExecuteAsync(request with { ExpectedFingerprint = validation.Fingerprint })).State == OperationStates.Succeeded, "Update or explicit start must succeed.");
        }
    }
}
