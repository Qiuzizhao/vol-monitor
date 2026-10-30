using System.Net.Http.Headers;
using System.Net.Http.Json;
using Noise.Core;

namespace Noise.Agent;

public sealed class AgentWorker : IAsyncDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private readonly HttpClient http;
    private readonly AgentConfig config;
    private readonly AudioCapture audio = new();
    private readonly string boot = Guid.NewGuid().ToString("N");
    private readonly List<double> baselineSamples = [];
    private string? currentCalibration, completedCalibration;
    private Task? task;
    private bool allowed;
    public bool Allowed
    {
        get => Volatile.Read(ref allowed);
        set { Volatile.Write(ref allowed, value); if (!value) audio.Stop(); }
    }
    public event Action<string, string>? Updated;
    public event Action? AuthorizationLost;

    public AgentWorker(AgentConfig config)
    {
        this.config = config;
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = AgentConfig.ValidateServer(config.Server), Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.ReadToken());
    }
    public void Start() => task = Run(stopping.Token);

    private async Task Run(CancellationToken ct)
    {
        DeviceCommand? command = null; long sequence = 0; var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!Allowed) audio.Stop();
                var summary = audio.Drain();
                var duration = summary is not null && audio.SamplesPerSecond > 0
                    ? (int)(summary.SampleCount * 1000 / audio.SamplesPerSecond) : 1000;
                if (duration is < 500 or > 2000) summary = null;
                var status = command?.State == "running" && !Allowed ? "blocked" :
                    audio.Error is not null ? "error" : summary is null ? "idle" : summary.LevelDbfs <= -110 ? "no_signal" : "ok";
                if (currentCalibration is not null && currentCalibration != completedCalibration && summary is not null && status == "ok")
                {
                    baselineSamples.Add(summary.LevelDbfs);
                    if (baselineSamples.Count >= 10 && command?.SessionId is { } session)
                    {
                        var result = await http.PostAsJsonAsync("api/device/calibration", new CalibrationReport(session,
                            currentCalibration, config.MicrophoneId, Acoustics.Percentile(baselineSamples, .5), baselineSamples.Count), ct);
                        if (!result.IsSuccessStatusCode) throw new HttpRequestException("校准失败，请教师重新发起校准。");
                        completedCalibration = currentCalibration; baselineSamples.Clear();
                    }
                    else status = "calibrating";
                }
                using var response = await http.PostAsJsonAsync("api/telemetry", new DeviceReport(boot, ++sequence,
                    command?.SessionId, status, config.MicrophoneId, summary?.LevelDbfs,
                    summary?.PeakDbfs, summary?.ClippingRatio, duration), ct);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    audio.Stop(); Updated?.Invoke("设备授权已失效", "采集已停止，等待重新接入。");
                    AuthorizationLost?.Invoke(); return;
                }
                response.EnsureSuccessStatusCode();
                var next = await response.Content.ReadFromJsonAsync<DeviceCommand>(ct) ?? throw new HttpRequestException("教师机响应无效。");
                failures = 0;
                if (next.SessionId != command?.SessionId)
                { audio.Stop(); baselineSamples.Clear(); currentCalibration = completedCalibration = null; }
                command = next;
                if (next.State == "running" && Allowed)
                {
                    if (!audio.Running || audio.Error is not null) { audio.Start(config.MicrophoneId); baselineSamples.Clear(); }
                    if (!Allowed) { audio.Stop(); continue; }
                    var nextCalibration = next.CalibrationId;
                    if (nextCalibration != currentCalibration)
                    { currentCalibration = nextCalibration; baselineSamples.Clear(); }
                    Updated?.Invoke(currentCalibration is not null && currentCalibration != completedCalibration
                        ? $"正在校准背景音量 {baselineSamples.Count}/10 秒" : "正在采集音量 · 不保存录音",
                        $"座位 {next.Seat} · {next.Zone} · 每秒上报一次");
                }
                else
                {
                    audio.Stop(); baselineSamples.Clear(); currentCalibration = null;
                    Updated?.Invoke(!Allowed ? "本机采集已关闭" : "待机 · 麦克风未采集",
                        next.Seat is null ? "已连接教师机，等待绑定座位" : $"座位 {next.Seat} · 等待教师开始实验");
                }
                await Task.Delay(next.State == "running" ? 1000 : 2000, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or
                                      System.Runtime.InteropServices.COMException or NotSupportedException)
            {
                // No capture continues when server control is unavailable.
                audio.Stop(); baselineSamples.Clear(); currentCalibration = null;
                var delay = Math.Min(30, Math.Pow(2, Math.Min(++failures, 5)));
                Updated?.Invoke("连接或采集异常 · 麦克风已停止", e.Message + $"（{delay:0} 秒后重试）");
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch (OperationCanceledException) { break; }
            }
        }
        audio.Stop();
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        if (task is not null) await task;
        audio.Dispose(); http.Dispose(); stopping.Dispose();
    }
}
