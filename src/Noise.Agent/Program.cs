namespace Noise.Agent;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--prepare-image"))
        {
            try { AgentConfig.Clear(); PendingIdentity.Clear(); return 0; }
            catch { return 1; }
        }
        ApplicationConfiguration.Initialize();
        DeploymentProfile? profile;
        try { profile = DeploymentProfile.Load(); }
        catch (Exception e) { MessageBox.Show(e.Message, "声域配置无法加载"); return 1; }
        if (profile?.ManagedMode == true && !args.Contains("--managed-child")) return AgentSupervisor.Run(profile);
        using var instance = new Mutex(true, "Local\\" + AgentSupervisor.Scope, out var created);
        if (!created) { AgentSupervisor.ShowExisting(); return 2; }
        var form = new MainForm(profile?.ManagedMode == true);
        if (args.Contains("--minimized")) form.WindowState = FormWindowState.Minimized;
        Application.Run(form);
        return 0;
    }
}
