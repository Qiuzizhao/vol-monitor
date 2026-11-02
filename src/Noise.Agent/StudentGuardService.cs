using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Noise.Core;

namespace Noise.Agent;

internal sealed class StudentGuardService : ServiceBase
{
    private readonly CancellationTokenSource stopping = new();
    private readonly string stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RoomScope", "Guard");
    private Task? running;
    public StudentGuardService() { ServiceName = GuardClient.ServiceName; CanStop = true; AutoLog = false; }
    protected override void OnStart(string[] args)
    {
        running = Run(stopping.Token);
        _ = running.ContinueWith(t => { DiagnosticLog.Write(stateDirectory, "系统守护异常停止", t.Exception); Environment.Exit(1); }, TaskContinuationOptions.OnlyOnFaulted);
    }
    protected override void OnStop() { stopping.Cancel(); try { running?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { } }
    private async Task Run(CancellationToken ct)
    {
        var profile = DeploymentProfile.Load() ?? throw new InvalidOperationException("缺少守护配置。");
        if (!profile.SystemGuard) throw new InvalidOperationException("未安装系统守护配置。");
        var state = new GuardState(Path.Combine(stateDirectory, "approved-exits.json"));
        await Task.WhenAll(Watch(state, ct), Listen(profile, state, ct));
    }
    private async Task Watch(GuardState state, CancellationToken ct)
    {
        var agent = Environment.ProcessPath!;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try
            {
                foreach (var session in SessionLauncher.ActiveSessions())
                {
                    if (state.IsPaused(session.Key)) continue;
                    var supervisorAlive = SessionLauncher.SupervisorAlive(session.Id, agent);
                    if (state.ShouldRestart(session.Key, supervisorAlive))
                    {
                        SessionLauncher.StopOrphans(session.Id, agent);
                        SessionLauncher.Start(session.Id, agent);
                    }
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or System.Management.ManagementException or UnauthorizedAccessException)
            { DiagnosticLog.Write(stateDirectory, "检测学生会话失败，稍后重试", e); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
    private async Task Listen(DeploymentProfile profile, GuardState state, CancellationToken ct)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(GuardClient.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 4096, 4096, security);
                File.WriteAllText(Path.Combine(stateDirectory, "guard-ready.json"), System.Text.Json.JsonSerializer.Serialize(new { ProcessId = Environment.ProcessId }));
                await pipe.WaitForConnectionAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(12));
                var session = SessionLauncher.ClientSession(pipe, Environment.ProcessPath!);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var packet = await GuardClient.ReadPassword(reader, timeout.Token);
                var request = System.Text.Json.JsonSerializer.Deserialize<GuardClient.Request>(packet);
                var approved = request?.Action == "start";
                if (approved) state.Resume(session.Key);
                else if (request?.Action == "exit" && request.Password is { } password)
                { approved = await AgentSupervisor.VerifyTeacher(profile.Server, password); if (approved) state.Pause(session.Key); }
                await writer.WriteLineAsync(approved ? "OK" : "DENIED");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or OperationCanceledException or HttpRequestException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
            { DiagnosticLog.Write(stateDirectory, "维护请求未完成", e); await Task.Delay(1000, ct); }
        }
    }
}
