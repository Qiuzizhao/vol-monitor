using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Noise.Agent;
using Xunit;

namespace Noise.Agent.Tests;

public sealed class SystemGuardTests
{
    [Fact]
    public void WindowsLoginKeyUsesTheTokenAuthenticationId()
    {
        using var identity=WindowsIdentity.GetCurrent();
        var first=SessionLauncher.AuthenticationId(identity.AccessToken);
        Assert.Equal(16,first.Length); Assert.Equal(first,SessionLauncher.AuthenticationId(identity.AccessToken));
    }
    [Theory]
    [InlineData(100u,100u,4u,true)]
    [InlineData(101u,100u,4u,false)]
    [InlineData(100u,100u,1u,false)]
    [InlineData(0u,0u,4u,false)]
    public void ALocalFakePipeCannotAuthorizeExit(uint pipe,uint service,uint state,bool expected)
        => Assert.Equal(expected,GuardServiceIdentity.Matches(pipe,service,state));

    [Fact]
    public void ApprovedExitSurvivesServiceRestartButDoesNotAffectANewLogin()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoomScope-guard-test-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(root, "state.json");
        try
        {
            var state = new GuardState(file); Assert.False(state.IsPaused("boot:login-A"));
            Assert.True(state.ShouldRestart("boot:login-A", false)); Assert.False(state.ShouldRestart("boot:login-A", true));
            state.Pause("boot:login-A");
            var restarted = new GuardState(file);
            Assert.True(restarted.IsPaused("boot:login-A")); Assert.False(restarted.IsPaused("boot:login-B"));
            Assert.False(restarted.ShouldRestart("boot:login-A", false)); Assert.True(restarted.ShouldRestart("boot:login-B", false));
            Assert.False(restarted.IsPaused("next-boot:login-A"));
            restarted.Resume("boot:login-A"); Assert.False(new GuardState(file).IsPaused("boot:login-A"));
        }
        finally { if (Directory.Exists(root) && Path.GetFullPath(root).StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MaintenancePipeIdentifiesTheCallingWindowsAccount()
    {
        var name = "RoomScope-test-" + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token); await waiting;
        string? sid = null; server.RunAsClient(() => sid = WindowsIdentity.GetCurrent().User!.Value);
        Assert.Equal(WindowsIdentity.GetCurrent().User!.Value, sid);
        using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(server, leaveOpen: true);
        await writer.WriteLineAsync("1".AsMemory(), timeout.Token);
        Assert.Equal("1", await GuardClient.ReadPassword(reader, timeout.Token));
    }
}
