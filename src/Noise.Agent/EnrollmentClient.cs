using System.Net.Http.Headers;
using System.Net.Http.Json;
using Noise.Core;

namespace Noise.Agent;

public static class EnrollmentClient
{
    public static async Task<AgentConfig> WaitForApproval(DeploymentProfile profile, string fingerprint,
        Func<string> microphoneId, Action<string, string> update, CancellationToken ct)
    {
        var uri = AgentConfig.ValidateServer(profile.Server);
        var identity = PendingIdentity.LoadOrCreate(uri.ToString(), fingerprint);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Enrollment", identity.Claim());
        await Task.Delay(Random.Shared.Next(200, 2200), ct);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                EnrollmentState? result;
                if (identity.RequestId is null)
                {
                    var window = await http.GetFromJsonAsync<EnrollmentWindow>("api/enrollment", ct);
                    if (window?.Open != true)
                    {
                        update("等待教师开启批量接入", "通用镜像配置已加载 · 尚未取得设备权限 · 麦克风未采集");
                        await Task.Delay(15000, ct); continue;
                    }
                    using var response = await http.PostAsJsonAsync("api/device-requests", new EnrollmentRequest(Environment.MachineName, fingerprint), ct);
                    response.EnsureSuccessStatusCode();
                    result = await response.Content.ReadFromJsonAsync<EnrollmentState>(ct);
                }
                else
                {
                    using var response = await http.GetAsync("api/device-requests/" + identity.RequestId, ct);
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        PendingIdentity.Clear(); identity = PendingIdentity.LoadOrCreate(uri.ToString(), fingerprint);
                        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Enrollment", identity.Claim());
                        await Task.Delay(15000, ct); continue;
                    }
                    response.EnsureSuccessStatusCode();
                    result = await response.Content.ReadFromJsonAsync<EnrollmentState>(ct);
                }
                if (result is null) throw new HttpRequestException("教师机响应无效。");
                identity = identity with { RequestId = result.RequestId }; identity.Save();
                if (result.Status == "approved" && result.DeviceId is not null)
                {
                    ct.ThrowIfCancellationRequested();
                    var config = AgentConfig.Create(uri.ToString(), result.DeviceId, identity.Claim(), Environment.MachineName, microphoneId(), fingerprint);
                    config.Save(); PendingIdentity.Clear(); return config;
                }
                if (result.Status == "rejected") throw new InvalidOperationException("教师已拒绝此接入申请，请联系教师；需要重试时点击“重新申请”。");
                if (result.Status is "expired" or "revoked")
                {
                    PendingIdentity.Clear(); identity = PendingIdentity.LoadOrCreate(uri.ToString(), fingerprint);
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Enrollment", identity.Claim());
                    await Task.Delay(15000, ct); continue;
                }
                update("接入申请已提交，等待教师确认", $"设备 {Environment.MachineName} · 硬件摘要 {fingerprint[..12]} · 麦克风未采集");
                await Task.Delay(5000, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                update("暂时无法连接教师机", e.Message + " · 检查地址及证书，15 秒后重试 · 麦克风未采集");
                await Task.Delay(15000, ct);
            }
        }
    }
}
