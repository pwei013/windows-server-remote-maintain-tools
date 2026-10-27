using System.Text.Json;

namespace IISManager.Setup.Core;

public static class SetupConfiguration
{
    public static string CreateLocalConfiguration(SetupPlan plan)
    {
        if (plan.Mode != SetupMode.Install) throw new SetupException("UPDATE_CONFIGURATION_FORBIDDEN", "更新保留现场配置，不生成替换配置。");
        var issues = SetupPlanValidator.Validate(plan);
        if (issues.Length != 0) throw new SetupException(issues[0].Code, issues[0].Message);
        string Thumb(string value) => value.Replace(" ", "").ToUpperInvariant();
        string Child(string value) => plan.DataDirectory.TrimEnd('\\', '/') + "\\" + value;
        var https = plan.TransportMode == "Https";
        var networks = plan.SourceAccessMode == "PrivateNetwork" ? [] : plan.AllowedNetworks;
        object result = plan.Component == SetupComponent.Agent
            ? new { Agent = new { Mode = "Windows", plan.TransportMode, plan.SourceAccessMode, plan.Port, StateDirectory = Child("operations"),
                AllowedSiteRoots = plan.AllowedSiteRoots, ReservedPorts = new[] { 7443, 7444, plan.Port }.Distinct().ToArray(),
                AccessToken = https ? "" : plan.AgentAccessToken.ToUpperInvariant(), AllowedConsoleNetworks = networks,
                ServerCertificateThumbprint = https ? Thumb(plan.ServerCertificateThumbprint) : "",
                AllowedClientThumbprints = https ? plan.AllowedClientThumbprints.Select(Thumb).Distinct(StringComparer.Ordinal).ToArray() : [] } }
            : (object)new { AllowedHosts = string.Join(';', plan.AllowedHosts), Console = new {
                plan.TransportMode, plan.SourceAccessMode, plan.ListenAddress, plan.Port, plan.AllowedHosts, ServerCertificateThumbprint = https ? Thumb(plan.ServerCertificateThumbprint) : "",
                AgentClientCertificateThumbprint = https ? Thumb(plan.ClientCertificateThumbprint) : "", AllowDevelopmentHttp = false,
                AllowedClientNetworks = networks, TaskDirectory = Child("console-tasks"), AgentTimeoutSeconds = 30,
                Servers = plan.Servers.Select(s => new { s.Id, s.Name, s.AgentUrl,
                    ServerCertificateThumbprint = https ? Thumb(s.ServerCertificateThumbprint) : "",
                    AccessToken = https ? "" : s.AccessToken.ToUpperInvariant(), Simulation = false }).ToArray() } };
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }
}
