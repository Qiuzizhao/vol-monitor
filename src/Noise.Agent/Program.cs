namespace Noise.Agent;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--guard-service")) { System.ServiceProcess.ServiceBase.Run(new StudentGuardService()); return 0; }
        if (args.Contains("--prepare-image"))
        {
            try { AgentConfig.Clear(); PendingIdentity.Clear(); return 0; }
            catch { return 1; }
        }
        ApplicationConfiguration.Initialize();
        DeploymentProfile? profile;
        try { profile = DeploymentProfile.Load(); }
        catch (Exception e) { MessageBox.Show(e.Message, "声域配置无法加载"); return 1; }
        if (profile?.SystemGuard == true && !args.Contains("--guarded-session") && !args.Contains("--managed-child"))
        {
            try { if (!GuardClient.RequestStart().GetAwaiter().GetResult()) return 1; AgentSupervisor.ShowExisting(); return 0; }
            catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { MessageBox.Show("学生端守护尚未就绪，请重新双击学生端安装入口。", "学生端"); return 1; }
        }
        if (profile?.ManagedMode == true && !args.Contains("--managed-child")) return AgentSupervisor.Run(profile);
        using var instance = new Mutex(true, "Local\\" + AgentSupervisor.Scope, out var created);
        if (!created) { AgentSupervisor.ShowExisting(); return 2; }
        using var form = new MainForm(profile?.ManagedMode == true);
        if (profile?.ManagedMode == true)
        {
            using var context = new ApplicationContext();
            form.FormClosed += (_, _) => context.ExitThread();
            form.StartInTray();
            Application.Run(context);
        }
        else
        {
            if (args.Contains("--minimized")) form.WindowState = FormWindowState.Minimized;
            Application.Run(form);
        }
        return 0;
    }
}
