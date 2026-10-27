using IISManager.Setup.Backend;
using IISManager.Setup.Core;

namespace IISManager.Setup;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var startupPhase = "TemporaryDirectory";
        try
        {
            SetupStartupEnvironment.PrepareTemporaryDirectory(Path.GetTempPath());
            startupPhase = "WindowsFormsInitialization";
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, eventArgs) => ShowSafeError(eventArgs.Exception, "UI_RESULT_UNKNOWN",
                "界面处理异常，请保留安装回执并只读核对现场。不要重新提交或自动回滚。");
            startupPhase = "CreateInstallerWindow";
            var payloads = new EmbeddedPayloadSource();
            if (args.Length == 0)
                Application.Run(new SetupForm(new WindowsSetupBackend(payloads), new AgentUninstallBackend()));
            else if (args.Length == 3 && args[0] == "--complete-console-firewall" && args[1] == "--receipt" &&
                     !string.IsNullOrWhiteSpace(args[2]))
                Application.Run(new RecoveryForm(new ConsoleFirewallRecoveryBackend(payloads), args[2]));
            else if (args.Length == 3 && args[0] == "--complete-console-ui-update" && args[1] == "--receipt" &&
                     !string.IsNullOrWhiteSpace(args[2]))
                Application.Run(new ConsoleUiUpdateRecoveryForm(new ConsoleUiUpdateRecoveryBackend(payloads), args[2]));
            else if (args.Length == 3 && args[0] == "--finish-console-ui-update" && args[1] == "--receipt" &&
                     !string.IsNullOrWhiteSpace(args[2]))
                Application.Run(new ConsoleUiUpdateRecoveryForm(new ConsoleUiUpdateRecoveryBackend(payloads, finishInterrupted: true), args[2]));
            else
            {
                Environment.ExitCode = 2;
                MessageBox.Show("启动参数无效。普通安装请直接打开安装器；Console 防火墙收尾请使用：\r\n\r\n" +
                    "IISManager.Setup.exe --complete-console-firewall --receipt <原安装回执路径>\r\n\r\n" +
                    "Console 部分界面更新收尾：\r\nIISManager.Setup.exe --complete-console-ui-update --receipt <原更新回执路径>\r\n\r\n" +
                    "已核对临时文件归档差异后的单独完成入口：\r\nIISManager.Setup.exe --finish-console-ui-update --receipt <收尾失败的关联回执路径>\r\n\r\n" +
                    "此入口先只读核对现有安装，再由你点击执行。",
                    "IIS 远程管理 · 安装向导", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            var code = exception is SetupException setup ? setup.Code : "INSTALLER_STARTUP_FAILED";
            ShowSafeError(exception, code,
                $"安装向导无法正常运行，失败阶段：{startupPhase}。请核对 Windows 环境、当前用户临时目录与安装文件。若安装已经开始，请保留回执并先核对现场。");
        }
    }

    private static void ShowSafeError(Exception exception, string code, string message)
    {
        var failure = SafeFailureSummary.From(exception);
        MessageBox.Show($"{message}\r\n\r\n{code} / {failure.LeafExceptionType} / 0x{failure.LeafHResult:X8}",
            "IIS 远程管理 · 安装向导", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
