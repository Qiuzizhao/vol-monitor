using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Noise.Core;
using Noise.Server;
using Xunit;

namespace Noise.Tests;

public sealed class SequentialAssignmentTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "roomscope-sequential-" + Guid.NewGuid());
    private RoomStore Open() => new(directory,"Room",continuousMonitoring:true,automaticIpEnrollment:true);
    private static string[] LabelsByIp(RoomStore store) => store.Snapshot().Devices
        .OrderBy(d=>int.Parse(d.Name.Split('.')[^1]))
        .Select(d=>store.Seats.Single(s=>s.Id==d.SeatId).Label).ToArray();

    [Theory]
    [InlineData("B3,A10,A2,B1,A1,B2", "A1,A2,A10,B1,B2,B3")]
    [InlineData("10,2,1,20", "1,2,10,20")]
    [InlineData("b02,A10,a01,A2", "a01,A2,A10,b02")]
    public void MatchesNumericIpsToNaturalSeatNamesRegardlessOfArrivalOrLayoutOrder(string labels,string expected)
    {
        using var store=Open();
        var names=labels.Split(',');store.SaveLayout(new([names]));
        var ips=new[]{100,2,10,1,20,3};
        for(var i=0;i<names.Length;i++)store.ConnectAddress("192.168.1."+ips[i]);
        var result=store.MatchSeatsInOrder();
        Assert.Equal(names.Length,result.Matched);Assert.True(result.Changed>0);
        Assert.Equal(expected.Split(','),LabelsByIp(store));
        Assert.Equal(0,store.MatchSeatsInOrder().Changed);
    }

    [Fact]
    public void InsufficientSeatsDoesNotPartiallyChangeExistingBindings()
    {
        using var store=Open();store.SaveLayout(new([["A1"]]));
        store.ConnectAddress("192.168.1.100");store.ConnectAddress("192.168.1.2");
        var before=store.Snapshot().Devices.Select(d=>(d.Id,d.SeatId)).ToArray();
        Assert.Contains("座位不足",Assert.Throws<ArgumentException>(()=>store.MatchSeatsInOrder()).Message);
        Assert.Equal(before,store.Snapshot().Devices.Select(d=>(d.Id,d.SeatId)).ToArray());
    }

    [Fact]
    public void IncludesOfflineStudentsPreservesLiveValuesOnRepeatAndPersistsAcrossRestart()
    {
        using(var store=Open())
        {
            store.SaveLayout(new([["B1","A2","A1"]]));
            store.ConnectAddress("192.168.1.10");var id=store.ConnectAddress("192.168.1.2");
            var session=store.Snapshot().Session!.Id;
            store.Report(id,new("boot",1,session,"ok","mic",-65,-55,0));
            var value=store.Snapshot().Seats.Single(s=>s.DeviceId==id).Score;
            Assert.Equal(2,store.MatchSeatsInOrder().Matched);
            Assert.Equal(value,store.Snapshot().Seats.Single(s=>s.DeviceId==id).Score);
            Assert.Equal("ok",store.Snapshot().Devices.Single(d=>d.Id==id).Status);
            Assert.NotNull(value);Assert.Equal(0,store.MatchSeatsInOrder().Changed);
            Assert.Equal(value,store.Snapshot().Seats.Single(s=>s.DeviceId==id).Score);
        }
        using var restarted=Open();
        Assert.All(restarted.Snapshot().Devices,d=>Assert.Equal("offline",d.Status));
        Assert.Equal(new[]{"A1","A2"},LabelsByIp(restarted));
        Assert.Equal(0,restarted.MatchSeatsInOrder().Changed);
    }

    [Fact]
    public async Task ApiRequiresTeacherCsrfHeaderAndReturnsMatchedCount()
    {
        using var factory=new AppFactory(automaticIpEnrollment:true);using var client=factory.Client();
        await client.PostAsJsonAsync("/api/telemetry",new DeviceReport("boot",1,null,"idle","mic"));
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsync("/api/teacher/devices/match-seats",null)).StatusCode);
        client.DefaultRequestHeaders.Add("X-RoomScope","1");
        var response=await client.PostAsync("/api/teacher/devices/match-seats",null);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.Equal(1,(await response.Content.ReadFromJsonAsync<SequentialMatchResult>())!.Matched);
    }

    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
