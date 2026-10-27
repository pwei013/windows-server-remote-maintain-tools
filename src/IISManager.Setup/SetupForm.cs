using System.Security;
using System.Security.Cryptography;
using System.Text;
using IISManager.Setup.Core;

namespace IISManager.Setup;

public sealed class SetupForm : Form
{
    private readonly ISetupBackend _backend;
    private readonly IAgentUninstallBackend? _uninstallBackend;
    private readonly Panel _content = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(26, 20, 26, 16) };
    private readonly Label _stepLabel = new() { AutoSize = true, ForeColor = Color.FromArgb(79, 109, 133) };
    private readonly Label _titleLabel = new() { AutoSize = true, Font = new Font("Segoe UI", 19, FontStyle.Bold) };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(750, 0), ForeColor = Color.FromArgb(149, 51, 35) };
    private readonly Button _back = new() { Text = "上一步", AutoSize = true, MinimumSize = new Size(98, 36) };
    private readonly Button _next = new() { Text = "检查并继续", AutoSize = true, MinimumSize = new Size(128, 36) };
    private readonly Button _install = new() { Text = "安装并启动", AutoSize = true, MinimumSize = new Size(138, 36), BackColor = Color.FromArgb(24, 108, 195), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _close = new() { Text = "关闭", AutoSize = true, MinimumSize = new Size(98, 36) };
    private readonly Button _uninstall = new() { Text = "卸载 Agent（保留程序、配置与数据）…", AutoSize = true, MinimumSize = new Size(260, 36), Visible = false };
    private readonly ComboBox _component = SelectBox("Agent · 服务器 IIS 管理端", "Console · 集中管理控制台");
    private readonly ComboBox _mode = SelectBox("安装新服务", "更新已有服务（保留现场设置）");
    private readonly ComboBox _transport = SelectBox("HTTP · 内网访问，无需证书（推荐）", "HTTPS · 双向证书验证");
    private readonly TextBox _agentToken = new() { UseSystemPasswordChar = true, ReadOnly = true };
    private readonly TextBox _directory = new();
    private readonly TextBox _dataDirectory = new();
    private readonly TextBox _listenAddress = new() { Text = "0.0.0.0" };
    private readonly NumericUpDown _port = new() { Minimum = 1024, Maximum = 65535 };
    private readonly ComboBox _serverCertificate = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _clientCertificate = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _hostName = new();
    private readonly TextBox _updateAgentHostName = new();
    private readonly TextBox _allowedHosts = MultiLineBox();
    private readonly TextBox _allowedClients = MultiLineBox();
    private readonly TextBox _siteRoots = MultiLineBox();
    private readonly ComboBox _identity = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _account = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly Label _identityNote = new() { AutoSize = true, MaximumSize = new Size(680, 0), ForeColor = Color.FromArgb(121, 79, 27) };
    private readonly CheckBox _grantKeyRead = new() { Text = "授权此服务身份读取选定证书的私钥（仅增加所需读取权限）", AutoSize = true };
    private readonly CheckBox _firewall = new() { Text = "创建此程序的入站防火墙规则（仅 Domain / Private、指定端口和内网范围）", AutoSize = true };
    private readonly DataGridView _servers = CreateServerGrid();
    private readonly TextBox _summary = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10) };
    private readonly ListBox _progress = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
    private readonly TextBox _receipt = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly Label _receiptCaption = new() { Text = "安装回执（可复制路径供核对）", AutoSize = true, Margin = new Padding(0, 18, 0, 7) };
    private readonly Label _outcome = new() { AutoSize = true, MaximumSize = new Size(760, 0), Font = new Font("Segoe UI", 12, FontStyle.Bold) };
    private readonly Label _resultNote = new() { AutoSize = true, MaximumSize = new Size(760, 0) };
    private SetupInspection? _inspection;
    private SetupCertificate[] _certificates = [];
    private SetupPlan? _confirmedPlan;
    private int _step;
    private bool _busy;
    private bool _executionStarted;
    private bool _certificatesLoaded;

    public SetupForm(ISetupBackend backend, IAgentUninstallBackend? uninstallBackend = null)
    {
        _backend = backend;
        _uninstallBackend = uninstallBackend;
        Text = "IIS 远程管理 · 安装向导";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        MinimumSize = new Size(940, 690);
        var desktop = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1100, 850);
        Size = new Size(Math.Max(940, Math.Min(1060, desktop.Width - 40)), Math.Max(690, Math.Min(790, desktop.Height - 40)));
        BackColor = Color.FromArgb(245, 248, 251);
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 103, FlowDirection = FlowDirection.TopDown, Padding = new Padding(28, 20, 20, 12), BackColor = Color.White, WrapContents = false };
        header.Controls.Add(_stepLabel); header.Controls.Add(_titleLabel);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 66, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(20, 13, 20, 12), BackColor = Color.White, WrapContents = false };
        footer.Controls.Add(_close); footer.Controls.Add(_install); footer.Controls.Add(_next); footer.Controls.Add(_back); footer.Controls.Add(_uninstall);
        Controls.Add(_content); Controls.Add(footer); Controls.Add(header);
        NameControls();
        _component.SelectedIndex = 0; _mode.SelectedIndex = 0;
        _transport.SelectedIndex = 0;
        SetDefaultPaths();
        _component.SelectedIndexChanged += (_, _) => { if (!_busy) { _inspection = null; _confirmedPlan = null; _password.Clear(); SetDefaultPaths(); RefreshEnabled(); } };
        _mode.SelectedIndexChanged += (_, _) => { _inspection = null; _confirmedPlan = null; };
        _transport.SelectedIndexChanged += async (_, _) => await ChangeTransportAsync();
        _directory.TextChanged += (_, _) => { _inspection = null; _confirmedPlan = null; };
        _identity.SelectedIndexChanged += (_, _) => UpdateIdentityFields();
        _next.Click += async (_, _) => await NextAsync();
        _back.Click += (_, _) => { if (_busy) return; _confirmedPlan = null; ShowStep(_step == 2 ? 1 : 0); };
        _install.Click += async (_, _) => await InstallAsync();
        _close.Click += (_, _) => Close();
        _uninstall.Click += (_, _) => ShowAgentUninstall();
        FormClosing += (_, args) => { if (_busy) { args.Cancel = true; ShowStatus("当前检查或安装仍在进行，请等待结果；不会自动重试或回滚。"); } else _password.Clear(); };
        ShowStep(0);
    }

    private SetupComponent Component => _component.SelectedIndex == 1 ? SetupComponent.Console : SetupComponent.Agent;
    private SetupMode Mode => _mode.SelectedIndex == 1 ? SetupMode.Update : SetupMode.Install;
    private bool HttpToken => _transport.SelectedIndex != 1;
    private static string ReceiptDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IISManager.Setup", "receipts");

    private void NameControls()
    {
        (Control Control, string Name)[] controls =
        [
            (_component, "ComponentPicker"), (_mode, "InstallationMode"), (_directory, "InstallDirectory"),
            (_transport, "TransportMode"), (_agentToken, "AgentAccessToken"),
            (_dataDirectory, "DataDirectory"), (_listenAddress, "ListenAddress"), (_port, "ServicePort"),
            (_serverCertificate, "ServerCertificate"), (_clientCertificate, "ClientCertificate"),
            (_hostName, "HostName"), (_updateAgentHostName, "UpdateAgentCertificateHost"), (_allowedHosts, "AllowedHosts"),
            (_allowedClients, "AllowedClientThumbprints"), (_siteRoots, "AllowedSiteRoots"),
            (_identity, "ServiceIdentity"), (_account, "ServiceAccount"), (_password, "ServicePassword"),
            (_grantKeyRead, "GrantCertificateKeyRead"), (_firewall, "ConfigureFirewall"), (_servers, "AgentServers"),
            (_summary, "ReviewSummary"), (_progress, "InstallationProgress"), (_receipt, "ReceiptPath"),
            (_status, "ValidationIssues"), (_outcome, "InstallationOutcome"), (_back, "PreviousStep"),
            (_next, "NextStep"), (_install, "InstallAndStart"), (_close, "CloseInstaller"), (_uninstall, "OpenAgentUninstall")
        ];
        foreach (var (control, name) in controls) control.Name = name;
        _directory.AccessibleName = "安装目录"; _dataDirectory.AccessibleName = "任务记录目录";
        _uninstall.AccessibleName = "只读核对并卸载已有 Agent 服务";
    }

    private static ComboBox SelectBox(params string[] items)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(items);
        return combo;
    }

    private static TextBox MultiLineBox() => new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 86, AcceptsReturn = true };

    private static DataGridView CreateServerGrid()
    {
        var grid = new DataGridView
        {
            Height = 190, AutoGenerateColumns = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AllowUserToAddRows = true, AllowUserToDeleteRows = true, RowHeadersVisible = true,
            BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
            SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = false
        };
        grid.ShowCellToolTips = false;
        foreach (var (name, title, weight) in new[] { ("Id", "编号", 15), ("Name", "显示名称", 20), ("Url", "Agent HTTPS 根地址", 30), ("Thumbprint", "Agent 服务端证书指纹", 35) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = title, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable });
        var tokenColumn = new DataGridViewTextBoxColumn { Name = "AccessToken", HeaderText = "Agent 访问 Token", FillWeight = 35, SortMode = DataGridViewColumnSortMode.NotSortable };
        grid.Columns.Add(tokenColumn);
        grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable;
        grid.CellFormatting += (_, args) =>
        {
            if (args.ColumnIndex == tokenColumn.Index && !string.IsNullOrEmpty(Convert.ToString(args.Value)))
            { args.Value = "●●●●●●●●"; args.FormattingApplied = true; }
        };
        grid.EditingControlShowing += (_, args) =>
        {
            if (args.Control is TextBox input)
            {
                var tokenEditing = grid.CurrentCell?.ColumnIndex == tokenColumn.Index;
                input.UseSystemPasswordChar = tokenEditing;
                if (tokenEditing)
                {
                    // CellFormatting masks the display value. Editing must start from the raw value,
                    // otherwise committing an unchanged cell would persist the display bullets.
                    input.Text = Convert.ToString(grid.CurrentCell?.Value) ?? "";
                    input.SelectionStart = input.TextLength;
                }
            }
        };
        return grid;
    }

    private void SetDefaultPaths()
    {
        _directory.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "IISManagerTool", Component.ToString());
        _dataDirectory.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IISManagerTool", Component.ToString());
        _port.Value = Component == SetupComponent.Agent ? 7444 : 7443;
        _grantKeyRead.Checked = false; _firewall.Checked = false;
    }

    private TableLayoutPanel NewTable()
    {
        var table = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(0, 0, 0, 10), BackColor = Color.Transparent };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    private static void AddField(TableLayoutPanel table, string label, Control control, string? hint = null)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var title = new Label { Text = label, AutoSize = true, Margin = new Padding(0, 10, 12, 12), MaximumSize = new Size(178, 0) };
        control.Dock = DockStyle.Fill; control.Margin = new Padding(0, 5, 0, 12);
        if (label.Length != 0 && string.IsNullOrEmpty(control.AccessibleName)) control.AccessibleName = label;
        table.Controls.Add(title, 0, row); table.Controls.Add(control, 1, row);
        if (hint is not null) AddNote(table, hint);
    }

    private static void AddNote(TableLayoutPanel table, string message, bool important = false)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var note = new Label { Text = message, AutoSize = true, MaximumSize = new Size(740, 0), Margin = new Padding(0, 2, 0, 17), ForeColor = important ? Color.FromArgb(137, 75, 20) : Color.FromArgb(88, 107, 123) };
        table.Controls.Add(note, 0, row); table.SetColumnSpan(note, 2);
    }

    private Control DirectoryPicker(TextBox input)
    {
        var panel = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        input.Dock = DockStyle.Fill;
        var button = new Button { Text = "浏览…", AutoSize = true, Dock = DockStyle.Fill };
        button.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "选择组件安装或记录目录", UseDescriptionForTitle = true, SelectedPath = input.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                input.Text = dialog.SelectedPath;
                if (ReferenceEquals(input, _directory) && Mode == SetupMode.Install)
                    ShowStatus($"新安装需要本组件专用的空目录。若刚选择的是父目录，可在路径末尾追加 IISManagerTool\\{Component} 子目录；后续检查会核对目录内容。");
            }
        };
        panel.Controls.Add(input, 0, 0); panel.Controls.Add(button, 1, 0); return panel;
    }

    private void ShowStep(int step)
    {
        _step = step; ClearContent(); _content.AutoScrollPosition = Point.Empty; ShowStatus("");
        _stepLabel.Text = step < 3 ? $"步骤 {step + 1} / 3 · 每次安装或更新一个组件" : "执行结果 · 保留回执以便核对";
        _titleLabel.Text = step switch { 0 => "选择组件与安装位置", 1 => Mode == SetupMode.Update ? "核对已有服务" : "设置连接与服务身份", 2 => "确认安装内容", _ => "安装进度与结果" };
        _back.Visible = step is 1 or 2; _next.Visible = step is 0 or 1; _install.Visible = step == 2;
        _install.Text = Mode == SetupMode.Update ? "更新并恢复原状态" : "安装并启动";
        _close.Visible = step is 0 or 1 or 2 or 3; _next.Text = step == 0 ? "检查并继续" : "检查设置并继续";
        AcceptButton = step == 2 ? _install : step < 2 ? _next : null;
        if (step == 0) ShowSelection();
        else if (step == 1) ShowSettings();
        else if (step == 2) ShowSummary();
        else ShowProgress();
        RefreshEnabled();
    }

    private void ClearContent()
    {
        // Preserve reusable input controls while disposing the old page's temporary layout controls.
        foreach (var control in ReusableControls()) control.Parent?.Controls.Remove(control);
        foreach (var control in _content.Controls.Cast<Control>().ToArray()) control.Dispose();
        _content.Controls.Clear();
    }

    private Control[] ReusableControls() => [_component, _mode, _transport, _agentToken, _directory, _dataDirectory, _listenAddress, _port,
            _serverCertificate, _clientCertificate, _hostName, _updateAgentHostName, _allowedHosts, _allowedClients,
            _siteRoots, _identity, _account, _password, _identityNote, _grantKeyRead, _firewall, _servers,
            _summary, _progress, _receipt, _receiptCaption, _outcome, _resultNote, _status];

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _password.Clear();
            _agentToken.Clear();
            foreach (var control in ReusableControls()) control.Dispose();
        }
        base.Dispose(disposing);
    }

    private void AddStatus(TableLayoutPanel table)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize)); _status.Margin = new Padding(0, 10, 0, 8);
        table.Controls.Add(_status, 0, row); table.SetColumnSpan(_status, 2);
    }

    private void ShowSelection()
    {
        var table = NewTable();
        AddNote(table, "Agent 安装到需要管理 IIS 的服务器；Console 安装到集中管理服务器。两端均内置运行时，无需另装 .NET。");
        AddField(table, "本次组件", _component);
        AddField(table, "操作", _mode);
        AddField(table, "安装目录", DirectoryPicker(_directory), "新安装必须使用专用空目录或新目录，例如 Program Files\\IISManagerTool\\Agent。选择已有父目录时，请附上组件子目录；不会覆盖其他软件。");
        AddNote(table, "向导会先读取固定名称的 Windows 服务与现场配置。检查失败时停止继续，不会把失败当作新安装。更新保持现有服务账号、配置及权限。");
        AddStatus(table); _content.Controls.Add(table);
    }

    private void ShowSettings()
    {
        var table = NewTable();
        if (Mode == SetupMode.Update)
        {
            AddNote(table, "已找到现有服务。更新仅替换运行文件，现场连接设置、服务账号与权限保持原样。以下内容仅供核对。");
            var existing = _inspection!.ExistingPlan!;
            AddField(table, "服务 / 当前状态", ReadOnlyBox($"{existing.ServiceName} · {_inspection.ServiceState}"));
            AddField(table, "安装目录", ReadOnlyBox(_inspection.InstallDirectory));
            AddField(table, "服务身份", ReadOnlyBox(_inspection.ServiceAccount));
            if (Component == SetupComponent.Agent && existing.TransportMode == "Https")
            {
                if (string.IsNullOrWhiteSpace(existing.HostName))
                    AddField(table, "证书访问主机名", _updateAgentHostName,
                        "旧安装未记录访问主机名。请填写现有 Console 连接此 Agent 所使用的域名或 IP；仅用于本次 TLS 预检查，不修改配置，也不会从证书的多个 SAN 中猜测。");
                else
                    AddField(table, "证书访问主机名", ReadOnlyBox(existing.HostName), "使用已有安装记录中的主机名进行本次 TLS 预检查，保持现场配置不变。");
            }
            var snapshot = new TextBox { ReadOnly = true, Multiline = true, Height = 390, ScrollBars = ScrollBars.Vertical, Text = DescribePlan(existing) };
            AddField(table, "保留的现场设置", snapshot);
            AddStatus(table); _content.Controls.Add(table); return;
        }
        AddField(table, "连接方式", _transport);
        AddNote(table, HttpToken
            ? "HTTP 模式无需证书，可直接使用内网 IP。内网来源自动允许，无需填写来源 IP；每台 Agent 继续使用独立随机访问 Token。HTTP 不加密传输，请在受信任内网使用。"
            : "HTTPS 模式使用本机 LocalMachine\\My 中已有、有效且带私钥的证书；证书须具备相应服务端或客户端用途。连接保留双向证书验证。");
        if (Component == SetupComponent.Console)
            AddField(table, "监听 IP", _listenAddress, "填写本机 IP，或 0.0.0.0 监听本机全部 IPv4 地址；端口不会占用 IIS 的 80 / 443。");
        else
            AddField(table, "监听范围", ReadOnlyBox(HttpToken ? "Agent 监听本机网络接口，内网来源自动允许，并验证访问 Token。" : "Agent 监听本机网络接口，内网来源自动允许，并验证 mTLS。"));
        AddField(table, "服务端口", _port);
        if (!HttpToken) AddField(table, "服务端证书", _serverCertificate);
        AddField(table, Component == SetupComponent.Console ? "控制台访问主机名" : "Agent 对外主机名", _hostName,
            HttpToken ? "填写访问本服务使用的内网 IP 或主机名，不包含协议、端口或路径；无需匹配证书。" : "填写所选服务端证书对应的主机名或 IP，不包含协议、端口或路径。客户端使用此地址验证服务端证书。");
        if (Component == SetupComponent.Console)
        {
            AddField(table, "Host 白名单", _allowedHosts, "每行一个准确主机名或 IP，不带协议与端口，不使用 *。包括上方实际访问主机名。");
            if (!HttpToken) AddField(table, "连接 Agent 的客户端证书", _clientCertificate);
            _servers.Columns["Url"]!.HeaderText = HttpToken ? "Agent HTTP 根地址" : "Agent HTTPS 根地址";
            _servers.Columns["Thumbprint"]!.Visible = !HttpToken;
            _servers.Columns["AccessToken"]!.Visible = HttpToken;
            AddField(table, "Agent 服务器清单", _servers, HttpToken
                ? "每行填齐编号、名称、HTTP 根地址（如 http://192.168.1.20:7444/）及该 Agent 安装页复制的访问 Token。Token 输入和显示均遮挡；地址不得包含路径或查询参数。"
                : "每行填齐编号、名称、HTTPS 根地址（如 https://agent.example.internal:7444/）及该 Agent 的服务端证书指纹。地址不得包含路径或查询参数。");
        }
        else
        {
            if (HttpToken) AddField(table, "Agent 访问 Token", TokenCopyControl(), "已自动生成此 Agent 的随机访问 Token。点击复制，粘贴到 Console 对应服务器行的 Token 字段；Token 不显示在确认页、结果或安装回执。请保存到受限密码库，以便以后添加 Console。");
            else AddField(table, "允许的 Console 客户端指纹", _allowedClients, "每行一个已授权 Console 客户端证书指纹。仅这些证书可调用 Agent。");
            AddField(table, "已有站点根目录", _siteRoots, "每行一个已存在的本机绝对目录。仅允许在这些目录范围内创建网站；向导不创建网站目录或上传内容。");
            var rootsButton = new Button { Text = "选择并加入站点根目录…", AutoSize = true };
            rootsButton.Click += (_, _) =>
            {
                using var dialog = new FolderBrowserDialog { Description = "选择已存在的站点根目录", UseDescriptionForTitle = true, ShowNewFolderButton = false };
                if (dialog.ShowDialog(this) == DialogResult.OK) _siteRoots.AppendText((_siteRoots.TextLength == 0 ? "" : Environment.NewLine) + dialog.SelectedPath);
            };
            AddField(table, "", rootsButton);
        }
        AddField(table, "访问来源", ReadOnlyBox("内网来源自动允许"), "程序根据真实 TCP 连接来源识别内网，无需填写 IP / CIDR；公网来源拒绝。转发头不会改变此判断。");
        AddField(table, "任务记录目录", DirectoryPicker(_dataDirectory));
        AddField(table, "受限安装回执 / 备份目录", ReadOnlyBox(ReceiptDirectory), "每次执行创建独立的回执子目录。此位置由安装器固定管理，与组件的任务记录目录分开。");
        AddField(table, "服务运行身份", _identity);
        AddField(table, "已有账号", _account);
        AddField(table, "账号密码", _password, "密码仅在本次服务创建时使用，不进入安装计划、回执或日志。更新不重新输入密码。");
        AddField(table, "", _identityNote);
        if (!HttpToken) AddField(table, "证书私钥读取", _grantKeyRead, "未勾选时，所选服务身份必须已经能读取全部选定证书的私钥。新 Console 虚拟服务账号尚未准备私钥权限时，请明确勾选上方授权；向导不会自动代选，也不会生成或导出私钥。");
        AddField(table, "可选网络配置", _firewall, "规则只针对本次程序、服务端口及固定内网范围，限 Domain / Private。无需填写来源网络；向导不关闭防火墙或放宽连接认证。");
        AddStatus(table); _content.Controls.Add(table); UpdateIdentityFields();
    }

    private static TextBox ReadOnlyBox(string value) => new() { ReadOnly = true, Text = value, BackColor = Color.FromArgb(237, 242, 247) };

    private Control TokenCopyControl()
    {
        var panel = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        _agentToken.Dock = DockStyle.Fill;
        var button = new Button { Text = "复制 Token", AutoSize = true, Dock = DockStyle.Fill };
        button.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(_agentToken.Text);
                ShowStatus("Agent 访问 Token 已复制，请粘贴到 Console 服务器清单对应的 Token 字段，并保存至受限密码库。剪贴板含访问凭据，使用后请清除。");
            }
            catch (Exception exception) { ShowSafeFailure(exception, "CLIPBOARD_UNAVAILABLE", "无法复制访问 Token，请先检查剪贴板是否被其他程序占用。"); }
        };
        panel.Controls.Add(_agentToken, 0, 0); panel.Controls.Add(button, 1, 0); return panel;
    }

    private async Task ChangeTransportAsync()
    {
        if (_busy || _step != 1 || Mode != SetupMode.Install) return;
        _confirmedPlan = null;
        _grantKeyRead.Checked = false;
        if (!HttpToken && !_certificatesLoaded)
        {
            SetBusy(true);
            try
            {
                _certificates = await Task.Run(_backend.GetCertificates);
                _certificatesLoaded = true;
                FillCertificates(_serverCertificate, certificate => certificate.ServerAuthentication);
                FillCertificates(_clientCertificate, certificate => certificate.ClientAuthentication);
            }
            catch (Exception exception)
            {
                ShowStep(1);
                ShowSafeFailure(exception, "CERTIFICATE_LIST_FAILED", "本机证书检查失败，未进行安装。请核对证书存储区和读取权限。");
                return;
            }
            finally { SetBusy(false); }
        }
        ShowStep(1);
    }

    private void InitializeSettings()
    {
        _identity.Items.Clear();
        if (Component == SetupComponent.Console)
            _identity.Items.Add(new IdentityChoice(ServiceIdentityKind.VirtualAccount, "专用虚拟服务账号（推荐）"));
        else
            _identity.Items.Add(new IdentityChoice(ServiceIdentityKind.LocalSystem, "LocalSystem · 本机 IIS 管理高权限"));
        _identity.Items.Add(new IdentityChoice(ServiceIdentityKind.ExistingAccount, "使用已有 Windows 服务账号"));
        _identity.SelectedIndex = Component == SetupComponent.Console ? 0 : -1;
        _account.Clear(); _password.Clear();
        _hostName.Clear(); _allowedHosts.Clear(); _allowedClients.Clear(); _siteRoots.Clear();
        _servers.Rows.Clear(); _listenAddress.Text = "0.0.0.0"; _grantKeyRead.Checked = false; _firewall.Checked = false;
        _transport.SelectedIndex = 0;
        _agentToken.Text = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _serverCertificate.Items.Clear(); _clientCertificate.Items.Clear();
        UpdateIdentityFields();
    }

    private void FillCertificates(ComboBox picker, Func<SetupCertificate, bool> predicate)
    {
        picker.Items.Clear();
        foreach (var certificate in _certificates.Where(certificate => certificate.HasPrivateKey && certificate.NotAfter > DateTimeOffset.UtcNow && predicate(certificate)))
            picker.Items.Add(new CertificateChoice(certificate));
        picker.SelectedIndex = -1;
    }

    private void UpdateIdentityFields()
    {
        var choice = _identity.SelectedItem as IdentityChoice;
        _account.Enabled = !_busy && choice?.Kind == ServiceIdentityKind.ExistingAccount;
        _password.Enabled = !_busy && choice?.Kind == ServiceIdentityKind.ExistingAccount;
        if (choice?.Kind != ServiceIdentityKind.ExistingAccount) { _account.Clear(); _password.Clear(); }
        _identityNote.Text = choice?.Kind switch
        {
            ServiceIdentityKind.LocalSystem => "已选择 LocalSystem。Agent 将具备本机 IIS 管理及系统级权限，只应安装在受信任的管理网络。",
            ServiceIdentityKind.ExistingAccount => "已有账号必须具有登录为服务以及本组件所需权限。Agent 账号需已具备 IIS 管理权限；向导不授予通用管理员权限。",
            ServiceIdentityKind.VirtualAccount => HttpToken
                ? "Console 默认使用独立虚拟服务账号，不需要账号密码，也无需证书私钥权限。"
                : "Console 默认使用独立虚拟服务账号，不需要账号密码。若尚未为该服务 SID 授予选定证书私钥读取权限，请明确勾选“授权此服务身份读取选定证书的私钥”；未勾选时已有读取权限是必要条件。",
            _ => "Agent 必须明确选择 LocalSystem 或已有 Windows 服务账号后才能继续。"
        };
    }

    private async Task NextAsync()
    {
        if (_busy || _executionStarted) return;
        ShowStatus("");
        if (_step == 0)
        {
            if (string.IsNullOrWhiteSpace(_directory.Text)) { ShowStatus("请先选择安装目录。"); return; }
            SetBusy(true);
            try
            {
                var component = Component; var directory = _directory.Text.Trim();
                var inspection = await Task.Run(() => _backend.Inspect(component, directory));
                if (inspection.Issues.Length != 0) { ShowIssues(inspection.Issues); return; }
                if (inspection.ServiceExists && Mode != SetupMode.Update) { ShowStatus("已找到此组件的服务，请选择“更新已有服务”后继续。"); return; }
                if (!inspection.ServiceExists && Mode != SetupMode.Install) { ShowStatus("未找到此组件的服务，不能更新。请核对目录或明确选择“安装新服务”。"); return; }
                if (inspection.ServiceExists && inspection.ExistingPlan is null) { ShowStatus("现有服务配置无法可靠读回，已停止更新。请先核对现场配置。"); return; }
                if (inspection.ExistingPlan is { } existing && existing.Component != component) { ShowStatus("读回的服务组件与选择不一致，已停止继续。请先核对现场配置。"); return; }
                _inspection = inspection;
                _updateAgentHostName.Clear();
                if (Mode == SetupMode.Install)
                {
                    _certificates = []; _certificatesLoaded = false;
                    InitializeSettings();
                }
                ShowStep(1);
            }
            catch (Exception exception) { ShowSafeFailure(exception, "INSPECTION_FAILED", "本机服务或证书检查失败，未进行安装。请先核对 Windows 环境与读取权限。"); }
            finally { SetBusy(false); }
        }
        else if (_step == 1)
        {
            try
            {
                if (Mode == SetupMode.Install && _identity.SelectedItem is not IdentityChoice)
                { ShowStatus("请明确选择服务运行身份。Agent 须选择 LocalSystem（本机 IIS 管理高权限）或已有 Windows 服务账号。"); return; }
                if (Mode == SetupMode.Update && Component == SetupComponent.Agent && _inspection?.ExistingPlan?.TransportMode == "Https" && string.IsNullOrWhiteSpace(_inspection?.ExistingPlan?.HostName) &&
                    !SetupPlanValidator.IsHost(_updateAgentHostName.Text.Trim()))
                { ShowStatus("请填写现有 Console 访问此 Agent 所使用的准确域名或 IP，不包含协议、端口、路径或通配符；该值只用于本次 TLS 预检查。"); return; }
                var plan = BuildPlan();
                var issues = SetupPlanValidator.Validate(plan);
                if (issues.Length != 0) { ShowIssues(issues); return; }
                if (Mode == SetupMode.Install && plan.IdentityKind == ServiceIdentityKind.ExistingAccount && _password.TextLength == 0)
                { ShowStatus("请输入已有服务账号的密码。密码仅用于本次安装，不会保存。"); return; }
                _confirmedPlan = plan;
                ShowStep(2);
            }
            catch (Exception exception) { ShowSafeFailure(exception, "PLAN_INVALID", "安装设置无法形成有效计划，请核对必填项与服务器清单。"); }
        }
    }

    private SetupPlan BuildPlan()
    {
        if (Mode == SetupMode.Update)
        {
            if (_inspection is not { ServiceExists: true, ExistingPlan: not null })
                throw new SetupException("EXISTING_CONFIGURATION_REQUIRED", "现场配置未读回。");
            var existing = _inspection.ExistingPlan;
            var hostName = existing.HostName;
            if (Component == SetupComponent.Agent && existing.TransportMode == "Https" && string.IsNullOrWhiteSpace(hostName))
            {
                hostName = _updateAgentHostName.Text.Trim();
                if (!SetupPlanValidator.IsHost(hostName))
                    throw new SetupException("UPDATE_AGENT_HOST_REQUIRED", "更新旧 Agent 需要明确的证书访问主机名。");
            }
            return existing with
            {
                Mode = SetupMode.Update, Component = Component, InstallDirectory = _directory.Text.Trim(),
                HostName = hostName,
                GrantCertificateKeyRead = false, ConfigureFirewall = false
            };
        }
        if (_identity.SelectedItem is not IdentityChoice identity) throw new SetupException("SERVICE_IDENTITY_REQUIRED", "请选择服务身份。");
        _servers.EndEdit();
        var servers = _servers.Rows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow).Select(row =>
            new SetupServer(CellText(row, "Id"), CellText(row, "Name"), CellText(row, "Url"), HttpToken ? "" : CellText(row, "Thumbprint"), HttpToken ? CellText(row, "AccessToken") : "")).ToArray();
        return new SetupPlan
        {
            Component = Component, Mode = SetupMode.Install, InstallDirectory = _directory.Text.Trim(), DataDirectory = _dataDirectory.Text.Trim(),
            TransportMode = HttpToken ? "HttpToken" : "Https", AgentAccessToken = Component == SetupComponent.Agent && HttpToken ? _agentToken.Text : "",
            HostName = _hostName.Text.Trim(), ListenAddress = _listenAddress.Text.Trim(), Port = (int)_port.Value,
            ServerCertificateThumbprint = !HttpToken ? (_serverCertificate.SelectedItem as CertificateChoice)?.Certificate.Thumbprint ?? "" : "",
            ClientCertificateThumbprint = Component == SetupComponent.Console && !HttpToken ? (_clientCertificate.SelectedItem as CertificateChoice)?.Certificate.Thumbprint ?? "" : "",
            SourceAccessMode = "PrivateNetwork", AllowedHosts = Component == SetupComponent.Console ? Lines(_allowedHosts) : [], AllowedNetworks = [],
            AllowedClientThumbprints = Component == SetupComponent.Agent && !HttpToken ? Lines(_allowedClients) : [], AllowedSiteRoots = Component == SetupComponent.Agent ? Lines(_siteRoots) : [],
            Servers = Component == SetupComponent.Console ? servers : [], IdentityKind = identity.Kind,
            AccountName = identity.Kind == ServiceIdentityKind.ExistingAccount ? _account.Text.Trim() : "",
            GrantCertificateKeyRead = !HttpToken && _grantKeyRead.Checked, ConfigureFirewall = _firewall.Checked
        };
    }

    private static string CellText(DataGridViewRow row, string key) => Convert.ToString(row.Cells[key].Value)?.Trim() ?? "";
    private static string[] Lines(TextBox control) => control.Lines.Select(line => line.Trim()).Where(line => line.Length != 0).ToArray();

    private void ShowSummary()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var note = new Label { Text = Mode == SetupMode.Update ? "将备份并更新运行文件，恢复更新前的服务状态：原为运行则恢复运行，原为停止则保持停止。现场设置与身份保留。请核对后点击“更新并恢复原状态”。" : "将安装以下组件、配置选定身份与连接设置，然后启动服务。请核对后点击“安装并启动”。", AutoSize = true, MaximumSize = new Size(770, 0), Margin = new Padding(0, 0, 0, 14) };
        _summary.Text = DescribePlan(_confirmedPlan!);
        panel.Controls.Add(note, 0, 0); panel.Controls.Add(_summary, 0, 1); panel.Controls.Add(_status, 0, 2); _content.Controls.Add(panel);
    }

    private static string DescribePlan(SetupPlan plan)
    {
        var http = plan.TransportMode == "HttpToken";
        var sourceAccess = plan.SourceAccessMode == "PrivateNetwork" ? "内网来源自动允许" : $"保留来源白名单：{string.Join("、", plan.AllowedNetworks)}";
        var text = new StringBuilder();
        text.AppendLine($"组件：{(plan.Component == SetupComponent.Agent ? "Agent · 服务器 IIS 管理端" : "Console · 集中管理控制台")}");
        text.AppendLine($"操作：{(plan.Mode == SetupMode.Update ? "更新现有服务" : "安装新服务")}");
        if (plan.Mode == SetupMode.Update) text.AppendLine("服务状态：恢复更新前状态；维护中已停止的服务保持停止。");
        text.AppendLine($"固定服务名：{plan.ServiceName}"); text.AppendLine($"安装目录：{plan.InstallDirectory}");
        text.AppendLine($"任务记录目录：{plan.DataDirectory}");
        text.AppendLine($"受限安装回执 / 备份目录：{ReceiptDirectory}\\<本次操作编号>");
        text.AppendLine($"服务身份：{plan.ServiceAccount}");
        text.AppendLine($"连接方式：{(http ? "HTTP · 内网 IP + Agent 访问 Token" : "HTTPS · 双向证书验证")}");
        text.AppendLine($"{(http ? "HTTP" : "HTTPS")} 监听：{(plan.Component == SetupComponent.Console ? plan.ListenAddress : "本机网络接口")}:{plan.Port}");
        text.AppendLine($"服务端访问主机名：{plan.HostName}");
        if (!http) text.AppendLine($"服务端证书指纹：{plan.ServerCertificateThumbprint}");
        if (plan.Mode == SetupMode.Update && plan.Component == SetupComponent.Agent && !http)
            text.AppendLine("证书访问主机名仅用于本次 TLS 预检查，不修改现场配置。");
        if (plan.Component == SetupComponent.Console)
        {
            text.AppendLine($"控制台访问地址：{(http ? "http" : "https")}://{plan.HostName}:{plan.Port}/");
            text.AppendLine($"Host 白名单：{string.Join("、", plan.AllowedHosts)}");
            text.AppendLine($"访问来源：{sourceAccess}");
            if (!http) text.AppendLine($"连接 Agent 的客户端证书指纹：{plan.ClientCertificateThumbprint}");
            text.AppendLine("Agent 服务器清单：");
            foreach (var server in plan.Servers)
                text.AppendLine($"  {server.Id} · {server.Name}\r\n  {server.AgentUrl}\r\n  " + (http ? "Agent 访问 Token：已设置（不显示）" : $"服务端证书指纹：{server.ServerCertificateThumbprint}"));
        }
        else
        {
            text.AppendLine(http ? "Agent 访问 Token：已设置（不显示）" : $"允许的 Console 客户端证书：{string.Join("、", plan.AllowedClientThumbprints)}");
            text.AppendLine($"访问来源：{sourceAccess}");
            text.AppendLine("允许的已有站点根目录："); foreach (var root in plan.AllowedSiteRoots) text.AppendLine("  " + root);
        }
        text.AppendLine();
        if (!http) text.AppendLine($"新增证书私钥读取授权：{(plan.GrantCertificateKeyRead ? "是（仅选定服务身份）" : "否，使用已有权限")}");
        text.AppendLine($"创建防火墙规则：{(plan.ConfigureFirewall ? "是，仅 Domain / Private、指定程序与端口" : "否")}");
        if (plan.ConfigureFirewall) text.AppendLine($"防火墙允许来源：{(plan.SourceAccessMode == "PrivateNetwork" ? "固定内网范围" : string.Join("、", plan.AllowedNetworks))}");
        text.AppendLine(); text.AppendLine("产物：Windows Server / win-x64 / .NET 10 / 自包含，不需要服务器另装 .NET。");
        text.AppendLine("不创建网站目录，不上传站点内容，不导出证书私钥。");
        return text.ToString();
    }

    private void ShowProgress()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize)); table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var index = 0; index < 4; index++) table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(_outcome, 0, 0); table.Controls.Add(_progress, 0, 1); table.Controls.Add(_resultNote, 0, 2);
        table.Controls.Add(_receiptCaption, 0, 3);
        table.Controls.Add(_receipt, 0, 4); table.Controls.Add(_status, 0, 5); _content.Controls.Add(table);
    }

    private async Task InstallAsync()
    {
        if (_busy || _executionStarted || _confirmedPlan is null) return;
        _executionStarted = true;
        var plan = _confirmedPlan;
        var action = plan.Mode == SetupMode.Update ? "更新" : "安装";
        using var password = CapturePassword(plan);
        _password.Clear(); _progress.Items.Clear(); _receipt.Clear();
        _receiptCaption.Text = "安装回执（可复制路径供核对）";
        _outcome.Text = plan.Mode == SetupMode.Update ? "正在更新，请等待原服务状态恢复检查…" : "正在安装，请等待执行结果…";
        _resultNote.Text = "执行期间不能关闭向导或重复提交。结果未知时保留回执，先只读核对现场，不自动重试或回滚。";
        ShowStep(3); SetBusy(true);
        var progress = new Progress<SetupProgress>(item =>
        {
            // Backend progress is a bounded, reviewed message contract, never exception text or command output.
            _progress.Items.Add($"{DateTime.Now:HH:mm:ss}  {item.Message} ({item.Stage})");
            if (_progress.Items.Count > 300) _progress.Items.RemoveAt(0);
            _progress.TopIndex = Math.Max(0, _progress.Items.Count - 1);
        });
        try
        {
            var outcome = await Task.Run(() => _backend.ExecuteAsync(plan, password, progress, CancellationToken.None));
            _receipt.Text = outcome.ReceiptPath;
            if (string.IsNullOrWhiteSpace(outcome.ReceiptPath))
            {
                _receiptCaption.Text = "回执路径尚未返回，请在以下受限安装回执目录中只读核对";
                _receipt.Text = ReceiptDirectory;
            }
            _outcome.Text = outcome.Succeeded
                ? plan.Mode == SetupMode.Update ? "更新完成，原服务状态恢复检查通过" : "安装完成，服务状态检查通过"
                : $"{action}未确认成功 · {SafeCode(outcome.State)} / {SafeCode(outcome.Code)}";
            _outcome.ForeColor = outcome.Succeeded ? Color.FromArgb(28, 127, 79) : Color.FromArgb(153, 57, 35);
            _resultNote.Text = outcome.Succeeded
                ? plan.Mode == SetupMode.Update
                    ? "此结果只表示运行文件更新及原 Windows 服务状态恢复检查通过；原为停止的服务保持停止。请按维护安排验证真实 IIS 查询、控制台与 Agent 的连接认证、来源限制及业务功能。"
                    : "此结果只表示安装及 Windows 服务状态检查通过。请继续验证真实 IIS 查询、控制台与 Agent 的连接认证、来源限制及业务功能。"
                : "保留安装回执与受限备份。对于 unknown / partial 或其他未确认结果，先只读核对服务、文件和配置现场，禁止自动重试或回滚。";
            if (!string.IsNullOrWhiteSpace(outcome.Message))
                _resultNote.Text = outcome.Message + Environment.NewLine + _resultNote.Text;
            if (!outcome.Succeeded) ShowStatus($"未确认{action}成功，请保留回执并只读核对现场。状态码：{SafeCode(outcome.Code)}。");
        }
        catch (Exception exception)
        {
            _outcome.Text = $"{action}结果未知，请先核对现场"; _outcome.ForeColor = Color.FromArgb(153, 57, 35);
            _receiptCaption.Text = "回执路径未确认，请在以下受限安装回执目录中只读核对";
            _receipt.Text = ReceiptDirectory;
            _resultNote.Text = $"{action}可能已开始；请保留受限安装回执目录内的回执与备份，先只读核对。向导不会重试或回滚。";
            ShowSafeFailure(exception, "EXECUTION_UNKNOWN", $"无法确认{action}结果。请检查受限安装回执目录，保留原回执后核对现场。");
        }
        finally { SetBusy(false); }
    }

    private SecureString? CapturePassword(SetupPlan plan)
    {
        if (plan.Mode == SetupMode.Update || plan.IdentityKind != ServiceIdentityKind.ExistingAccount) return null;
        var result = new SecureString();
        foreach (var character in _password.Text) result.AppendChar(character);
        result.MakeReadOnly(); return result;
    }

    private void ShowIssues(IEnumerable<SetupIssue> issues) => ShowStatus(string.Join(Environment.NewLine,
        issues.Select(issue => $"• {issue.Message} ({SafeCode(issue.Code)})")));

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
        _status.Text = text; _status.Visible = text.Length != 0;
        if (_status.Visible && _content.Contains(_status))
        {
            _status.Parent?.PerformLayout();
            _content.ScrollControlIntoView(_status);
        }
    }

    private void SetBusy(bool busy) { _busy = busy; UseWaitCursor = busy; RefreshEnabled(); }

    private void ShowAgentUninstall()
    {
        if (_busy || _executionStarted || _step != 0 || Component != SetupComponent.Agent || _uninstallBackend is null) return;
        _inspection = null; _confirmedPlan = null; _password.Clear();
        using var form = new AgentUninstallForm(_uninstallBackend, _directory.Text.Trim());
        form.ShowDialog(this);
        // The modal operation can remove the service. Never reuse an earlier installation inspection.
        _inspection = null; _confirmedPlan = null;
        ShowStep(0);
    }

    private void RefreshEnabled()
    {
        _content.Enabled = !_busy || _step == 3; _back.Enabled = !_busy; _next.Enabled = !_busy && !_executionStarted;
        _install.Enabled = !_busy && !_executionStarted; _close.Enabled = !_busy;
        _uninstall.Visible = _step == 0 && Component == SetupComponent.Agent && _uninstallBackend is not null;
        _uninstall.Enabled = !_busy && !_executionStarted;
        if (_step == 1 && Mode == SetupMode.Install) UpdateIdentityFields();
    }

    private sealed record IdentityChoice(ServiceIdentityKind Kind, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record CertificateChoice(SetupCertificate Certificate)
    {
        public override string ToString() => $"{Certificate.DisplayName} · 到期 {Certificate.NotAfter:yyyy-MM-dd} · {Certificate.Thumbprint}";
    }
}
