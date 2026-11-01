using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Noise.Core;
using Noise.Server;
using Xunit;

namespace Noise.Tests;

public sealed class LayoutAndAddressTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "roomscope-layout-" + Guid.NewGuid());
    private readonly RoomStore store;
    public LayoutAndAddressTests() => store = new(directory, "Room");
    private (string Id, string Claim, string Request) Enroll()
    {
        store.SetEnrollmentWindow(true);
        var claim = new string('B', 64);
        var pending = store.RequestEnrollment(new("PC01", new string('A', 64)), claim, "::ffff:192.168.1.21");
        store.ApproveEnrollments(new([new(pending.RequestId!, "seat-01")]));
        return (store.PollEnrollment(pending.RequestId!, claim).DeviceId!, claim, pending.RequestId!);
    }

    [Fact]
    public void BlankRowsAndColumnsPersistAndMovedSeatKeepsItsDevice()
    {
        var device = Enroll();
        store.SaveLayout(new([["02", null, "1", null], [null, null, null, null], ["03", null, null, null]]));
        var snapshot = store.Snapshot();
        Assert.Equal(3, snapshot.LayoutRows); Assert.Equal(4, snapshot.LayoutColumns);
        Assert.Equal(3, snapshot.Seats.Length);
        var moved = snapshot.Seats.Single(s => s.Label == "1");
        Assert.Equal("seat-01", moved.Id); Assert.Equal(1, moved.Row); Assert.Equal(3, moved.Column);
        Assert.Equal(device.Id, moved.DeviceId);
        Assert.Equal("192.168.1.21", moved.DeviceName);
        using var restarted = new RoomStore(directory, "Restarted");
        var saved = restarted.Snapshot();
        Assert.Equal(3, saved.LayoutRows); Assert.Equal(4, saved.LayoutColumns);
        Assert.Equal(device.Id, saved.Seats.Single(s => s.Id == "seat-01").DeviceId);
    }

    [Fact]
    public void ClearingABoundSeatUnbindsItAndStopsItsCaptureCommand()
    {
        var device = Enroll(); var session = store.Start(new("Class"));
        Assert.Equal("running", store.Report(device.Id, new("boot", 1, session.Id, "idle", "mic")).State);
        store.SaveLayout(new([["02"]]));
        Assert.Null(Assert.Single(store.Snapshot().Devices).SeatId);
        Assert.Equal("idle", store.Report(device.Id, new("boot", 2, session.Id, "idle", "mic")).State);
        Assert.Equal(device.Id, store.Authenticate(device.Claim));
    }

    [Fact]
    public void InvalidLayoutsDoNotChangeTheCurrentLayout()
    {
        Assert.Throws<ArgumentException>(() => store.SaveLayout(new([["1", "01"]])));
        Assert.Throws<ArgumentException>(() => store.SaveLayout(new([[null, " "]])));
        Assert.Throws<ArgumentException>(() => store.SaveLayout(new([["01", "02"], ["03"]])));
        Assert.Equal(36, store.Snapshot().Seats.Length);
        Assert.Equal(6, store.Snapshot().LayoutColumns);
    }

    [Fact]
    public void MachineNumberFindsItsLabelInACustomLayout()
    {
        store.SaveLayout(new([["10", null, "01"]]));
        store.SetEnrollmentWindow(true);
        store.RequestEnrollment(new("PC10", new string('A', 64)), new string('B', 64));
        Assert.Equal("seat-10", Assert.Single(store.EnrollmentRequests().Requests).SuggestedSeatId);
    }

    [Fact]
    public void IpChangesUpdateDisplayWithoutChangingIdentityOrSeat()
    {
        var device = Enroll();
        Assert.Equal("192.168.1.21", Assert.Single(store.Snapshot().Devices).Name);
        store.Report(device.Id, new("boot", 1, null, "idle", "mic"), "192.168.1.35");
        var snapshot = store.Snapshot();
        Assert.Equal("192.168.1.35", Assert.Single(snapshot.Devices).Name);
        Assert.Equal("192.168.1.35", snapshot.Seats[0].DeviceName);
        Assert.Equal("seat-01", snapshot.Devices[0].SeatId);
        Assert.Equal(device.Id, store.Authenticate(device.Claim));
        using var restarted = new RoomStore(directory, "Restarted");
        Assert.Equal("192.168.1.35", Assert.Single(restarted.Snapshot().Devices).Name);
    }

    [Fact]
    public void WrongEnrollmentCredentialCannotChangeTheObservedIp()
    {
        store.SetEnrollmentWindow(true);
        var request = store.RequestEnrollment(new("PC01", new string('A', 64)), new string('B', 64), "192.168.1.21");
        Assert.Throws<UnauthorizedAccessException>(() => store.PollEnrollment(request.RequestId!, new string('C', 64), "192.168.1.99"));
        Assert.Equal("192.168.1.21", Assert.Single(store.EnrollmentRequests().Requests).IpAddress);
    }

    [Fact]
    public async Task ApiUsesTheConnectionIpAndIgnoresReportedNamesAndForwardedHeaders()
    {
        using var factory = new AppFactory(IPAddress.Parse("192.168.1.81"));
        using var teacher = await factory.Teacher(); using var agent = factory.Client();
        await teacher.PostAsync("/api/teacher/enrollment/open", null);
        agent.DefaultRequestHeaders.Authorization = new("Enrollment", new string('B', 64));
        agent.DefaultRequestHeaders.Add("X-Forwarded-For", "192.168.1.99");
        var response = await agent.PostAsJsonAsync("/api/device-requests", new EnrollmentRequest("SpoofedName", new string('A', 64)));
        var pending = (await response.Content.ReadFromJsonAsync<EnrollmentState>())!;
        var overview = (await teacher.GetFromJsonAsync<EnrollmentOverview>("/api/teacher/enrollment"))!;
        Assert.Equal("192.168.1.81", Assert.Single(overview.Requests).IpAddress);
        await teacher.PostAsJsonAsync("/api/teacher/enrollment/approve", new EnrollmentBatch([new(pending.RequestId!, "seat-01")]));
        var dashboard = (await teacher.GetFromJsonAsync<Dashboard>("/api/teacher/dashboard"))!;
        Assert.Equal("192.168.1.81", Assert.Single(dashboard.Devices).Name);
    }

    [Fact]
    public async Task LayoutApiRequiresTeacherAuthorizationAndAcceptsAFullSheet()
    {
        using var factory = new AppFactory(); using var unauthenticated = factory.Client();
        unauthenticated.DefaultRequestHeaders.Add("X-RoomScope", "1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await unauthenticated.PutAsJsonAsync("/api/teacher/layout", new LayoutRequest([["01"]]))).StatusCode);
        using var teacher = await factory.Teacher();
        var cells = Enumerable.Range(0, 30).Select(r => Enumerable.Range(0, 30).Select(c => (string?)$"座位{r * 30 + c:000}").ToArray()).ToArray();
        var response = await teacher.PutAsJsonAsync("/api/teacher/layout", new LayoutRequest(cells));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = (await response.Content.ReadFromJsonAsync<Dashboard>())!;
        Assert.Equal(30, snapshot.LayoutRows); Assert.Equal(30, snapshot.LayoutColumns); Assert.Equal(900, snapshot.Seats.Length);
    }

    public void Dispose()
    {
        store.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var full = Path.GetFullPath(directory); var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("roomscope-layout-", StringComparison.Ordinal)) Directory.Delete(full, true);
    }
}
