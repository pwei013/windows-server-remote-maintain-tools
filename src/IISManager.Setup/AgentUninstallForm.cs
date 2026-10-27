using System.Text;
using IISManager.Setup.Core;

namespace IISManager.Setup;

/// <summary>Operator-reviewed removal of the fixed Agent service; no file or IIS resource cleanup.</summary>
public sealed class AgentUninstallForm : Form
{
    private readonly IAgentUninstallBackend _backend;
    private readonly TextBox _directory = new() { Dock = DockStyle.Fill };
    private readonly Button _browse = new() { Text = "浏览…", AutoSize = true };
    private readonly TextBox _summary = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White,
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle
    };
    private readonly CheckBox _confirm = new()
    {
        Text = "我已核对以上 Agent，确认停止并卸载 IISManager.Agent 服务，保留全部文件和数据。",
        AutoSize = true, MaximumSize = new Size(880, 0), Margin = new Padding(0, 12, 0, 12)
    };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(880, 0), ForeColor = Color.FromArgb(149, 51, 35) };
    private readonly Label _outcome = new() { AutoSize = true, MaximumSize = new Size(880, 0), Font = new Font("Segoe UI", 12, FontStyle.Bold), Visible = false };
    private readonly ListBox _progress = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true, Visible = false };
    private readonly TextBox _receipt = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly Label _receiptCaption = new() { Text = "卸载回执（执行后显示，可复制路径）", AutoSize = true, Margin = new Padding(0, 12, 0, 7) };
    private readonly Label _resultNote = new() { AutoSize = true, MaximumSize = new Size(880, 0), Visible = false };
    private readonly Button _check = new() { Text = "只读核对 Agent", AutoSize = true, MinimumSize = new Size(145, 36) };
    private readonly Button _execute = new()
    {
        Text = "停止并卸载 Agent 服务", AutoSize = true, MinimumSize = new Size(202, 36),
        BackColor = Color.FromArgb(153, 57, 35), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Enabled = false
    };
    private readonly Button _close = new() { Text = "返回安装向导", AutoSize = true, MinimumSize = new Size(128, 36) };
    private readonly Label _step = new() { Text = "只读核对 · 审阅后再执行", AutoSize = true, ForeColor = Color.FromArgb(79, 109, 133) };
    private readonly TableLayoutPanel _table = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10 };
    private AgentUninstallInspection? _inspection;
    private bool _busy;
    private bool _executionStarted;

    public AgentUninstallForm(IAgentUninstallBackend backend, string installDirectory)
    {
        _backend = backend;
        Text = "IIS 远程管理 · 卸载 Agent 服务";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10); MinimumSize = new Size(940, 720);
        var desktop = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1100, 850);
        Size = new Size(Math.Max(940, Math.Min(1060, desktop.Width - 40)), Math.Max(720, Math.Min(820, desktop.Height - 40)));
        BackColor = Color.FromArgb(245, 248, 251);
        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 103, FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(28, 20, 20, 12), BackColor = Color.White, WrapContents = false
        };
        header.Controls.Add(_step);
        header.Controls.Add(new Label { Text = "卸载已有 Agent 服务", AutoSize = true, Font = new Font("Segoe UI", 19, FontStyle.Bold) });
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 66, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(20, 13, 20, 12), BackColor = Color.White, WrapContents = false
        };
        footer.Controls.Add(_close); footer.Controls.Add(_execute); footer.Controls.Add(_check);
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(26, 20, 26, 16), AutoScroll = true };
        for (var i = 0; i < 10; i++) _table.RowStyles.Add(new RowStyle(i == 2 ? SizeType.Percent : SizeType.AutoSize, i == 2 ? 100 : 0));
        _table.RowStyles[6].SizeType = SizeType.Absolute; _table.RowStyles[6].Height = 0;
        _table.Controls.Add(new Label
        {
            Text = "停止并移除固定的 IISManager.Agent 服务后，Console 将无法连接此 Agent。程序、配置、日志、操作账本、安装归属记录、防火墙规则及证书均保留；IIS 站点、应用池和其他 Windows 服务不作修改。",
            AutoSize = true, MaximumSize = new Size(880, 0), Margin = new Padding(0, 0, 0, 12)
        }, 0, 0);
        var directoryRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 12) };
        directoryRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        directoryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        directoryRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        directoryRow.Controls.Add(new Label { Text = "已有 Agent 程序目录", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 0) }, 0, 0);
        directoryRow.Controls.Add(_directory, 1, 0); directoryRow.Controls.Add(_browse, 2, 0);
        _table.Controls.Add(directoryRow, 0, 1); _table.Controls.Add(_summary, 0, 2);
        _table.Controls.Add(_confirm, 0, 3); _table.Controls.Add(_status, 0, 4);
        _table.Controls.Add(_outcome, 0, 5); _table.Controls.Add(_progress, 0, 6);
        _table.Controls.Add(_resultNote, 0, 7); _table.Controls.Add(_receiptCaption, 0, 8); _table.Controls.Add(_receipt, 0, 9);
        content.Controls.Add(_table); Controls.Add(content); Controls.Add(footer); Controls.Add(header);
        NameControls();
        _directory.Text = installDirectory;
        _summary.Text = "点击“只读核对 Agent”，读取实际服务、程序目录、身份及任务账本。只读核对不会停止服务。";
        _directory.TextChanged += (_, _) => InvalidateInspection();
        _confirm.CheckedChanged += (_, _) => RefreshEnabled();
        _check.Click += async (_, _) => await InspectAsync();
        _execute.Click += async (_, _) => await ExecuteAsync();
        _close.Click += (_, _) => Close();
        _browse.Click += (_, _) =>
        {
            if (_busy || _executionStarted) return;
            using var picker = new FolderBrowserDialog { Description = "选择已有 Agent 的实际程序目录", UseDescriptionForTitle = true, ShowNewFolderButton = false, SelectedPath = _directory.Text };
            if (picker.ShowDialog(this) == DialogResult.OK) _directory.Text = picker.SelectedPath;
        };
        FormClosing += (_, args) =>
        {
            if (!_busy) return;
            args.Cancel = true;
            ShowStatus(_executionStarted ? "卸载仍在执行，请等待结果并保留回执；不会重复提交或自动回滚。" : "只读核对正在进行，请等待核对结果。");
        };
        CancelButton = _close; RefreshEnabled();
    }

    private static string ReceiptDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IISManager.Setup", "receipts");

    private void NameControls()
    {
        (Control Control, string Name, string AccessibleName)[] controls =
        [
            (_directory, "AgentUninstallDirectory", "已有 Agent 的实际程序目录"),
            (_browse, "BrowseAgentUninstallDirectory", "选择已有 Agent 程序目录"),
            (_summary, "AgentUninstallReview", "Agent 卸载核对内容和保留范围"),
            (_confirm, "ConfirmAgentUninstall", "确认停止并卸载以上 Agent 服务，保留文件数据"),
            (_status, "AgentUninstallIssues", "Agent 卸载核对或执行提示"),
            (_outcome, "AgentUninstallOutcome", "Agent 卸载结果"),
            (_progress, "AgentUninstallProgress", "Agent 卸载进度"),
            (_receipt, "AgentUninstallReceipt", "Agent 卸载回执路径"),
            (_check, "InspectAgentUninstall", "只读核对已有 Agent"),
            (_execute, "ExecuteAgentUninstall", "停止并卸载固定的 IISManager.Agent 服务"),
            (_close, "CloseAgentUninstall", "返回安装向导")
        ];
        foreach (var (control, name, accessibleName) in controls) { control.Name = name; control.AccessibleName = accessibleName; }
    }

    private void InvalidateInspection()
    {
        if (_executionStarted) return;
        _inspection = null; _confirm.Checked = false;
        _step.Text = "只读核对 · 审阅后再执行";
        _summary.Text = "目录已更改，请重新只读核对；先前的审阅结果已失效。";
        ShowStatus(""); RefreshEnabled();
    }

    private async Task InspectAsync()
    {
        if (_busy || _executionStarted) return;
        _inspection = null; _confirm.Checked = false; ShowStatus("");
        _summary.Text = "正在只读核对固定 Agent 服务、实际程序目录、身份及任务账本…";
        SetBusy(true);
        try
        {
            var directory = _directory.Text.Trim();
            var inspection = await Task.Run(() => _backend.Inspect(directory));
            if (inspection.CanUninstall && inspection.ServiceExists && inspection.Issues.Length == 0 &&
                SetupPlanValidator.IsWindowsDirectory(inspection.InstallDirectory) &&
                inspection.Fingerprint is { Length: 64 } && inspection.Fingerprint.All(Uri.IsHexDigit))
            {
                _inspection = inspection;
                _summary.Text = Describe(inspection);
                _step.Text = "只读核对通过 · 请审阅并勾选确认";
                ShowStatus("只读核对已通过。执行时会再次核对现场与任务账本。", true);
            }
            else
            {
                _step.Text = "只读核对未通过 · 未执行卸载";
                _summary.Text = inspection.ServiceExists
                    ? "现有服务尚不满足安全卸载条件。请根据下方状态码先核实现场。"
                    : "未核实到符合条件的 Agent 服务。请核对程序目录及当前 Windows 服务状态。";
                ShowStatus(inspection.Issues.Length == 0
                    ? "核对结果缺少必要证据，不能执行卸载。 (AGENT_UNINSTALL_INSPECTION_UNVERIFIED)"
                    : string.Join(Environment.NewLine, inspection.Issues.Select(issue => $"• {issue.Message} ({SafeCode(issue.Code)})")));
            }
        }
        catch (Exception exception)
        {
            _step.Text = "只读核对失败 · 未执行卸载";
            _summary.Text = "无法核实已有 Agent；未停止服务或修改现场。";
            ShowSafeFailure(exception, "AGENT_UNINSTALL_INSPECTION_FAILED", "请核对实际目录、Windows 环境和读取权限。");
        }
        finally { SetBusy(false); }
    }

    private static string Describe(AgentUninstallInspection review)
    {
        var text = new StringBuilder();
        text.AppendLine("固定服务：IISManager.Agent");
        text.AppendLine($"实际状态：{review.ServiceState}");
        text.AppendLine($"实际服务身份：{review.ServiceAccount}");
        text.AppendLine($"实际程序目录：{review.InstallDirectory}");
        text.AppendLine($"实际数据目录：{review.DataDirectory}");
        text.AppendLine();
        text.AppendLine("执行内容：停止 Agent（原为停止则保持停止），再次核对任务账本，移除固定服务注册并只读确认服务已不存在。");
        text.AppendLine("卸载后 Console 无法连接或管理此 Agent；需要继续使用时须另行安装服务。");
        text.AppendLine();
        text.AppendLine("保留：全部程序文件、配置和 Token、数据、日志、操作账本、安装归属记录及原安装回执。");
        text.AppendLine("保留：防火墙规则、证书、私钥、文件权限及账号。");
        text.AppendLine("不修改：受管 IIS 站点、应用程序、虚拟目录、应用池及其他 Windows 服务。");
        text.AppendLine();
        text.AppendLine("本次结果另存受限卸载回执。卸载超时或结果未知时先只读核实，不自动重试或回滚，也不自动重新启动服务。");
        return text.ToString();
    }

    private async Task ExecuteAsync()
    {
        if (_busy || _executionStarted || !_confirm.Checked || _inspection is not { CanUninstall: true } review) return;
        _executionStarted = true;
        _confirm.Checked = false; _confirm.Visible = false;
        _step.Text = "正在卸载 · 请等待服务移除核对结果";
        ShowStatus("");
        _summary.Visible = false; _table.RowStyles[2].SizeType = SizeType.Absolute; _table.RowStyles[2].Height = 0;
        _table.RowStyles[6].SizeType = SizeType.Percent; _table.RowStyles[6].Height = 100;
        _outcome.Visible = true; _outcome.Text = "正在停止并卸载 Agent 服务…";
        _progress.Visible = true; _resultNote.Visible = true;
        _resultNote.Text = "正在再次核对现场；全部文件与数据保留。请等待结果，不重复提交。";
        SetBusy(true);
        var progress = new Progress<SetupProgress>(item =>
        {
            _progress.Items.Add($"{DateTime.Now:HH:mm:ss}  {item.Message} ({SafeCode(item.Stage)})");
            if (_progress.Items.Count > 300) _progress.Items.RemoveAt(0);
            _progress.TopIndex = Math.Max(0, _progress.Items.Count - 1);
        });
        try
        {
            var request = new AgentUninstallRequest(review.InstallDirectory, review.Fingerprint);
            var outcome = await _backend.UninstallAsync(request, progress, CancellationToken.None);
            _receipt.Text = string.IsNullOrWhiteSpace(outcome.ReceiptPath) ? ReceiptDirectory : outcome.ReceiptPath;
            _receiptCaption.Text = string.IsNullOrWhiteSpace(outcome.ReceiptPath)
                ? "回执路径未返回，请在以下受限目录中只读核对"
                : "本次卸载回执（可复制路径供核对）";
            _outcome.Text = outcome.Succeeded ? "Agent 服务已卸载，服务不存在核对通过"
                : $"Agent 卸载未确认成功 · {SafeCode(outcome.State)} / {SafeCode(outcome.Code)}";
            _outcome.ForeColor = outcome.Succeeded ? Color.FromArgb(28, 127, 79) : Color.FromArgb(153, 57, 35);
            _resultNote.Text = outcome.Succeeded
                ? "Agent 服务注册已移除。全部程序、配置、日志和任务账本保留，防火墙、证书及受管 IIS 资源未作修改；请保留卸载回执。"
                : outcome.State == "Failed"
                ? "服务变更尚未开始。请根据状态码只读核实服务、目录和任务账本，再重新审阅；已有文件与数据保留。"
                : "服务可能已停止或进入删除阶段。请保留卸载回执和全部文件，先只读核实 Windows 服务状态；不重复提交，不自动回滚或重新启动。";
            _step.Text = outcome.Succeeded ? "卸载完成 · 文件与数据保留" : "卸载结果未确认 · 先只读核实现场";
        }
        catch (Exception exception)
        {
            _step.Text = "卸载结果未知 · 请先只读核实";
            _outcome.Text = "无法确认 Agent 卸载结果"; _outcome.ForeColor = Color.FromArgb(153, 57, 35);
            _receipt.Text = ReceiptDirectory;
            _receiptCaption.Text = "回执路径未确认，请在以下受限目录中只读核对";
            _resultNote.Text = "服务操作可能已经开始。请保留回执与全部文件，不重复卸载、不自动回滚或重新启动服务。";
            ShowSafeFailure(exception, "AGENT_UNINSTALL_EXECUTION_UNKNOWN", "请先只读核实实际服务与卸载回执。");
        }
        finally { SetBusy(false); }
    }

    private void ShowSafeFailure(Exception exception, string fallbackCode, string message)
    {
        var code = exception is SetupException setup ? SafeCode(setup.Code) : fallbackCode;
        var failure = SafeFailureSummary.From(exception);
        ShowStatus($"{message} ({code} / {failure.LeafExceptionType} / 0x{failure.LeafHResult:X8})");
    }

    private static string SafeCode(string? code) => string.IsNullOrEmpty(code) ? "UNKNOWN" :
        new string(code.Take(64).Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());

    private void ShowStatus(string message, bool success = false)
    {
        _status.Text = message; _status.Visible = message.Length != 0;
        _status.ForeColor = success ? Color.FromArgb(28, 127, 79) : Color.FromArgb(149, 51, 35);
    }

    private void SetBusy(bool value) { _busy = value; UseWaitCursor = value; RefreshEnabled(); }

    private void RefreshEnabled()
    {
        _directory.Enabled = !_busy && !_executionStarted; _browse.Enabled = !_busy && !_executionStarted;
        _check.Visible = !_executionStarted; _check.Enabled = !_busy && !_executionStarted;
        _confirm.Enabled = !_busy && !_executionStarted && _inspection is { CanUninstall: true };
        _execute.Visible = !_executionStarted;
        _execute.Enabled = !_busy && !_executionStarted && _confirm.Checked && _inspection is { CanUninstall: true };
        _close.Enabled = !_busy;
        // Enter must never submit a destructive operation; the operator clicks the explicit uninstall button.
        AcceptButton = !_executionStarted && !_busy ? _check : null;
    }
}
