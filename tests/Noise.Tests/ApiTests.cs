using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Noise.Core;
using Noise.Server;
using Xunit;

namespace Noise.Tests;

public sealed class AppFactory : WebApplicationFactory<Program>
{
    public string DataPath { get; } = Path.Combine(Path.GetTempPath(), "roomscope-api-" + Guid.NewGuid());
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
        .UseSetting("DataDirectory", DataPath).UseSetting("TeacherPassword", "test-password-123")
        .UseSetting("Logging:LogLevel:Default", "Error");
    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    public async Task<HttpClient> Teacher()
    {
        var client = Client(); client.DefaultRequestHeaders.Add("X-RoomScope", "1");
        var response = await client.PostAsJsonAsync("/api/login", new { password = "test-password-123" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); return client;
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(DataPath))
        { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(DataPath, true); }
    }
}

public sealed class ApiTests
{
    [Fact]
    public async Task ImageEnrollmentIsPendingUntilTeacherApprovalAndClaimIsNotPublic()
    {
        using var factory = new AppFactory(); using var teacher = await factory.Teacher(); using var agent = factory.Client(); using var stranger = factory.Client();
        stranger.DefaultRequestHeaders.Add("X-RoomScope", "1");
        await teacher.PostAsync("/api/teacher/enrollment/open", null);
        var claim = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        agent.DefaultRequestHeaders.Authorization = new("Enrollment", claim);
        var submission = await agent.PostAsJsonAsync("/api/device-requests", new EnrollmentRequest("PC01", MachineIdentity.Fingerprint(null, ["001122334455"])));
        Assert.Equal(HttpStatusCode.OK, submission.StatusCode);
        var pending = (await submission.Content.ReadFromJsonAsync<EnrollmentState>())!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync("/api/device-requests/" + pending.RequestId)).StatusCode);
        agent.DefaultRequestHeaders.Authorization = new("Bearer", claim);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.PostAsJsonAsync("/api/telemetry", new DeviceReport("boot", 1, null, "idle"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.PostAsJsonAsync("/api/teacher/enrollment/approve", new EnrollmentBatch([new(pending.RequestId!, "seat-01")]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await teacher.PostAsJsonAsync("/api/teacher/enrollment/approve", new EnrollmentBatch([new(pending.RequestId!, "seat-01")]))).StatusCode);
        agent.DefaultRequestHeaders.Authorization = new("Enrollment", claim);
        var approved = await agent.GetFromJsonAsync<EnrollmentState>("/api/device-requests/" + pending.RequestId);
        Assert.Equal("approved", approved!.Status);
        agent.DefaultRequestHeaders.Authorization = new("Bearer", claim);
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsJsonAsync("/api/telemetry", new DeviceReport("boot", 1, null, "idle"))).StatusCode);
    }

    [Fact]
    public void FirstRunPasswordExistsBeforeAnyLoginRequest()
    {
        using var factory = new AppFactory(); using var client = factory.Client();
        Assert.True(File.Exists(Path.Combine(factory.DataPath, "initial-password.txt")));
        Assert.True(File.Exists(Path.Combine(factory.DataPath, "teacher-auth.json")));
    }

    [Fact]
    public async Task TeacherAuthAndCsrfHeaderAreRequired()
    {
        using var factory = new AppFactory(); using var client = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/teacher/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/login", new { password = "test-password-123" })).StatusCode);
        using var teacher = await factory.Teacher();
        Assert.Equal(HttpStatusCode.OK, (await teacher.GetAsync("/api/teacher/dashboard")).StatusCode);
    }

    [Fact]
    public async Task FullEnrollmentCalibrationAndExportFlow()
    {
        using var factory = new AppFactory(); using var teacher = await factory.Teacher(); using var agent = factory.Client();
        var pair = await teacher.GetFromJsonAsync<JsonElement>("/api/teacher/pairing");
        var enrolled = await agent.PostAsJsonAsync("/api/devices/enroll", new EnrollRequest(pair.GetProperty("code").GetString()!, "Test PC"));
        var identity = (await enrolled.Content.ReadFromJsonAsync<EnrollResult>())!;
        agent.DefaultRequestHeaders.Authorization = new("Bearer", identity.Token);
        Assert.Equal(HttpStatusCode.OK,(await teacher.PutAsJsonAsync($"/api/teacher/devices/{identity.DeviceId}/seat", new SeatAssignment("seat-01"))).StatusCode);
        var start = await teacher.PostAsJsonAsync("/api/teacher/sessions", new SessionRequest("Integration test"));
        var sessionId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var report = await agent.PostAsJsonAsync("/api/telemetry", new DeviceReport("boot",1,sessionId,"idle","mic"));
        Assert.Equal("running",(await report.Content.ReadFromJsonAsync<DeviceCommand>())!.State);
        Assert.Equal(HttpStatusCode.OK,(await teacher.PostAsync("/api/teacher/calibrations",null)).StatusCode);
        var snapshot = (await teacher.GetFromJsonAsync<Dashboard>("/api/teacher/dashboard"))!;
        var calibration = await agent.PostAsJsonAsync("/api/device/calibration", new CalibrationReport(sessionId,snapshot.CalibrationId!,"mic",-65,10));
        Assert.Equal(HttpStatusCode.OK,calibration.StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await agent.PostAsJsonAsync("/api/telemetry",new DeviceReport("boot",2,sessionId,"ok","mic",-35,-25,0))).StatusCode);
        snapshot = (await teacher.GetFromJsonAsync<Dashboard>("/api/teacher/dashboard"))!;
        Assert.Equal(100d,snapshot.Seats[0].Score);
        Assert.Equal(HttpStatusCode.OK,(await teacher.PostAsJsonAsync("/api/teacher/annotations", new AnnotationRequest("播放测试音源"))).StatusCode);
        var export = await teacher.GetAsync($"/api/teacher/sessions/{sessionId}/experiment.json");
        Assert.Equal("application/json",export.Content.Headers.ContentType!.MediaType);
        Assert.Contains("播放测试音源",JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(await export.Content.ReadAsStringAsync()),new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        await teacher.PostAsync("/api/teacher/sessions/current/pause",null);
        report=await agent.PostAsJsonAsync("/api/telemetry",new DeviceReport("boot",3,sessionId,"idle","mic"));
        Assert.Equal("idle",(await report.Content.ReadFromJsonAsync<DeviceCommand>())!.State);
        await teacher.DeleteAsync($"/api/teacher/devices/{identity.DeviceId}");
        Assert.Equal(HttpStatusCode.Unauthorized,(await agent.PostAsJsonAsync("/api/telemetry",new DeviceReport("boot",4,sessionId,"idle","mic"))).StatusCode);
    }

    [Fact]
    public async Task TelemetryDoesNotAcceptAudioPayloadsOrUnknownFields()
    {
        using var factory = new AppFactory(); using var agent = factory.Client();
        using var payload = new StringContent("{\"bootId\":\"b\",\"seq\":1,\"sessionId\":null,\"captureStatus\":\"idle\",\"audio\":\"base64\"}",Encoding.UTF8,"application/json");
        Assert.Equal(HttpStatusCode.BadRequest,(await agent.PostAsync("/api/telemetry",payload)).StatusCode);
    }
}
