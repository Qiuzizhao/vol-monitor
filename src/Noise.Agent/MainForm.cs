using System.Net.Http.Json;
using Noise.Core;

namespace Noise.Agent;

public sealed class MainForm : Form
{
    private readonly TextBox server = new() { Text = "https://192.168.1.10:5081", Dock = DockStyle.Fill };
    private readonly Label name = new() { Text = Environment.MachineName, Dock = DockStyle.Fill };
    private readonly ComboBox microphone = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label status = new() { Text = "尚未连接教师机", AutoSize = true, ForeColor = Color.Teal, Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold) };
    private readonly Label detail = new() { Text = "正在连接教师机……", AutoSize = true, MaximumSize = new Size(380, 0) };
    private readonly Button connect = new() { Text = "申请接入", AutoSize = true };
    private readonly Button reset = new() { Text = "断开并重新配置", AutoSize = true, Visible = false };
    private readonly Button retryEnrollment = new() { Text = "重新申请", AutoSize = true, Visible = false };
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Information, Text = "机房音量 · 学生端", Visible = true };
    private readonly TableLayoutPanel settingsPanel = new() { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Visible = false };
    private AgentWorker? worker;
    private AgentConfig? config;
    private DeploymentProfile? profile;
    private string fingerprint = "";
    private CancellationTokenSource? enrollmentStopping;
    private Task? enrollmentTask;
    private bool closing;
    private readonly bool managed;
    private bool maintenanceExit;
    private readonly EventWaitHandle showSignal = new(false, EventResetMode.AutoReset, AgentSupervisor.ShowEvent);
    private readonly System.Windows.Forms.Timer showTimer = new() { Interval = 500 };

    public MainForm(bool managed = false)
    {
        this.managed = managed;
        tray.Visible = !managed;
        Text = "机房音量 · 学生端";
        ClientSize = new Size(440, 240); MinimumSize = new Size(440, 280);
        Font = new Font("Microsoft YaHei UI", 10); BackColor = Color.White;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 2, RowCount = 5, AutoScroll = true };
        root.ColumnStyles.Add(new(SizeType.Absolute, 105)); root.ColumnStyles.Add(new(SizeType.Percent, 100));
        Controls.Add(root);
        root.Controls.Add(status, 0, 0); root.SetColumnSpan(status, 2);
        root.Controls.Add(detail, 0, 1); root.SetColumnSpan(detail, 2);
        root.Controls.Add(name, 0, 2); root.SetColumnSpan(name, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 0) };
        var settings = new Button { Text = "连接设置", AutoSize = true };
        settings.Click += (_, _) => { settingsPanel.Visible = !settingsPanel.Visible; ClientSize = new Size(440, settingsPanel.Visible ? 410 : 240); };
        buttons.Controls.Add(settings); buttons.Controls.Add(retryEnrollment);
        if (managed)
        {
            var maintenance = new Button { Text = "管理员退出", AutoSize = true };
            maintenance.Click += async (_, _) => await MaintenanceExit();
            buttons.Controls.Add(maintenance);
        }
        root.Controls.Add(buttons, 0, 3); root.SetColumnSpan(buttons, 2);
        settingsPanel.ColumnStyles.Add(new(SizeType.Absolute, 90)); settingsPanel.ColumnStyles.Add(new(SizeType.Percent, 100));
        AddRow(settingsPanel, "教师地址", server, 0); AddRow(settingsPanel, "麦克风", microphone, 1);
        var refresh = new Button { Text = "刷新麦克风", AutoSize = true }; refresh.Click += (_, _) => RefreshDevices();
        settingsPanel.Controls.Add(refresh, 1, 2);
        var connections = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        connections.Controls.Add(connect); connections.Controls.Add(reset); settingsPanel.Controls.Add(connections, 1, 3);
        root.Controls.Add(settingsPanel, 0, 4); root.SetColumnSpan(settingsPanel, 2);
        var menu = new ContextMenuStrip(); menu.Items.Add("显示窗口", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        if (!managed) menu.Items.Add("退出", null, (_, _) => Close()); tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        Resize += (_, _) => { if (!managed && WindowState == FormWindowState.Minimized) Hide(); };
        showTimer.Tick += (_, _) => { if (showSignal.WaitOne(0)) { Show(); WindowState = FormWindowState.Normal; Activate(); } };
        showTimer.Start();
        connect.Click += async (_, _) => await Enroll();
        reset.Click += async (_, _) =>
        {
            await StopEnrollment();
            if (worker is not null) await worker.DisposeAsync(); worker = null; config = null;
            try { AgentConfig.Clear(); PendingIdentity.Clear(); } catch (IOException e) { detail.Text = e.Message; }
            SetConnected(false); status.Text = "已断开，麦克风停止";
        };
        retryEnrollment.Click += async (_, _) =>
        {
            await StopEnrollment();
            try { PendingIdentity.Clear(); StartEnrollment(); }
            catch (Exception e) { detail.Text = e.Message; }
        };
        FormClosing += OnClosing;
        RefreshDevices();
        connect.Enabled = false;
        Shown += async (_, _) => await Initialize();
    }

    private async Task Initialize()
    {
        try
        {
            profile = DeploymentProfile.Load();
            if (profile is not null) server.Text = profile.Server;
            status.Text = "正在检查本机设备身份";
            fingerprint = await Task.Run(HardwareIdentity.Read);
            if (closing) return;
            config = AgentConfig.Load(fingerprint, out var loadNotice);
            if (loadNotice is not null) detail.Text = loadNotice;
            if (config is not null && profile is not null && AgentConfig.ValidateServer(config.Server) != AgentConfig.ValidateServer(profile.Server))
            { AgentConfig.Clear(); config = null; }
            if (config is not null)
            {
                server.Text = config.Server; name.Text = Environment.MachineName;
                var hasSavedMic = microphone.Items.Cast<MicrophoneItem>().Any(m => m.Id == config.MicrophoneId);
                if (!hasSavedMic) { config = config with { MicrophoneId = SelectedMicrophone() }; config.Save(); }
                for (var i = 0; i < microphone.Items.Count; i++)
                    if (((MicrophoneItem)microphone.Items[i]!).Id == config.MicrophoneId) microphone.SelectedIndex = i;
                BeginWorker(config);
            }
            else if (profile?.AutoEnroll == true) StartEnrollment();
            else { status.Text = "等待连接"; settingsPanel.Visible = true; ClientSize = new Size(440, 410); }
        }
        catch (Exception) { status.Text = "连接设置异常"; detail.Text = "请联系教师检查。"; SetConnected(false); }
        finally { if (!closing) connect.Enabled = true; }
    }

    private string SelectedMicrophone() => microphone.SelectedItem is MicrophoneItem m ? m.Id : "";

    private void StartEnrollment()
    {
        if (profile?.AutoEnroll != true || fingerprint.Length == 0 || closing) return;
        retryEnrollment.Visible = false;
        enrollmentStopping = new CancellationTokenSource();
        enrollmentTask = EnrollFromImage(enrollmentStopping.Token);
    }

    private async Task EnrollFromImage(CancellationToken ct)
    {
        try
        {
            config = await EnrollmentClient.WaitForApproval(profile!, fingerprint, SelectedMicrophone,
                (message, description) => { if (!closing) { status.Text = message; detail.Text = description; } }, ct);
            ct.ThrowIfCancellationRequested();
            BeginWorker(config);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { if (!closing) { status.Text = "自动接入未完成"; detail.Text = e.Message; retryEnrollment.Visible = true; } }
    }

    private async Task StopEnrollment()
    {
        if (enrollmentStopping is not null) await enrollmentStopping.CancelAsync();
        if (enrollmentTask is not null) await enrollmentTask;
        enrollmentTask = null; enrollmentStopping?.Dispose(); enrollmentStopping = null;
    }

    private static void AddRow(TableLayoutPanel root, string label, Control control, int row)
    {
        root.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 4, 8) }, 0, row);
        control.Margin = new Padding(0, 5, 0, 5); root.Controls.Add(control, 1, row);
    }
    private void RefreshDevices()
    {
        try
        {
            microphone.Items.Clear(); foreach (var (id, label) in AudioCapture.ListDevices()) microphone.Items.Add(new MicrophoneItem(id, label));
            if (microphone.Items.Count > 0) microphone.SelectedIndex = 0;
            else { status.Text = "未找到麦克风"; detail.Text = "请连接麦克风，并检查 Windows 麦克风隐私权限。"; }
            if (microphone.Items.Count > 0)
            {
                var defaultId = AudioCapture.DefaultDeviceId();
                for (var i = 0; i < microphone.Items.Count; i++)
                    if (((MicrophoneItem)microphone.Items[i]!).Id == defaultId) microphone.SelectedIndex = i;
            }
        }
        catch (Exception e) { detail.Text = "无法枚举麦克风：" + e.Message; }
    }
    private async Task Enroll()
    {
        await StopEnrollment();
        connect.Enabled = false;
        try
        {
            var uri = AgentConfig.ValidateServer(server.Text);
            if (fingerprint.Length == 0) throw new InvalidOperationException("无法确认本机硬件标识，请检查系统后重启客户端。");
            if (microphone.SelectedItem is not MicrophoneItem mic) throw new ArgumentException("请先选择麦克风。");
            profile = new DeploymentProfile(uri.ToString());
            StartEnrollment();
        }
        catch (Exception e) { status.Text = "连接失败"; detail.Text = e.Message; }
        finally { connect.Enabled = true; }
    }
    private void BeginWorker(AgentConfig saved)
    {
        retryEnrollment.Visible = false;
        worker = new AgentWorker(saved);
        worker.Updated += (message, description) =>
        {
            if (IsDisposed || closing) return;
            try { BeginInvoke(() => { status.Text = message; Text = "声域 · " + message; detail.Text = description; tray.Text = "声域 · " + message[..Math.Min(message.Length, 45)]; }); }
            catch (InvalidOperationException) { }
        };
        worker.AuthorizationLost += () =>
        {
            if (IsDisposed || closing) return;
            try
            {
                BeginInvoke(async () =>
                {
                    if (closing) return;
                    try
                    {
                        if (worker is not null) await worker.DisposeAsync();
                        worker = null; config = null;
                        AgentConfig.Clear(); PendingIdentity.Clear(); SetConnected(false);
                        if (profile?.AutoEnroll == true) { await StopEnrollment(); StartEnrollment(); }
                        else status.Text = "授权已撤销，请重新申请接入";
                    }
                    catch (Exception e) { detail.Text = e.Message; }
                });
            }
            catch (InvalidOperationException) { }
        };
        SetConnected(true); worker.Start();
    }
    private void SetConnected(bool value)
    {
        server.Enabled = microphone.Enabled = connect.Visible = !value;
        reset.Visible = value && !managed;
    }
    private async Task MaintenanceExit()
    {
        using var dialog = new Form { Text = "管理员退出", ClientSize = new Size(390, 155), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        var label = new Label { Text = "维护密码", AutoSize = true, Location = new Point(16, 15) };
        var password = new TextBox { UseSystemPasswordChar = true, MaxLength = 256, Location = new Point(16, 48), Width = 355 };
        var confirm = new Button { Text = "验证并退出", DialogResult = DialogResult.OK, Location = new Point(245, 98), Width = 125 };
        dialog.Controls.AddRange([label, password, confirm]); dialog.AcceptButton = confirm;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            Enabled = false;
            if (!await AgentSupervisor.RequestMaintenance(password.Text)) { detail.Text = "维护密码不正确，无法退出。"; return; }
            maintenanceExit = true;
            Close();
        }
        catch (Exception e) when (e is IOException or OperationCanceledException) { detail.Text = "维护验证未完成，请确认教师机在线：" + e.Message; }
        finally { password.Clear(); if (!closing) Enabled = true; }
    }
    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (closing) return;
        if (managed && !maintenanceExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            WindowState = FormWindowState.Minimized;
            return;
        }
        e.Cancel = true; closing = true; Enabled = false;
        await StopEnrollment();
        if (worker is not null) await worker.DisposeAsync();
        Close();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { showTimer.Dispose(); showSignal.Dispose(); tray.Dispose(); }
        base.Dispose(disposing);
    }
    private sealed record MicrophoneItem(string Id, string Name) { public override string ToString() => Name; }
}
