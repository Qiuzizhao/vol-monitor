using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Noise.Server;
using Xunit;

namespace Noise.Tests;

public sealed class MaintenanceTests
{
    [Fact]
    public async Task LanLiveConnectionDoesNotUseOrDependOnTheExitPassword()
    {
        using var factory=new AppFactory(IPAddress.Parse("192.168.1.50"));
        using var http=factory.Client(); Assert.Equal(HttpStatusCode.OK,(await http.GetAsync("/api/teacher/dashboard")).StatusCode);
        var client=factory.Server.CreateWebSocketClient(); client.ConfigureRequest=request=>request.Headers["Origin"]="https://localhost";
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var socket=await client.ConnectAsync(new Uri("wss://localhost/ws/dashboard"),timeout.Token);
        var buffer=new byte[200000]; await socket.ReceiveAsync(buffer,timeout.Token);
        var access=factory.Services.GetRequiredService<TeacherAccess>(); access.SetPassword("1","1");
        var packet=await socket.ReceiveAsync(buffer,timeout.Token); Assert.Equal(WebSocketMessageType.Text,packet.MessageType);
        Assert.False(access.Verify("test-password-123")); Assert.True(access.Verify("1")); socket.Abort();
    }

    [Fact]
    public async Task StudentPackageCarriesStablePublicAuthorityWithoutPrivateKeys()
    {
        using var factory=new AppFactory(IPAddress.Loopback,packageFixture:true);
        using var teacher=factory.Client(); teacher.DefaultRequestHeaders.Add("X-RoomScope","1");
        var source=Path.Combine(factory.DataPath,"package-source");
        File.WriteAllText(Path.Combine(source,"roomscope-root.cer"),"public classroom root");
        File.WriteAllText(Path.Combine(source,"authority.pfx"),"must not enter student package");
        var response=await teacher.PostAsJsonAsync("/api/teacher/student-package",new { password="1",confirmation="1" });
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var zip=new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        Assert.NotNull(zip.GetEntry("程序/roomscope-root.cer")); Assert.DoesNotContain(zip.Entries,e=>e.FullName.EndsWith(".pfx",StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalTeacherCanChangePasswordAndItImmediatelyReplacesTheOldPasswordAndPersists()
    {
        using var factory = new AppFactory(IPAddress.Loopback); using var teacher = factory.Client();
        teacher.DefaultRequestHeaders.Add("X-RoomScope", "1");
        var status = await teacher.GetFromJsonAsync<JsonElement>("/api/teacher/maintenance");
        Assert.False(status.GetProperty("configured").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await teacher.PutAsJsonAsync("/api/teacher/maintenance", new { password = "", confirmation = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await teacher.PutAsJsonAsync("/api/teacher/maintenance", new { password = "new-password-123", confirmation = "does-not-match" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await teacher.PostAsJsonAsync("/api/login", new { password = "test-password-123" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await teacher.PutAsJsonAsync("/api/teacher/maintenance", new { password = "new-password-123", confirmation = "new-password-123" })).StatusCode);
        using var student = factory.Client(); student.DefaultRequestHeaders.Add("X-RoomScope", "1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await student.PostAsJsonAsync("/api/login", new { password = "test-password-123" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await student.PostAsJsonAsync("/api/login", new { password = "new-password-123" })).StatusCode);
        var reloaded = new TeacherAccess(factory.DataPath, "ignored-configuration");
        Assert.True(reloaded.Configured); Assert.True(reloaded.Verify("new-password-123")); Assert.False(reloaded.Verify("test-password-123"));
        Assert.False(File.Exists(Path.Combine(factory.DataPath, "initial-password.txt")));
        Assert.DoesNotContain("new-password-123", File.ReadAllText(Path.Combine(factory.DataPath, "teacher-auth.json")));
    }

    [Fact]
    public async Task PasswordManagementAndPackageGenerationRequireTheActualLocalTeacherAndCsrfHeader()
    {
        using var factory = new AppFactory(IPAddress.Parse("192.168.1.20")); using var remote = await factory.Teacher();
        Assert.Equal(HttpStatusCode.Forbidden, (await remote.GetAsync("/api/teacher/maintenance")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await remote.PutAsJsonAsync("/api/teacher/maintenance", new { password = "new-password-123", confirmation = "new-password-123" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await remote.PostAsJsonAsync("/api/teacher/student-package", new { })).StatusCode);
        using var localFactory = new AppFactory(IPAddress.Loopback); using var local = localFactory.Client();
        Assert.Equal(HttpStatusCode.Forbidden, (await local.PutAsJsonAsync("/api/teacher/maintenance", new { password = "new-password-123", confirmation = "new-password-123" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await local.PostAsJsonAsync("/api/teacher/student-package", new { })).StatusCode);
    }

    [Fact]
    public async Task MaintenancePasswordChangeDoesNotAffectPasswordlessLanAccess()
    {
        using var factory = new AppFactory(); using var teacher = await factory.Teacher();
        Assert.Equal(HttpStatusCode.OK, (await teacher.GetAsync("/api/teacher/dashboard")).StatusCode);
        factory.Services.GetRequiredService<TeacherAccess>().SetPassword("new-password-123", "new-password-123");
        Assert.Equal(HttpStatusCode.OK, (await teacher.GetAsync("/api/teacher/dashboard")).StatusCode);
        using var stranger = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await stranger.GetAsync("/api/teacher/dashboard")).StatusCode);
        stranger.DefaultRequestHeaders.Add("X-RoomScope", "1");
        var verified = await stranger.PostAsJsonAsync("/api/maintenance/verify", new { password = "new-password-123" });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode); Assert.False(verified.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.PostAsJsonAsync("/api/maintenance/verify", new { password = "test-password-123" })).StatusCode);
    }

    [Fact]
    public async Task FirstPackageRequiresPasswordAndIncludesOnlyTheStudentTemplateAndPublicConnectionSettings()
    {
        using var factory = new AppFactory(IPAddress.Loopback, packageFixture: true); using var teacher = factory.Client();
        teacher.DefaultRequestHeaders.Add("X-RoomScope", "1");
        Assert.Equal(HttpStatusCode.BadRequest, (await teacher.PostAsJsonAsync("/api/teacher/student-package", new { })).StatusCode);
        var response = await teacher.PostAsJsonAsync("/api/teacher/student-package", new { password = "chosen-password-123", confirmation = "chosen-password-123" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        Assert.Equal(5, zip.Entries.Count); Assert.NotNull(zip.GetEntry("启动学生端.cmd"));
        using var profile = JsonDocument.Parse(zip.GetEntry("程序/agent-deployment.json")!.Open());
        Assert.Equal("https://192.168.1.10:5081", profile.RootElement.GetProperty("Server").GetString());
        foreach (var flag in new[] { "AutoEnroll", "ManagedMode", "SystemGuard" }) Assert.True(profile.RootElement.GetProperty(flag).GetBoolean());
        Assert.False(profile.RootElement.GetProperty("EnableLoginStartup").GetBoolean());
        foreach (var entry in zip.Entries)
        {
            Assert.DoesNotContain(".pfx", entry.FullName); Assert.DoesNotContain("data/", entry.FullName);
            using var reader = new StreamReader(entry.Open()); Assert.DoesNotContain("chosen-password-123", reader.ReadToEnd());
        }
        var access = factory.Services.GetRequiredService<TeacherAccess>(); Assert.True(access.Configured); Assert.True(access.Verify("chosen-password-123"));
        Assert.False(access.Verify("test-password-123"));
        Assert.Equal(HttpStatusCode.OK, (await teacher.PostAsJsonAsync("/api/teacher/student-package", new { })).StatusCode);
        Assert.True(access.Verify("chosen-password-123"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(9)]
    [InlineData(300)]
    public async Task PackageAndPasswordChangesAcceptAnyNonemptyLength(int length)
    {
        using var factory = new AppFactory(IPAddress.Loopback, packageFixture: true); using var teacher = factory.Client();
        teacher.DefaultRequestHeaders.Add("X-RoomScope", "1");
        var password = new string('1', length);
        Assert.Equal(HttpStatusCode.OK, (await teacher.PostAsJsonAsync("/api/teacher/student-package", new { password, confirmation = password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await teacher.PostAsJsonAsync("/api/login", new { password })).StatusCode);
        var next = new string('密', length);
        Assert.Equal(HttpStatusCode.OK, (await teacher.PutAsJsonAsync("/api/teacher/maintenance", new { password = next, confirmation = next })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await teacher.PostAsJsonAsync("/api/login", new { password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await teacher.PostAsJsonAsync("/api/login", new { password = next })).StatusCode);
        var reloaded = new TeacherAccess(factory.DataPath, null);
        Assert.True(reloaded.Verify(next)); Assert.False(reloaded.Verify(password));
    }

    [Fact]
    public async Task MissingPackageFilesDoNotChangeTheCurrentPassword()
    {
        using var factory = new AppFactory(IPAddress.Loopback); using var teacher = factory.Client();
        teacher.DefaultRequestHeaders.Add("X-RoomScope", "1");
        Assert.Equal(HttpStatusCode.BadRequest, (await teacher.PostAsJsonAsync("/api/teacher/student-package", new { password = "new-password-123", confirmation = "new-password-123" })).StatusCode);
        var access = factory.Services.GetRequiredService<TeacherAccess>();
        Assert.False(access.Configured); Assert.True(access.Verify("test-password-123"));
    }
}
