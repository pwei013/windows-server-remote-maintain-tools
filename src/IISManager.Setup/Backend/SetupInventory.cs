using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using IISManager.Setup.Core;
using Microsoft.Win32;

namespace IISManager.Setup.Backend;

internal sealed record InstallationMarker(int Version, string Component, string InstallDirectory, string DataDirectory,
    string HostName, string[] AllowedNetworks, string Account, string State, Guid Generation, PayloadFile[] RuntimeFiles,
    string TransportMode = "Https", string SourceAccessMode = "Allowlist");
internal sealed record FileEvidence(string Path, long Size, string Sha256, string Sddl, string ParentSddl);
internal sealed record InstallationEvidence(SetupPlan Plan, ServiceEvidence Service, InstallationMarker? Marker,
    PayloadFile[] RuntimeFiles, FileEvidence[] ConfigurationFiles, string LedgerDirectory, FileEvidence? MarkerEvidence);

internal static class SetupInventory
{
    internal const string MarkerName = ".iismanager-install.json";
    internal static InstallationEvidence Read(SetupComponent component, string installDirectory, PayloadManifest manifest, string legacyHost = "", string[]? legacyNetworks = null)
        => ReadCore(component, installDirectory, manifest, legacyHost, legacyNetworks, null);

    internal static InstallationEvidence ReadRecoverableConsole(VerifiedPartialConsoleReceipt receipt, PayloadManifest manifest)
        => ReadCore(SetupComponent.Console, receipt.InstallDirectory, manifest, "", null, receipt);

    // Available only to the separately verified, operator-reviewed UI update completion.
    // Ordinary updates still require a Complete marker and the exact old runtime baseline.
    internal static InstallationEvidence ReadPartialConsoleUi(VerifiedPartialConsoleUiReceipt receipt, PayloadManifest manifest)
        => ReadCore(SetupComponent.Console, receipt.InstallDirectory, manifest, "", null, null, receipt);

    private static InstallationEvidence ReadCore(SetupComponent component, string installDirectory, PayloadManifest manifest,
        string legacyHost, string[]? legacyNetworks, VerifiedPartialConsoleReceipt? recovery,
        VerifiedPartialConsoleUiReceipt? uiRecovery = null)
    {
        var root = SetupFileSecurity.CanonicalPath(installDirectory);
        SetupFileSecurity.RejectReparseChain(root, false);
        SetupFileSecurity.RequireTrustedAncestors(root);
        var candidate = new SetupPlan { Component = component, InstallDirectory = root };
        var service = WindowsServiceManager.Read(candidate.ServiceName) ?? throw new SetupException("SERVICE_NOT_FOUND", "固定组件服务不存在，不能按更新处理。");
        if (!service.BinaryPath.Equals(WindowsServiceManager.BinaryPath(candidate), StringComparison.OrdinalIgnoreCase) || service.ServiceType != 16 || service.State is not (1 or 4))
            throw new SetupException("SERVICE_OWNERSHIP_UNVERIFIED", "服务入口、内容根、Production 参数或稳定状态与组件不符，拒绝修改。");
        RejectEnvironmentOverrides(candidate);
        InstallationMarker? marker = null;
        var markerPath = Path.Combine(root, MarkerName);
        if (recovery is not null && (File.Exists(markerPath) || Directory.Exists(markerPath)))
            throw new SetupException("RECOVERY_MARKER_ALREADY_EXISTS", "现有归属记录表明安装已进入后续阶段，不能重放这次防火墙收尾。");
        if (File.Exists(markerPath))
        {
            SetupFileSecurity.RequireTrustedProgramFile(markerPath);
            using var markerStream = ReadLimited(markerPath);
            marker = JsonSerializer.Deserialize<InstallationMarker>(markerStream, PayloadArchive.JsonOptions) ?? throw new SetupException("INSTALL_MARKER_INVALID", "安装归属记录无效。");
            if (marker.Version != 1 || marker.Component != component.ToString() || !marker.InstallDirectory.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                marker.Generation == Guid.Empty || (uiRecovery is null ? marker.State != "Complete" :
                    marker.State != "Updating" || marker.Generation != uiRecovery.Id) || !WindowsServiceManager.SameAccount(marker.Account, service.Account) ||
                marker.RuntimeFiles is null || marker.RuntimeFiles.Length == 0 || marker.AllowedNetworks is null || !SetupPlanValidator.IsHost(marker.HostName) ||
                marker.TransportMode is not ("Https" or "HttpToken") || marker.SourceAccessMode is not ("Allowlist" or "PrivateNetwork"))
                throw new SetupException("INSTALL_MARKER_UNVERIFIED", "安装归属、账号或完成状态不能证明，拒绝自动接续部分安装。");
            PayloadArchive.ValidateManifest(manifest with { LegacyRuntimeFiles = marker.RuntimeFiles }, component);
            if (uiRecovery is not null) uiRecovery.RequireMarker(marker);
        }
        if (uiRecovery is not null && marker is null)
            throw new SetupException("UI_RECOVERY_MARKER_MISSING", "部分更新收尾必须有与原回执相符的 Updating 归属记录。");
        var runtime = recovery is not null || uiRecovery is not null ? manifest.Files.Where(f => f.Kind == PayloadFileKind.Runtime).ToArray() : marker?.RuntimeFiles ?? manifest.LegacyRuntimeFiles ??
            throw new SetupException("LEGACY_BASELINE_MISSING", "无安装归属记录，也没有受支持的旧手工包精确基线。");
        if (runtime.Length == 0) throw new SetupException("LEGACY_BASELINE_MISSING", "旧包精确运行基线为空。");
        foreach (var file in runtime)
        {
            var filePath = SetupFileSecurity.ResolveRelative(root, file.Path);
            if (uiRecovery is not null)
            {
                var observed = uiRecovery.ObservedRuntime.SingleOrDefault(f => f.Path == file.Path);
                if (observed is null)
                {
                    SetupFileSecurity.RejectReparseChain(filePath, true);
                    if (File.Exists(filePath) || Directory.Exists(filePath))
                        throw new SetupException("UI_RECOVERY_RUNTIME_DRIFT", "已核验缺失的新资源路径被占用，拒绝收尾。");
                    continue;
                }
                SetupFileSecurity.RequireTrustedProgramFile(filePath); VerifyFile(filePath, observed);
            }
            else { SetupFileSecurity.RequireTrustedProgramFile(filePath); VerifyFile(filePath, file); }
        }
        var config = Directory.EnumerateFiles(root, "appsettings*.json", SearchOption.TopDirectoryOnly)
            .Append(Path.Combine(root, "NLog.config")).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (config.Length > 32 || !File.Exists(Path.Combine(root, "appsettings.json"))) throw new SetupException("CONFIGURATION_UNVERIFIED", "组件配置缺失或数量异常。");
        foreach (var path in config) SetupFileSecurity.RequireTrustedProgramFile(path);
        var snapshots = config.Select(Snapshot).ToArray();
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "appsettings.json", "appsettings.Production.json", "appsettings.Local.json" })
        {
            var path = Path.Combine(root, name); if (!File.Exists(path)) continue;
            using var stream = ReadLimited(path); using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 64 });
            Flatten(json.RootElement, "", values);
        }
        string Required(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new SetupException("CONFIGURATION_FIELD_MISSING", "现有公开运行配置缺少必要字段，请操作员先核实。");
        string Optional(string key) => values.TryGetValue(key, out var value) ? value ?? "" : "";
        string[] Array(string key) => ReadArray(values, key);
        var section = component + ":";
        var transport = values.ContainsKey(section + "TransportMode") ? Required(section + "TransportMode") : "Https";
        if (transport is not ("Https" or "HttpToken") || marker is not null && marker.TransportMode != transport)
            throw new SetupException("CONFIGURATION_TRANSPORT_UNVERIFIED", "现场连接模式无效或与安装记录漂移，拒绝更新或自动降低连接保护。");
        var https = transport == "Https";
        var sourceAccess = values.ContainsKey(section + "SourceAccessMode") ? Required(section + "SourceAccessMode") : "Allowlist";
        if (sourceAccess is not ("Allowlist" or "PrivateNetwork") || marker is not null && marker.SourceAccessMode != sourceAccess)
            throw new SetupException("CONFIGURATION_SOURCE_POLICY_UNVERIFIED", "现场来源策略无效或与安装记录漂移，不能自动转换已有访问限制。");
        if (sourceAccess == "PrivateNetwork" && marker is null && recovery is null)
            throw new SetupException("PRIVATE_NETWORK_INSTALL_OWNERSHIP_REQUIRED", "自动允许内网来源的更新须有完整安装归属记录，不能转换旧手工包的访问策略。");
        if (!https && marker is null && recovery is null)
            throw new SetupException("HTTP_INSTALL_OWNERSHIP_REQUIRED", "HTTP 更新须有本安装器写入的完整归属记录；旧手工 HTTPS 基线不能自动迁移为 HTTP。");
        var ledgerRaw = Required(section + (component == SetupComponent.Agent ? "StateDirectory" : "TaskDirectory"));
        var ledger = Path.IsPathFullyQualified(ledgerRaw) ? SetupFileSecurity.CanonicalPath(ledgerRaw) : Path.GetFullPath(Path.Combine(root, ledgerRaw));
        SetupFileSecurity.RejectReparseChain(ledger, false);
        if (ledger.Equals(root, StringComparison.OrdinalIgnoreCase)) throw new SetupException("LEDGER_PATH_UNVERIFIED", "账本不能使用程序根目录。");
        var data = marker?.DataDirectory ?? Path.GetDirectoryName(ledger)!;
        if (marker is not null && !ledger.Equals(Path.Combine(marker.DataDirectory, component == SetupComponent.Agent ? "operations" : "console-tasks"), StringComparison.OrdinalIgnoreCase))
            throw new SetupException("CONFIGURATION_MARKER_DRIFT", "数据目录与安装归属记录漂移，拒绝更新。");
        var kind = WindowsServiceManager.SameAccount(service.Account, "LocalSystem") ? ServiceIdentityKind.LocalSystem :
            WindowsServiceManager.SameAccount(service.Account, @"NT SERVICE\" + candidate.ServiceName) ? ServiceIdentityKind.VirtualAccount : ServiceIdentityKind.ExistingAccount;
        if (component == SetupComponent.Console && kind == ServiceIdentityKind.LocalSystem)
            throw new SetupException("CONSOLE_IDENTITY_TOO_PRIVILEGED", "现有控制台使用 LocalSystem，首版拒绝自动升级；请操作员先迁移至最小权限身份，不由安装器更改账号。");
        if (!int.TryParse(Required(section + "Port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
            throw new SetupException("CONFIGURATION_PORT_INVALID", "原组件端口不在支持范围。");
        var hosts = component == SetupComponent.Console ? Array("Console:AllowedHosts") : [];
        var host = marker?.HostName ?? (SetupPlanValidator.IsHost(legacyHost) ? legacyHost : hosts.FirstOrDefault(SetupPlanValidator.IsHost) ?? "");
        // A legacy Agent has no access-name setting. Inspect returns the empty field for the wizard;
        // execution requires the explicitly supplied preflight-only name and never persists it to JSON.
        var networks = component == SetupComponent.Console ? Array("Console:AllowedClientNetworks") :
            sourceAccess == "PrivateNetwork" || !https || values.ContainsKey("Agent:AllowedConsoleNetworks")
                ? Array("Agent:AllowedConsoleNetworks") : marker?.AllowedNetworks ?? legacyNetworks ?? [];
        var servers = component == SetupComponent.Console ? ReadServers(values, transport) : [];
        var plan = candidate with { Mode = SetupMode.Update, DataDirectory = data, HostName = host, Port = port,
            TransportMode = transport, AgentAccessToken = component == SetupComponent.Agent && !https ? Required("Agent:AccessToken") : "",
            SourceAccessMode = sourceAccess,
            IdentityKind = kind, AccountName = kind == ServiceIdentityKind.ExistingAccount ? service.Account : "",
            ServerCertificateThumbprint = https ? Required(section + "ServerCertificateThumbprint") : Optional(section + "ServerCertificateThumbprint"),
            ClientCertificateThumbprint = component == SetupComponent.Console && https ? Required("Console:AgentClientCertificateThumbprint") : "",
            AllowedClientThumbprints = component == SetupComponent.Agent ? Array("Agent:AllowedClientThumbprints") : [],
            AllowedSiteRoots = component == SetupComponent.Agent ? Array("Agent:AllowedSiteRoots") : [],
            ListenAddress = component == SetupComponent.Console ? Required("Console:ListenAddress") : "0.0.0.0",
            AllowedHosts = hosts, AllowedNetworks = networks, Servers = servers };
        var effectiveNetworks = SetupPlanValidator.GetEffectiveAllowedNetworks(plan);
        if (recovery is not null && (component != SetupComponent.Console || transport != "HttpToken" || sourceAccess != "PrivateNetwork" ||
            service.State != 1 || !WindowsServiceManager.SameAccount(service.Account, recovery.Service.Account) ||
            !service.BinaryPath.Equals(recovery.Service.BinaryPath, StringComparison.Ordinal) ||
            service.ServiceType != recovery.Service.ServiceType || service.StartType != recovery.Service.StartType))
            throw new SetupException("RECOVERY_CONFIGURATION_OR_SERVICE_DRIFT", "当前控制台连接模式、来源策略或服务状态与原失败回执不符，拒绝收尾。");
        if (marker is not null && !effectiveNetworks.SequenceEqual(marker.AllowedNetworks, StringComparer.OrdinalIgnoreCase))
            throw new SetupException("CONFIGURATION_MARKER_DRIFT", "来源网络与安装归属记录漂移，拒绝更新。");
        if (component == SetupComponent.Agent && (Required("Agent:Mode") != "Windows" || kind == ServiceIdentityKind.VirtualAccount || https && plan.AllowedClientThumbprints.Length == 0 || plan.AllowedSiteRoots.Length == 0 ||
                !https && (!SetupPlanValidator.IsAccessToken(plan.AgentAccessToken) || effectiveNetworks.Length == 0)) ||
            component == SetupComponent.Console && (Required("Console:AllowDevelopmentHttp") != "False" && Required("Console:AllowDevelopmentHttp") != "false" || hosts.Length == 0 || servers.Length == 0 || effectiveNetworks.Length == 0))
            throw new SetupException("CONFIGURATION_PRODUCTION_UNVERIFIED", "原组件无法证明安全 Production 策略，拒绝更新。");
        if (https && (!SetupPlanValidator.IsThumbprint(plan.ServerCertificateThumbprint) || component == SetupComponent.Console && !SetupPlanValidator.IsThumbprint(plan.ClientCertificateThumbprint)))
            throw new SetupException("CONFIGURATION_CERTIFICATE_INVALID", "原配置证书指纹无效。");
        if (effectiveNetworks.Any(n => !SetupPlanValidator.IsNetwork(n)) ||
            component == SetupComponent.Agent && (https && plan.AllowedClientThumbprints.Any(t => !SetupPlanValidator.IsThumbprint(t)) || plan.AllowedSiteRoots.Any(p => !SetupPlanValidator.IsWindowsDirectory(p, allowDriveRoot: true))) ||
            component == SetupComponent.Console && (plan.AllowedHosts.Any(h => !SetupPlanValidator.IsHost(h)) || !System.Net.IPAddress.TryParse(plan.ListenAddress, out _)))
            throw new SetupException("CONFIGURATION_PRODUCTION_UNVERIFIED", "现场主机、网络、目录或证书白名单无效，拒绝更新。");
        CheckKnownTree(root, runtime, config, ledger, uiRecovery?.TemporaryFiles.Select(f => f.Path));
        CheckLogConfiguration(Path.Combine(root, "NLog.config"));
        return new(plan, service, marker, runtime, snapshots, ledger, marker is null ? null : Snapshot(markerPath));
    }
    internal static FileEvidence Snapshot(string path)
    {
        SetupFileSecurity.RejectReparseChain(path, false);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 256L * 1024 * 1024) throw new SetupException("FILE_SIZE_UNSUPPORTED", "文件超出首版处理范围。");
        return new(path, stream.Length, Convert.ToHexString(SHA256.HashData(stream)), SetupFileSecurity.ReadSddl(path), SetupFileSecurity.ReadSddl(Path.GetDirectoryName(path)!));
    }
    internal static void VerifyFile(string path, PayloadFile file)
    {
        if (!File.Exists(path)) throw new SetupException("RUNTIME_FILE_MISSING", "基线运行文件缺失，拒绝覆盖未知现场。");
        var actual = Snapshot(path);
        if (actual.Size != file.Size || !actual.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new SetupException("RUNTIME_BASELINE_DRIFT", "运行文件大小或 SHA256 与归属基线不符，拒绝更新。");
    }
    internal static void VerifySnapshots(IEnumerable<FileEvidence> files)
    {
        foreach (var before in files)
        {
            var after = Snapshot(before.Path);
            if (before.Size != after.Size || before.Sha256 != after.Sha256) throw new SetupException("CONFIGURATION_DRIFT", "配置内容在执行前后漂移，停止写入。");
            SetupFileSecurity.RequireSameDescriptor(before.Sddl, after.Sddl);
            SetupFileSecurity.RequireSameDescriptor(before.ParentSddl, after.ParentSddl);
        }
    }
    internal static void VerifyRecoveryTree(InstallationEvidence existing)
    {
        var root = existing.Plan.InstallDirectory;
        var actualConfigurations = Directory.EnumerateFiles(root, "appsettings*.json", SearchOption.TopDirectoryOnly)
            .Append(Path.Combine(root, "NLog.config")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualConfigurations.SetEquals(existing.ConfigurationFiles.Select(f => f.Path)))
            throw new SetupException("RECOVERY_CONFIGURATION_SET_DRIFT", "控制台配置文件集合在审查后变化，拒绝收尾。");
        CheckKnownTree(root, existing.RuntimeFiles, existing.ConfigurationFiles.Select(f => f.Path).ToArray(), existing.LedgerDirectory);
    }
    internal static void CheckLedger(string directory)
    {
        SetupFileSecurity.RejectReparseChain(directory, false);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id) || id == Guid.Empty)
                throw new SetupException("LEDGER_UNVERIFIED", "账本文件名无效，拒绝更新。");
            using var stream = ReadLimited(path); using var document = JsonDocument.Parse(stream);
            var state = document.RootElement.GetProperty("state").GetString();
            if (state is "queued" or "running" or "unknown") throw new SetupException("TASKS_UNFINISHED", "账本存在排队、运行或未知任务，请先由操作员核实，不清空或重放。");
            if (state is not ("succeeded" or "failed" or "partial" or "expired")) throw new SetupException("LEDGER_UNVERIFIED", "账本状态无法核实，拒绝更新。");
            if (document.RootElement.GetProperty("request").GetProperty("operationId").GetGuid() != id)
                throw new SetupException("LEDGER_UNVERIFIED", "账本请求编号与文件名不符。");
        }
    }
    internal static FileStream LockStoppedLedger(SetupComponent component, string ledger) => new(Path.Combine(ledger,
        component == SetupComponent.Agent ? ".owner.lock" : ".console.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    internal static FileStream ReadLimited(string path)
    {
        SetupFileSecurity.RejectReparseChain(path, false);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 4 * 1024 * 1024) { stream.Dispose(); throw new SetupException("JSON_SIZE_UNSUPPORTED", "配置、归属或账本记录过大，拒绝处理。"); }
        return stream;
    }
    internal static void Flatten(JsonElement element, string prefix, Dictionary<string, string?> values)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new SetupException("CONFIGURATION_DUPLICATED", "配置存在重复键，拒绝解释歧义。");
                Flatten(property.Value, prefix.Length == 0 ? property.Name : prefix + ":" + property.Name, values);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        { values[prefix] = null; var i = 0; foreach (var item in element.EnumerateArray()) Flatten(item, prefix + ":" + i++, values); }
        else values[prefix] = element.ValueKind == JsonValueKind.Null ? null : element.ToString();
    }
    private static string[] ReadArray(Dictionary<string, string?> values, string key)
    {
        var items = values.Where(pair => pair.Key.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase) && !pair.Key[(key.Length + 1)..].Contains(':'))
            .Select(pair => (Index: int.TryParse(pair.Key[(key.Length + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : -1, Value: pair.Value)).OrderBy(x => x.Index).ToArray();
        if (items.Where((item, index) => item.Index != index || string.IsNullOrWhiteSpace(item.Value)).Any()) throw new SetupException("CONFIGURATION_ARRAY_UNVERIFIED", "配置数组索引或值无效。");
        return items.Select(x => x.Value!).ToArray();
    }
    private static SetupServer[] ReadServers(Dictionary<string, string?> values, string transport)
    {
        var ids = values.Keys.Where(k => k.StartsWith("Console:Servers:", StringComparison.OrdinalIgnoreCase)).Select(k => k.Split(':')[2]).Distinct().OrderBy(k => int.TryParse(k, out var n) ? n : -1).ToArray();
        var result = new List<SetupServer>();
        for (var index = 0; index < ids.Length; index++)
        {
            if (ids[index] != index.ToString(CultureInfo.InvariantCulture)) throw new SetupException("CONFIGURATION_SERVER_UNVERIFIED", "Agent 配置索引无效。");
            string Get(string name) => values.TryGetValue("Console:Servers:" + ids[index] + ":" + name, out var value) && value is not null ? value : throw new SetupException("CONFIGURATION_SERVER_UNVERIFIED", "Agent 配置字段缺失。");
            string Optional(string name) => values.TryGetValue("Console:Servers:" + ids[index] + ":" + name, out var value) ? value ?? "" : "";
            if (values.TryGetValue("Console:Servers:" + ids[index] + ":Simulation", out var sim) && !string.Equals(sim, "false", StringComparison.OrdinalIgnoreCase)) throw new SetupException("CONFIGURATION_SERVER_UNVERIFIED", "Production Agent 不能处于模拟模式。");
            var server = new SetupServer(Get("Id"), Get("Name"), Get("AgentUrl"), Optional("ServerCertificateThumbprint"), Optional("AccessToken"));
            if (!SetupPlanValidator.IsAgentUrl(server.AgentUrl, transport) || (transport == "Https"
                    ? !SetupPlanValidator.IsThumbprint(server.ServerCertificateThumbprint) : !SetupPlanValidator.IsAccessToken(server.AccessToken)))
                throw new SetupException("CONFIGURATION_SERVER_UNVERIFIED", "原 Agent 地址或连接认证配置无效。");
            result.Add(server);
        }
        return result.ToArray();
    }
    internal static void RejectEnvironmentOverrides(SetupPlan plan)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + plan.ServiceName, writable: false);
        var variables = Environment.GetEnvironmentVariables();
        if (key?.GetValue("Environment") is not null || variables.Keys.Cast<string>().Any(k => k.StartsWith(plan.Component + "__", StringComparison.OrdinalIgnoreCase) || k.Equals("AllowedHosts", StringComparison.OrdinalIgnoreCase) ||
            new[] { "DOTNET_", "ASPNETCORE_" }.Any(prefix => k.StartsWith(prefix + plan.Component + "__", StringComparison.OrdinalIgnoreCase) || k.Equals(prefix + "AllowedHosts", StringComparison.OrdinalIgnoreCase))) ||
            new[] { "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT" }.Any(name => variables[name] is string value && !string.IsNullOrWhiteSpace(value) && !value.Equals("Production", StringComparison.Ordinal)))
            throw new SetupException("CONFIGURATION_ENVIRONMENT_UNVERIFIED", "服务或本机存在相关环境覆盖，首版不能证明实际配置，拒绝按 JSON 猜测。");
    }
    internal static void CheckKnownTree(string root, PayloadFile[] runtime, string[] configs, string ledger, IEnumerable<string>? extraKnown = null)
    {
        var known = runtime.Select(f => SetupFileSecurity.ResolveRelative(root, f.Path)).Concat(configs).Append(Path.Combine(root, MarkerName)).Append(Path.Combine(root, "PUBLISH-FILES.json"))
            .Concat(extraKnown ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        void Walk(string directory)
        {
            SetupFileSecurity.RejectReparseChain(directory, false);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                SetupFileSecurity.RejectReparseChain(path, false);
                if (Directory.Exists(path))
                {
                    if (path.Equals(Path.Combine(root, "logs"), StringComparison.OrdinalIgnoreCase) || SetupFileSecurity.Under(path: ledger, root: path)) continue;
                    Walk(path);
                }
                else if (!known.Contains(path)) throw new SetupException("DIRECTORY_OWNERSHIP_UNVERIFIED", "组件目录存在未登记文件，拒绝覆盖非本组件目录。");
            }
        }
        Walk(root);
    }
    private static void CheckLogConfiguration(string path)
    {
        using var stream = ReadLimited(path);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        var xml = XDocument.Load(reader);
        if (xml.Descendants().Any(e => e.Name.LocalName is "include" or "extensions")) throw new SetupException("LOG_CONFIGURATION_UNVERIFIED", "自定义日志扩展或包含文件不能证明写入边界，请操作员准备。");
        foreach (var attribute in xml.Descendants().Attributes().Where(a => a.Name.LocalName is "fileName" or "archiveFileName"))
        {
            const string prefix = "${basedir}/logs/";
            var suffix = attribute.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? attribute.Value[prefix.Length..].Replace("${shortdate}", "2000-01-01", StringComparison.Ordinal) : "";
            if (!attribute.Value.StartsWith("${basedir}/logs/", StringComparison.OrdinalIgnoreCase) ||
                !PayloadArchive.IsSafeRelativePath(suffix) || suffix.Contains("${", StringComparison.Ordinal))
                throw new SetupException("LOG_CONFIGURATION_UNVERIFIED", "首版仅支持组件 logs 内的日志路径，拒绝猜测其他目录权限。");
        }
    }
}
