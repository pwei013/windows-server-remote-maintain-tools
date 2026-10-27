using System.Text;
using IISManager.Setup.Backend;
using IISManager.Setup.Core;

namespace IISManager.Setup;

public sealed class ConsoleUiUpdateRecoveryForm : Form
{
    private readonly ConsoleUiUpdateRecoveryBackend backend;
    private readonly string originalReceiptPath;
    private readonly Label title = new() { Text = "核对并完成 Console 界面更新", AutoSize = true, Font = new Font("Segoe UI", 19, FontStyle.Bold) };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(920, 0), ForeColor = Color.FromArgb(149, 51, 35) };
    private readonly TextBox summary = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly TextBox receipt = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly Label receiptCaption = new() { Text = "原更新回执（只读核对）", AutoSize = true };
    private readonly Button check = new() { Text = "重新只读核对", AutoSize = true, MinimumSize = new Size(130, 38) };
    private readonly Button execute = new()
    {
        Text = "确认完成界面更新", AutoSize = true, MinimumSize = new Size(210, 38), Enabled = false,
        BackColor = Color.FromArgb(24, 108, 195), ForeColor = Color.White, FlatStyle = FlatStyle.Flat
    };
    private readonly Button close = new() { Text = "关闭", AutoSize = true, MinimumSize = new Size(98, 38) };
    private ConsoleUiUpdateRecoveryInspection? inspection;
    private bool busy, submitted;

    public ConsoleUiUpdateRecoveryForm(ConsoleUiUpdateRecoveryBackend backend, string receiptPath)
    {
        this.backend = backend; originalReceiptPath = receiptPath;
        Text = "IIS 远程管理 · Console 界面更新收尾";
        StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96); Font = new Font("Segoe UI", 10);
        MinimumSize = new Size(940, 690); Size = new Size(1060, 790);
        BackColor = Color.FromArgb(245, 248, 251);
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 90, Padding = new Padding(28, 24, 20, 12), BackColor = Color.White };
        header.Controls.Add(title);
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 70, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(20, 14, 20, 12), BackColor = Color.White
        };
        footer.Controls.Add(close); footer.Controls.Add(execute); footer.Controls.Add(check);
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(26, 18, 26, 20) };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize)); table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++) table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label
        {
            Text = "先只读核对本次部分更新。通过后审阅待补齐文件及服务状态，再由你确认执行一次收尾。",
            AutoSize = true, MaximumSize = new Size(920, 0), Margin = new Padding(0, 0, 0, 14)
        }, 0, 0);
        table.Controls.Add(summary, 0, 1); table.Controls.Add(status, 0, 2);
        table.Controls.Add(receiptCaption, 0, 3); table.Controls.Add(receipt, 0, 4);
        Controls.Add(table); Controls.Add(footer); Controls.Add(header);
        summary.Name = "UiUpdateRecoveryReview"; summary.AccessibleName = "Console 界面更新收尾核对内容";
        status.Name = "UiUpdateRecoveryStatus"; status.AccessibleName = "核对及执行状态";
        receipt.Name = "UiUpdateRecoveryReceipt"; receipt.AccessibleName = "原更新或关联回执路径";
        execute.Name = "CompleteConsoleUiUpdate"; execute.AccessibleName = "确认完成界面更新并恢复审阅的服务状态";
        check.Name = "InspectConsoleUiUpdate"; check.AccessibleName = "只读核对 Console 部分更新";
        receipt.Text = receiptPath;
        check.Click += async (_, _) => await InspectAsync();
        execute.Click += async (_, _) => await ExecuteAsync(); close.Click += (_, _) => Close();
        Shown += async (_, _) => await InspectAsync();
        FormClosing += (_, args) => { if (busy) { args.Cancel = true; status.Text = "核对或收尾仍在进行，请等待结果。"; } };
        CancelButton = close;
    }

    private async Task InspectAsync()
    {
        if (busy || submitted) return;
        inspection = null; status.Text = "正在只读核对，请稍候…";
        summary.Text = "正在验证原回执、完整旧备份、新载荷、现场内容、权限、配置、账本及服务停止状态…";
        SetBusy(true);
        try
        {
            var result = await Task.Run(() => backend.Inspect(originalReceiptPath));
            if (result is { CanRecover: true, Plan: not null } && result.Issues.Length == 0)
            {
                inspection = result; summary.Text = Describe(result);
                status.Text = "只读核对通过。请审阅文件清单及服务状态，再点击“确认完成界面更新”。";
                status.ForeColor = Color.FromArgb(28, 127, 79);
            }
            else
            {
                summary.Text = "未满足此收尾入口的条件，未提交任何更新。请保留原回执、备份和现场。";
                status.Text = string.Join(Environment.NewLine, result.Issues.Select(i => i.Message + " (" + SafeCode(i.Code) + ")"));
                status.ForeColor = Color.FromArgb(149, 51, 35);
            }
        }
        catch (Exception exception) { ShowFailure(exception, "UI_RECOVERY_INSPECTION_FAILED"); }
        finally { SetBusy(false); }
    }

    private static string Describe(ConsoleUiUpdateRecoveryInspection review)
    {
        var plan = review.Plan!; var text = new StringBuilder();
        text.AppendLine($"固定服务：{plan.ServiceName} · 当前已停止");
        text.AppendLine($"程序目录：{plan.InstallDirectory}");
        text.AppendLine($"数据目录：{plan.DataDirectory}");
        text.AppendLine($"服务身份：{plan.ServiceAccount}");
        text.AppendLine($"原更新编号：{review.OriginalReceiptId:D}");
        if (review.InterruptedReceiptId is not null)
            text.AppendLine($"已核对的归档阶段失败回执：{review.InterruptedReceiptId:D} · 原一次性标记保留");
        text.AppendLine();
        text.AppendLine($"已符合完整新载荷：{review.PreservedRuntime} 个运行文件，核验后保留。");
        text.AppendLine($"待补齐：{review.PendingFiles.Length} 个界面运行文件：");
        foreach (var path in review.PendingFiles) text.AppendLine("  " + path);
        text.AppendLine();
        text.AppendLine("已审阅遗留临时产物（受限复制、读回记录后移除原临时文件）：");
        if (review.TemporaryFiles.Length == 0) text.AppendLine("  无");
        foreach (var file in review.TemporaryFiles) text.AppendLine("  " + file);
        if (review.RetainedFiles is { Length: > 0 })
        {
            text.AppendLine("上次已归档临时产物（保留原位置、当前字节及权限）：");
            foreach (var file in review.RetainedFiles) text.AppendLine("  " + file);
        }
        text.AppendLine();
        text.AppendLine(review.RestoreRunning ? "服务状态：补齐并读回完整程序后，恢复更新前的 Running。" : "服务状态：保持更新前的 Stopped。");
        text.AppendLine("保留当前配置、Agent Token、服务账号、原文件权限、账本及防火墙；不处理 IIS 站点或应用程序池。");
        text.AppendLine("旧回执未保存原配置快照，本次只证明当前配置合法，并核验审阅至执行期间未漂移。");
        text.AppendLine("执行前再核对现场，保留原回执及备份，另存关联回执和一次性标记；结果未知时停止，不自动重试或回滚。");
        return text.ToString();
    }

    private async Task ExecuteAsync()
    {
        if (busy || submitted || inspection is not { CanRecover: true, Plan: not null }) return;
        submitted = true; SetBusy(true); title.Text = "Console 界面更新收尾进度与结果";
        status.Text = "正在执行已审阅的一次收尾，请等待独立读回。";
        summary.Text = "";
        var progress = new Progress<SetupProgress>(item => summary.AppendText($"{DateTime.Now:HH:mm:ss} {item.Message} ({SafeCode(item.Stage)})\r\n"));
        try
        {
            var outcome = await backend.ExecuteAsync(originalReceiptPath, progress, CancellationToken.None);
            status.Text = outcome.Succeeded ? "界面更新收尾完成，文件及服务状态核验通过。" :
                $"收尾未确认成功 · {SafeCode(outcome.State)} / {SafeCode(outcome.Code)}";
            status.ForeColor = outcome.Succeeded ? Color.FromArgb(28, 127, 79) : Color.FromArgb(149, 51, 35);
            summary.AppendText(Environment.NewLine + outcome.Message);
            if (!string.IsNullOrWhiteSpace(outcome.ReceiptPath))
            { receiptCaption.Text = "本次关联回执（原回执保留）"; receipt.Text = outcome.ReceiptPath; }
        }
        catch (Exception exception)
        {
            ShowFailure(exception, "UI_RECOVERY_EXECUTION_UNKNOWN");
            summary.AppendText("\r\n结果未知，保留新旧回执并先只读核对，不再次提交或自动回滚。");
            receiptCaption.Text = "本次关联回执路径未确认；请在以下受限目录只读核对";
            receipt.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IISManager.Setup", "receipts");
        }
        finally { SetBusy(false); }
    }

    private void ShowFailure(Exception exception, string code)
    {
        var failure = SafeFailureSummary.From(exception);
        status.Text = $"无法核实结果，请保留回执并只读核对。 ({SafeCode(code)} / {failure.LeafExceptionType} / 0x{failure.LeafHResult:X8})";
        status.ForeColor = Color.FromArgb(149, 51, 35);
    }

    private static string SafeCode(string value) => new(value.Take(64).Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
    private void SetBusy(bool value)
    {
        busy = value; UseWaitCursor = value; close.Enabled = !value;
        check.Enabled = !value && !submitted; check.Visible = !submitted;
        execute.Enabled = !value && !submitted && inspection is { CanRecover: true, Plan: not null }; execute.Visible = !submitted;
        AcceptButton = execute.Enabled ? execute : null;
    }
}
