using System.Net;
using System.Reflection;
using System.Text.Json;
using Noise.Agent;
using Xunit;

namespace Noise.Agent.Tests;

public sealed class ManagedModeTests
{
    [Fact]
    public async Task NormalExitAlwaysRestartsUntilMaintenanceCancellation()
    {
        using var stop = new CancellationTokenSource();
        var starts = 0; var delays = new List<TimeSpan>();
        await AgentSupervisor.RestartLoop(_ =>
        {
            if (++starts == 4) stop.Cancel();
            return Task.CompletedTask;
        }, stop.Token, (duration, _) => { delays.Add(duration); return Task.CompletedTask; });
        Assert.Equal(4, starts);
        Assert.Equal([2d, 4d, 8d], delays.Select(d => d.TotalSeconds));
    }

    [Fact]
    public async Task FailedLaunchRetriesWithBoundedBackoff()
    {
        using var stop = new CancellationTokenSource();
        var delays = new List<TimeSpan>();
        await AgentSupervisor.RestartLoop(_ => throw new IOException("failed launch"), stop.Token,
            (duration, _) => { delays.Add(duration); if (delays.Count == 8) stop.Cancel(); return Task.CompletedTask; });
        Assert.Equal(8, delays.Count);
        Assert.All(delays, delay => Assert.InRange(delay.TotalSeconds, 2, 30));
        Assert.Equal(30, delays[^1].TotalSeconds);
    }

    [Fact]
    public async Task MaintenanceCancellationDoesNotLaunchAChild()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await AgentSupervisor.RestartLoop(_ => throw new Exception("must not run"), stop.Token);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.Redirect, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public async Task MaintenanceRequiresSuccessfulTeacherAuthentication(HttpStatusCode status, bool expected)
    {
        var handler = new LoginHandler(status);
        Assert.Equal(expected, await AgentSupervisor.VerifyTeacher("https://192.168.1.10:5081", "teacher-test-password", handler));
        Assert.True(handler.Called);
    }

    [Fact]
    public void StudentCloseHidesToTrayWithoutTaskbarEntry()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm(managed: true);
                var args = new FormClosingEventArgs(CloseReason.UserClosing, false);
                typeof(MainForm).GetMethod("OnClosing", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!.Invoke(form, [form, args]);
                Assert.True(args.Cancel);
                Assert.False(form.Visible);
                Assert.False(form.ShowInTaskbar);
                var tray = (NotifyIcon)typeof(MainForm).GetField("tray", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
                Assert.True(tray.Visible);
                Assert.Contains(tray.ContextMenuStrip!.Items.Cast<ToolStripItem>(), item => item.Text == "退出（需要密码）");
            }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (error is not null) throw error;
    }

    [Fact]
    public void ManagedStartupCreatesAHiddenWindowWithOnlyATrayIcon()
    {
        RunOnUiThread(() =>
        {
            using var form = new MainForm(managed: true);
            form.StartInTray();
            Assert.True(form.IsHandleCreated);
            Assert.False(form.Visible);
            Assert.False(form.ShowInTaskbar);
            var tray = (NotifyIcon)typeof(MainForm).GetField("tray", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
            Assert.True(tray.Visible);
        });
    }

    [Fact]
    public void TrayWindowCanBeOpenedAndHiddenAgain()
    {
        RunOnUiThread(() =>
        {
            using var form = new MainForm(managed: true);
            // Keep this window lifecycle test separate from device enrollment.
            typeof(MainForm).GetField("initialized", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(form, true);
            form.ShowWindow();
            Assert.True(form.Visible);
            Assert.True(form.ShowInTaskbar);
            form.WindowState = FormWindowState.Minimized;
            Assert.False(form.Visible);
            Assert.False(form.ShowInTaskbar);
        });
    }

    [Fact]
    public async Task AuthorizedCancellationStopsAnActiveChildWithoutRestart()
    {
        using var stop = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var task = AgentSupervisor.RestartLoop(async ct =>
        {
            starts++;
            ready.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, stop.Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.CancelAsync();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, starts);
    }

    private static void RunOnUiThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { error = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (error is not null) throw error;
    }

    private sealed class LoginHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public bool Called { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Called = true;
            Assert.Equal("https://192.168.1.10:5081/api/login", request.RequestUri!.ToString());
            Assert.Equal("1", Assert.Single(request.Headers.GetValues("X-RoomScope")));
            var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("teacher-test-password", json.RootElement.GetProperty("password").GetString());
            return new HttpResponseMessage(status);
        }
    }
}
