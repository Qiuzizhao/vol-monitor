using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Noise.Core;
using Noise.Server;
using Xunit;

namespace Noise.Tests;

public sealed class AutomaticIpTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "roomscope-ip-" + Guid.NewGuid());
    private RoomStore Open(bool automatic = true) => new(directory, "Room", continuousMonitoring: true, automaticIpEnrollment: automatic);

    [Fact]
    public void MissingCredentialsAndDifferentHardwareAtSameIpKeepIdentityAndSeatAcrossRestart()
    {
        string id;
        using (var store = Open())
        {
            var first = store.RequestEnrollment(new("PC", ""), "", "192.168.1.10");
            Assert.Equal("approved", first.Status); id = first.DeviceId!;
            store.Assign(id, "seat-17");
            var again = store.RequestEnrollment(new("Changed", new string('B', 64)), new string('C', 64), "::ffff:192.168.1.10");
            Assert.Equal(id, again.DeviceId); Assert.Single(store.Snapshot().Devices);
            Assert.True(store.EnrollmentStatus().Automatic); Assert.Empty(store.EnrollmentRequests().Requests);
            store.SetEnrollmentWindow(false);
            Assert.True(store.EnrollmentStatus().Open);
        }
        using var restarted = Open();
        Assert.Equal(id, restarted.PollEnrollment("old-lost-request", "", "192.168.1.10").DeviceId);
        Assert.Equal("seat-17", Assert.Single(restarted.Snapshot().Devices).SeatId);
        Assert.NotEqual(id, restarted.ConnectAddress("192.168.1.11"));
    }

    [Fact]
    public void ConcurrentConnectionsFromOneIpCreateOnlyOneDeviceAndFullRoomStillListsNewIp()
    {
        using var store = Open(); store.SaveLayout(new([["A1"]]));
        var ids = Enumerable.Range(0, 20).AsParallel().Select(_ => store.ConnectAddress("192.168.1.10")).ToArray();
        Assert.Single(ids.Distinct()); Assert.Single(store.Snapshot().Devices);
        var other = store.ConnectAddress("192.168.1.11");
        Assert.Null(store.Snapshot().Devices.Single(d => d.Id == other).SeatId);
        Assert.Equal(2, store.Snapshot().Devices.Length);
    }

    [Fact]
    public void UpgradeMergesExistingDuplicateIpsAndKeepsBoundSeat()
    {
        string kept;
        using (var legacy = Open(false))
        {
            legacy.SetEnrollmentWindow(true);
            var a = legacy.RequestEnrollment(new("PC01", new string('A', 64)), new string('B', 64), "192.168.1.10");
            var b = legacy.RequestEnrollment(new("PC02", new string('C', 64)), new string('D', 64), "192.168.1.10");
            legacy.ApproveEnrollments(new([new(a.RequestId!, "seat-17"), new(b.RequestId!, "seat-18")]));
            kept = legacy.PollEnrollment(a.RequestId!, new string('B', 64)).DeviceId!;
            Assert.Equal(2, legacy.Snapshot().Devices.Length);
        }
        using var upgraded = Open();
        Assert.Equal(kept, upgraded.ConnectAddress("192.168.1.10"));
        Assert.Equal("seat-17", Assert.Single(upgraded.Snapshot().Devices).SeatId);
    }

    [Fact]
    public async Task ActualPeerAutomaticallyConnectsWithoutTokenAndIgnoresForwardedIp()
    {
        using var factory = new AppFactory(automaticIpEnrollment: true);
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.168.1.99");
        var response = await client.PostAsJsonAsync("/api/telemetry", new DeviceReport("boot", 1, null, "idle", "mic"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var command = (await response.Content.ReadFromJsonAsync<DeviceCommand>())!;
        Assert.Equal("running", command.State);
        var first = (await client.GetFromJsonAsync<Dashboard>("/api/teacher/dashboard"))!;
        var device = Assert.Single(first.Devices); Assert.Equal("192.168.1.20", device.Name);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "lost-or-invalid-credential");
        response = await client.PostAsJsonAsync("/api/telemetry", new DeviceReport("after-reboot", 1, command.SessionId, "ok", "mic", -65, -55, 0));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var second = (await client.GetFromJsonAsync<Dashboard>("/api/teacher/dashboard"))!;
        Assert.Equal(device.Id, Assert.Single(second.Devices).Id);
        Assert.Equal(578.9, second.Seats.Single(s => s.DeviceId == device.Id).Score);
        var reconnect = await client.PostAsJsonAsync("/api/device-requests", new EnrollmentRequest("other", ""));
        Assert.Equal(device.Id, (await reconnect.Content.ReadFromJsonAsync<EnrollmentState>())!.DeviceId);
    }

    [Theory]
    [InlineData("203.0.113.20", false)]
    [InlineData("192.168.1.20", true)]
    public async Task PublicPeersAndForeignBrowserOriginsDoNotRegisterDevices(string ip, bool foreignOrigin)
    {
        using var factory = new AppFactory(IPAddress.Parse(ip), automaticIpEnrollment: true);
        using var client = factory.Client();
        if (foreignOrigin) client.DefaultRequestHeaders.Add("Origin", "https://foreign.example");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/telemetry", new DeviceReport("b", 1, null, "idle"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/device-requests", new EnrollmentRequest("PC", ""))).StatusCode);
        Assert.Empty(factory.Services.GetService(typeof(RoomStore)) is RoomStore store ? store.Snapshot().Devices : throw new InvalidOperationException());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
