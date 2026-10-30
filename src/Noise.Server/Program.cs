using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Noise.Core;
using Noise.Server;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
try
{
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new ChineseConsoleLoggerProvider());
if (string.IsNullOrEmpty(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5080");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 8192);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "RoomScope.Teacher";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 15, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("enrollment", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddSingleton<RoomStore>(services =>
{
    var config = services.GetRequiredService<IConfiguration>();
    var directory = DataDirectory(config, services.GetRequiredService<IWebHostEnvironment>());
    return new RoomStore(directory, config["Room:Name"] ?? "信息科技机房 01",
        config.GetValue("Room:Rows", 6), config.GetValue("Room:Columns", 6));
});
builder.Services.AddSingleton<TeacherAccess>(services =>
{
    var config = services.GetRequiredService<IConfiguration>();
    var directory = DataDirectory(config, services.GetRequiredService<IWebHostEnvironment>());
    Directory.CreateDirectory(directory);
    return new TeacherAccess(directory, config["TeacherPassword"]);
});
builder.Services.AddHostedService<RoomTicker>();
var app = builder.Build();
_ = app.Services.GetRequiredService<TeacherAccess>();

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    ctx.Response.Headers["Permissions-Policy"] = "camera=(), microphone=()";
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/ws"))
    {
        ctx.Response.Headers.CacheControl = "no-store";
        var ip = ctx.Connection.RemoteIpAddress;
        if (!ctx.Request.IsHttps && (ip is null || !IPAddress.IsLoopback(ip)))
        { await Results.Json(new { error = "局域网接入需要 HTTPS，请配置教师机证书。" }, statusCode: 400).ExecuteAsync(ctx); return; }
        if (ctx.Request.Method is not ("GET" or "HEAD") &&
            (ctx.Request.Path.StartsWithSegments("/api/teacher") || ctx.Request.Path == "/api/login") &&
            ctx.Request.Headers["X-RoomScope"] != "1")
        { ctx.Response.StatusCode = 403; return; }
    }
    try { await next(); }
    catch (UnauthorizedAccessException e) when (!ctx.Response.HasStarted)
    { await Results.Json(new { error = e.Message }, statusCode: 401).ExecuteAsync(ctx); }
    catch (ArgumentException e) when (!ctx.Response.HasStarted)
    { await Results.Json(new { error = e.Message }, statusCode: 400).ExecuteAsync(ctx); }
});
app.UseRateLimiter();
app.UseAuthentication();
app.Use(async (ctx, next) =>
{
    if (LocalTeacherAccess.IsLocal(ctx))
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "teacher")], "LocalTeacher"));
    await next();
});
app.UseAuthorization();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => new { status = "ok", version = typeof(RoomStore).Assembly.GetName().Version!.ToString(3) });
app.MapPost("/api/login", async (LoginRequest request, TeacherAccess access, HttpContext ctx) =>
{
    if (string.IsNullOrEmpty(request.Password) || !access.Verify(request.Password)) return Results.Json(new { error = "密码不正确。" }, statusCode: 401);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "teacher")], CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok(new { ok = true });
}).RequireRateLimiting("auth");

app.MapGet("/api/enrollment", (RoomStore store) => store.EnrollmentStatus()).RequireRateLimiting("enrollment");
app.MapPost("/api/device-requests", (EnrollmentRequest request, HttpContext ctx, RoomStore store) =>
    store.RequestEnrollment(request, EnrollmentClaim(ctx))).RequireRateLimiting("enrollment");
app.MapGet("/api/device-requests/{id}", (string id, HttpContext ctx, RoomStore store) =>
    store.PollEnrollment(id, EnrollmentClaim(ctx))).RequireRateLimiting("enrollment");
app.MapPost("/api/telemetry", (DeviceReport request, HttpContext ctx, RoomStore store) =>
{
    var id = DeviceIdentity(ctx, store);
    return id is null ? Results.Unauthorized() : Results.Ok(store.Report(id, request));
});
app.MapPost("/api/device/calibration", (CalibrationReport request, HttpContext ctx, RoomStore store) =>
{
    var id = DeviceIdentity(ctx, store);
    if (id is null) return Results.Unauthorized();
    store.SaveCalibration(id, request);
    return Results.Ok(new { ok = true });
});

var teacher = app.MapGroup("/api/teacher").RequireAuthorization();
teacher.MapGet("/dashboard", (RoomStore store) => store.Snapshot());
teacher.MapGet("/connection", (IConfiguration configuration) => new { server = configuration["ClassroomServerUrl"] });
teacher.MapPost("/logout", async (HttpContext ctx) => { await ctx.SignOutAsync(); return Results.Ok(); });
teacher.MapGet("/enrollment", (RoomStore store) => store.EnrollmentRequests());
teacher.MapPost("/enrollment/open", (RoomStore store) => store.SetEnrollmentWindow(true));
teacher.MapPost("/enrollment/close", (RoomStore store) => store.SetEnrollmentWindow(false));
teacher.MapPost("/enrollment/approve", (EnrollmentBatch request, RoomStore store) => { store.ApproveEnrollments(request); return Results.Ok(); });
teacher.MapPost("/enrollment/{id}/reject", (string id, RoomStore store) => { store.RejectEnrollment(id); return Results.Ok(); });
teacher.MapPut("/devices/{id}/seat", (string id, SeatAssignment request, RoomStore store) => { store.Assign(id, request.SeatId); return Results.Ok(); });
teacher.MapDelete("/devices/{id}", (string id, RoomStore store) => { store.DeleteDevice(id); return Results.Ok(); });
teacher.MapPost("/sessions", (SessionRequest request, RoomStore store) => store.Start(request));
teacher.MapPost("/sessions/current/{action}", (string action, RoomStore store) => { store.Transition(action); return Results.Ok(); });
teacher.MapPost("/calibrations", (RoomStore store) => { store.Calibrate(); return Results.Ok(); });
teacher.MapPost("/annotations", (AnnotationRequest request, RoomStore store) => { store.AddAnnotation(request.Text); return Results.Ok(); });
teacher.MapGet("/sessions", (RoomStore store) => store.History());
teacher.MapGet("/sessions/{id}/export.csv", (string id, RoomStore store) => Results.File(Encoding.UTF8.GetBytes(store.Export(id)), "text/csv; charset=utf-8", $"roomscope-{id[..Math.Min(id.Length, 8)]}.csv"));
teacher.MapGet("/sessions/{id}/experiment.json", (string id, RoomStore store) =>
    Results.File(JsonSerializer.SerializeToUtf8Bytes(store.ExportExperiment(id), new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
        "application/json", $"experiment-{id[..Math.Min(id.Length, 8)]}.json"));

app.MapGet("/ws/dashboard", async (HttpContext ctx, RoomStore store) =>
{
    var origin = ctx.Request.Headers.Origin.ToString();
    if (!ctx.WebSockets.IsWebSocketRequest || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
        !string.Equals(uri.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
    { ctx.Response.StatusCode = 400; return; }
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
    timeout.CancelAfter(TimeSpan.FromHours(8));
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    // Receive concurrently so peer closes and disconnects are observed, even while idle.
    var receiving = ObserveClose(socket, timeout.Token);
    try
    {
        while (!timeout.IsCancellationRequested && socket.State == WebSocketState.Open && !receiving.IsCompleted)
        {
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(store.Snapshot(), options), WebSocketMessageType.Text, true, timeout.Token);
            await Task.Delay(1000, timeout.Token);
        }
    }
    catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
    finally
    {
        await timeout.CancelAsync();
        try { await receiving; } catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
        if (socket.State == WebSocketState.CloseReceived)
            try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None); } catch (WebSocketException) { }
    }
}).RequireAuthorization();

app.Map("/api/{**path}", () => Results.NotFound(new { error = "接口不存在。" }));
app.MapFallbackToFile("index.html");
app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine("教师端已就绪。");
    Console.WriteLine("本机打开 http://127.0.0.1:5080 即可进入看板，无需密码。");
});
app.Lifetime.ApplicationStopped.Register(() => Console.WriteLine("教师服务已停止。"));
app.Run();
}
catch (Exception e) when (e is not Microsoft.Extensions.Hosting.HostAbortedException)
{
    Console.Error.WriteLine("教师服务启动或运行失败，请检查端口是否被占用、证书是否有效、当前账户是否可访问数据目录。");
    Environment.ExitCode = 1;
}

static string DataDirectory(IConfiguration config, IWebHostEnvironment environment) =>
    Path.GetFullPath(config["DataDirectory"] ?? Path.Combine(environment.ContentRootPath, "data"));
static string? DeviceIdentity(HttpContext ctx, RoomStore store)
{
    var header = ctx.Request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.Ordinal) ? store.Authenticate(header[7..]) : null;
}
static string EnrollmentClaim(HttpContext ctx)
{
    var header = ctx.Request.Headers.Authorization.ToString();
    return header.StartsWith("Enrollment ", StringComparison.Ordinal) ? header[11..] : "";
}
static async Task ObserveClose(WebSocket socket, CancellationToken cancellationToken)
{
    var buffer = new byte[256];
    while (socket.State == WebSocketState.Open)
    {
        var result = await socket.ReceiveAsync(buffer, cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close) return;
        // This endpoint is output-only; unsolicited input closes it.
        await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Output only", cancellationToken);
        return;
    }
}

public sealed record LoginRequest(string Password);
public sealed class RoomTicker(RoomStore store, ILogger<RoomTicker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                try { store.Tick(); } catch (Exception e) { logger.LogError(e, "Room aggregation failed."); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

public partial class Program;
