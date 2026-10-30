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
    public void StudentCloseMinimizesInsteadOfExitingAndTrayIsHidden()
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
                Assert.Equal(FormWindowState.Minimized, form.WindowState);
                var tray = (NotifyIcon)typeof(MainForm).GetField("tray", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
                Assert.False(tray.Visible);
            }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
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
