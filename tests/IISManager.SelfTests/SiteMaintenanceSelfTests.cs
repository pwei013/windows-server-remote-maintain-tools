using System.Reflection;
using System.Text.Json;
using IISManager.Agent;
using IISManager.Agent.Backends;
using IISManager.Contracts;
using IISManager.Core;
using Microsoft.Extensions.Logging.Abstractions;

internal static class SiteMaintenanceSelfTests
{
    internal static readonly (string Name, Func<Task> Test)[] Cases =
    [
        ("site maintenance patches only submitted root fields", RootPatch),
        ("site pool changes only the root application and preserves child pools", RootPoolPatch),
        ("site maintenance replaces bindings without changing unrelated site data", BindingsPatch),
        ("application creation records directory ownership and pool associations", CreateApplication),
        ("application partial updates preserve virtual directories and siblings", UpdateApplication),
        ("application pool changes preserve owned directories and other applications", ApplicationPoolPatch),
        ("virtual directory paths remain scoped to their owning application", DirectoryOwnership),
        ("virtual directory updates preserve siblings and never implicitly create", UpdateDirectory),
        ("virtual directories reject an independent application pool in both validation layers", DirectoryPoolPatchRejected),
        ("directory editing combines physical path and owning pool in one reviewed change", DirectoryInlineEdit),
        ("directory owning-pool-only changes preserve every physical mapping", DirectoryOwnerPoolOnly),
        ("directory owning-pool updates retain create field state and existence guards", DirectoryOwnerPoolGuards),
        ("Windows directory edit applies only explicitly selected fields without replay", NativeDirectoryInlinePatch),
        ("maintenance rejects duplicate and missing resources", ResourceExistence),
        ("maintenance canonical application and directory paths reject ambiguity", CanonicalPaths),
        ("maintenance rejects null duplicate and unrecognized patch fields", PatchFields),
        ("maintenance requires a stopped site and all old and new pools stopped", StoppedPreconditions),
        ("maintenance impact includes shared pools across sites", SharedImpact),
        ("maintenance prechecks do not mutate IIS simulation state", ValidationIsReadOnly),
        ("maintenance rejects physical paths outside allowed existing directories", DirectoryGuards),
        ("maintenance rejects a symbolic link in the physical directory chain", ReparseGuard),
        ("maintenance target guards are exclusive and old request hashes remain stable", TargetCompatibility),
        ("maintenance detects stale topology before execution", FingerprintDrift),
        ("maintenance requires an unexpired request and a precheck fingerprint", EnvelopeGuards),
        ("maintenance readback requires matching full topology and stopped pool states", ReadbackGuards),
        ("maintenance requests are durable deduplicated operations with before and after", DurableQueue),
        ("maintenance binding patches retain port conflict and TLS certificate checks", BindingGuards),
        ("maintenance TLS mutations require exclusive matching ownership and unchanged policy", TlsOwnership),
        ("maintenance TLS readback verifies removals replacement and unrelated records", TlsReadback)
    ];

    private const string Site = "MaintenanceSite";
    private const string Pool = "SIMULATION-DefaultPool";
    private static OperationRequest Request(string type, object parameters, string? application = null, string? directory = null, string site = Site) => new()
    {
        OperationId = Guid.NewGuid(), Type = type, Target = new() { SiteName = site, ApplicationPath = application, VirtualDirectoryPath = directory },
        Parameters = JsonSerializer.SerializeToElement(parameters, Protocol.Json), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(3)
    };
    private static async Task<ExecutionResult> Execute(SimulationIisBackend backend, OperationRequest request)
    {
        var validation = await backend.ValidateAsync(request);
        Require(validation.Valid, "Synthetic maintenance fixture must be valid.");
        var result = await backend.ExecuteAsync(request with { ExpectedFingerprint = validation.Fingerprint });
        Require(result.State == OperationStates.Succeeded, "Validated simulated maintenance must succeed.");
        return result;
    }
    private static async Task Reject(Fixture fixture, OperationRequest request, string code)
    {
        var result = await fixture.Backend.ValidateAsync(request);
        Require(!result.Valid && result.Errors.Any(error => error.Code == code), "Invalid maintenance must return the expected safe code: " + code);
    }
    private static async Task<SiteTopology> Read(Fixture fixture, string site = Site) =>
        (await fixture.Backend.GetSiteTopologyAsync()).Sites.Single(value => value.Name == site);

    private static async Task RootPatch()
    {
        using var fixture = new Fixture(); await fixture.Seed();
        var before = await Read(fixture);
        var request = Request(OperationTypes.UpdateSite, new { physicalPath = fixture.Second });
        var validation = await fixture.Backend.ValidateAsync(request);
        Require(validation.Changes.Any(change => change.Field == "applications[/].physicalPath"), "The precheck must explain the root path change.");
        var after = (await Execute(fixture.Backend, request)).After!.Value.Deserialize<SiteTopology>(Protocol.Json)!;
        var root = after.Applications.Single(application => application.Path == "/");
        Require(root.PhysicalPath == fixture.Second && root.VirtualDirectories.Single(directory => directory.Path == "/").PhysicalPath == fixture.Second,
            "Both root projections must reflect the new directory.");
        Require(root.ApplicationPoolName == Pool && CanonicalJson.Hash(before.Bindings) == CanonicalJson.Hash(after.Bindings) && after.State == "Stopped",
            "Missing fields and site state must remain unchanged.");
    }
    private static async Task BindingsPatch()
    {
        using var fixture = new Fixture(); await fixture.Seed(); await fixture.Seed("OtherSite", 18912);
        var before = await Read(fixture); var other = await Read(fixture, "OtherSite");
        await Execute(fixture.Backend, Request(OperationTypes.UpdateSite, new { bindings = new[] { new BindingConfig { Port = 18913, HostName = "changed.simulation.test" } } }));
        var after = await Read(fixture);
        Require(after.Bindings.Single().Port == 18913 && CanonicalJson.Hash(after.Applications) == CanonicalJson.Hash(before.Applications), "Only submitted bindings may change.");
        Require(CanonicalJson.Hash(other) == CanonicalJson.Hash(await Read(fixture, "OtherSite")), "Other sites must remain unchanged.");
    }
    private static async Task RootPoolPatch()
    {
        using var fixture = new Fixture(); await fixture.Seed(); await fixture.Application(); await fixture.Seed("OtherSite", 18912);
        await Execute(fixture.Backend, Request(OperationTypes.CreateVirtualDirectory, new { physicalPath = fixture.Second }, "/", "/assets"));
        const string newPool = "RootReplacementPool";
        await Execute(fixture.Backend, Request(OperationTypes.CreatePool, new { autoStart = false }) with { Target = new() { ApplicationPoolName = newPool } });
        var before = await Read(fixture); var other = await Read(fixture, "OtherSite");
        var request = Request(OperationTypes.UpdateSite, new { applicationPoolName = newPool });
        Require(SiteMaintenanceRequestSafety.IsValid(request), "Sparse root pool changes must pass the transport contract.");
        var validation = await fixture.Backend.ValidateAsync(request);
        Require(validation.Valid && validation.Changes.Single().Field == "applications[/].applicationPoolName" &&
            validation.Changes.Single().Before == Pool && validation.Changes.Single().After == newPool,
            "The precheck must show only the root application's pool change.");
        var after = (await Execute(fixture.Backend, request)).After!.Value.Deserialize<SiteTopology>(Protocol.Json)!;
        var root = after.Applications.Single(application => application.Path == "/");
        Require(root.ApplicationPoolName == newPool && root.PhysicalPath == fixture.First &&
            CanonicalJson.Hash(root.VirtualDirectories) == CanonicalJson.Hash(before.Applications.Single(application => application.Path == "/").VirtualDirectories),
            "Changing the root pool must preserve its physical mappings and owned virtual directories.");
        Require(CanonicalJson.Hash(before.Applications.Single(application => application.Path == "/api")) ==
            CanonicalJson.Hash(after.Applications.Single(application => application.Path == "/api")) &&
            CanonicalJson.Hash(before.Bindings) == CanonicalJson.Hash(after.Bindings) &&
            CanonicalJson.Hash(other) == CanonicalJson.Hash(await Read(fixture, "OtherSite")),
            "A site's root pool change cannot overwrite child pools, bindings or another site.");
        var snapshot = await fixture.Backend.GetSnapshotAsync();
        Require(snapshot.Sites.Single(site => site.Name == Site).ApplicationPoolName == newPool &&
            snapshot.ApplicationPools.Single(pool => pool.Name == newPool).Applications.SequenceEqual([Site + "/"]) &&
            snapshot.ApplicationPools.Single(pool => pool.Name == Pool).Applications.Contains(Site + "/api") &&
            !snapshot.ApplicationPools.Single(pool => pool.Name == Pool).Applications.Contains(Site + "/"),
            "Independent snapshot and pool associations must reflect the root-only move.");
    }
    private static async Task CreateApplication()
    {
        using var fixture = new Fixture(); await fixture.Seed();
        var result = await Execute(fixture.Backend, Request(OperationTypes.CreateApplication, new { physicalPath = fixture.Second, applicationPoolName = Pool }, "/api"));
        var after = result.After!.Value.Deserialize<SiteTopology>(Protocol.Json)!; var application = after.Applications.Single(item => item.Path == "/api");
        Require(application.PhysicalPath == fixture.Second && application.ApplicationPoolName == Pool && application.EnabledProtocols == "http" && application.VirtualDirectories.Single().Path == "/",
            "Applications must have an independently owned root directory.");
        Require((await fixture.Backend.GetPoolAsync(Pool))!.Applications.Contains(Site + "/api"), "Pool impact must include the new application.");
    }
    private static async Task UpdateApplication()
    {
        using var fixture = new Fixture(); await fixture.Seed(); await fixture.Application();
        await Execute(fixture.Backend, Request(OperationTypes.CreateVirtualDirectory, new { physicalPath = fixture.Second }, "/api", "/assets"));
        var before = await Read(fixture);
        await Execute(fixture.Backend, Request(OperationTypes.UpdateApplication, new { physicalPath = fixture.First }, "/api"));
        var after = await Read(fixture); var app = after.Applications.Single(item => item.Path == "/api");
        Require(app.PhysicalPath == fixture.First && app.ApplicationPoolName == Pool && app.VirtualDirectories.Single(item => item.Path == "/assets").PhysicalPath == fixture.Second,
            "Patching an application root must preserve its virtual directories and pool.");
        Require(CanonicalJson.Hash(before.Applications.Single(item => item.Path == "/")) == CanonicalJson.Hash(after.Applications.Single(item => item.Path == "/")), "A child update must preserve the root application.");
    }
    private static async Task DirectoryOwnership()
    {
        using var fixture = new Fixture(); await fixture.Seed(); await fixture.Application();
        await Execute(fixture.Backend, Request(OperationTypes.CreateVirtualDirectory, new { physicalPath = fixture.First }, "/", "/assets"));
        await Execute(fixture.Backend, Request(OperationTypes.CreateVirtualDirectory, new { physicalPath = fixture.Second }, "/api", "/assets"));
        var site = await Read(fixture);
        Require(site.Applications.Single(item => item.Path == "/").VirtualDirectories.Single(item => item.Path == "/assets").PhysicalPath == fixture.First &&
            site.Applications.Single(item => item.Path == "/api").VirtualDirectories.Single(item => item.Path == "/assets").PhysicalPath == fixture.Second,
            "The same relative directory path in two apps represents distinct resources.");
        await Reject(fixture, Request(OperationTypes.CreateVirtualDirectory, new { physicalPath = fixture.First }, "/", "/api/assets"), "directory.applicationConflict");
        await Reject(fixture, Request(OperationTypes.CreateApplication, new { physicalPath = fixture.First, applicationPoolName = Pool }, "/assets"), "application.directoryConflict");
    }
    private static async Task ApplicationPoolPatch()
    {
        using var fixture = new Fixture(); await fixture.Seed(); await fixture.Application();
        await Execute(fixture.Backend, Request(OperationTypes.CreateApplication, new { physicalPath = fixture.First, applicationPoolName = Pool }, "/other"));
        await Execute(fixture.Backend, Request(OperationTypes.CreateVirtualDirectory, new { physicalPath = fixture.First }, "/api", "/assets"));
        await Execute(fixture.Backend, Request(OperationTypes.CreateVirtualDirectory, new { physicalPath = fixture.Second }, "/", "/assets"));
        const string newPool = "ApplicationReplacementPool";
        await Execute(fixture.Backend, Request(OperationTypes.CreatePool, new { autoStart = false }) with { Target = new() { ApplicationPoolName = newPool } });
        var before = await Read(fixture);
        var request = Request(OperationTypes.UpdateApplication, new { applicationPoolName = newPool }, "/API");
        Require(SiteMaintenanceRequestSafety.IsValid(request), "Sparse child application pool changes must pass the transport contract.");
        var validation = await fixture.Backend.ValidateAsync(request);
        Require(validation.Valid && validation.Changes.Single().Field == "applications[/api].applicationPoolName",
            "The precheck must identify the existing canonical application path and pool-only patch.");
        var after = (await Execute(fixture.Backend, request)).After!.Value.Deserialize<SiteTopology>(Protocol.Json)!;
        var app = after.Applications.Single(application => application.Path == "/api");
        Require(app.ApplicationPoolName == newPool && app.PhysicalPath == fixture.Second &&
            CanonicalJson.Hash(app.VirtualDirectories) == CanonicalJson.Hash(before.Applications.Single(application => application.Path == "/api").VirtualDirectories),
            "Owned virtual directories must stay attached to the application whose pool changed.");
        foreach (var path in new[] { "/", "/other" })
            Require(CanonicalJson.Hash(before.Applications.Single(application => application.Path == path)) ==
                CanonicalJson.Hash(after.Applications.Single(application => application.Path == path)),
                "A child pool change cannot change the root or sibling application.");
        Require(CanonicalJson.Hash(before.Bindings) == CanonicalJson.Hash(after.Bindings) && after.State == "Stopped" &&
            (await fixture.Backend.GetPoolAsync(newPool))!.Applications.SequenceEqual([Site + "/api"]),
            "Pool associations must update without altering site configuration or starting any resource.");
    }
    private static async Task UpdateDirectory()
    {
        using var fixture = new Fixture(); await fixture.Seed(); await fixture.Application();
        await Execute(fixture.Backend, Request(OperationTypes.CreateVirtualDirectory, new { physicalPath = fixture.First }, "/api", "/assets"));
        await Execute(fixture.Backend, Request(OperationTypes.UpdateVirtualDirectory, new { physicalPath = fixture.Second }, "/API", "/ASSETS"));
        var app = (await Read(fixture)).Applications.Single(item => item.Path == "/api");
        Require(app.PhysicalPath == fixture.Second && app.VirtualDirectories.Single(item => item.Path == "/assets").PhysicalPath == fixture.Second, "Directory update must preserve the application root and canonical existing path casing.");
        await Reject(fixture, Request(OperationTypes.UpdateVirtualDirectory, new { physicalPath = fixture.First }, "/api", "/missing"), "directory.notFound");
    }
    private static async Task ResourceExistence()
    {
        using var fixture = new Fixture(); await fixture.Seed(); await fixture.Application();
        await Reject(fixture, Request(OperationTypes.CreateApplication, new { physicalPath = fixture.Second, applicationPoolName = Pool }, "/API"), "application.exists");
        await Reject(fixture, Request(OperationTypes.UpdateApplication, new { physicalPath = fixture.First }, "/missing"), "application.notFound");
        await Reject(fixture, Request(OperationTypes.UpdateSite, new { applicationPoolName = "missing" }), "site.poolNotFound");
        await Reject(fixture, Request(OperationTypes.CreateApplication, new { physicalPath = fixture.Second }, "/new"), "application.fieldsRequired");
    }
    private static async Task DirectoryPoolPatchRejected()
    {
        using var fixture = new Fixture(); await fixture.Seed(); await fixture.Application();
        await Execute(fixture.Backend, Request(OperationTypes.CreateVirtualDirectory, new { physicalPath = fixture.First }, "/api", "/assets"));
        var before = await Read(fixture);
        foreach (var (type, directory) in new[] { (OperationTypes.CreateVirtualDirectory, "/new"), (OperationTypes.UpdateVirtualDirectory, "/assets") })
        {
            var request = Request(type, new { physicalPath = fixture.Second, applicationPoolName = Pool }, "/api", directory);
            Require(!SiteMaintenanceRequestSafety.IsValid(request), "The transport contract cannot allow virtual directories to own an independent pool.");
            await Reject(fixture, request, "parameters.unknownField");
        }
        Require(CanonicalJson.Hash(before) == CanonicalJson.Hash(await Read(fixture)), "Rejected virtual directory pool requests must remain read-only.");
    }
    private static async Task DirectoryInlineEdit()
    {
        foreach (var owner in new[] { "/", "/api" })
        {
            using var fixture = new Fixture(); await fixture.Seed(); await fixture.Application();
            foreach (var application in new[] { "/", "/api" })
                await Execute(fixture.Backend, Request(OperationTypes.CreateVirtualDirectory, new { physicalPath = fixture.First }, application, "/assets"));
            const string replacement = "DirectoryOwnerPool";
            await Execute(fixture.Backend, Request(OperationTypes.CreatePool, new { autoStart = false }) with { Target = new() { ApplicationPoolName = replacement } });
            var before = await Read(fixture);
            var request = Request(OperationTypes.UpdateVirtualDirectory, new { physicalPath = fixture.Second, ownerApplicationPoolName = replacement }, owner, "/assets");
            Require(SiteMaintenanceRequestSafety.IsValid(request), "Combined directory edits must be accepted by the transport contract.");
            var validation = await fixture.Backend.ValidateAsync(request);
            Require(validation.Valid && validation.Changes.Length == 2 && validation.Changes.Any(item => item.Field == $"applications[{owner}].applicationPoolName") &&
                validation.Warnings.Any(item => item.Contains("所属应用") && item.Contains("全部")), "One precheck must explain both changed fields and the owner's full scope.");
            var result = await fixture.Backend.ExecuteAsync(request with { ExpectedFingerprint = validation.Fingerprint });
            var after = await Read(fixture);
            var applicationAfter = after.Applications.Single(item => item.Path == owner);
            var applicationBefore = before.Applications.Single(item => item.Path == owner);
            Require(result.State == OperationStates.Succeeded && applicationAfter.ApplicationPoolName == replacement &&
                applicationAfter.VirtualDirectories.Single(item => item.Path == "/assets").PhysicalPath == fixture.Second &&
                applicationAfter.PhysicalPath == applicationBefore.PhysicalPath &&
                applicationAfter.VirtualDirectories.Single(item => item.Path == "/").PhysicalPath == applicationBefore.PhysicalPath,
                "The same operation must change the selected directory and owner pool while preserving its application root mapping.");
            Require(CanonicalJson.Hash(before.Applications.Where(item => item.Path != owner).ToArray()) ==
                CanonicalJson.Hash(after.Applications.Where(item => item.Path != owner).ToArray()), "Other applications and their directory mappings cannot change.");
        }
    }
    private static async Task DirectoryOwnerPoolOnly()
    {
        using var fixture = new Fixture(); await fixture.Seed(); await fixture.Application();
        foreach (var path in new[] { "/assets", "/images" })
            await Execute(fixture.Backend, Request(OperationTypes.CreateVirtualDirectory, new { physicalPath = fixture.First }, "/api", path));
        const string replacement = "DirectoryPoolOnly";
        await Execute(fixture.Backend, Request(OperationTypes.CreatePool, new { autoStart = false }) with { Target = new() { ApplicationPoolName = replacement } });
        var before = await Read(fixture);
        var request = Request(OperationTypes.UpdateVirtualDirectory, new { ownerApplicationPoolName = replacement }, "/api", "/assets");
        Require(SiteMaintenanceRequestSafety.IsValid(request), "The physical field may be omitted when only the owner pool is selected.");
        var validation = await fixture.Backend.ValidateAsync(request);
        Require(validation.Valid && validation.Changes.Single().Field == "applications[/api].applicationPoolName", "The precheck must not imply directory path writes.");
        await Execute(fixture.Backend, request);
        var after = await Read(fixture);
        Require(CanonicalJson.Hash(before.Applications.Single(item => item.Path == "/api").VirtualDirectories) ==
            CanonicalJson.Hash(after.Applications.Single(item => item.Path == "/api").VirtualDirectories), "Every owned physical mapping must be preserved by a pool-only update.");
        var snapshot = await fixture.Backend.GetSnapshotAsync();
        Require(snapshot.ApplicationPools.Single(item => item.Name == replacement).Applications.SequenceEqual([Site + "/api"]) &&
            snapshot.ApplicationPools.Single(item => item.Name == Pool).Applications.Contains(Site + "/"), "Pool associations must reflect exactly the owning application.");
    }
    private static async Task DirectoryOwnerPoolGuards()
    {
        using var fixture = new Fixture(); await fixture.Seed(); await fixture.Application();
        await Execute(fixture.Backend, Request(OperationTypes.CreateVirtualDirectory, new { physicalPath = fixture.First }, "/api", "/assets"));
        var before = await Read(fixture);
        var create = Request(OperationTypes.CreateVirtualDirectory, new { physicalPath = fixture.Second, ownerApplicationPoolName = Pool }, "/api", "/new");
        Require(!SiteMaintenanceRequestSafety.IsValid(create), "Creating a virtual directory must not change its owner's pool.");
        await Reject(fixture, create, "parameters.unknownField");
        foreach (var invalid in new[] { "{}", "{\"ownerApplicationPoolName\":null}", "{\"ownerApplicationPoolName\":\"\"}",
            "{\"ownerApplicationPoolName\":\"A\",\"OwnerApplicationPoolName\":\"B\"}" })
        {
            var request = Request(OperationTypes.UpdateVirtualDirectory, new { }, "/api", "/assets") with { Parameters = JsonSerializer.Deserialize<JsonElement>(invalid) };
            Require(!SiteMaintenanceRequestSafety.IsValid(request) && !(await fixture.Backend.ValidateAsync(request)).Valid, "Empty, null, invalid or duplicated owner fields must be rejected by both layers.");
        }
        await Reject(fixture, Request(OperationTypes.UpdateVirtualDirectory, new { ownerApplicationPoolName = "MissingPool" }, "/api", "/assets"), "site.poolNotFound");
        await Reject(fixture, Request(OperationTypes.UpdateVirtualDirectory, new { ownerApplicationPoolName = Pool }, "/api", "/missing"), "directory.notFound");
        await Execute(fixture.Backend, Request(OperationTypes.CreatePool, new { autoStart = true }) with { Target = new() { ApplicationPoolName = "RunningReplacement" } });
        await Reject(fixture, Request(OperationTypes.UpdateVirtualDirectory, new { ownerApplicationPoolName = "RunningReplacement" }, "/api", "/assets"), "pool.mustBeStopped");
        Require(CanonicalJson.Hash(before) == CanonicalJson.Hash(await Read(fixture)), "Rejected edits cannot partially change a directory or its owning application.");
    }
    private static Task NativeDirectoryInlinePatch()
    {
        var method = NativeBackend.GetMethod("ApplyVirtualDirectoryUpdate", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var selection in new[] { (Pool: true, Path: false), (Pool: false, Path: true), (Pool: true, Path: true) })
        {
            var poolCalls = 0; var pathCalls = 0;
            var patch = new VirtualDirectoryParameters { OwnerApplicationPoolName = selection.Pool ? "NewPool" : null, PhysicalPath = selection.Path ? "new" : null };
            Action<string> setPool = value => { Require(value == "NewPool", "Only the reviewed owner pool may be written."); poolCalls++; };
            Action<string> setPath = value => { Require(value == "new", "Only the reviewed path may be written."); pathCalls++; };
            method.Invoke(null, [patch, "NewPool", "new", setPool, setPath]);
            Require(poolCalls == (selection.Pool ? 1 : 0) && pathCalls == (selection.Path ? 1 : 0), "The native adapter must write each selected field once and preserve unchecked fields.");
        }
        return Task.CompletedTask;
    }
    private static async Task CanonicalPaths()
    {
        using var fixture = new Fixture(); await fixture.Seed();
        foreach (var invalid in new[] { "/", "api", "/api/", "/a//b", "/a/../b", "/a/%2e%2e", "/a%2fb", "/a\\b", "/a?b", "/a#b", "/a;b", "/a.", "/ a", "/a " })
            await Reject(fixture, Request(OperationTypes.CreateApplication, new { physicalPath = fixture.Second, applicationPoolName = Pool }, invalid), "target.invalid");
        await Reject(fixture, Request(OperationTypes.CreateVirtualDirectory, new { physicalPath = fixture.Second }, "/", "/"), "target.invalid");
    }
    private static async Task PatchFields()
    {
        using var fixture = new Fixture(); await fixture.Seed();
        foreach (var (json, code) in new[] { ("{}", "parameters.empty"), ("{\"physicalPath\":null}", "parameters.nullField"),
                     ("{\"serverAutoStart\":true}", "parameters.unknownField"), ("{\"applicationPoolName\":\"a\",\"ApplicationPoolName\":\"b\"}", "parameters.duplicateField") })
        {
            using var document = JsonDocument.Parse(json);
            await Reject(fixture, Request(OperationTypes.UpdateSite, new { }) with { Parameters = document.RootElement.Clone() }, code);
        }
    }
    private static async Task StoppedPreconditions()
    {
        using var fixture = new Fixture(); await fixture.Seed();
        await Execute(fixture.Backend, Request(OperationTypes.SiteStart, new { }));
        await Reject(fixture, Request(OperationTypes.UpdateSite, new { physicalPath = fixture.Second }), "site.mustBeStopped");
        await Execute(fixture.Backend, Request(OperationTypes.SiteStop, new { }));
        await Execute(fixture.Backend, Request(OperationTypes.Start, new { }) with { Target = new() { ApplicationPoolName = Pool } });
        await Reject(fixture, Request(OperationTypes.CreateApplication, new { physicalPath = fixture.Second, applicationPoolName = Pool }, "/api"), "pool.mustBeStopped");
    }
    private static async Task SharedImpact()
    {
        using var fixture = new Fixture(); await fixture.Seed(); await fixture.Seed("OtherSite", 18912);
        var result = await fixture.Backend.ValidateAsync(Request(OperationTypes.UpdateSite, new { physicalPath = fixture.Second }));
        Require(result.Valid && result.AffectedApplications.Contains(Site + "/") && result.AffectedApplications.Contains("OtherSite/"), "Shared pool associations must be disclosed before changing its target.");
    }
    private static async Task ValidationIsReadOnly()
    {
        using var fixture = new Fixture(); await fixture.Seed();
        var before = await Read(fixture);
        for (var repeat = 0; repeat < 3; repeat++) Require((await fixture.Backend.ValidateAsync(Request(OperationTypes.CreateApplication, new { physicalPath = fixture.Second, applicationPoolName = Pool }, "/api"))).Valid, "Repeated prechecks must remain valid.");
        Require(CanonicalJson.Hash(before) == CanonicalJson.Hash(await Read(fixture)), "No precheck may create IIS objects.");
    }
    private static async Task DirectoryGuards()
    {
        using var fixture = new Fixture(); await fixture.Seed();
        await Reject(fixture, Request(OperationTypes.UpdateSite, new { physicalPath = Path.GetTempPath() }), "site.pathNotAllowed");
        await Reject(fixture, Request(OperationTypes.UpdateSite, new { physicalPath = Path.Combine(fixture.Root, "absent") }), "site.pathNotFound");
        await Reject(fixture, Request(OperationTypes.UpdateSite, new { physicalPath = "relative-directory" }), "site.pathInvalid");
    }
    private static async Task ReparseGuard()
    {
        using var fixture = new Fixture(); await fixture.Seed();
        var link = Path.Combine(fixture.Root, "link"); Directory.CreateSymbolicLink(link, fixture.Second);
        await Reject(fixture, Request(OperationTypes.UpdateSite, new { physicalPath = link }), "site.pathReparsePoint");
    }
    private static async Task TargetCompatibility()
    {
        var target = new OperationTarget { SiteName = Site };
        var serialized = JsonSerializer.Serialize(target, Protocol.Json);
        Require(!serialized.Contains("applicationPath", StringComparison.Ordinal) && !serialized.Contains("virtualDirectoryPath", StringComparison.Ordinal), "Optional maintenance target fields must not change old wire requests.");
        using var fixture = new Fixture(); await fixture.Seed();
        foreach (var invalid in new[]
        {
            Request(OperationTypes.UpdateSite, new { physicalPath = fixture.Second }, "/api"),
            Request(OperationTypes.UpdateSite, new { physicalPath = fixture.Second }) with { Target = target with { ServiceName = "service" } },
            Request(OperationTypes.CreateApplication, new { physicalPath = fixture.Second, applicationPoolName = Pool }, "/api", "/assets"),
            Request(OperationTypes.SiteStop, new { }, "/api")
        }) Require(!(await fixture.Backend.ValidateAsync(invalid)).Valid, "Old and new targets must reject unrelated selectors.");
        var original = Request(OperationTypes.SiteStop, new { });
        var withoutNewFields = JsonSerializer.SerializeToElement(new { original.OperationId, original.Type,
            target = new { applicationPoolName = (string?)null, siteName = Site }, original.Parameters, original.ExpectedFingerprint, original.ExpiresAt }, Protocol.Json);
        Require(CanonicalJson.Hash(original) == CanonicalJson.Hash(withoutNewFields), "Existing canonical request hashes must stay stable.");
    }
    private static async Task FingerprintDrift()
    {
        using var fixture = new Fixture(); await fixture.Seed();
        var request = Request(OperationTypes.CreateApplication, new { physicalPath = fixture.Second, applicationPoolName = Pool }, "/api");
        var before = await fixture.Backend.ValidateAsync(request);
        await Execute(fixture.Backend, Request(OperationTypes.UpdateSite, new { physicalPath = fixture.Second }));
        var stale = await fixture.Backend.ExecuteAsync(request with { ExpectedFingerprint = before.Fingerprint });
        Require(stale.State == OperationStates.Failed && stale.Error?.Code == "configuration.conflict" && !(await Read(fixture)).Applications.Any(item => item.Path == "/api"), "Stale topology must never authorize a write.");
    }
    private static async Task EnvelopeGuards()
    {
        using var fixture = new Fixture(); await fixture.Seed();
        var request = Request(OperationTypes.UpdateSite, new { physicalPath = fixture.Second });
        Require((await fixture.Backend.ExecuteAsync(request)).State == OperationStates.Failed, "A valid fingerprint is mandatory.");
        var validation = await fixture.Backend.ValidateAsync(request);
        Require((await fixture.Backend.ExecuteAsync(request with { ExpectedFingerprint = validation.Fingerprint, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) })).State == OperationStates.Expired,
            "Expired requests must remain nonmutating.");
        Require((await Read(fixture)).Applications.Single().PhysicalPath == fixture.First, "Rejected requests must preserve configuration.");
    }
    private static async Task ReadbackGuards()
    {
        using var fixture = new Fixture(); await fixture.Seed();
        var site = await Read(fixture); var snapshot = await fixture.Backend.GetSnapshotAsync();
        var rules = typeof(SimulationIisBackend).Assembly.GetType("IISManager.Agent.Backends.BackendRules", true)!;
        var method = rules.GetMethod("MaintenanceMatches", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool Matches(SiteTopology actual, IisSnapshot states) => (bool)method.Invoke(null, [actual, site, site, states])!;
        Require(Matches(site, snapshot), "Identical independently read state must match.");
        Require(!Matches(site with { State = "Started" }, snapshot) && !Matches(site with { Bindings = [] }, snapshot) &&
            !Matches(site with { Applications = [site.Applications[0] with { PhysicalPath = fixture.Second }] }, snapshot) &&
            !Matches(site with { Applications = [site.Applications[0] with { ApplicationPoolName = "UnexpectedPool" }] }, snapshot),
            "State, bindings, physical path and pool association drift must remain unverified.");
        Require(!Matches(site, snapshot with { ApplicationPools = snapshot.ApplicationPools.Select(pool => pool with { State = "Started" }).ToArray() }), "Readback must verify pool states independently.");
    }
    private static async Task DurableQueue()
    {
        using var fixture = new Fixture(); await fixture.Seed();
        using var store = new FileOperationStore(Path.Combine(fixture.Root, "ledger"));
        var coordinator = new OperationCoordinator(fixture.Backend, store, NullLogger<OperationCoordinator>.Instance); await coordinator.InitializeAsync();
        var request = Request(OperationTypes.CreateApplication, new { physicalPath = fixture.Second, applicationPoolName = Pool }, "/api");
        var validation = await coordinator.ValidateAsync(request); request = request with { ExpectedFingerprint = validation.Fingerprint };
        var accepted = await coordinator.SubmitAsync(request); var duplicate = await coordinator.SubmitAsync(request);
        Require(duplicate.AcceptedAt == accepted.AcceptedAt, "Duplicate submissions must return the same durable operation.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var runner = coordinator.RunAsync(cancellation.Token);
        try
        {
            OperationRecord? record;
            do { await Task.Delay(10, cancellation.Token); record = await coordinator.GetAsync(request.OperationId, cancellation.Token); }
            while (record is null || !OperationStates.IsFinal(record.State));
            Require(record.State == OperationStates.Succeeded && record.Before!.Value.GetProperty("applications").GetArrayLength() == 1 &&
                record.After!.Value.GetProperty("applications").GetArrayLength() == 2, "The ledger must preserve full before and after evidence.");
            Require((await coordinator.SubmitAsync(request)).State == OperationStates.Succeeded && (await Read(fixture)).Applications.Length == 2,
                "A completed duplicate must not create a second application.");
        }
        finally { cancellation.Cancel(); try { await runner; } catch (OperationCanceledException) { } }
    }
    private static async Task BindingGuards()
    {
        using var fixture = new Fixture(); await fixture.Seed(); await fixture.Seed("OtherSite", 18912);
        await Reject(fixture, Request(OperationTypes.UpdateSite, new { bindings = new[] { new BindingConfig { Port = 7444 } } }), "binding.portInvalid");
        await Reject(fixture, Request(OperationTypes.UpdateSite, new { bindings = new[] { new BindingConfig { Port = 18912, HostName = "maintenance.simulation.test" } } }), "binding.conflict");
        await Reject(fixture, Request(OperationTypes.UpdateSite, new { bindings = new[] { new BindingConfig { Protocol = "https", Port = 18914, HostName = "unknown.simulation.test", CertificateThumbprint = new string('B', 40), UseSni = true } } }), "certificate.notFound");
        await Execute(fixture.Backend, Request(OperationTypes.UpdateSite, new { bindings = new[] { new BindingConfig { Protocol = "https", Port = 18914, HostName = "demo.simulation.test", CertificateThumbprint = new string('A', 40), UseSni = true } } }));
    }
    private static Type NativeBackend => typeof(SimulationIisBackend).Assembly.GetType("IISManager.Agent.Backends.WindowsIisBackend", true)!;
    private static Type ClaimType => typeof(SimulationIisBackend).Assembly.GetType("IISManager.Agent.Backends.HttpSysCertificateRecord", true)!;
    private static object Claim(string thumbprint, int port = 443, uint flags = 0) => Activator.CreateInstance(ClaimType,
        ["ip", "*", port, "", thumbprint, "MY", Guid.Empty, 0u, 0u, 0u, flags, null, null])!;
    private static Array Claims(params object[] values)
    {
        var result = Array.CreateInstance(ClaimType, values.Length);
        for (var index = 0; index < values.Length; index++) result.SetValue(values[index], index);
        return result;
    }
    private static SiteTopology TlsSite(string thumbprint) => new() { Name = Site, State = "Stopped", Bindings =
        [new BindingConfig { Protocol = "https", IpAddress = "*", Port = 443, HostName = "app.example.test", CertificateThumbprint = thumbprint }] };
    private static Task TlsOwnership()
    {
        var before = TlsSite(new string('A', 40)); var after = TlsSite(new string('B', 40));
        var method = NativeBackend.GetMethod("CheckMaintenanceTlsOwnership", BindingFlags.NonPublic | BindingFlags.Static)!;
        List<ApiError> Validate(SiteTopology[] sites, Array claims)
        {
            var errors = new List<ApiError>(); method.Invoke(null, [before, after, sites, claims, errors]); return errors;
        }
        Require(Validate([before], Claims(Claim(new string('A', 40)))).Count == 0, "A matching exclusive standard TLS record can be replaced.");
        Require(Validate([before, before with { Name = "OtherSite" }], Claims(Claim(new string('A', 40))))
            .Any(error => error.Code == "binding.httpSysSharedRecord"), "TLS records used by another site cannot be changed.");
        Require(Validate([before], Claims(Claim(new string('C', 40)))).Any(error => error.Code == "binding.httpSysOwnershipUnverified"), "An external or mismatched record must never be overwritten.");
        Require(Validate([before], Claims(Claim(new string('A', 40), flags: 1))).Any(error => error.Code == "binding.httpSysPolicyUnsupported"), "Custom policy must not be silently reset.");
        return Task.CompletedTask;
    }
    private static Task TlsReadback()
    {
        var oldThumb = new string('A', 40); var newThumb = new string('B', 40);
        var before = TlsSite(oldThumb); var after = TlsSite(newThumb);
        var method = NativeBackend.GetMethod("MaintenanceTlsMatches", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool Matches(SiteTopology proposed, Array prior, Array current) => (bool)method.Invoke(null, [before, proposed, prior, current])!;
        Require(Matches(after, Claims(Claim(oldThumb)), Claims(Claim(newThumb))), "An exact replacement should match.");
        Require(!Matches(after, Claims(Claim(oldThumb)), Claims(Claim(oldThumb))), "An unchanged old TLS certificate must not be reported successful.");
        Require(!Matches(after, Claims(Claim(oldThumb), Claim(oldThumb, 8443)), Claims(Claim(newThumb), Claim(newThumb, 8443))), "Unexpected changes to unrelated global TLS records require partial status.");
        Require(!Matches(before with { Bindings = [] }, Claims(Claim(oldThumb)), Claims(Claim(oldThumb))), "Removed bindings must not leave an unverified global TLS record.");
        Require(Matches(before with { Bindings = [] }, Claims(Claim(oldThumb)), Claims()), "Independent absence proves the old exclusive TLS record is removed.");
        return Task.CompletedTask;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new TestFailure(message); }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetFullPath("work"), "iis-maintenance-tests-" + Guid.NewGuid().ToString("N"));
        public string First => Path.Combine(Root, "first");
        public string Second => Path.Combine(Root, "second");
        public SimulationIisBackend Backend { get; }
        public Fixture()
        {
            Directory.CreateDirectory(First); Directory.CreateDirectory(Second);
            Backend = new(new AgentOptions { Mode = "Simulation", AllowedSiteRoots = [Root], ReservedPorts = [7443, 7444] });
        }
        public async Task Seed(string site = Site, int port = 18911) => _ = await Execute(Backend, Request(OperationTypes.CreateSite,
            new SiteParameters { PhysicalPath = First, ApplicationPoolName = Pool, Bindings = [new BindingConfig { Port = port, HostName = "maintenance.simulation.test" }] }, site: site));
        public async Task Application() => _ = await Execute(Backend, Request(OperationTypes.CreateApplication, new { physicalPath = Second, applicationPoolName = Pool }, "/api"));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
