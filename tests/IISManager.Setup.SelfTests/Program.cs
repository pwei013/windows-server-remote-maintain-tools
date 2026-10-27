using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IISManager.Setup.Core;

var tests = new (string Name, Action Run)[]
{
    ("有效 Agent / Console 安装计划", ValidPlans),
    ("Windows 目录拒绝相对、UNC、设备名与 ADS", WindowsPaths),
    ("Windows 路径按大小写及分隔符识别包含关系", OverlappingDirectories),
    ("证书、来源网络与角色输入拒绝不安全值", InvalidPlanInputs),
    ("Console 精确 Host 及 HTTPS Agent 地址", ConsoleBoundaries),
    ("非法枚举与缺失服务项安全拒绝", InvalidEnumsAndServers),
    ("更新仅校验选择和路径且禁止重建配置", UpdateContract),
    ("Agent 配置固定 Windows 模式及外部状态目录", AgentConfiguration),
    ("Console 配置固定 HTTPS 与非 Simulation", ConsoleConfiguration),
    ("HTTP 内网计划无需证书并要求独立访问令牌", HttpTokenPlans),
    ("HTTP 拒绝缺失令牌、无限网络及认证模式混用", HttpTokenBoundaries),
    ("HTTP 配置写入令牌和实际 TCP 来源范围", HttpTokenConfiguration),
    ("新安装自动允许内网且不要求管理来源", PrivateNetworkPlans),
    ("自动内网配置与旧精确来源策略分离", PrivateNetworkConfiguration),
    ("自动内网应用来源固定且不含全部网络", EffectiveSourceNetworks),
    ("防火墙排除 IPv6 回环且保留应用来源策略", FirewallSourceNetworks),
    ("启动前创建缺失的会话临时目录并清理写入探针", StartupMissingTemporaryDirectory),
    ("启动临时目录验证保留既有文件", StartupExistingTemporaryDirectory),
    ("不可用或相对临时目录安全拒绝且保留现场", StartupInvalidTemporaryDirectory),
    ("安全失败摘要保留底层 COM 错误且不泄露异常正文", SafeFailureDetails),
    ("安全失败摘要限制异常链长度并标记截断", SafeFailureDepth),
    ("安装审阅序列化及 ToString 不泄露令牌", TokenReviewRedaction),
    ("清单限定组件、目标框架、RID 和自包含模式", ManifestTargets),
    ("清单必须包括运行时、默认配置及 Console UI", RequiredFiles),
    ("清单拒绝非法哈希、种类、重复及路径", InvalidManifests),
    ("运行文件不能覆盖配置、账本、日志或私钥", ProtectedPayloadFiles),
    ("历史清单只能列运行文件", LegacyManifest),
    ("收尾真实路径契约接受组件内文件并拒绝倒置与前缀同名", RecoveryRuntimePaths),
    ("收尾暂存、备份与账本集合按子路径筛选", RecoveryEvidencePathSets),
    ("Console 界面补全仅返回网页资源及端点清单差异", ConsoleUiScope),
    ("Console 界面补全拒绝后台程序修改及运行文件移除", ConsoleUiRuntimeBoundaries),
    ("Console 界面补全拒绝路径大小写与文件目录歧义", ConsoleUiPathBoundaries),
    ("Console 界面补全拒绝配置、凭据和密钥伪资源", ConsoleUiProtectedResources),
    ("Console 界面补全复用运行清单元数据校验", ConsoleUiManifestBoundaries),
    ("Console 界面补全要求实际内容差异", ConsoleUiActualChanges),
    ("临时归档只接受原样或已核实的服务读取 ACE 迁移", TemporaryArchiveAcceptedShapes),
    ("临时归档拒绝所有者、组、保护位和 ACL 版本变化", TemporaryArchiveDescriptorBoundaries),
    ("临时归档逐项拒绝 ACE 类型、权限、标志和顺序变化", TemporaryArchiveAceBoundaries),
    ("临时归档白名单不能推广到其他原始 ACL 形状", TemporaryArchiveOriginalBoundaries),
    ("临时归档要求实际规范服务 SID 且不修改输入", TemporaryArchiveIdentityBoundaries),
    ("ZIP 拒绝 ZipSlip、ADS 与大小写重名", UnsafeZipPaths),
    ("ZIP 拒绝链接与目录伪装", ZipLinks),
    ("ZIP 精确匹配清单文件集合及大小", ArchiveFileSet),
    ("异常或未知字段清单拒绝读取", MalformedManifestJson),
    ("解包逐文件核对字节、哈希及完整集合", ValidExtraction),
    ("哈希不符保留失败阶段且不写出目标目录", HashMismatch),
    ("非空阶段目录拒绝覆盖既有文件", NonEmptyExtraction),
    ("重解析目录拒绝解包", LinkedExtraction)
}.Concat(AgentUninstallTests.All).ToArray();

var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception exception)
    {
        failures++;
        Console.WriteLine("FAIL " + test.Name + " · " + exception.GetType().Name + " · " + exception.Message);
    }
}
Console.WriteLine($"Setup self-tests: {tests.Length - failures}/{tests.Length} passed.");
if (failures != 0) return 1;
try
{
    var options = ParseVerificationArguments(args);
    if (options.PayloadDirectory is not null) ValidateActualPayloads(options.PayloadDirectory);
    if (options.InstallerAssembly is not null) ValidateEmbeddedPayloads(options.InstallerAssembly, options.PayloadDirectory!);
    return 0;
}
catch (Exception exception)
{
    var detail = exception switch
    {
        SetupException setup => setup.Code,
        ArgumentException argument => argument.Message,
        InvalidOperationException validation => validation.Message,
        _ => "PAYLOAD_CHECK_FAILED"
    };
    Console.WriteLine("FAIL payload/installer validation · " + exception.GetType().Name + " · " + detail);
    return 1;
}

static (string? PayloadDirectory, string? InstallerAssembly) ParseVerificationArguments(string[] arguments)
{
    if (arguments.Length == 0) return (null, null);
    if (arguments.Length is not (2 or 4) || arguments[0] != "--payload-directory" || string.IsNullOrWhiteSpace(arguments[1]) ||
        arguments.Length == 4 && (arguments[2] != "--installer-assembly" || string.IsNullOrWhiteSpace(arguments[3])))
        throw new ArgumentException("Usage: [--payload-directory <ZIP-directory> [--installer-assembly <published-DLL>]]");
    return (Path.GetFullPath(arguments[1]), arguments.Length == 4 ? Path.GetFullPath(arguments[3]) : null);
}

static void ValidateEmbeddedPayloads(string installerAssembly, string payloadDirectory)
{
    // Read only PE/ECMA-335 metadata. Never load the assembly or execute its WinForms entry point.
    using var assemblyStream = File.Open(installerAssembly, FileMode.Open, FileAccess.Read, FileShare.Read);
    using var pe = new PEReader(assemblyStream);
    var header = pe.PEHeaders.CorHeader;
    Check(pe.HasMetadata && header is not null, "Installer DLL must contain managed PE metadata.");
    var metadata = pe.GetMetadataReader();
    Check(metadata.IsAssembly && metadata.GetString(metadata.GetAssemblyDefinition().Name) == "IISManager.Setup",
        "Published installer assembly identity does not match IISManager.Setup.");
    var resourcesDirectory = header!.ResourcesDirectory;
    Check(resourcesDirectory.RelativeVirtualAddress > 0 && resourcesDirectory.Size >= sizeof(int), "Installer managed resource directory is missing.");
    var resourceBlock = pe.GetSectionData(resourcesDirectory.RelativeVirtualAddress);
    Check(resourcesDirectory.Size <= resourceBlock.Length, "Installer managed resource directory extends outside its PE section.");
    var resources = new Dictionary<string, ManifestResource>(StringComparer.Ordinal);
    foreach (var handle in metadata.ManifestResources)
    {
        var resource = metadata.GetManifestResource(handle);
        Check(resources.TryAdd(metadata.GetString(resource.Name), resource), "Installer contains duplicate manifest resource names.");
    }

    foreach (var component in new[] { SetupComponent.Agent, SetupComponent.Console })
    {
        var name = "IISManager.Setup.Payload." + component + ".zip";
        Check(resources.TryGetValue(name, out var resource), "Installer is missing the exact embedded " + component + " ZIP resource name.");
        Check(resource.Implementation.IsNil, "Installer payload must be embedded in this DLL rather than linked externally.");
        Check(resource.Offset >= 0 && resource.Offset <= resourcesDirectory.Size - sizeof(int), "Installer payload resource offset is outside the managed resource directory.");
        var offset = checked((int)resource.Offset);
        var content = resourceBlock.GetReader(offset, resourcesDirectory.Size - offset);
        var length = content.ReadInt32(); // Each embedded resource starts with its little-endian byte length.
        Check(length >= 0 && length <= content.RemainingBytes, "Installer payload resource byte length is invalid.");
        using var zip = File.Open(Path.Combine(payloadDirectory, component + ".zip"), FileMode.Open, FileAccess.Read, FileShare.Read);
        Check(zip.Length == length, "Installer embedded " + component + " ZIP length differs from the verified ZIP.");
        var expectedHash = SHA256.HashData(zip);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var remaining = length;
        while (remaining > 0)
        {
            var bytes = content.ReadBytes(Math.Min(65536, remaining));
            hash.AppendData(bytes);
            remaining -= bytes.Length;
        }
        Check(hash.GetHashAndReset().SequenceEqual(expectedHash), "Installer embedded " + component + " ZIP SHA256 differs from the verified ZIP.");
        Console.WriteLine($"PASS embedded {component} payload · {length} bytes and SHA256 match the verified ZIP");
    }
    Console.WriteLine("Installer embedded payload validation: 2/2 passed.");
}

static void ValidateActualPayloads(string payloadDirectory)
{
    using var workspace = new TestWorkspace();
    foreach (var component in new[] { SetupComponent.Agent, SetupComponent.Console })
    {
        using var stream = File.Open(Path.Combine(payloadDirectory, component + ".zip"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var payload = new PayloadArchive(stream, component);
        var stage = Path.Combine(workspace.Directory, component.ToString());
        payload.ExtractTo(stage);

        var expected = payload.Manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var actual = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(stage, file).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        Check(expected.SetEquals(actual), "Actual extracted payload file set differs from its manifest.");
        foreach (var file in payload.Manifest.Files)
        {
            var destination = Path.Combine(stage, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Check(new FileInfo(destination).Length == file.Size, "Actual extracted payload size differs from its manifest.");
            using var content = File.OpenRead(destination);
            Check(Convert.ToHexString(SHA256.HashData(content)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase),
                "Actual extracted payload hash differs from its manifest.");
        }

        if (component == SetupComponent.Console)
        {
            using var configuration = File.OpenRead(Path.Combine(stage, "appsettings.json"));
            using var document = JsonDocument.Parse(configuration);
            Check(document.RootElement.TryGetProperty("Console", out var console) && console.ValueKind == JsonValueKind.Object,
                "Actual default Console configuration section is missing.");
            foreach (var property in new[] { "AllowedHosts", "AllowedClientNetworks" })
                Check(console.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0,
                    "Actual default Console " + property + " must be an empty array to prevent configuration array tail merging.");
        }

        Console.WriteLine($"PASS actual {component} payload · {payload.Manifest.Files.Length} files extracted and verified");
    }
    Console.WriteLine("Actual payload validation: 2/2 passed; temporary extraction directories will be removed.");
}

static void ValidPlans()
{
    Check(SetupPlanValidator.Validate(AgentPlan()).Length == 0, "Valid Agent plan rejected.");
    Check(SetupPlanValidator.Validate(ConsolePlan()).Length == 0, "Valid Console plan rejected.");
    Check(AgentPlan().ServiceName == "IISManager.Agent" && ConsolePlan().ServiceAccount == @"NT SERVICE\IISManager.Console", "Fixed service identity changed.");
}

static void WindowsPaths()
{
    foreach (var invalid in new[]
    {
        "relative", @"C:relative", @"C:\", @"\\server\share\app", @"\\?\C:\app",
        @"C:\app\..\escape", @"C:\app\.\child", @"C:\app\NUL.txt", @"C:\app\COM1",
        @"C:\app\COM¹.txt", @"C:\app\LPT²", @"C:\app\item:stream", @"C:\app\trailing.",
        @"C:\app\trailing ", @"C:\app\\child"
    })
        Check(!SetupPlanValidator.IsWindowsDirectory(invalid), "Unsafe Windows directory accepted: " + invalid);
    Check(SetupPlanValidator.IsWindowsDirectory(@"D:\Sites"), "Normal directory rejected.");
    Check(SetupPlanValidator.IsWindowsDirectory(@"D:\", allowDriveRoot: true), "Allowed site drive root rejected.");
    Check(!SetupPlanValidator.IsWindowsDirectory(@"D:\", allowDriveRoot: false), "Program drive root accepted.");
}

static void OverlappingDirectories()
{
    foreach (var directory in new[] { @"c:/tools/iismanager.agent", @"C:\Tools", @"C:\Tools\IISManager.Agent\data" })
        HasIssue(AgentPlan() with { DataDirectory = directory }, "DIRECTORIES_OVERLAP");
    Check(!SetupPlanValidator.PathsOverlap(@"C:\Tools\App", @"C:\Tools\App2"), "Sibling prefix mistaken for containment.");
    Check(!SetupPlanValidator.PathsOverlap(@"C:\Tools\App", @"D:\Tools\App"), "Different drives mistaken for containment.");
}

static void InvalidPlanInputs()
{
    foreach (var network in new[] { "*", "0.0.0.0/0", "::/0", "10.0.0.1/33", "::1/129", "10.0.0.1/24/1", " 10.0.0.1" })
        HasIssue(AgentPlan() with { AllowedNetworks = [network] }, "NETWORK_ALLOWLIST_REQUIRED");
    Check(SetupPlanValidator.IsNetwork("10.0.0.1") && SetupPlanValidator.IsNetwork("2001:db8::/64"), "Explicit IP/CIDR rejected.");
    HasIssue(AgentPlan() with { AllowedClientThumbprints = ["not-a-thumbprint"] }, "CLIENT_ALLOWLIST_REQUIRED");
    HasIssue(AgentPlan() with { ServerCertificateThumbprint = new string('a', 39) }, "SERVER_CERTIFICATE_REQUIRED");
    HasIssue(AgentPlan() with { IdentityKind = ServiceIdentityKind.VirtualAccount }, "AGENT_IDENTITY_REQUIRED");
    HasIssue(AgentPlan() with { IdentityKind = ServiceIdentityKind.ExistingAccount, AccountName = "" }, "SERVICE_ACCOUNT_REQUIRED");
    HasIssue(AgentPlan() with { Port = 443 }, "PORT_INVALID");
    HasIssue(AgentPlan() with { AllowedSiteRoots = [] }, "SITE_ROOT_REQUIRED");
}

static void ConsoleBoundaries()
{
    foreach (var host in new[] { "*", "*.example.test", "console.example.test:7443", "https://console.example.test", " console.example.test" })
        HasIssue(ConsolePlan() with { AllowedHosts = [host] }, "HOST_ALLOWLIST_REQUIRED");
    HasIssue(ConsolePlan() with { AllowedHosts = ["other.example.test"] }, "HOST_NOT_ALLOWED");
    Check(SetupPlanValidator.Validate(ConsolePlan() with { AllowedHosts = ["CONSOLE.EXAMPLE.TEST"] }).Length == 0, "Host matching must ignore case.");
    foreach (var url in new[]
    {
        "http://agent.example.test:7444/", "https://user:password@agent.example.test:7444/",
        "https://agent.example.test:7444/api", "https://agent.example.test:7444/?q=value", "https://agent.example.test:7444/#fragment"
    })
        HasIssue(ConsolePlan() with { Servers = [Server() with { AgentUrl = url }] }, "AGENT_SERVER_INVALID");
    HasIssue(ConsolePlan() with { Servers = [Server(), Server() with { Id = "SERVER-1" }] }, "AGENT_IDS_DUPLICATED");
    HasIssue(ConsolePlan() with { ClientCertificateThumbprint = "" }, "CONSOLE_CLIENT_CERTIFICATE_REQUIRED");
    HasIssue(ConsolePlan() with { ListenAddress = "console.example.test" }, "LISTEN_ADDRESS_INVALID");
}

static void InvalidEnumsAndServers()
{
    HasIssue(AgentPlan() with { Component = (SetupComponent)99 }, "PLAN_ENUM_INVALID");
    HasIssue(AgentPlan() with { Mode = (SetupMode)99 }, "PLAN_ENUM_INVALID");
    HasIssue(AgentPlan() with { IdentityKind = (ServiceIdentityKind)99 }, "PLAN_ENUM_INVALID");
    Check(SetupPlanValidator.Validate(ConsolePlan() with { Servers = [null!] }).Length > 0, "Null server entry must produce a validation issue.");
}

static void UpdateContract()
{
    var update = new SetupPlan { Component = SetupComponent.Console, Mode = SetupMode.Update, InstallDirectory = @"C:\Tools\IISManager.Console" };
    Check(SetupPlanValidator.Validate(update).Length == 0, "Update must read settings from the installed service rather than demand install defaults.");
    HasIssue(update with { InstallDirectory = @"..\app" }, "INSTALL_PATH_INVALID");
    HasIssue(update with { IdentityKind = (ServiceIdentityKind)99 }, "PLAN_ENUM_INVALID");
    ThrowsCode(() => SetupConfiguration.CreateLocalConfiguration(update), "UPDATE_CONFIGURATION_FORBIDDEN");
}

static void AgentConfiguration()
{
    var thumb = string.Join(" ", Enumerable.Repeat("aa", 20));
    var plan = AgentPlan() with
    {
        ServerCertificateThumbprint = thumb,
        AllowedClientThumbprints = [new string('b', 40), new string('B', 40)],
        IdentityKind = ServiceIdentityKind.ExistingAccount,
        AccountName = @"DOMAIN\prepared-account"
    };
    var json = SetupConfiguration.CreateLocalConfiguration(plan);
    using var document = JsonDocument.Parse(json);
    var agent = document.RootElement.GetProperty("Agent");
    Check(agent.GetProperty("Mode").GetString() == "Windows", "Agent simulation must not enter production config.");
    Check(agent.GetProperty("StateDirectory").GetString() == @"C:\IISManagerData\Agent\operations", "Agent ledger must use the separate data directory.");
    Check(agent.GetProperty("ServerCertificateThumbprint").GetString() == new string('A', 40), "Certificate thumbprint must normalize.");
    Check(agent.GetProperty("AllowedClientThumbprints").GetArrayLength() == 1, "Equivalent client thumbprints must deduplicate.");
    Check(agent.GetProperty("ReservedPorts").EnumerateArray().Select(p => p.GetInt32()).Order().SequenceEqual(new[] { 7443, 7444 }), "Management ports must remain reserved.");
    Check(!json.Contains(plan.AccountName, StringComparison.Ordinal) && !json.Contains("password", StringComparison.OrdinalIgnoreCase), "Service credentials or account must not enter application config.");
}

static void ConsoleConfiguration()
{
    using var document = JsonDocument.Parse(SetupConfiguration.CreateLocalConfiguration(ConsolePlan()));
    var root = document.RootElement;
    var console = root.GetProperty("Console");
    Check(root.GetProperty("AllowedHosts").GetString() == "console.example.test", "Framework Host allowlist must match the application allowlist.");
    Check(!console.GetProperty("AllowDevelopmentHttp").GetBoolean(), "Production must disable development HTTP.");
    Check(console.GetProperty("TaskDirectory").GetString() == @"C:\IISManagerData\Console\console-tasks", "Console ledger must use the separate data directory.");
    Check(console.GetProperty("AllowedClientNetworks")[0].GetString() == "10.2.0.0/24", "Console source allowlist changed.");
    var server = console.GetProperty("Servers")[0];
    Check(!server.GetProperty("Simulation").GetBoolean() && server.GetProperty("AgentUrl").GetString()!.StartsWith("https://", StringComparison.Ordinal), "Production Agent connections must use HTTPS without Simulation.");
    Check(console.GetProperty("AgentClientCertificateThumbprint").GetString() == new string('B', 40), "Client certificate pin changed.");
}

static void HttpTokenPlans()
{
    Check(new SetupPlan().TransportMode == "HttpToken", "New installs must default to certificate-free HTTP.");
    Check(SetupPlanValidator.Validate(HttpAgentPlan()).Length == 0, "HTTP Agent without certificates must be accepted.");
    Check(SetupPlanValidator.Validate(HttpConsolePlan()).Length == 0, "HTTP Console without certificates must be accepted.");
    Check(SetupPlanValidator.IsAgentUrl("http://192.168.1.20:7444/", "HttpToken"), "HTTP IP address must be accepted.");
    Check(SetupPlanValidator.IsAgentUrl("http://[fd00::20]:7444/", "HttpToken"), "HTTP IPv6 address must be accepted.");
}

static void HttpTokenBoundaries()
{
    foreach (var invalid in new[] { "", new string('A', 63), new string('A', 65), new string('G', 64), new string('A', 64) + "\n" })
    {
        HasIssue(HttpAgentPlan() with { AgentAccessToken = invalid }, "AGENT_TOKEN_REQUIRED");
        HasIssue(HttpConsolePlan() with { Servers = [HttpServer() with { AccessToken = invalid }] }, "AGENT_SERVER_INVALID");
    }
    HasIssue(HttpAgentPlan() with { TransportMode = "http" }, "TRANSPORT_MODE_INVALID");
    HasIssue(HttpAgentPlan() with { GrantCertificateKeyRead = true }, "HTTP_KEY_GRANT_FORBIDDEN");
    foreach (var network in new[] { "0.0.0.0/0", "::/0", "::ffff:10.2.0.1/96", "10.2.0.1/+24", "fe80::1%en0" })
    {
        HasIssue(HttpAgentPlan() with { AllowedNetworks = [network] }, "NETWORK_ALLOWLIST_REQUIRED");
        HasIssue(HttpConsolePlan() with { AllowedNetworks = [network] }, "NETWORK_ALLOWLIST_REQUIRED");
    }
    foreach (var url in new[] { "https://192.168.1.20:7444/", "http://192.168.1.20:7444/api", "http://192.168.1.20:7444/?q=x", "http://user@192.168.1.20:7444/" })
        HasIssue(HttpConsolePlan() with { Servers = [HttpServer() with { AgentUrl = url }] }, "AGENT_SERVER_INVALID");
    HasIssue(ConsolePlan() with { Servers = [HttpServer()] }, "AGENT_SERVER_INVALID");
}

static void HttpTokenConfiguration()
{
    using var agentDocument = JsonDocument.Parse(SetupConfiguration.CreateLocalConfiguration(HttpAgentPlan()));
    var agent = agentDocument.RootElement.GetProperty("Agent");
    Check(agent.GetProperty("TransportMode").GetString() == "HttpToken", "HTTP Agent mode must be explicit.");
    Check(agent.GetProperty("AccessToken").GetString() == new string('A', 64), "HTTP Agent token must be written to actual configuration.");
    Check(agent.GetProperty("AllowedConsoleNetworks")[0].GetString() == "10.2.0.0/24", "Agent must enforce the configured TCP source range.");
    Check(agent.GetProperty("ServerCertificateThumbprint").GetString() == "" && agent.GetProperty("AllowedClientThumbprints").GetArrayLength() == 0,
        "HTTP Agent must not depend on stale certificate settings.");
    using var consoleDocument = JsonDocument.Parse(SetupConfiguration.CreateLocalConfiguration(HttpConsolePlan()));
    var console = consoleDocument.RootElement.GetProperty("Console");
    Check(console.GetProperty("TransportMode").GetString() == "HttpToken" && !console.GetProperty("AllowDevelopmentHttp").GetBoolean(),
        "Production HTTP must use its explicit transport mode instead of the development bypass.");
    Check(console.GetProperty("ServerCertificateThumbprint").GetString() == "" && console.GetProperty("AgentClientCertificateThumbprint").GetString() == "",
        "HTTP Console must not require certificates.");
    var server = console.GetProperty("Servers")[0];
    Check(server.GetProperty("AccessToken").GetString() == new string('B', 64) && !server.GetProperty("Simulation").GetBoolean(),
        "HTTP production target must have its own token and must not use Simulation.");
}

static void TokenReviewRedaction()
{
    var agent = HttpAgentPlan(); var server = HttpServer();
    Check(!agent.ToString().Contains(agent.AgentAccessToken, StringComparison.Ordinal), "Agent plan review must redact the token.");
    Check(!server.ToString().Contains(server.AccessToken, StringComparison.Ordinal), "Agent server review must redact the token.");
    Check(!JsonSerializer.Serialize(agent).Contains(agent.AgentAccessToken, StringComparison.Ordinal), "Generic plan evidence must exclude tokens.");
    Check(!JsonSerializer.Serialize(HttpConsolePlan()).Contains(server.AccessToken, StringComparison.Ordinal), "Generic server evidence must exclude tokens.");
}

static void ManifestTargets()
{
    var fixture = PayloadFixture.Create(SetupComponent.Agent);
    PayloadArchive.ValidateManifest(fixture.Manifest, SetupComponent.Agent);
    foreach (var manifest in new[]
    {
        fixture.Manifest with { Component = "Console" }, fixture.Manifest with { TargetFramework = "net9.0" },
        fixture.Manifest with { RuntimeIdentifier = "osx-arm64" }, fixture.Manifest with { SelfContained = false }
    })
        ThrowsCode(() => PayloadArchive.ValidateManifest(manifest, SetupComponent.Agent), "PAYLOAD_TARGET_INVALID");
}

static void RequiredFiles()
{
    var fixture = PayloadFixture.Create(SetupComponent.Agent);
    ThrowsCode(() => PayloadArchive.ValidateManifest(fixture.Without("hostfxr.dll"), SetupComponent.Agent), "PAYLOAD_RUNTIME_MISSING");
    ThrowsCode(() => PayloadArchive.ValidateManifest(fixture.Without("NLog.config"), SetupComponent.Agent), "PAYLOAD_DEFAULT_CONFIG_MISSING");
    var console = PayloadFixture.Create(SetupComponent.Console);
    ThrowsCode(() => PayloadArchive.ValidateManifest(console.Without("wwwroot/index.html"), SetupComponent.Console), "PAYLOAD_UI_MISSING");
}

static void InvalidManifests()
{
    var fixture = PayloadFixture.Create(SetupComponent.Agent);
    var first = fixture.Manifest.Files[0];
    foreach (var invalid in new[]
    {
        first with { Sha256 = "invalid" }, first with { Sha256 = new string('g', 64) },
        first with { Size = -1 }, first with { Size = 256L * 1024 * 1024 + 1 },
        first with { Kind = (PayloadFileKind)99 }, first with { Path = "../outside.dll" },
        first with { Path = PayloadArchive.ManifestName }
    })
        ThrowsCode(() => PayloadArchive.ValidateManifest(fixture.Manifest with { Files = [.. fixture.Manifest.Files, invalid] }, SetupComponent.Agent), "PAYLOAD_MANIFEST_INVALID");
    ThrowsCode(() => PayloadArchive.ValidateManifest(fixture.Manifest with { Files = [.. fixture.Manifest.Files, first with { Path = first.Path.ToLowerInvariant() }] }, SetupComponent.Agent), "PAYLOAD_MANIFEST_INVALID");
    ThrowsCode(() => PayloadArchive.ValidateManifest(fixture.Manifest with { Files = null! }, SetupComponent.Agent), "PAYLOAD_MANIFEST_INVALID");
}

static void ProtectedPayloadFiles()
{
    var fixture = PayloadFixture.Create(SetupComponent.Agent);
    foreach (var path in new[] { "appsettings.Local.json", "config/appsettings.Production.json", "logs/runtime.dll", "Data/runtime.dll", "cert.pfx", "keys/private.PEM" })
        ThrowsCode(() => PayloadArchive.ValidateManifest(fixture.AddFile(path), SetupComponent.Agent), "PAYLOAD_PROTECTED_FILE");
    var config = new PayloadFile("appsettings.Local.json", 0, Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())), PayloadFileKind.DefaultConfiguration);
    ThrowsCode(() => PayloadArchive.ValidateManifest(fixture.Manifest with { Files = [.. fixture.Manifest.Files, config] }, SetupComponent.Agent), "PAYLOAD_CONFIG_INVALID");
}

static void LegacyManifest()
{
    var fixture = PayloadFixture.Create(SetupComponent.Agent);
    var runtime = fixture.Manifest.Files.Where(f => f.Kind == PayloadFileKind.Runtime).ToArray();
    PayloadArchive.ValidateManifest(fixture.Manifest with { LegacyRuntimeFiles = runtime }, SetupComponent.Agent);
    ThrowsCode(() => PayloadArchive.ValidateManifest(fixture.Manifest with { LegacyRuntimeFiles = fixture.Manifest.Files }, SetupComponent.Agent), "PAYLOAD_CONFIG_INVALID");
    ThrowsCode(() => PayloadArchive.ValidateManifest(fixture.Manifest with { LegacyRuntimeFiles = [runtime[0], runtime[0]] }, SetupComponent.Agent), "PAYLOAD_MANIFEST_INVALID");
}

static void UnsafeZipPaths()
{
    var fixture = PayloadFixture.Create(SetupComponent.Agent);
    foreach (var path in new[] { "../outside.dll", "/absolute.dll", "C:/outside.dll", "folder\\file.dll", "folder//file.dll", "folder/file.dll:stream", "NUL.dll", "COM³.dll" })
    {
        using var zip = fixture.Archive(extra: [new ZipItem(path, [])]);
        ThrowsCode(() => OpenAndDispose(zip), "PAYLOAD_PATH_INVALID");
    }
    using var duplicate = fixture.Archive(extra: [new ZipItem("iismanager.agent.DLL", [])]);
    ThrowsCode(() => OpenAndDispose(duplicate), "PAYLOAD_PATH_INVALID");
}

static void ZipLinks()
{
    var fixture = PayloadFixture.Create(SetupComponent.Agent);
    using var unixLink = fixture.Archive(extra: [new ZipItem("link.dll", Encoding.UTF8.GetBytes("../outside"), 0xA000 << 16)]);
    ThrowsCode(() => OpenAndDispose(unixLink), "PAYLOAD_LINK_FORBIDDEN");
    using var reparse = fixture.Archive(extra: [new ZipItem("reparse.dll", [], 0x400)]);
    ThrowsCode(() => OpenAndDispose(reparse), "PAYLOAD_LINK_FORBIDDEN");
    using var directory = fixture.Archive(extra: [new ZipItem("folder/", [])]);
    ThrowsCode(() => OpenAndDispose(directory), "PAYLOAD_PATH_INVALID");
}

static void ArchiveFileSet()
{
    var fixture = PayloadFixture.Create(SetupComponent.Agent);
    using var extra = fixture.Archive(extra: [new ZipItem("unexpected.dll", [])]);
    ThrowsCode(() => OpenAndDispose(extra), "PAYLOAD_FILE_SET_MISMATCH");
    using var missing = fixture.Archive(omit: "hostpolicy.dll");
    ThrowsCode(() => OpenAndDispose(missing), "PAYLOAD_FILE_SET_MISMATCH");
    var first = fixture.Manifest.Files[0];
    var mismatch = fixture.Manifest with { Files = fixture.Manifest.Files.Select(f => f.Path == first.Path ? f with { Size = f.Size + 1 } : f).ToArray() };
    using var size = fixture.Archive(manifest: mismatch);
    ThrowsCode(() => OpenAndDispose(size), "PAYLOAD_SIZE_MISMATCH");
}

static void MalformedManifestJson()
{
    var fixture = PayloadFixture.Create(SetupComponent.Agent);
    using var nullManifest = fixture.Archive(manifestJson: "null");
    ThrowsCode(() => OpenAndDispose(nullManifest), "PAYLOAD_MANIFEST_INVALID");
    using var malformed = fixture.Archive(manifestJson: "{invalid-json");
    Throws<JsonException>(() => OpenAndDispose(malformed));
    var json = JsonSerializer.Serialize(fixture.Manifest, PayloadArchive.JsonOptions);
    using var unknown = fixture.Archive(manifestJson: json[..^1] + ",\"unrecognized\":true}");
    Throws<JsonException>(() => OpenAndDispose(unknown));
}

static void ValidExtraction()
{
    using var workspace = new TestWorkspace();
    var fixture = PayloadFixture.Create(SetupComponent.Console);
    using var zip = fixture.Archive();
    using var payload = new PayloadArchive(zip, SetupComponent.Console);
    var stage = Path.Combine(workspace.Directory, "stage");
    payload.ExtractTo(stage);
    foreach (var item in fixture.Items)
        Check(File.ReadAllBytes(Path.Combine(stage, item.Path.Replace('/', Path.DirectorySeparatorChar))).SequenceEqual(item.Data), "Extracted bytes differ: " + item.Path);
    Check(Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Count() == fixture.Items.Length, "Extraction included an unlisted file or manifest.");
}

static void HashMismatch()
{
    using var workspace = new TestWorkspace();
    var fixture = PayloadFixture.Create(SetupComponent.Agent);
    var manifest = fixture.Manifest with { Files = fixture.Manifest.Files.Select((file, index) => index == 0 ? file with { Sha256 = new string('0', 64) } : file).ToArray() };
    using var zip = fixture.Archive(manifest: manifest);
    using var payload = new PayloadArchive(zip, SetupComponent.Agent);
    var sentinel = Path.Combine(workspace.Directory, "outside.txt");
    File.WriteAllText(sentinel, "unchanged");
    var stage = Path.Combine(workspace.Directory, "stage");
    ThrowsCode(() => payload.ExtractTo(stage), "PAYLOAD_HASH_MISMATCH");
    Check(File.ReadAllText(sentinel) == "unchanged", "Failed extraction wrote outside the stage.");
    Check(Directory.Exists(stage), "Failed stage must remain available for diagnosis.");
}

static void NonEmptyExtraction()
{
    using var workspace = new TestWorkspace();
    var fixture = PayloadFixture.Create(SetupComponent.Agent);
    using var zip = fixture.Archive();
    using var payload = new PayloadArchive(zip, SetupComponent.Agent);
    var stage = Path.Combine(workspace.Directory, "stage");
    Directory.CreateDirectory(stage);
    File.WriteAllText(Path.Combine(stage, "existing.txt"), "preserve");
    ThrowsCode(() => payload.ExtractTo(stage), "PAYLOAD_STAGE_NOT_EMPTY");
    Check(File.ReadAllText(Path.Combine(stage, "existing.txt")) == "preserve" && Directory.EnumerateFiles(stage).Count() == 1, "Nonempty stage must remain unchanged.");
}

static void LinkedExtraction()
{
    using var workspace = new TestWorkspace();
    var target = Path.Combine(workspace.Directory, "target");
    Directory.CreateDirectory(target);
    var alias = Path.Combine(workspace.Directory, "alias");
    Directory.CreateSymbolicLink(alias, target);
    var fixture = PayloadFixture.Create(SetupComponent.Agent);
    using var zip = fixture.Archive();
    using var payload = new PayloadArchive(zip, SetupComponent.Agent);
    ThrowsCode(() => payload.ExtractTo(Path.Combine(alias, "stage")), "PATH_REPARSE_POINT_FORBIDDEN");
    Check(!Directory.EnumerateFileSystemEntries(target).Any(), "Reparse target must remain untouched.");
}

static void ConsoleUiScope()
{
    var fixture = PayloadFixture.Create(SetupComponent.Console);
    var original = fixture.Manifest.Files.Where(file => file.Kind == PayloadFileKind.Runtime && file.Path != "wwwroot/index.html")
        .Concat(Enumerable.Range(0, 337).Select(index => UiFile("Library" + index + ".dll")))
        .Concat(new[] { UiFile("IISManager.Console.staticwebassets.endpoints.json") })
        .Concat(new[] { "index.html", "app.css", "app.js" }.SelectMany(name => new[] { "", ".br", ".gz" }
            .Select(suffix => UiFile("wwwroot/" + name + suffix)))).ToArray();
    Check(original.Length == 354, "Synthetic runtime baseline must exercise the observed 354-file shape.");
    var updated = original.Select(file => file.Path.StartsWith("wwwroot/", StringComparison.Ordinal) ||
            file.Path == "IISManager.Console.staticwebassets.endpoints.json" ? file with { Sha256 = new string('b', 64) } : file)
        .Concat(Enumerable.Range(0, 12).SelectMany(index => new[] { "", ".br", ".gz" }
            .Select(suffix => UiFile("wwwroot/assets/fluent/icon" + index + ".svg" + suffix)))).ToArray();
    var manifest = fixture.Manifest with { Files = [.. updated, .. fixture.Manifest.Files.Where(file => file.Kind == PayloadFileKind.DefaultConfiguration)] };
    var scope = ConsoleUiUpdatePolicy.GetPendingScope(original, manifest);
    Check(updated.Length == 390 && scope.Length == 46, "Observed runtime shape must include ten changed resources and 36 added resources.");
    Check(scope.All(file => file.Path.StartsWith("wwwroot/", StringComparison.Ordinal) ||
        file.Path == "IISManager.Console.staticwebassets.endpoints.json"), "A backend runtime file entered the UI scope.");
    var text = UiFile("wwwroot/assets/fluent/README.md");
    Check(ConsoleUiUpdatePolicy.GetPendingScope(original, manifest with { Files = [.. manifest.Files, text] }).Contains(text),
        "Packaged offline license/readme resources must be permitted.");
}

static void ConsoleUiRuntimeBoundaries()
{
    var manifest = PayloadFixture.Create(SetupComponent.Console).Manifest;
    var original = manifest.Files.Where(file => file.Kind == PayloadFileKind.Runtime).ToArray();
    foreach (var path in new[] { "IISManager.Console.dll", "coreclr.dll", "IISManager.Console.runtimeconfig.json" })
        ThrowsCode(() => ConsoleUiUpdatePolicy.GetPendingScope(original, manifest with { Files = manifest.Files
            .Select(file => file.Path == path ? file with { Sha256 = new string('a', 64) } : file).ToArray() }), "UI_UPDATE_SCOPE_FORBIDDEN");
    foreach (var path in new[] { "Extension.dll", "wwwroot/Extension.dll", "wwwroot/Extension.dll.br" })
        ThrowsCode(() => ConsoleUiUpdatePolicy.GetPendingScope(original, manifest with { Files = [.. manifest.Files, UiFile(path)] }), "UI_UPDATE_SCOPE_FORBIDDEN");
    var removable = UiFile("wwwroot/old.css");
    ThrowsCode(() => ConsoleUiUpdatePolicy.GetPendingScope([.. original, removable], manifest), "UI_UPDATE_RUNTIME_REMOVED");
}

static void ConsoleUiPathBoundaries()
{
    var manifest = PayloadFixture.Create(SetupComponent.Console).Manifest;
    var original = manifest.Files.Where(file => file.Kind == PayloadFileKind.Runtime).ToArray();
    ThrowsCode(() => ConsoleUiUpdatePolicy.GetPendingScope(original, manifest with { Files = manifest.Files.Select(file =>
        file.Path == "wwwroot/index.html" ? file with { Path = "wwwroot/Index.html" } : file).ToArray() }), "PAYLOAD_UI_MISSING");
    var style = UiFile("wwwroot/app.css");
    ThrowsCode(() => ConsoleUiUpdatePolicy.GetPendingScope([.. original, style], manifest with { Files = [.. manifest.Files,
        style with { Path = "wwwroot/App.css" }] }), "UI_UPDATE_PATH_AMBIGUOUS");
    ThrowsCode(() => ConsoleUiUpdatePolicy.GetPendingScope(original, manifest with { Files = [.. manifest.Files,
        UiFile("wwwroot/Assets/one.svg"), UiFile("wwwroot/assets/two.svg")] }), "UI_UPDATE_PATH_AMBIGUOUS");
    ThrowsCode(() => ConsoleUiUpdatePolicy.GetPendingScope(original, manifest with { Files = [.. manifest.Files,
        UiFile("wwwroot/styles.css"), UiFile("wwwroot/styles.css/one.svg")] }), "UI_UPDATE_PATH_AMBIGUOUS");
    ThrowsCode(() => ConsoleUiUpdatePolicy.GetPendingScope(original, manifest with { Files = [.. manifest.Files,
        UiFile("WWWROOT/one.svg")] }), "UI_UPDATE_PATH_AMBIGUOUS");
    ThrowsCode(() => ConsoleUiUpdatePolicy.GetPendingScope(original, manifest with { Files = [.. manifest.Files,
        UiFile("wwwroot/index.HTML")] }), "PAYLOAD_MANIFEST_INVALID");
}

static void ConsoleUiProtectedResources()
{
    var manifest = PayloadFixture.Create(SetupComponent.Console).Manifest;
    var original = manifest.Files.Where(file => file.Kind == PayloadFileKind.Runtime).ToArray();
    foreach (var path in new[] { "wwwroot/secrets.json", "wwwroot/client-token.txt", "wwwroot/keys/icon.svg",
        "wwwroot/config/app.css", "wwwroot/.env", "wwwroot/.git/README.md", "wwwroot/client.key.gz", "wwwroot/NLog.config.br",
        "wwwroot/certificate.pem.br", "wwwroot/app.css.gz.br" })
        ThrowsCode(() => ConsoleUiUpdatePolicy.GetPendingScope(original, manifest with { Files = [.. manifest.Files, UiFile(path)] }), "UI_UPDATE_SCOPE_FORBIDDEN");
    ThrowsCode(() => ConsoleUiUpdatePolicy.GetPendingScope(original, manifest with { Files = [.. manifest.Files,
        UiFile("wwwroot/appsettings.json.gz")] }), "PAYLOAD_PROTECTED_FILE");
}

static void ConsoleUiManifestBoundaries()
{
    var manifest = PayloadFixture.Create(SetupComponent.Console).Manifest;
    var original = manifest.Files.Where(file => file.Kind == PayloadFileKind.Runtime).ToArray();
    foreach (var invalid in new[] { UiFile("wwwroot/../escape.css"), UiFile("wwwroot/file.css") with { Size = -1 },
        UiFile("wwwroot/file.css") with { Sha256 = "invalid" }, UiFile("wwwroot/file.css") with { Kind = (PayloadFileKind)99 } })
        ThrowsCode(() => ConsoleUiUpdatePolicy.GetPendingScope(original, manifest with { Files = [.. manifest.Files, invalid] }), "PAYLOAD_MANIFEST_INVALID");
    ThrowsCode(() => ConsoleUiUpdatePolicy.GetPendingScope([.. original, original[0] with { Path = original[0].Path.ToUpperInvariant() }],
        manifest), "PAYLOAD_MANIFEST_INVALID");
    ThrowsCode(() => ConsoleUiUpdatePolicy.GetPendingScope([.. original, UiFile("appsettings.json") with { Kind = PayloadFileKind.DefaultConfiguration }],
        manifest), "PAYLOAD_CONFIG_INVALID");
    ThrowsCode(() => ConsoleUiUpdatePolicy.GetPendingScope(original, manifest with { Component = "Agent" }), "PAYLOAD_TARGET_INVALID");
    ThrowsCode(() => ConsoleUiUpdatePolicy.GetPendingScope(null!, manifest), "UI_UPDATE_MANIFEST_INVALID");
    ThrowsCode(() => ConsoleUiUpdatePolicy.GetPendingScope(original, null!), "UI_UPDATE_MANIFEST_INVALID");
}

static void ConsoleUiActualChanges()
{
    var manifest = PayloadFixture.Create(SetupComponent.Console).Manifest;
    var original = manifest.Files.Where(file => file.Kind == PayloadFileKind.Runtime).ToArray();
    ThrowsCode(() => ConsoleUiUpdatePolicy.GetPendingScope(original, manifest), "UI_UPDATE_HAS_NO_CHANGES");
    ThrowsCode(() => ConsoleUiUpdatePolicy.GetPendingScope(original, manifest with { Files = manifest.Files.Select(file =>
        file with { Sha256 = file.Sha256.ToLowerInvariant() }).ToArray() }), "UI_UPDATE_HAS_NO_CHANGES");
    var file = original.Single(file => file.Path == "wwwroot/index.html");
    var changed = file with { Size = file.Size + 1 };
    Check(ConsoleUiUpdatePolicy.GetPendingScope(original, manifest with { Files = manifest.Files.Select(file =>
        file.Path == changed.Path ? changed : file).ToArray() }).SequenceEqual(new[] { changed }), "Size-only drift must remain an actual manifest difference.");
}

static PayloadFile UiFile(string path) => new(path, 1, new string('a', 64), PayloadFileKind.Runtime);

static void TemporaryArchiveAcceptedShapes()
{
    foreach (var serviceSid in new[] { "S-1-5-80-100-200-300-400-500", "S-1-5-80-4294967295-0-1-20-300" })
    {
        var original = ArchiveDescriptor(serviceSid);
        var unchanged = ArchiveDescriptor(serviceSid);
        var moved = ArchiveDescriptor(serviceSid, moved: true);
        Check(TemporaryMoveArchivePolicy.RequireAcceptedArchive(original, unchanged, serviceSid) == TemporaryArchiveAclShape.Unchanged,
            "Exact unchanged temporary archive descriptor must be accepted.");
        Check(TemporaryMoveArchivePolicy.RequireAcceptedArchive(original, moved, serviceSid) == TemporaryArchiveAclShape.ServiceReadExecuteMadeExplicit,
            "Only the observed inherited-to-explicit service-read archive shape may be accepted.");
    }
}

static void TemporaryArchiveDescriptorBoundaries()
{
    const string serviceSid = "S-1-5-80-100-200-300-400-500";
    var original = ArchiveDescriptor(serviceSid);
    var archived = ArchiveDescriptor(serviceSid, moved: true);
    foreach (var mutate in new Func<ArchiveSecurityDescriptor, ArchiveSecurityDescriptor>[]
    {
        descriptor => descriptor with { OwnerSid = "S-1-5-18" },
        descriptor => descriptor with { GroupSid = "S-1-5-18" },
        descriptor => descriptor with { DaclProtected = true },
        descriptor => descriptor with { Revision = 4 },
        descriptor => descriptor with { Dacl = [] },
        descriptor => descriptor with { Dacl = null! }
    })
    {
        ThrowsCode(() => TemporaryMoveArchivePolicy.RequireAcceptedArchive(mutate(original), archived, serviceSid), "ARCHIVED_TEMP_ACL_SHAPE_REJECTED");
        ThrowsCode(() => TemporaryMoveArchivePolicy.RequireAcceptedArchive(original, mutate(archived), serviceSid), "ARCHIVED_TEMP_ACL_SHAPE_REJECTED");
    }
    ThrowsCode(() => TemporaryMoveArchivePolicy.RequireAcceptedArchive(null!, archived, serviceSid), "ARCHIVED_TEMP_ACL_SHAPE_REJECTED");
    ThrowsCode(() => TemporaryMoveArchivePolicy.RequireAcceptedArchive(original, null!, serviceSid), "ARCHIVED_TEMP_ACL_SHAPE_REJECTED");
}

static void TemporaryArchiveAceBoundaries()
{
    const string serviceSid = "S-1-5-80-100-200-300-400-500";
    var original = ArchiveDescriptor(serviceSid);
    var archived = ArchiveDescriptor(serviceSid, moved: true);
    foreach (var mutate in new Func<ArchiveAce, ArchiveAce>[]
    {
        ace => ace with { AceType = 1 },
        ace => ace with { AceType = 5 },
        ace => ace with { AceType = 9 },
        ace => ace with { IsCallback = true },
        ace => ace with { OpaqueLength = 1 },
        ace => ace with { OpaqueLength = -1 },
        ace => ace with { AccessMask = 0x001F01FF },
        ace => ace with { AccessMask = 0x80000000 },
        ace => ace with { AccessMask = ace.AccessMask | 0x00000002 },
        ace => ace with { AccessMask = ace.AccessMask & ~0x00000020U },
        ace => ace with { AceFlags = 16 },
        ace => ace with { AceFlags = 1 },
        ace => ace with { Sid = "S-1-1-0" },
        ace => ace with { Sid = "S-1-5-80-101-200-300-400-500" },
        ace => ace with { Sid = null! }
    })
        ThrowsCode(() => TemporaryMoveArchivePolicy.RequireAcceptedArchive(original,
            archived with { Dacl = [mutate(archived.Dacl[0]), archived.Dacl[1], archived.Dacl[2]] }, serviceSid), "ARCHIVED_TEMP_ACL_SHAPE_REJECTED");
    ThrowsCode(() => TemporaryMoveArchivePolicy.RequireAcceptedArchive(original,
        archived with { Dacl = [archived.Dacl[0], archived.Dacl[2], archived.Dacl[1]] }, serviceSid), "ARCHIVED_TEMP_ACL_SHAPE_REJECTED");
    ThrowsCode(() => TemporaryMoveArchivePolicy.RequireAcceptedArchive(original,
        archived with { Dacl = [.. archived.Dacl, new ArchiveAce(0, 0, 0x001200A9, "S-1-1-0")] }, serviceSid), "ARCHIVED_TEMP_ACL_SHAPE_REJECTED");
    ThrowsCode(() => TemporaryMoveArchivePolicy.RequireAcceptedArchive(original,
        archived with { Dacl = [archived.Dacl[0], archived.Dacl[1]] }, serviceSid), "ARCHIVED_TEMP_ACL_SHAPE_REJECTED");
    ThrowsCode(() => TemporaryMoveArchivePolicy.RequireAcceptedArchive(original,
        archived with { Dacl = [archived.Dacl[0], null!, archived.Dacl[2]] }, serviceSid), "ARCHIVED_TEMP_ACL_SHAPE_REJECTED");
    foreach (var index in new[] { 1, 2 })
    {
        var aces = archived.Dacl.ToArray();
        aces[index] = aces[index] with { AceFlags = 0 };
        ThrowsCode(() => TemporaryMoveArchivePolicy.RequireAcceptedArchive(original, archived with { Dacl = aces }, serviceSid), "ARCHIVED_TEMP_ACL_SHAPE_REJECTED");
        aces = archived.Dacl.ToArray();
        aces[index] = aces[index] with { AccessMask = 0x001200A9 };
        ThrowsCode(() => TemporaryMoveArchivePolicy.RequireAcceptedArchive(original, archived with { Dacl = aces }, serviceSid), "ARCHIVED_TEMP_ACL_SHAPE_REJECTED");
    }
}

static void TemporaryArchiveOriginalBoundaries()
{
    const string serviceSid = "S-1-5-80-100-200-300-400-500";
    var valid = ArchiveDescriptor(serviceSid);
    foreach (var original in new[]
    {
        ArchiveDescriptor(serviceSid, moved: true),
        valid with { Dacl = [valid.Dacl[1], valid.Dacl[0], valid.Dacl[2]] },
        valid with { Dacl = [valid.Dacl[0], valid.Dacl[1], valid.Dacl[2] with { AceFlags = 0 }] },
        valid with { Dacl = [valid.Dacl[0], valid.Dacl[1], valid.Dacl[2] with { AccessMask = 0x001F01FF }] },
        valid with { Dacl = [valid.Dacl[0] with { AceType = 1 }, valid.Dacl[1], valid.Dacl[2]] }
    })
        ThrowsCode(() => TemporaryMoveArchivePolicy.RequireAcceptedArchive(original, original, serviceSid), "ARCHIVED_TEMP_ACL_SHAPE_REJECTED");
}

static void TemporaryArchiveIdentityBoundaries()
{
    const string serviceSid = "S-1-5-80-100-200-300-400-500";
    var original = ArchiveDescriptor(serviceSid);
    var archived = ArchiveDescriptor(serviceSid, moved: true);
    var before = JsonSerializer.Serialize(new[] { original, archived });
    foreach (var invalid in new[] { "", "S-1-5-18", "S-1-5-32-544", "S-1-5-21-100-200-300-400-500",
        "S-1-5-80-100-200-300-400", "S-1-5-80-100-200-300-400-500-600", "S-1-5-80-0100-200-300-400-500",
        "S-1-5-80-4294967296-200-300-400-500", "S-1-5-80-+100-200-300-400-500", "s-1-5-80-100-200-300-400-500",
        "S-1-5-80-101-200-300-400-500" })
        ThrowsCode(() => TemporaryMoveArchivePolicy.RequireAcceptedArchive(original, archived, invalid), "ARCHIVED_TEMP_ACL_SHAPE_REJECTED");
    ThrowsCode(() => TemporaryMoveArchivePolicy.RequireAcceptedArchive(original, archived, null!), "ARCHIVED_TEMP_ACL_SHAPE_REJECTED");
    TemporaryMoveArchivePolicy.RequireAcceptedArchive(original, archived, serviceSid);
    Check(before == JsonSerializer.Serialize(new[] { original, archived }), "Archive policy must not normalize or mutate input evidence.");
}

static ArchiveSecurityDescriptor ArchiveDescriptor(string serviceSid, bool moved = false)
{
    var system = new ArchiveAce(0, 16, 0x001F01FF, "S-1-5-18");
    var administrators = new ArchiveAce(0, 16, 0x001F01FF, "S-1-5-32-544");
    var service = new ArchiveAce(0, moved ? (byte)0 : (byte)16, 0x001200A9, serviceSid);
    return new("S-1-5-32-544", "S-1-5-32-544", false, 2,
        moved ? [service, system, administrators] : [system, administrators, service]);
}

static void PrivateNetworkPlans()
{
    Check(new SetupPlan().SourceAccessMode == "PrivateNetwork", "New installation must default to private sources.");
    foreach (var plan in new[] { AgentPlan(), ConsolePlan(), HttpAgentPlan(), HttpConsolePlan() })
    {
        Check(SetupPlanValidator.Validate(plan with { SourceAccessMode = "PrivateNetwork", AllowedNetworks = [] }).Length == 0,
            "Private source mode must not require manual networks.");
        HasIssue(plan with { SourceAccessMode = "privatenetwork" }, "SOURCE_ACCESS_MODE_INVALID");
        HasIssue(plan with { SourceAccessMode = "Any" }, "SOURCE_ACCESS_MODE_INVALID");
        HasIssue(plan with { SourceAccessMode = "Allowlist", AllowedNetworks = [] }, "NETWORK_ALLOWLIST_REQUIRED");
    }
}

static void PrivateNetworkConfiguration()
{
    foreach (var plan in new[] { AgentPlan(), ConsolePlan(), HttpAgentPlan(), HttpConsolePlan() })
    {
        using var document = JsonDocument.Parse(SetupConfiguration.CreateLocalConfiguration(
            plan with { SourceAccessMode = "PrivateNetwork", AllowedNetworks = ["0.0.0.0/0"] }));
        var section = document.RootElement.GetProperty(plan.Component.ToString());
        Check(section.GetProperty("SourceAccessMode").GetString() == "PrivateNetwork", "Private policy missing from installed configuration.");
        var networks = section.GetProperty(plan.Component == SetupComponent.Agent ? "AllowedConsoleNetworks" : "AllowedClientNetworks");
        Check(networks.GetArrayLength() == 0, "Private mode must not persist unused source arrays.");
        using var legacy = JsonDocument.Parse(SetupConfiguration.CreateLocalConfiguration(plan));
        Check(legacy.RootElement.GetProperty(plan.Component.ToString()).GetProperty("SourceAccessMode").GetString() == "Allowlist",
            "Explicit allowlist mode must be retained.");
    }
}

static void EffectiveSourceNetworks()
{
    var expected = new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "169.254.0.0/16",
        "100.64.0.0/10", "::1/128", "fc00::/7", "fe80::/10" };
    var plan = HttpAgentPlan() with { SourceAccessMode = "PrivateNetwork", AllowedNetworks = [] };
    var actual = SetupPlanValidator.GetEffectiveAllowedNetworks(plan);
    Check(actual.SequenceEqual(expected) && actual.All(SetupPlanValidator.IsNetwork), "Private firewall networks must match the fixed bounded policy.");
    actual[0] = "0.0.0.0/0";
    Check(SetupPlanValidator.GetEffectiveAllowedNetworks(plan).SequenceEqual(expected), "Caller must not mutate shared default networks.");
    var legacy = AgentPlan();
    var clone = SetupPlanValidator.GetEffectiveAllowedNetworks(legacy);
    Check(clone.SequenceEqual(legacy.AllowedNetworks) && !ReferenceEquals(clone, legacy.AllowedNetworks), "Legacy networks must be preserved in an independent array.");
    ThrowsCode(() => SetupPlanValidator.GetEffectiveAllowedNetworks(plan with { SourceAccessMode = "Any" }), "SOURCE_ACCESS_MODE_INVALID");
    ThrowsCode(() => SetupPlanValidator.GetEffectiveAllowedNetworks(legacy with { AllowedNetworks = [] }), "SOURCE_ACCESS_MODE_INVALID");
    Check(SetupPlanValidator.GetEffectiveAllowedNetworks(legacy with { Mode = SetupMode.Update, AllowedNetworks = [] }).Length == 0,
        "Existing HTTPS Agent without a network setting must retain its mTLS update behavior.");
    ThrowsCode(() => SetupPlanValidator.GetEffectiveAllowedNetworks(HttpAgentPlan() with { Mode = SetupMode.Update, AllowedNetworks = [] }), "SOURCE_ACCESS_MODE_INVALID");
    ThrowsCode(() => SetupPlanValidator.GetEffectiveAllowedNetworks(ConsolePlan() with { Mode = SetupMode.Update, AllowedNetworks = [] }), "SOURCE_ACCESS_MODE_INVALID");
}

static void FirewallSourceNetworks()
{
    var plan = HttpConsolePlan() with { SourceAccessMode = "PrivateNetwork", AllowedNetworks = [] };
    var firewall = SetupPlanValidator.GetFirewallAllowedNetworks(plan);
    Check(firewall.Length == 8 && !firewall.Contains("::1/128"), "Windows firewall must exclude the rejected IPv6 loopback subnet.");
    Check(firewall.Contains("127.0.0.0/8") && firewall.Contains("fc00::/7") && firewall.Contains("fe80::/10"),
        "Valid IPv4 loopback and IPv6 private networks must remain available.");
    Check(SetupPlanValidator.GetEffectiveAllowedNetworks(plan).Length == 9 &&
        SetupPlanValidator.GetEffectiveAllowedNetworks(plan).Contains("::1/128"), "Runtime and marker source policy must retain IPv6 loopback.");
    firewall[0] = "modified";
    Check(SetupPlanValidator.GetFirewallAllowedNetworks(plan)[0] != "modified", "Firewall networks must not share mutable state.");
    var allowlist = plan with { SourceAccessMode = "Allowlist", AllowedNetworks = ["::1", "0:0:0:0:0:0:0:1/128", "10.1.2.3/32"] };
    Check(SetupPlanValidator.GetFirewallAllowedNetworks(allowlist).SequenceEqual(new[] { "10.1.2.3/32" }),
        "Equivalent IPv6 loopback representations must not reach native firewall COM.");
    Check(SetupPlanValidator.GetEffectiveAllowedNetworks(allowlist).Length == 3, "Filtering must not change saved allowlist policy.");
    Check(SetupPlanValidator.GetFirewallAllowedNetworks(allowlist with { AllowedNetworks = ["::1/128"] }).Length == 0,
        "An unsupported loopback-only firewall policy must stay empty for native preflight rejection.");
    ThrowsCode(() => SetupPlanValidator.GetFirewallAllowedNetworks(plan with { SourceAccessMode = "invalid" }), "SOURCE_ACCESS_MODE_INVALID");
}

static void StartupMissingTemporaryDirectory()
{
    using var workspace = new TestWorkspace();
    var sessionTemp = Path.Combine(workspace.Directory, "user-temp", "2");
    Check(!Directory.Exists(sessionTemp), "Missing-session fixture already exists.");
    Throws<DirectoryNotFoundException>(() => File.WriteAllText(Path.Combine(sessionTemp, "theme.tmp"), "theme"));
    SetupStartupEnvironment.PrepareTemporaryDirectory(sessionTemp);
    Check(Directory.Exists(sessionTemp), "Configured session TEMP was not created.");
    Check(!Directory.EnumerateFileSystemEntries(sessionTemp).Any(), "Startup probe must be removed.");
    var theme = Path.Combine(sessionTemp, "theme.tmp");
    File.WriteAllText(theme, "theme");
    Check(File.ReadAllText(theme) == "theme", "The visual-style temporary write still fails.");
}

static void StartupExistingTemporaryDirectory()
{
    using var workspace = new TestWorkspace();
    var sentinel = Path.Combine(workspace.Directory, "existing.txt");
    File.WriteAllText(sentinel, "unchanged");
    SetupStartupEnvironment.PrepareTemporaryDirectory(workspace.Directory);
    Check(File.ReadAllText(sentinel) == "unchanged", "Startup preparation changed an existing temporary file.");
    Check(Directory.EnumerateFileSystemEntries(workspace.Directory).Count() == 1, "Startup probe was retained.");
}

static void StartupInvalidTemporaryDirectory()
{
    using var workspace = new TestWorkspace();
    var occupied = Path.Combine(workspace.Directory, "file-instead-of-directory");
    File.WriteAllText(occupied, "unchanged");
    ThrowsCode(() => SetupStartupEnvironment.PrepareTemporaryDirectory(occupied), "INSTALLER_TEMP_UNAVAILABLE");
    Check(File.ReadAllText(occupied) == "unchanged", "Startup preparation replaced a file occupying the TEMP path.");
    foreach (var invalid in new[] { "", "relative-session-temp", "." })
        ThrowsCode(() => SetupStartupEnvironment.PrepareTemporaryDirectory(invalid), "INSTALLER_TEMP_PATH_INVALID");
}

static void SafeFailureDetails()
{
    const string sensitive = "synthetic-secret-never-output";
    var native = new COMException(sensitive, unchecked((int)0xD000000D));
    var wrapped = new SetupException("FIREWALL_COM_SET_REMOTEADDRESSES_FAILED", "固定安全说明", new TargetInvocationException(sensitive, native));
    var summary = SafeFailureSummary.From(wrapped);
    Check(summary.LeafExceptionType == "COMException" && summary.LeafHResult == unchecked((int)0xD000000D), "Leaf native HRESULT lost.");
    Check(summary.ExceptionChain.Length == 3 && summary.ExceptionChain[1].ExceptionType == "TargetInvocationException" &&
        !summary.ChainTruncated, "Exception wrappers must be safely retained.");
    native.Data["config"] = sensitive;
    Check(!JsonSerializer.Serialize(summary).Contains(sensitive, StringComparison.Ordinal), "Exception summary exposed message or Data.");
    var win32 = SafeFailureSummary.From(new System.ComponentModel.Win32Exception(5, sensitive));
    Check(win32.ExceptionChain[0].NativeErrorCode == 5, "Native Win32 error code lost.");
}

static void SafeFailureDepth()
{
    Exception exception = new InvalidOperationException("synthetic-sensitive-value");
    for (var i = 0; i < 30; i++) exception = new Exception("synthetic-sensitive-value", exception);
    var summary = SafeFailureSummary.From(exception);
    Check(summary.ExceptionChain.Length == 16 && summary.ChainTruncated, "Unbounded exception traversal must stop and report truncation.");
    Check(!JsonSerializer.Serialize(summary).Contains("synthetic-sensitive-value", StringComparison.Ordinal), "Truncated chain leaked an exception message.");
    Throws<ArgumentNullException>(() => SafeFailureSummary.From(null!));
}

static SetupPlan AgentPlan() => new()
{
    Component = SetupComponent.Agent, Mode = SetupMode.Install, TransportMode = "Https", SourceAccessMode = "Allowlist",
    InstallDirectory = @"C:\Tools\IISManager.Agent", DataDirectory = @"C:\IISManagerData\Agent",
    HostName = "agent.example.test", Port = 7444, ServerCertificateThumbprint = new string('a', 40),
    AllowedClientThumbprints = [new string('b', 40)], AllowedNetworks = ["10.2.0.0/24"],
    AllowedSiteRoots = [@"D:\Sites"], IdentityKind = ServiceIdentityKind.LocalSystem
};

static SetupPlan ConsolePlan() => new()
{
    Component = SetupComponent.Console, Mode = SetupMode.Install, TransportMode = "Https", SourceAccessMode = "Allowlist",
    InstallDirectory = @"C:\Tools\IISManager.Console", DataDirectory = @"C:\IISManagerData\Console",
    HostName = "console.example.test", ListenAddress = "0.0.0.0", Port = 7443,
    ServerCertificateThumbprint = new string('a', 40), ClientCertificateThumbprint = new string('b', 40),
    AllowedHosts = ["console.example.test"], AllowedNetworks = ["10.2.0.0/24"], Servers = [Server()],
    IdentityKind = ServiceIdentityKind.VirtualAccount
};

static SetupServer Server() => new("server-1", "测试 IIS 服务器", "https://agent.example.test:7444/", new string('c', 40));
static SetupPlan HttpAgentPlan() => AgentPlan() with { TransportMode = "HttpToken", HostName = "192.168.1.20",
    ServerCertificateThumbprint = "", AllowedClientThumbprints = [], AgentAccessToken = new string('a', 64) };
static SetupPlan HttpConsolePlan() => ConsolePlan() with { TransportMode = "HttpToken", ServerCertificateThumbprint = "",
    ClientCertificateThumbprint = "", Servers = [HttpServer()] };
static SetupServer HttpServer() => new("server-1", "测试 IIS 服务器", "http://192.168.1.20:7444/", "", new string('b', 64));
static void RecoveryRuntimePaths()
{
    var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "IISManager.Console"));
    var child = Path.Combine(root, "System.Private.Xml.Linq.dll");
    var nested = Path.Combine(root, "wwwroot", "app.js");
    Check(SetupPathBoundary.IsUnder(path: child, root: root), "A real component child was rejected.");
    Check(!SetupPathBoundary.IsUnder(path: root, root: child), "Reversed containment was accepted.");
    Check(SetupPathBoundary.GetRuntimeRelativePath(root: root, path: child) == "System.Private.Xml.Linq.dll", "DLL relative path failed.");
    Check(SetupPathBoundary.GetRuntimeRelativePath(root: root, path: nested) == "wwwroot/app.js", "Nested runtime relative path failed.");
    foreach (var outside in new[] { Path.Combine(root + "-other", "app.js"), Path.Combine(Path.GetDirectoryName(root)!, "app.js"), root })
        ThrowsCode(() => SetupPathBoundary.GetRuntimeRelativePath(root: root, path: outside), "UI_RECOVERY_RUNTIME_RECORD_INVALID");
    foreach (var invalid in new[] { "relative.dll", Path.Combine(root, "..", "other.dll"), Path.Combine(root, "wwwroot", ".", "app.js") })
        ThrowsCode(() => SetupPathBoundary.GetRuntimeRelativePath(root: root, path: invalid), "UI_RECOVERY_RUNTIME_RECORD_INVALID");
    Check(SetupPathBoundary.IsUnder(path: child.ToUpperInvariant(), root: root.ToLowerInvariant()), "Windows case-insensitive containment failed.");
}
static void RecoveryEvidencePathSets()
{
    var original = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "IISManager.Setup", "receipts", Guid.NewGuid().ToString("N")));
    var stage = Path.Combine(original, "stage");
    var backup = Path.Combine(original, "runtime-backup");
    var ledger = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "IISManager.Console", "console-tasks"));
    var files = new[] { Path.Combine(stage, "wwwroot", "app.js"), Path.Combine(backup, "wwwroot", "app.js"),
        Path.Combine(ledger, "done.json"), Path.Combine(original, "receipt.json"), Path.Combine(stage + "-other", "app.js") };
    foreach (var directory in new[] { stage, backup, ledger })
        Check(files.Where(file => SetupPathBoundary.IsUnder(path: file, root: directory)).Count() == 1,
            "Evidence filtering rejected a child or included an unrelated path.");
    Check(!SetupPathBoundary.IsUnder(path: original, root: stage), "An ancestor escaped the evidence boundary.");
}
static void HasIssue(SetupPlan plan, string code) => Check(SetupPlanValidator.Validate(plan).Any(issue => issue.Code == code), "Missing validation issue: " + code);
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void OpenAndDispose(Stream stream) { using var payload = new PayloadArchive(stream, SetupComponent.Agent, leaveOpen: true); }
static void ThrowsCode(Action action, string code)
{
    try { action(); }
    catch (SetupException exception) when (exception.Code == code) { return; }
    throw new InvalidOperationException("Expected SetupException: " + code);
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected exception: " + typeof(T).Name);
}

sealed record ZipItem(string Path, byte[] Data, int? Attributes = null);

sealed class PayloadFixture(PayloadManifest manifest, ZipItem[] items)
{
    public PayloadManifest Manifest { get; } = manifest;
    public ZipItem[] Items { get; } = items;

    public static PayloadFixture Create(SetupComponent component)
    {
        var app = "IISManager." + component;
        var paths = new[] { app + ".exe", app + ".dll", app + ".deps.json", app + ".runtimeconfig.json", "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "appsettings.json", "NLog.config" };
        if (component == SetupComponent.Console) paths = [.. paths, "wwwroot/index.html"];
        var items = paths.Select(path => new ZipItem(path, Encoding.UTF8.GetBytes("synthetic-test-content:" + path))).ToArray();
        var files = items.Select(item => new PayloadFile(item.Path, item.Data.Length, Convert.ToHexString(SHA256.HashData(item.Data)),
            item.Path is "appsettings.json" or "NLog.config" ? PayloadFileKind.DefaultConfiguration : PayloadFileKind.Runtime)).ToArray();
        return new(new(component.ToString(), "net10.0", "win-x64", true, files), items);
    }

    public PayloadManifest Without(string path) => Manifest with { Files = Manifest.Files.Where(file => file.Path != path).ToArray() };
    public PayloadManifest AddFile(string path) => Manifest with { Files = [.. Manifest.Files, new PayloadFile(path, 0, Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())), PayloadFileKind.Runtime)] };

    public MemoryStream Archive(PayloadManifest? manifest = null, ZipItem[]? extra = null, string? omit = null, string? manifestJson = null)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, new(PayloadArchive.ManifestName, Encoding.UTF8.GetBytes(manifestJson ?? JsonSerializer.Serialize(manifest ?? Manifest, PayloadArchive.JsonOptions))));
            foreach (var item in Items.Where(item => item.Path != omit).Concat(extra ?? [])) Write(archive, item);
        }
        stream.Position = 0;
        return stream;
    }

    private static void Write(ZipArchive archive, ZipItem item)
    {
        var entry = archive.CreateEntry(item.Path, CompressionLevel.NoCompression);
        if (item.Attributes is { } attributes) entry.ExternalAttributes = attributes;
        using var content = entry.Open();
        content.Write(item.Data);
    }
}

sealed class TestWorkspace : IDisposable
{
    public string Directory { get; } = Path.Combine(System.IO.Directory.GetCurrentDirectory(), "work", "setup-selftests", Guid.NewGuid().ToString("N"));
    public TestWorkspace() => System.IO.Directory.CreateDirectory(Directory);
    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
}
