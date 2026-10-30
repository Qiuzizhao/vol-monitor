using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Noise.Agent;

public sealed record DeploymentProfile(string Server, bool AutoEnroll = true, string? IdentityDirectory = null,
    bool ManagedMode = false, bool EnableLoginStartup = false)
{
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "agent-deployment.json");
    public static DeploymentProfile? Load()
    {
        if (!File.Exists(FilePath)) return null;
        var result = JsonSerializer.Deserialize<DeploymentProfile>(File.ReadAllText(FilePath), new JsonSerializerOptions
        { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidOperationException("镜像部署配置为空。");
        AgentConfig.ValidateServer(result.Server);
        if (!string.IsNullOrWhiteSpace(result.IdentityDirectory))
        {
            var path = Environment.ExpandEnvironmentVariables(result.IdentityDirectory);
            if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
                new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network)
                throw new InvalidOperationException("身份目录必须是完整的本地路径，不支持网络共享或映射网络盘。");
        }
        return result;
    }
    public static string StateDirectory
    {
        get
        {
            var path = Load()?.IdentityDirectory;
            return string.IsNullOrWhiteSpace(path)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoomScope")
                : Path.Combine(Environment.ExpandEnvironmentVariables(path), WindowsIdentity.GetCurrent().User!.Value);
        }
    }
}
