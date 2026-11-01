using System.Net.Http.Headers;
using System.Net.Http.Json;
using Noise.Core;

namespace Noise.Agent;

public sealed class AgentWorker : IAsyncDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private readonly HttpClient http;
    private readonly AgentConfig config;
    private readonly IAudioInput audio;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly string boot = Guid.NewGuid().ToString("N");
    private readonly List<double> baselineSamples = [];
    private string? currentCalibration, completedCalibration;
    private Task? task;
    public event Action<string, string>? Updated;
    public event Action? AuthorizationLost;

    public AgentWorker(AgentConfig config) : this(config,
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { BaseAddress = AgentConfig.ValidateServer(config.Server), Timeout = TimeSpan.FromSeconds(5) }, new AudioCapture())
    {
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.ReadToken());
    }
    internal AgentWorker(AgentConfig config, HttpClient http, IAudioInput audio,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    { this.config = config; this.http = http; this.audio = audio; this.delay = delay ?? Task.Delay; }
    public void Start() => task = Run(stopping.Token);

    private async Task Run(CancellationToken ct)
    {
        DeviceCommand? command = null; long sequence = 0; var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var summary = audio.Drain();
                var duration = summary is not null && audio.SamplesPerSecond > 0
                    ? (int)(summary.SampleCount * 1000 / audio.SamplesPerSecond) : 1000;
                if (duration is < 500 or > 2000) summary = null;
                var status = audio.Error is not null ? "error" : summary is null ? "idle" : summary.LevelDbfs <= -110 ? "no_signal" : "ok";
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
                if (next.State == "running")
                {
                    if (!audio.Running || audio.Error is not null) { audio.Start(config.MicrophoneId); baselineSamples.Clear(); }
                    var nextCalibration = next.CalibrationId;
                    if (nextCalibration != currentCalibration)
                    { currentCalibration = nextCalibration; baselineSamples.Clear(); }
                    Updated?.Invoke(currentCalibration is not null && currentCalibration != completedCalibration
                        ? $"正在校准 {baselineSamples.Count}/10 秒" : "音量采集中",
                        $"座位 {next.Seat} · {next.Zone}");
                }
                else
                {
                    audio.Stop(); baselineSamples.Clear(); currentCalibration = null;
                    Updated?.Invoke("待机",
                        next.Seat is null ? "已连接教师机，等待绑定座位" : $"座位 {next.Seat} · 等待教师开始监测");
                }
                await delay(TimeSpan.FromSeconds(next.State == "running" ? 1 : 2), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or
                                      System.Runtime.InteropServices.COMException or NotSupportedException)
            {
                // No capture continues when server control is unavailable.
                audio.Stop(); baselineSamples.Clear(); currentCalibration = null;
                var seconds = Math.Min(30, Math.Pow(2, Math.Min(++failures, 5)));
                Updated?.Invoke("连接异常，已停止采集", $"{seconds:0} 秒后自动重连。");
                try { await delay(TimeSpan.FromSeconds(seconds), ct); } catch (OperationCanceledException) { break; }
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
