using System.Net;
using System.Net.Http.Json;
using Noise.Agent;
using Noise.Core;
using Xunit;

namespace Noise.Agent.Tests;

public sealed class AutomaticIpConnectionTests
{
    [Fact]
    public async Task NewConnectionUsesNoSavedClaimHardwareFingerprintOrDpapiToken()
    {
        using var handler = new ConnectionHandler(true);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5080/") };
        var config = await EnrollmentClient.TryAutomaticConnection(http, () => "mic", CancellationToken.None);
        Assert.NotNull(config); Assert.True(config.IpIdentity);
        Assert.Equal("stable-device", config.DeviceId); Assert.Equal("mic", config.MicrophoneId);
        Assert.Equal("", config.ReadToken()); Assert.Null(config.HardwareFingerprint);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task OlderTeacherStillUsesItsExistingProtocol()
    {
        using var handler = new ConnectionHandler(false);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5080/") };
        Assert.Null(await EnrollmentClient.TryAutomaticConnection(http, () => throw new Exception(), CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    private sealed class ConnectionHandler(bool automatic) : HttpMessageHandler
    {
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++; Assert.Null(request.Headers.Authorization);
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new EnrollmentWindow(true, null, automatic)) };
            Assert.Equal("/api/device-requests", request.RequestUri!.AbsolutePath);
            Assert.Equal("", (await request.Content!.ReadFromJsonAsync<EnrollmentRequest>(ct))!.HardwareFingerprint);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new EnrollmentState("approved", "ip-stable-device", "stable-device")) };
        }
    }
}
