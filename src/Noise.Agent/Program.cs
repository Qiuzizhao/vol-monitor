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
        using var instance = new Mutex(true, "Local\\RoomScope.Agent." + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, out var created);
        if (!created) return 2;
        ApplicationConfiguration.Initialize();
        var form = new MainForm();
        if (args.Contains("--minimized")) form.WindowState = FormWindowState.Minimized;
        Application.Run(form);
        return 0;
    }
}
