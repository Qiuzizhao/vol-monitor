using System.Net;
using System.Net.Http.Json;
using Noise.Core;
using Xunit;

namespace Noise.Agent.Tests;

public sealed class AutomaticCaptureTests
{
    [Fact]
    public async Task TeacherStartPauseResumeEndControlCaptureWithoutStudentInteraction()
    {
        var audio = new FakeAudio();
        var states = new Queue<string>(["idle", "running", "paused", "running", "idle"]);
        var activeAtUpload = new List<bool>();
        using var http = Client(() =>
        {
            activeAtUpload.Add(audio.Running);
            return states.Count > 0 ? Command(states.Dequeue()) : new(HttpStatusCode.Unauthorized);
        });
        var worker = new AgentWorker(Config(), http, audio, (_, _) => Task.CompletedTask);
        var revoked = false; worker.AuthorizationLost += () => revoked = true;
        worker.Start();
        await worker.DisposeAsync();
        Assert.Equal([false, false, true, false, true, false], activeAtUpload);
        Assert.Equal(2, audio.Starts);
        Assert.False(audio.Running);
        Assert.True(revoked);
    }

    [Fact]
    public async Task NetworkFailureStopsCaptureBeforeRetry()
    {
        var audio = new FakeAudio(); var requests = 0; var activeAtDelay = new List<bool>();
        using var http = Client(() => ++requests switch
        {
            1 => Command("running"),
            2 => throw new HttpRequestException("simulated disconnect"),
            _ => new(HttpStatusCode.Unauthorized)
        });
        var worker = new AgentWorker(Config(), http, audio, (_, _) => { activeAtDelay.Add(audio.Running); return Task.CompletedTask; });
        worker.Start(); await worker.DisposeAsync();
        Assert.Equal([true, false], activeAtDelay);
        Assert.False(audio.Running);
    }

    [Fact]
    public async Task UnauthorizedDeviceNeverStartsCapture()
    {
        var audio = new FakeAudio();
        using var http = Client(() => new(HttpStatusCode.Unauthorized));
        var worker = new AgentWorker(Config(), http, audio, (_, _) => Task.CompletedTask);
        worker.Start(); await worker.DisposeAsync();
        Assert.Equal(0, audio.Starts);
    }

    private static AgentConfig Config() => new("https://teacher.invalid", "test", "not-read", "test", "fake-mic");
    private static HttpClient Client(Func<HttpResponseMessage> respond) => new(new FakeHandler(respond)) { BaseAddress = new("https://teacher.invalid") };
    private static HttpResponseMessage Command(string state) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new DeviceCommand("session", state, null, null, "01", "front", "test"))
    };
    private sealed class FakeHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond());
    }
    private sealed class FakeAudio : IAudioInput
    {
        public string? Error => null;
        public int SamplesPerSecond => 48000;
        public bool Running { get; private set; }
        public int Starts { get; private set; }
        public AudioSummary? Drain() => null;
        public void Start(string deviceId) { Starts++; Running = true; }
        public void Stop() => Running = false;
        public void Dispose() => Stop();
    }
}
