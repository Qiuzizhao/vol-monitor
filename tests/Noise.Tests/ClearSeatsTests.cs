using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Noise.Core;
using Noise.Server;
using Xunit;

namespace Noise.Tests;

public sealed class ClearSeatsTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"roomscope-clear-seats-"+Guid.NewGuid());
    private readonly ManualClock clock=new();
    private RoomStore Open()=>new(directory,"Room",timeProvider:clock,continuousMonitoring:true,automaticIpEnrollment:true);

    [Fact]
    public void ClearsAllIncludingOfflineAndDuplicatesWithoutDeletingDevicesOrLayoutAndPersists()
    {
        string[] ids;Seat[] layout;
        using(var store=Open())
        {
            ids=new[]{2,3,10}.Select(n=>store.ConnectAddress("192.168.1."+n)).ToArray();
            layout=store.Seats.ToArray();store.Assign(ids[1],store.Snapshot().Devices.First(d=>d.Id==ids[0]).SeatId!);
            clock.Advance(11);store.ConnectAddress("192.168.1.2");
            var session=store.Snapshot().Session!.Id;
            store.Report(ids[0],new("boot",1,session,"ok","mic",-65,-55,0));
            var before=store.Snapshot();Assert.Contains(before.Devices,d=>d.Status=="offline");
            Assert.Equal(3,store.ClearSeatAssignments().Cleared);
            var cleared=store.Snapshot();Assert.Equal(ids,cleared.Devices.Select(d=>d.Id));
            Assert.All(cleared.Devices,d=>Assert.Null(d.SeatId));Assert.Equal(layout,store.Seats);
            Assert.Equal(before.Devices.Select(d=>(d.Id,d.MicrophoneId,d.LastSeen)),cleared.Devices.Select(d=>(d.Id,d.MicrophoneId,d.LastSeen)));
            Assert.Equal(ids[0],store.ConnectAddress("192.168.1.2"));
            Assert.Null(store.Report(ids[0],new("boot",2,session,"ok","mic",-65,-55,0)).Seat);
            Assert.All(store.Snapshot().Devices,d=>Assert.Null(d.SeatId));
        }
        using var reopened=Open();Assert.Equal(layout,reopened.Seats);
        Assert.Equal(ids.Order(),reopened.Snapshot().Devices.Select(d=>d.Id).Order());
        Assert.All(reopened.Snapshot().Devices,d=>Assert.Null(d.SeatId));
        Assert.False(reopened.SequentialMatching);
    }

    [Fact]
    public void ClearIsIdempotentInvalidatesOldMatchingReviewAndAllowsFreshMatching()
    {
        using var store=Open();Assert.Equal(0,store.ClearSeatAssignments().Cleared);
        store.ConnectAddress("192.168.1.10");store.ConnectAddress("192.168.1.2");store.MatchSeatsInOrder();
        var review=store.PreviewSequentialMatch();Assert.True(store.SequentialMatching);
        Assert.Equal(2,store.ClearSeatAssignments().Cleared);Assert.False(store.SequentialMatching);
        Assert.Throws<ArgumentException>(()=>store.ConfirmSequentialMatch(new(review.ExpectedState)));
        Assert.Equal(0,store.ClearSeatAssignments().Cleared);
        var fresh=store.PreviewSequentialMatch();Assert.Equal(2,store.ConfirmSequentialMatch(new(fresh.ExpectedState)).Matched);
        Assert.True(store.SequentialMatching);Assert.All(store.Snapshot().Devices,d=>Assert.NotNull(d.SeatId));
    }

    [Fact]
    public async Task ApiRequiresTeacherCsrfAndReturnsClearedCount()
    {
        using var factory=new AppFactory(automaticIpEnrollment:true);using var client=factory.Client();
        await client.PostAsJsonAsync("/api/telemetry",new DeviceReport("boot",1,null,"idle","mic"));
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsync("/api/teacher/devices/clear-seats",null)).StatusCode);
        var before=(await client.GetFromJsonAsync<Dashboard>("/api/teacher/dashboard"))!;
        Assert.NotNull(Assert.Single(before.Devices).SeatId);
        client.DefaultRequestHeaders.Add("X-RoomScope","1");
        var response=await client.PostAsync("/api/teacher/devices/clear-seats",null);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.Equal(1,(await response.Content.ReadFromJsonAsync<ClearSeatsResult>())!.Cleared);
        var after=(await client.GetFromJsonAsync<Dashboard>("/api/teacher/dashboard"))!;
        Assert.Null(Assert.Single(after.Devices).SeatId);Assert.Equal(before.Seats.Select(s=>s.Id),after.Seats.Select(s=>s.Id));
    }

    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
