using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http.Json;
using System.Security.Principal;
using Microsoft.Win32;

namespace Noise.Agent;

internal static class AgentSupervisor
{
    internal static string Scope => "RoomScope.Agent." + WindowsIdentity.GetCurrent().User!.Value;
    internal static string ShowEvent => "Local\\" + Scope + ".Show";
    private static string PipeName => Scope + ".Maintenance";

    internal static void ShowExisting()
    {
        try { using var signal = EventWaitHandle.OpenExisting(ShowEvent); signal.Set(); }
        catch (WaitHandleCannotBeOpenedException) { }
    }

    internal static int Run(DeploymentProfile profile)
    {
        using var instance = new Mutex(true, "Local\\" + Scope + ".Supervisor", out var created);
        if (!created) { ShowExisting(); return 0; }
        if (profile.EnableLoginStartup)
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            key.SetValue("RoomScopeAgent", '"' + Environment.ProcessPath! + '"');
        }
        using var stopping = new CancellationTokenSource();
        SessionEndingEventHandler onEnding = (_, _) => stopping.Cancel();
        SystemEvents.SessionEnding += onEnding;
        var maintenance = ListenForMaintenance(profile, stopping);
        try
        {
            RestartLoop(async ct =>
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
                start.ArgumentList.Add("--managed-child");
                using var child = Process.Start(start) ?? throw new IOException("学生端无法启动。");
                try { await child.WaitForExitAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The approved client closes its worker and tray icon itself.
                    // Force termination only if graceful shutdown does not finish.
                    using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await child.WaitForExitAsync(grace.Token); }
                    catch (OperationCanceledException)
                    {
                        try { if (!child.HasExited) child.Kill(); }
                        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    }
                    throw;
                }
            }, stopping.Token).GetAwaiter().GetResult();
        }
        finally
        {
            stopping.Cancel();
            maintenance.GetAwaiter().GetResult();
            SystemEvents.SessionEnding -= onEnding;
        }
        return 0;
    }

    // All unapproved exits restart, including exit code zero. Only an authenticated
    // maintenance request or Windows session ending cancels the loop.
    internal static async Task RestartLoop(Func<CancellationToken, Task> runChild, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var started = Stopwatch.StartNew();
            try { await runChild(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
            if (ct.IsCancellationRequested) break;
            failures = started.Elapsed > TimeSpan.FromMinutes(1) ? 0 : Math.Min(failures + 1, 5);
            try { await delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, failures))), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    internal static async Task<bool> VerifyTeacher(string server, string password, HttpMessageHandler? handler = null)
    {
        if (password.Length == 0) return false;
        using var http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
        { BaseAddress = AgentConfig.ValidateServer(server), Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.Add("X-RoomScope", "1");
        using var response = await http.PostAsJsonAsync("api/maintenance/verify", new { Password = password });
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            using var legacy = await http.PostAsJsonAsync("api/login", new { Password = password });
            return legacy.StatusCode == System.Net.HttpStatusCode.OK;
        }
        return response.StatusCode == System.Net.HttpStatusCode.OK;
    }

    private static async Task ListenForMaintenance(DeploymentProfile profile, CancellationTokenSource stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stopping.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var password = await GuardClient.ReadPassword(reader, timeout.Token);
                var approved = !string.IsNullOrEmpty(password) && (profile.SystemGuard
                    ? await GuardClient.RequestExit(password) : await VerifyTeacher(profile.Server, password));
                if (approved)
                {
                    await writer.WriteLineAsync("OK");
                    // Set cancellation only after the response is sent; the child may then close.
                    stopping.Cancel();
                }
                else await writer.WriteLineAsync("DENIED");
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or OperationCanceledException or HttpRequestException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { try { await Task.Delay(1000, stopping.Token); } catch (OperationCanceledException) { break; } }
        }
    }

    internal static async Task<bool> RequestMaintenance(string password)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(password.AsMemory(), timeout.Token);
        return await reader.ReadLineAsync(timeout.Token) == "OK";
    }
}
