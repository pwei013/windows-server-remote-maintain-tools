using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using IISManager.Setup.Core;

namespace IISManager.Setup.Backend;

internal sealed record FirewallEvidence(string Name, string Program, int Port, string Networks);

internal static class SetupFirewall
{
    internal static string RuleName(SetupPlan plan) => "IISManager.Setup." + plan.Component;
    internal static void Preflight(SetupPlan plan, string? exactRuleName = null)
    {
        if (!plan.ConfigureFirewall) return;
        var networks = SetupPlanValidator.GetFirewallAllowedNetworks(plan);
        if (networks.Length == 0 || networks.Any(n => !SetupPlanValidator.IsNetwork(n)))
            throw new SetupException("FIREWALL_NETWORKS_INVALID", "防火墙来源须为固定内网范围或已核验白名单，不能允许全部网络。");
        var policy = Create("HNetCfg.FwPolicy2"); object? rules = null;
        try
        {
            if (Convert.ToInt32(Get(policy, "LocalPolicyModifyState")) != 0 ||
                !Convert.ToBoolean(Get(policy, "FirewallEnabled", 1)) || !Convert.ToBoolean(Get(policy, "FirewallEnabled", 2)))
                throw new SetupException("FIREWALL_POLICY_UNVERIFIED", "Domain/Private 防火墙未启用或本地修改受策略限制；不修改全局防火墙策略。");
            rules = Get(policy, "Rules");
            if (Find(rules!, exactRuleName ?? RuleName(plan)) is { } existing)
            {
                Release(existing);
                throw new SetupException("FIREWALL_RULE_EXISTS", "已存在同名规则，拒绝覆盖；请操作员先核实归属。");
            }
        }
        finally { Release(rules); Release(policy); }
    }
    internal static FirewallEvidence Add(SetupPlan plan, Guid installationId, Action<FirewallEvidence> beforeWrite)
    {
        if (installationId == Guid.Empty) throw new SetupException("FIREWALL_RULE_ID_INVALID", "防火墙规则须使用本次安装回执的唯一编号。");
        var evidence = new FirewallEvidence(RuleName(plan) + "." + installationId.ToString("D"), Path.Combine(plan.InstallDirectory, plan.ExecutableName), plan.Port,
            string.Join(',', SetupPlanValidator.GetFirewallAllowedNetworks(plan).Distinct(StringComparer.OrdinalIgnoreCase)));
        Preflight(plan, evidence.Name);
        var policy = Create("HNetCfg.FwPolicy2"); object? rules = null; object? rule = null; object? read = null;
        try
        {
            rules = Get(policy, "Rules"); rule = Create("HNetCfg.FWRule");
            Set(rule, "Name", evidence.Name); Set(rule, "Description", "IISManager installer: restricted management access");
            Set(rule, "Grouping", "IISManager"); Set(rule, "ServiceName", plan.ServiceName);
            Set(rule, "ApplicationName", evidence.Program); Set(rule, "Protocol", 6); Set(rule, "LocalPorts", evidence.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set(rule, "RemoteAddresses", evidence.Networks); Set(rule, "Direction", 1); Set(rule, "Profiles", 3);
            Set(rule, "Action", 1); Set(rule, "EdgeTraversal", false); Set(rule, "Enabled", true);
            if (Find(rules!, evidence.Name) is { } concurrent)
            {
                Release(concurrent);
                throw new SetupException("FIREWALL_RULE_EXISTS", "本次唯一规则名已存在，拒绝覆盖。");
            }
            beforeWrite(evidence);
            Call(rules!, "Add", rule);
            read = Find(rules!, evidence.Name) ?? throw new SetupException("FIREWALL_READBACK_MISSING", "新增防火墙规则未能独立读回，停止写入。");
            if (!Matches(read, evidence) || !string.Equals(Convert.ToString(Get(read, "ServiceName")), plan.ServiceName, StringComparison.Ordinal)) throw new SetupException("FIREWALL_READBACK_MISMATCH", "防火墙规则程序、端口、来源或配置与计划不符，停止写入。");
            return evidence;
        }
        finally { Release(read); Release(rule); Release(rules); Release(policy); }
    }
    internal static bool ExactRuleExists(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new SetupException("FIREWALL_RULE_ID_INVALID", "只读核验须提供安装回执中的唯一防火墙规则名。");
        var policy = Create("HNetCfg.FwPolicy2"); object? rules = null; object? read = null;
        try
        {
            rules = Get(policy, "Rules");
            read = Find(rules!, name);
            return read is not null;
        }
        finally { Release(read); Release(rules); Release(policy); }
    }
    internal static void RequireNoRulesWithPrefix(string prefix) => RequireRuleNamespace(prefix, null);
    internal static void VerifyRecoveryRule(SetupPlan plan, Guid installationId)
    {
        var evidence = new FirewallEvidence(RuleName(plan) + "." + installationId.ToString("D"), Path.Combine(plan.InstallDirectory, plan.ExecutableName), plan.Port,
            string.Join(',', SetupPlanValidator.GetFirewallAllowedNetworks(plan).Distinct(StringComparer.OrdinalIgnoreCase)));
        RequireRuleNamespace(RuleName(plan), evidence.Name);
        var policy = Create("HNetCfg.FwPolicy2"); object? rules = null; object? read = null;
        try
        {
            if (Convert.ToInt32(Get(policy, "LocalPolicyModifyState")) != 0 ||
                !Convert.ToBoolean(Get(policy, "FirewallEnabled", 1)) || !Convert.ToBoolean(Get(policy, "FirewallEnabled", 2)))
                throw new SetupException("FIREWALL_POLICY_UNVERIFIED", "Domain/Private 防火墙状态或本地修改策略已漂移，停止收尾。");
            rules = Get(policy, "Rules");
            read = Find(rules!, evidence.Name) ?? throw new SetupException("FIREWALL_READBACK_MISSING", "收尾规则未能独立读回，停止后续操作。");
            if (!Matches(read, evidence) || !string.Equals(Convert.ToString(Get(read, "ServiceName")), plan.ServiceName, StringComparison.Ordinal))
                throw new SetupException("FIREWALL_READBACK_MISMATCH", "收尾规则程序、服务、端口或来源已漂移，停止后续操作。");
        }
        finally { Release(read); Release(rules); Release(policy); }
    }
    private static void RequireRuleNamespace(string prefix, string? permittedName)
    {
        var policy = Create("HNetCfg.FwPolicy2"); object? rules = null;
        try
        {
            rules = Get(policy, "Rules");
            foreach (var item in (IEnumerable)rules!)
            {
                if (item is null) continue;
                try
                {
                    var name = Convert.ToString(Get(item, "Name")) ?? "";
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !name.Equals(permittedName, StringComparison.OrdinalIgnoreCase))
                        throw new SetupException("RECOVERY_FIREWALL_NAMESPACE_OCCUPIED", "存在控制台安装器命名空间内的规则，不能覆盖或重放收尾。");
                }
                finally { Release(item); }
            }
        }
        catch (Exception exception) when (exception is not SetupException) { throw ComFailure("ENUMERATE_RULES", exception); }
        finally { Release(rules); Release(policy); }
    }
    private static bool Matches(object rule, FirewallEvidence value) =>
        string.Equals(Convert.ToString(Get(rule, "Name")), value.Name, StringComparison.Ordinal) &&
        string.Equals(Convert.ToString(Get(rule, "ApplicationName")), value.Program, StringComparison.OrdinalIgnoreCase) &&
        Convert.ToInt32(Get(rule, "Protocol")) == 6 && Convert.ToString(Get(rule, "LocalPorts")) == value.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
        Convert.ToInt32(Get(rule, "Direction")) == 1 && Convert.ToInt32(Get(rule, "Profiles")) == 3 && Convert.ToInt32(Get(rule, "Action")) == 1 &&
        Convert.ToBoolean(Get(rule, "Enabled")) && !Convert.ToBoolean(Get(rule, "EdgeTraversal")) &&
        Networks(Convert.ToString(Get(rule, "RemoteAddresses")) ?? "").SequenceEqual(Networks(value.Networks), StringComparer.OrdinalIgnoreCase);
    // Firewall COM may return dotted IPv4 masks or canonical network addresses. Compare the exact address sets.
    private static string[] Networks(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(NormalizeNetwork)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    private static string NormalizeNetwork(string value)
    {
        var parts = value.Split('/');
        if (parts.Length > 2 || !System.Net.IPAddress.TryParse(parts[0], out var address)) return "INVALID";
        var bytes = address.GetAddressBytes(); var max = bytes.Length * 8; var prefix = max;
        if (parts.Length == 2 && !int.TryParse(parts[1], out prefix))
        {
            if (bytes.Length != 4 || !System.Net.IPAddress.TryParse(parts[1], out var mask) || mask.GetAddressBytes().Length != 4) return "INVALID";
            var bitString = string.Concat(mask.GetAddressBytes().Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
            prefix = bitString.TakeWhile(c => c == '1').Count();
            if (bitString[prefix..].Contains('1')) return "INVALID";
        }
        if (prefix is <= 0 || prefix > max) return "INVALID";
        for (var bit = prefix; bit < max; bit++) bytes[bit / 8] &= (byte)~(1 << (7 - bit % 8));
        return new System.Net.IPAddress(bytes) + "/" + prefix;
    }
    private static object? Find(object rules, string name)
    {
        try
        {
            foreach (var item in (IEnumerable)rules)
            {
                if (item is null) continue;
                var retained = false;
                try
                {
                    if (string.Equals(Convert.ToString(Get(item, "Name")), name, StringComparison.OrdinalIgnoreCase))
                    {
                        retained = true;
                        return item;
                    }
                }
                finally { if (!retained) Release(item); }
            }
            return null;
        }
        catch (Exception exception) when (exception is not SetupException)
        {
            throw ComFailure("ENUMERATE_RULES", exception);
        }
    }
    private static object Create(string name) => ComOperation(name == "HNetCfg.FwPolicy2" ? "CREATE_POLICY" : "CREATE_RULE", () =>
        Activator.CreateInstance(Type.GetTypeFromProgID(name, throwOnError: true)!)
        ?? throw new SetupException("FIREWALL_COM_UNAVAILABLE", "正式 Windows 防火墙 COM API 不可用。"));
    private static object? Get(object value, string property, params object[] index) => ComOperation("GET_" + property.ToUpperInvariant(), () =>
        value.GetType().InvokeMember(property, BindingFlags.GetProperty, null, value, index));
    private static void Set(object value, string property, object argument) => ComOperation("SET_" + property.ToUpperInvariant(), () =>
        value.GetType().InvokeMember(property, BindingFlags.SetProperty, null, value, [argument]));
    private static void Call(object value, string method, params object[] arguments) => ComOperation("CALL_" + method.ToUpperInvariant(), () =>
        value.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, value, arguments));
    private static T ComOperation<T>(string operation, Func<T> action)
    {
        try { return action(); }
        catch (Exception exception) when (exception is not SetupException)
        {
            throw ComFailure(operation, exception);
        }
    }
    // Operations and property names are internal constants; never include argument values or exception messages.
    private static SetupException ComFailure(string operation, Exception exception) => new("FIREWALL_COM_" + operation + "_FAILED",
        "Windows 防火墙操作失败（" + operation + "）；请根据安装回执先只读核实实际状态。", exception);
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
