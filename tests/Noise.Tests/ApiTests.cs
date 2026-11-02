using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Noise.Core;
using Noise.Server;
using Xunit;

namespace Noise.Tests;

public sealed class AppFactory(IPAddress? peer = null, bool packageFixture = false) : WebApplicationFactory<Program>
{
    public string DataPath { get; } = Path.Combine(Path.GetTempPath(), "roomscope-api-" + Guid.NewGuid());
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder
        .UseSetting("DataDirectory", DataPath).UseSetting("TeacherPassword", "test-password-123")
        .UseSetting("ClassroomServerUrl", "https://192.168.1.10:5081")
        .UseSetting("Logging:LogLevel:Default", "Error");
        if (peer is not null) builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new PeerFilter(peer)));
        if (packageFixture) builder.ConfigureServices(services =>
        {
            var root = Path.Combine(DataPath, "package-source"); Directory.CreateDirectory(root);
            using (var zip = System.IO.Compression.ZipFile.Open(Path.Combine(root, "student-template.zip"), System.IO.Compression.ZipArchiveMode.Create))
                foreach (var name in new[] { "启动学生端.cmd", "使用说明.txt", "程序/Noise.Agent.exe" })
                { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("学生端程序样例"); }
            File.WriteAllText(Path.Combine(root, "roomscope-teacher.cer"), "public certificate fixture");
            File.WriteAllText(Path.Combine(root, "teacher.pfx"), "PRIVATE KEY MUST NOT BE INCLUDED");
            services.AddSingleton(new StudentPackageBuilder(root, "https://192.168.1.10:5081"));
        });
    }
    private sealed class PeerFilter(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) => { context.Connection.RemoteIpAddress = address; return nextMiddleware(); });
            next(app);
        };
    }
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
        if (disposing)
        {
            var fullPath = Path.GetFullPath(DataPath);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(fullPath).StartsWith("roomscope-api-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test data directory.");
            for (var attempt = 0; Directory.Exists(fullPath); attempt++)
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(fullPath, true); break; }
                catch (IOException) when (attempt < 5) { Thread.Sleep(50); }
            }
        }
    }
}

public sealed class ApiTests
{
    [Fact]
    public async Task PairingCodeEndpointsHaveBeenRemoved()
    {
        using var factory = new AppFactory(); using var teacher = await factory.Teacher();
        Assert.Equal(HttpStatusCode.NotFound, (await teacher.GetAsync("/api/teacher/pairing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await teacher.PostAsync("/api/teacher/pairing/rotate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await teacher.PostAsJsonAsync("/api/devices/enroll", new { pairingCode = "old-code", name = "PC01" })).StatusCode);
    }

    [Fact]
    public async Task LocalTeacherNeedsNoPasswordButStillRequiresCsrfHeader()
    {
        using var factory = new AppFactory(IPAddress.Loopback); using var client = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/teacher/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/teacher/enrollment/open", null)).StatusCode);
        client.DefaultRequestHeaders.Add("X-RoomScope", "1");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/teacher/enrollment/open", null)).StatusCode);
        var connection = await client.GetFromJsonAsync<JsonElement>("/api/teacher/connection");
        Assert.Equal("https://192.168.1.10:5081", connection.GetProperty("server").GetString());
    }

    [Fact]
    public async Task LocalPeerWithForeignHostDoesNotGainTeacherAccess()
    {
        using var factory = new AppFactory(IPAddress.Loopback); using var client = factory.Client();
        client.DefaultRequestHeaders.Host = "untrusted.invalid";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/teacher/dashboard")).StatusCode);
    }

    [Fact]
    public async Task ExternalPageCannotUseLocalPasswordlessAccess()
    {
        using var factory = new AppFactory(IPAddress.Loopback); using var client = factory.Client();
        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/teacher/dashboard")).StatusCode);
    }

    [Fact]
    public async Task ForwardedLocalAddressDoesNotGrantRemoteTeacherAccess()
    {
        using var factory = new AppFactory(IPAddress.Parse("192.168.1.20")); using var client = factory.Client();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "127.0.0.1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/teacher/dashboard")).StatusCode);
    }

    [Fact]
    public async Task PasswordlessDashboardDoesNotBypassMaintenancePasswordVerification()
    {
        using var factory = new AppFactory(IPAddress.Loopback); using var client = factory.Client();
        client.DefaultRequestHeaders.Add("X-RoomScope", "1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/login", new { password = "wrong" })).StatusCode);
    }

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
        await teacher.PostAsync("/api/teacher/enrollment/open", null);
        var claim = new string('A', 64);
        agent.DefaultRequestHeaders.Authorization = new("Enrollment", claim);
        var submitted = await agent.PostAsJsonAsync("/api/device-requests", new EnrollmentRequest("Test PC", new string('B', 64)));
        var pending = (await submitted.Content.ReadFromJsonAsync<EnrollmentState>())!;
        var approved = await teacher.PostAsJsonAsync("/api/teacher/enrollment/approve", new EnrollmentBatch([new(pending.RequestId!, "seat-01")]));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var registered = (await agent.GetFromJsonAsync<EnrollmentState>("/api/device-requests/" + pending.RequestId))!;
        var identity = new { DeviceId = registered.DeviceId!, Token = claim };
        agent.DefaultRequestHeaders.Authorization = new("Bearer", identity.Token);
        Assert.Equal(HttpStatusCode.OK,(await teacher.PutAsJsonAsync($"/api/teacher/devices/{identity.DeviceId}/seat", new SeatAssignment("seat-01"))).StatusCode);
        var live = (await teacher.GetFromJsonAsync<Dashboard>("/api/teacher/dashboard"))!;
        var sessionId = live.Session!.Id;
        Assert.Equal("running", live.Session.Status);
        var report = await agent.PostAsJsonAsync("/api/telemetry", new DeviceReport("boot",1,sessionId,"idle","mic"));
        Assert.Equal("running",(await report.Content.ReadFromJsonAsync<DeviceCommand>())!.State);
        Assert.Equal(HttpStatusCode.NotFound,(await teacher.PostAsync("/api/teacher/calibrations",null)).StatusCode);
        var snapshot = (await teacher.GetFromJsonAsync<Dashboard>("/api/teacher/dashboard"))!;
        Assert.Null(snapshot.CalibrationId);
        Assert.Equal(HttpStatusCode.OK,(await agent.PostAsJsonAsync("/api/telemetry",new DeviceReport("boot",2,sessionId,"ok","mic",-35,-25,0))).StatusCode);
        snapshot = (await teacher.GetFromJsonAsync<Dashboard>("/api/teacher/dashboard"))!;
        Assert.Equal(88.2,snapshot.Seats[0].Score);
        Assert.Equal(HttpStatusCode.OK,(await teacher.PostAsJsonAsync("/api/teacher/annotations", new AnnotationRequest("播放测试音源"))).StatusCode);
        var export = await teacher.GetAsync($"/api/teacher/sessions/{sessionId}/experiment.json");
        Assert.Equal("application/json",export.Content.Headers.ContentType!.MediaType);
        Assert.Contains("播放测试音源",JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(await export.Content.ReadAsStringAsync()),new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.Equal(HttpStatusCode.NotFound, (await teacher.PostAsJsonAsync("/api/teacher/sessions", new SessionRequest("old start"))).StatusCode);
        foreach (var action in new[] { "pause", "resume", "end" })
            Assert.Equal(HttpStatusCode.NotFound, (await teacher.PostAsync("/api/teacher/sessions/current/" + action,null)).StatusCode);
        report=await agent.PostAsJsonAsync("/api/telemetry",new DeviceReport("boot",3,sessionId,"idle","mic"));
        Assert.Equal("running",(await report.Content.ReadFromJsonAsync<DeviceCommand>())!.State);
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
