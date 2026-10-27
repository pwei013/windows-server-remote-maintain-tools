using System.Text;
using IISManager.Setup.Backend;
using IISManager.Setup.Core;

namespace IISManager.Setup;

public sealed class RecoveryForm : Form
{
    private readonly ConsoleFirewallRecoveryBackend _backend;
    private readonly string _originalReceiptPath;
    private readonly Label _stepLabel = new() { AutoSize = true, ForeColor = Color.FromArgb(79, 109, 133) };
    private readonly Label _title = new() { Text = "核对并完成 Console 防火墙安装", AutoSize = true, Font = new Font("Segoe UI", 19, FontStyle.Bold) };
    private readonly Label _intro = new()
    {
        Text = "此入口用于防火墙地址校验失败后、已注册但仍停止的 Console。先只读核对原回执与现场；通过后可补齐规则、记录安装归属并启动现有服务。程序、配置、Token 及权限均保留。",
        AutoSize = true, MaximumSize = new Size(880, 0), Margin = new Padding(0, 0, 0, 14)
    };
    private readonly TextBox _summary = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.White, Dock = DockStyle.Fill
    };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(880, 0), ForeColor = Color.FromArgb(149, 51, 35) };
    private readonly Label _outcome = new() { AutoSize = true, MaximumSize = new Size(880, 0), Font = new Font("Segoe UI", 12, FontStyle.Bold), Visible = false };
    private readonly ListBox _progress = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true, Visible = false };
    private readonly Label _resultNote = new() { AutoSize = true, MaximumSize = new Size(880, 0), Visible = false };
    private readonly Label _receiptCaption = new() { Text = "原安装回执（只读核对）", AutoSize = true, Margin = new Padding(0, 16, 0, 7) };
    private readonly TextBox _receipt = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly Button _check = new() { Text = "重新只读核对", AutoSize = true, MinimumSize = new Size(128, 36) };
    private readonly Button _execute = new()
    {
        Text = "补齐防火墙并启动 Console", AutoSize = true, MinimumSize = new Size(230, 36),
        BackColor = Color.FromArgb(24, 108, 195), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Enabled = false
    };
    private readonly Button _close = new() { Text = "关闭", AutoSize = true, MinimumSize = new Size(98, 36) };
    private readonly TableLayoutPanel _table = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8 };
    private ConsoleFirewallRecoveryInspection? _inspection;
    private bool _busy;
    private bool _executionStarted;

    public RecoveryForm(ConsoleFirewallRecoveryBackend backend, string receiptPath)
    {
        _backend = backend;
        _originalReceiptPath = receiptPath;
        Text = "IIS 远程管理 · Console 防火墙收尾";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        MinimumSize = new Size(940, 690);
        var desktop = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1100, 850);
        Size = new Size(Math.Max(940, Math.Min(1060, desktop.Width - 40)), Math.Max(690, Math.Min(790, desktop.Height - 40)));
        BackColor = Color.FromArgb(245, 248, 251);

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 103, FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(28, 20, 20, 12), BackColor = Color.White, WrapContents = false
        };
        header.Controls.Add(_stepLabel); header.Controls.Add(_title);
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 66, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(20, 13, 20, 12), BackColor = Color.White, WrapContents = false
        };
        footer.Controls.Add(_close); footer.Controls.Add(_execute); footer.Controls.Add(_check);
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(26, 20, 26, 16) };
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        for (var index = 0; index < 3; index++) _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _table.Controls.Add(_intro, 0, 0); _table.Controls.Add(_summary, 0, 1);
        _table.Controls.Add(_status, 0, 2); _table.Controls.Add(_outcome, 0, 3);
        _table.Controls.Add(_progress, 0, 4); _table.Controls.Add(_resultNote, 0, 5);
        _table.Controls.Add(_receiptCaption, 0, 6); _table.Controls.Add(_receipt, 0, 7);
        content.Controls.Add(_table);
        Controls.Add(content); Controls.Add(footer); Controls.Add(header);
        NameControls();
        _receipt.Text = receiptPath;
        _stepLabel.Text = "只读核对 · 核对通过后再执行";
        _summary.Text = "正在准备只读核对…";
        _check.Click += async (_, _) => await InspectAsync();
        _execute.Click += async (_, _) => await ExecuteAsync();
        _close.Click += (_, _) => Close();
        Shown += async (_, _) => await InspectAsync();
        FormClosing += (_, args) =>
        {
            if (_busy)
            {
                args.Cancel = true;
                ShowStatus(_executionStarted
                    ? "收尾仍在进行，请等待结果；禁止重复提交、自动重试或回滚。"
                    : "只读核对仍在进行，请等待核对结果。");
            }
        };
        CancelButton = _close;
        RefreshEnabled();
    }

    private static string ReceiptDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IISManager.Setup", "receipts");

    private void NameControls()
    {
        (Control Control, string Name, string AccessibleName)[] controls =
        [
            (_summary, "RecoveryReviewSummary", "Console 防火墙收尾核对内容"),
            (_status, "RecoveryValidationIssues", "核对或执行提示"),
            (_outcome, "RecoveryOutcome", "Console 防火墙收尾结果"),
            (_progress, "RecoveryProgress", "Console 防火墙收尾进度"),
            (_receipt, "RecoveryReceiptPath", "安装回执路径"),
            (_check, "InspectConsoleRecovery", "重新只读核对现有 Console"),
            (_execute, "CompleteConsoleFirewall", "补齐防火墙并启动 Console"),
            (_close, "CloseConsoleRecovery", "关闭 Console 防火墙收尾")
        ];
        foreach (var (control, name, accessibleName) in controls)
        { control.Name = name; control.AccessibleName = accessibleName; }
    }

    private async Task InspectAsync()
    {
        if (_busy || _executionStarted) return;
        _inspection = null;
        ShowStatus("");
        _summary.Text = "正在只读核对原回执、现有程序、配置、服务及权限…";
        SetBusy(true);
        try
        {
            var inspection = await Task.Run(() => _backend.Inspect(_originalReceiptPath));
            _receipt.Text = inspection.ReceiptPath;
            if (inspection.CanRecover && inspection.Plan is not null && inspection.OriginalReceiptId != Guid.Empty && inspection.Issues.Length == 0)
            {
                _inspection = inspection;
                _summary.Text = DescribeInspection(inspection);
                _stepLabel.Text = "只读核对通过 · 请审阅现有设置与执行内容";
                ShowStatus("核对通过。请确认下方现有设置，再点击“补齐防火墙并启动 Console”；执行时会再次核对现场。");
                _status.ForeColor = Color.FromArgb(28, 127, 79);
            }
            else
            {
                _stepLabel.Text = "只读核对未通过 · 未执行收尾";
                _summary.Text = "原回执与现有 Console 未满足此收尾入口的条件。请保留现场及回执，按提示只读核对。";
                ShowStatus(inspection.Issues.Length == 0
                    ? "核对结果缺少必要证据，不能执行收尾。 (RECOVERY_INSPECTION_UNVERIFIED)"
                    : string.Join(Environment.NewLine, inspection.Issues.Select(issue => $"• {issue.Message} ({SafeCode(issue.Code)})")));
            }
        }
        catch (Exception exception)
        {
            _stepLabel.Text = "只读核对失败 · 未执行收尾";
            _summary.Text = "无法完成只读核对。请保留现有程序、配置与回执。";
            ShowSafeFailure(exception, "RECOVERY_INSPECTION_FAILED", "无法确认原回执与现有服务状态，未执行收尾。");
        }
        finally { SetBusy(false); }
    }

    private static string DescribeInspection(ConsoleFirewallRecoveryInspection inspection)
    {
        var plan = inspection.Plan!;
        var urlHost = Uri.CheckHostName(plan.HostName) == UriHostNameType.IPv6
            ? "[" + plan.HostName.Trim('[', ']') + "]" : plan.HostName;
        var text = new StringBuilder();
        text.AppendLine("执行内容：添加修正后的 Console 入站防火墙规则、记录安装归属，然后启动现有 Console。");
        text.AppendLine($"固定服务名：{plan.ServiceName} · 已核对为停止状态");
        text.AppendLine($"现有程序目录：{plan.InstallDirectory}");
        text.AppendLine($"现有任务记录目录：{plan.DataDirectory}");
        text.AppendLine($"现有服务身份：{plan.ServiceAccount}");
        text.AppendLine($"现有监听：{plan.ListenAddress}:{plan.Port}");
        text.AppendLine($"现有访问地址：http://{urlHost}:{plan.Port}/");
        text.AppendLine($"现有 Host 白名单：{string.Join("、", plan.AllowedHosts)}");
        text.AppendLine($"原安装编号：{inspection.OriginalReceiptId:D}");
        text.AppendLine($"原安装回执：{inspection.ReceiptPath}");
        text.AppendLine();
        text.AppendLine("防火墙规则：仅指定 Console 程序、服务、TCP 端口，限 Domain / Private。");
        text.AppendLine($"程序：{Path.Combine(plan.InstallDirectory, plan.ExecutableName)}");
        text.AppendLine($"TCP 端口：{plan.Port}");
        text.AppendLine("防火墙允许来源（8 个范围）：");
        foreach (var network in SetupPlanValidator.GetFirewallAllowedNetworks(plan)) text.AppendLine("  " + network);
        text.AppendLine("IPv6 本机回环由程序继续允许；防火墙规则不使用 Windows 拒绝的 ::1/128。");
        text.AppendLine();
        text.AppendLine("现有运行文件、配置、Agent 访问 Token、服务身份及文件权限保留。");
        text.AppendLine("原安装回执保留，本次结果另存新回执；收尾仅提交一次，结果未知时先只读核对。");
        return text.ToString();
    }

    private async Task ExecuteAsync()
    {
        if (_busy || _executionStarted || _inspection is not { CanRecover: true, Plan: not null }) return;
        _executionStarted = true;
        ShowStatus("");
        _stepLabel.Text = "正在收尾 · 请等待独立读回结果";
        _title.Text = "Console 防火墙收尾进度与结果";
        _intro.Text = "正在再次核对现场，然后补齐防火墙并启动现有服务。执行期间不能关闭或重复提交。";
        _summary.Visible = false;
        _table.RowStyles[1].SizeType = SizeType.Absolute; _table.RowStyles[1].Height = 0;
        _table.RowStyles[4].SizeType = SizeType.Percent; _table.RowStyles[4].Height = 100;
        _outcome.Visible = true; _progress.Visible = true; _resultNote.Visible = true;
        _outcome.Text = "正在补齐防火墙，请等待执行结果…";
        _resultNote.Text = "结果未知时保留原回执与本次回执，先只读核对；不会自动重试或回滚。";
        _receiptCaption.Text = "原安装回执；本次结果返回后显示新回执";
        SetBusy(true);
        var progress = new Progress<SetupProgress>(item =>
        {
            // Backend messages use a bounded, reviewed contract; never display raw exception text.
            _progress.Items.Add($"{DateTime.Now:HH:mm:ss}  {item.Message} ({item.Stage})");
            if (_progress.Items.Count > 300) _progress.Items.RemoveAt(0);
            _progress.TopIndex = Math.Max(0, _progress.Items.Count - 1);
        });
        try
        {
            var outcome = await Task.Run(() => _backend.ExecuteAsync(_originalReceiptPath, progress, CancellationToken.None));
            _receiptCaption.Text = string.IsNullOrWhiteSpace(outcome.ReceiptPath)
                ? "本次回执路径未返回，请在以下受限目录中只读核对；原回执保留"
                : "本次收尾回执（可复制路径供核对）；原回执保留";
            _receipt.Text = string.IsNullOrWhiteSpace(outcome.ReceiptPath) ? ReceiptDirectory : outcome.ReceiptPath;
            _outcome.Text = outcome.Succeeded
                ? "Console 收尾完成，服务运行状态检查通过"
                : $"Console 收尾未确认成功 · {SafeCode(outcome.State)} / {SafeCode(outcome.Code)}";
            _outcome.ForeColor = outcome.Succeeded ? Color.FromArgb(28, 127, 79) : Color.FromArgb(153, 57, 35);
            _resultNote.Text = outcome.Succeeded
                ? "此结果表示防火墙、安装归属与 Windows 服务状态检查通过。请继续通过内网浏览器验证控制台访问及 Console 与 Agent 的真实 IIS 查询。"
                : "保留原安装回执、本次回执与现场。先只读核对服务、文件、配置及防火墙，不重复提交或自动回滚。";
            if (!string.IsNullOrWhiteSpace(outcome.Message)) _resultNote.Text = outcome.Message + Environment.NewLine + _resultNote.Text;
            _stepLabel.Text = outcome.Succeeded ? "执行结果 · 保留新旧回执" : "执行结果未确认 · 保留新旧回执并核对现场";
        }
        catch (Exception exception)
        {
            _stepLabel.Text = "执行结果未知 · 保留原回执并只读核对";
            _outcome.Text = "Console 收尾结果未知，请先核对现场";
            _outcome.ForeColor = Color.FromArgb(153, 57, 35);
            _receiptCaption.Text = "本次回执路径未确认，请在以下受限目录中只读核对；原回执保留";
            _receipt.Text = ReceiptDirectory;
            _resultNote.Text = "收尾可能已开始。请保留新旧回执及现有配置；此入口不会自动重试或回滚。";
            ShowSafeFailure(exception, "RECOVERY_EXECUTION_UNKNOWN", "无法确认收尾结果，请先只读核对现场。");
        }
        finally { SetBusy(false); }
    }

    private void ShowSafeFailure(Exception exception, string fallbackCode, string message)
    {
        var code = exception is SetupException setupException ? SafeCode(setupException.Code) : fallbackCode;
        var failure = SafeFailureSummary.From(exception);
        ShowStatus($"{message} ({code} / {failure.LeafExceptionType} / 0x{failure.LeafHResult:X8})");
    }

    private static string SafeCode(string? code) => string.IsNullOrEmpty(code) ? "UNKNOWN" :
        new string(code.Take(64).Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());

    private void ShowStatus(string text)
    {
        _status.Text = text;
        _status.Visible = text.Length != 0;
        _status.ForeColor = Color.FromArgb(149, 51, 35);
    }

    private void SetBusy(bool busy) { _busy = busy; UseWaitCursor = busy; RefreshEnabled(); }

    private void RefreshEnabled()
    {
        _check.Enabled = !_busy && !_executionStarted;
        _check.Visible = !_executionStarted;
        _execute.Enabled = !_busy && !_executionStarted && _inspection is { CanRecover: true, Plan: not null };
        _execute.Visible = !_executionStarted;
        _close.Enabled = !_busy;
        AcceptButton = _execute.Enabled ? _execute : null;
    }
}
