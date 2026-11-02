using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Noise.Agent;

internal static class SessionLauncher
{
    internal sealed record Session(int Id, string Sid, string Key);
    private static readonly string boot = BootKey();
    private static string BootKey()
    {
        using var query = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
        using var rows = query.Get();
        foreach (var row in rows) using (row) return row["LastBootUpTime"].ToString()!;
        throw new InvalidOperationException("无法读取系统启动时间。");
    }
    internal static IEnumerable<Session> ActiveSessions()
    {
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count)) throw new Win32Exception();
        try
        {
            var size = Marshal.SizeOf<WtsSession>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<WtsSession>(buffer + i * size);
                if (item.Id > 0 && item.State == 0) { var session = ReadSession(item.Id); if (session is not null) yield return session; }
            }
        }
        finally { WTSFreeMemory(buffer); }
    }
    private static Session? ReadSession(int id)
    {
        if (!WTSQueryUserToken((uint)id, out var token)) return null;
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            var sid = identity.User!.Value;
            var logon = AuthenticationId(token);
            return new(id, sid, boot + ":" + sid + ":" + logon);
        }
    }
    internal static string AuthenticationId(SafeAccessTokenHandle token)
    {
        if (!GetTokenInformation(token, 10, out var info, Marshal.SizeOf<TokenStatistics>(), out _)) throw new Win32Exception();
        return info.AuthenticationId.High.ToString("X8") + info.AuthenticationId.Low.ToString("X8");
    }
    internal static Session ClientSession(NamedPipeServerStream pipe, string agent)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var id)) throw new Win32Exception();
        using var process = Process.GetProcessById((int)id);
        if (!string.Equals(process.MainModule?.FileName, agent, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("维护请求程序无效。");
        var session = ReadSession(process.SessionId) ?? throw new UnauthorizedAccessException("Windows 登录已结束。");
        string? sid = null;
        pipe.RunAsClient(() => sid = WindowsIdentity.GetCurrent().User?.Value);
        if (sid != session.Sid) throw new UnauthorizedAccessException("维护请求身份无效。");
        return session;
    }
    internal static bool SupervisorAlive(int sessionId, string agent)
    {
        using var query = new ManagementObjectSearcher("SELECT ExecutablePath,CommandLine FROM Win32_Process WHERE Name='Noise.Agent.exe' AND SessionId=" + sessionId);
        using var rows = query.Get();
        foreach (var row in rows) using (row)
            if (string.Equals(row["ExecutablePath"] as string, agent, StringComparison.OrdinalIgnoreCase) &&
                (row["CommandLine"] as string)?.Contains("--guarded-session", StringComparison.Ordinal) == true) return true;
        return false;
    }
    internal static void StopOrphans(int sessionId, string agent)
    {
        foreach (var process in Process.GetProcessesByName("Noise.Agent"))
        using (process)
        {
            if (process.SessionId != sessionId) continue;
            var path = process.MainModule?.FileName;
            if (path is null) continue;
            if (!string.Equals(path, agent, StringComparison.OrdinalIgnoreCase) &&
                !File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "agent-deployment.json"))) continue;
            process.Kill(); process.WaitForExit(5000);
        }
    }
    internal static void Start(int sessionId, string executable)
    {
        if (!WTSQueryUserToken((uint)sessionId, out var token)) throw new Win32Exception();
        using (token)
        {
            if (!CreateEnvironmentBlock(out var environment, token, false)) throw new Win32Exception();
            try
            {
                var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = "winsta0\\default" };
                if (!CreateProcessAsUser(token, executable, new System.Text.StringBuilder('"' + executable + "\" --guarded-session"), IntPtr.Zero, IntPtr.Zero,
                    false, 0x00000400 | 0x08000000, environment, Path.GetDirectoryName(executable)!, ref startup, out var process)) throw new Win32Exception();
                CloseHandle(process.Thread); CloseHandle(process.Process);
            }
            finally { DestroyEnvironmentBlock(environment); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct WtsSession { public int Id; public IntPtr Station; public int State; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title; public int X,Y,XSize,YSize,XCount,YCount,Fill,Flags; public short Show,ReservedSize; public IntPtr ReservedData,Input,Output,Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process,Thread; public uint Id,ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenStatistics
    {
        public Luid TokenId,AuthenticationId; public long ExpirationTime; public int TokenType,ImpersonationLevel;
        public uint DynamicCharged,DynamicAvailable,GroupCount,PrivilegeCount; public Luid ModifiedId;
    }
    [DllImport("wtsapi32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool WTSEnumerateSessions(IntPtr server,int reserved,int version,out IntPtr buffer,out int count);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr buffer);
    [DllImport("wtsapi32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool WTSQueryUserToken(uint id,out SafeAccessTokenHandle token);
    [DllImport("userenv.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CreateEnvironmentBlock(out IntPtr environment,SafeAccessTokenHandle token,bool inherit);
    [DllImport("userenv.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessAsUser(SafeAccessTokenHandle token,string application,System.Text.StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,IntPtr environment,string directory,ref StartupInfo startup,out ProcessInfo process);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint id);
    [DllImport("advapi32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token,int informationClass,out TokenStatistics information,int length,out int needed);
}
