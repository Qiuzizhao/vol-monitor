using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Noise.Core;

namespace Noise.Agent;

public sealed record AgentConfig(string Server, string DeviceId, string ProtectedToken, string Name,
    string MicrophoneId, string? HardwareFingerprint = null)
{
    public static string DirectoryPath => DeploymentProfile.StateDirectory;
    private static string FilePath => Path.Combine(DirectoryPath, "agent.json");
    public string ReadToken()
    {
        if (!MachineIdentity.Matches(HardwareFingerprint, HardwareIdentity.Read()))
            throw new InvalidOperationException("此凭证来自另一台电脑，请重新接入。");
        return Unprotect(ProtectedToken, HardwareFingerprint!);
    }
    public static AgentConfig Create(string server, string deviceId, string token, string name, string microphoneId, string fingerprint) =>
        new(server, deviceId, Protect(token, fingerprint), name, microphoneId, fingerprint);
    public void Save() { Directory.CreateDirectory(DirectoryPath); File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
    public static void Clear() { if (File.Exists(FilePath)) File.Delete(FilePath); }
    public static AgentConfig? Load(string fingerprint, out string? notice)
    {
        notice = null;
        try
        {
            if (!File.Exists(FilePath)) return null;
            var config = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(FilePath));
            if (config is null || !MachineIdentity.Matches(config.HardwareFingerprint, fingerprint))
            {
                notice = "检测到镜像复制或旧版凭证，已清除本地设备身份，等待重新接入。";
                Clear(); PendingIdentity.Clear(); return null;
            }
            _ = Unprotect(config.ProtectedToken, fingerprint);
            return config;
        }
        catch (Exception e) when (e is IOException or JsonException or CryptographicException or FormatException)
        {
            notice = "设备凭证不可用，需重新接入：" + e.Message;
            Clear(); PendingIdentity.Clear(); return null;
        }
    }
    internal static string Protect(string value, string fingerprint) => Convert.ToBase64String(ProtectedData.Protect(
        Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(fingerprint), DataProtectionScope.CurrentUser));
    internal static string Unprotect(string value, string fingerprint) => Encoding.UTF8.GetString(ProtectedData.Unprotect(
        Convert.FromBase64String(value), Encoding.UTF8.GetBytes(fingerprint), DataProtectionScope.CurrentUser));
    public static Uri ValidateServer(string address)
    {
        if (!Uri.TryCreate(address.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.Scheme == "http" && !uri.IsLoopback ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
            throw new ArgumentException("请输入教师机 HTTPS 地址；HTTP 仅允许用于本机调试。");
        return uri;
    }
}

public sealed record PendingIdentity(string Server, string Fingerprint, string ProtectedClaim, string? RequestId)
{
    private static string FilePath => Path.Combine(AgentConfig.DirectoryPath, "enrollment.json");
    public string Claim() => AgentConfig.Unprotect(ProtectedClaim, Fingerprint);
    public void Save() { Directory.CreateDirectory(AgentConfig.DirectoryPath); File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
    public static void Clear() { if (File.Exists(FilePath)) File.Delete(FilePath); }
    public static PendingIdentity LoadOrCreate(string server, string fingerprint)
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var old = JsonSerializer.Deserialize<PendingIdentity>(File.ReadAllText(FilePath));
                if (old is not null && old.Server == server && MachineIdentity.Matches(old.Fingerprint, fingerprint))
                { _ = old.Claim(); return old; }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or CryptographicException or FormatException) { }
        var result = new PendingIdentity(server, fingerprint, AgentConfig.Protect(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), fingerprint), null);
        result.Save(); return result;
    }
}
