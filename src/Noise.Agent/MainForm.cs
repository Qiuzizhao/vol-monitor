using System.Net.Http.Json;
using Noise.Core;

namespace Noise.Agent;

public sealed class MainForm : Form
{
    private readonly TextBox server = new() { Text = "https://192.168.1.10:5081", Dock = DockStyle.Fill };
    private readonly TextBox name = new() { Text = Environment.MachineName, Dock = DockStyle.Fill };
    private readonly TextBox pairing = new() { Dock = DockStyle.Fill, MaxLength = 8 };
    private readonly ComboBox microphone = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox allow = new() { Text = "允许本机在教师开始实验后采集音量", AutoSize = true, Checked = false };
    private readonly Label status = new() { Text = "尚未连接教师机", AutoSize = true, ForeColor = Color.Teal, Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold) };
    private readonly Label detail = new() { Text = "配对后，需要在教师端将此设备绑定到座位。", AutoSize = true, MaximumSize = new Size(620, 0) };
    private readonly Button connect = new() { Text = "配对并连接", AutoSize = true };
    private readonly Button reset = new() { Text = "断开并重新配置", AutoSize = true, Visible = false };
    private readonly Button retryEnrollment = new() { Text = "重新申请", AutoSize = true, Visible = false };
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Information, Text = "声域 RoomScope · 学生端", Visible = true };
    private AgentWorker? worker;
    private AgentConfig? config;
    private DeploymentProfile? profile;
    private string fingerprint = "";
    private CancellationTokenSource? enrollmentStopping;
    private Task? enrollmentTask;
    private bool closing;

    public MainForm()
    {
        Text = "声域 RoomScope · 学生端";
        ClientSize = new Size(720, 510); MinimumSize = new Size(640, 540);
        Font = new Font("Microsoft YaHei UI", 10); BackColor = Color.White;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 2, RowCount = 11, AutoScroll = true };
        root.ColumnStyles.Add(new(SizeType.Absolute, 105)); root.ColumnStyles.Add(new(SizeType.Percent, 100));
        Controls.Add(root);
        var title = new Label { Text = "声域 / 本地音量采集", AutoSize = true, Font = new Font(Font.FontFamily, 19, FontStyle.Bold), Margin = new Padding(0, 0, 0, 20) };
        root.Controls.Add(title, 0, 0); root.SetColumnSpan(title, 2);
        AddRow(root, "教师机地址", server, 1); AddRow(root, "设备名称", name, 2); AddRow(root, "配对码", pairing, 3); AddRow(root, "麦克风", microphone, 4);
        var refresh = new Button { Text = "刷新麦克风", AutoSize = true }; refresh.Click += (_, _) => RefreshDevices();
        root.Controls.Add(refresh, 1, 5);
        root.Controls.Add(allow, 1, 6);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(connect); buttons.Controls.Add(reset); buttons.Controls.Add(retryEnrollment); root.Controls.Add(buttons, 1, 7);
        root.Controls.Add(status, 0, 8); root.SetColumnSpan(status, 2);
        root.Controls.Add(detail, 0, 9); root.SetColumnSpan(detail, 2);
        var note = new Label { Text = "音频只在本机内存中计算，处理后丢弃。\n每秒上传音量统计，不上传录音，不识别讲话内容。关闭此窗口将停止采集。", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 16, 0, 0) };
        root.Controls.Add(note, 0, 10); root.SetColumnSpan(note, 2);
        var menu = new ContextMenuStrip(); menu.Items.Add("显示窗口", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("关闭本机采集", null, (_, _) => allow.Checked = false);
        menu.Items.Add("退出", null, (_, _) => Close()); tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        connect.Click += async (_, _) => await Enroll();
        reset.Click += async (_, _) =>
        {
            await StopEnrollment();
            if (worker is not null) await worker.DisposeAsync(); worker = null; config = null;
            try { AgentConfig.Clear(); PendingIdentity.Clear(); } catch (IOException e) { detail.Text = e.Message; }
            allow.Checked = false;
            SetConnected(false); status.Text = "已断开，麦克风停止";
        };
        retryEnrollment.Click += async (_, _) =>
        {
            await StopEnrollment();
            try { PendingIdentity.Clear(); StartEnrollment(); }
            catch (Exception e) { detail.Text = e.Message; }
        };
        allow.CheckedChanged += (_, _) => { if (worker is not null) worker.Allowed = allow.Checked; };
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
            else status.Text = "尚未连接教师机";
        }
        catch (Exception e) { status.Text = "设备身份或配置无法加载"; detail.Text = e.Message; SetConnected(false); }
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
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(8) };
            using var response = await http.PostAsJsonAsync("api/devices/enroll", new EnrollRequest(pairing.Text.Trim().ToUpperInvariant(), name.Text.Trim(), fingerprint));
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"配对失败（{(int)response.StatusCode}），请检查地址、证书和配对码。");
            var result = await response.Content.ReadFromJsonAsync<EnrollResult>() ?? throw new InvalidOperationException("配对响应无效。");
            config = AgentConfig.Create(uri.ToString(), result.DeviceId, result.Token, name.Text.Trim(), mic.Id, fingerprint);
            PendingIdentity.Clear();
            config.Save(); BeginWorker(config);
        }
        catch (Exception e) { status.Text = "连接失败"; detail.Text = e.Message; }
        finally { connect.Enabled = true; }
    }
    private void BeginWorker(AgentConfig saved)
    {
        retryEnrollment.Visible = false;
        worker = new AgentWorker(saved) { Allowed = allow.Checked };
        worker.Updated += (message, description) =>
        {
            if (IsDisposed || closing) return;
            try { BeginInvoke(() => { status.Text = message; detail.Text = description; tray.Text = "声域 · " + message[..Math.Min(message.Length, 45)]; }); }
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
                        worker = null; config = null; allow.Checked = false;
                        AgentConfig.Clear(); PendingIdentity.Clear(); SetConnected(false);
                        if (profile?.AutoEnroll == true) { await StopEnrollment(); StartEnrollment(); }
                        else status.Text = "授权已撤销，请重新配对";
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
        server.Enabled = name.Enabled = pairing.Enabled = microphone.Enabled = connect.Visible = !value;
        reset.Visible = value;
    }
    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (closing) return;
        e.Cancel = true; closing = true; Enabled = false;
        await StopEnrollment();
        if (worker is not null) await worker.DisposeAsync();
        tray.Dispose(); Close();
    }
    private sealed record MicrophoneItem(string Id, string Name) { public override string ToString() => Name; }
}
