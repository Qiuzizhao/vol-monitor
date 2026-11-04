using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Noise.Core;
using Noise.Server;
using Xunit;

namespace Noise.Tests;

public sealed class SeatShiftTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"roomscope-shift-"+Guid.NewGuid());
    private RoomStore Open()=>new(directory,"Room",continuousMonitoring:true,automaticIpEnrollment:true);
    private static string SeatId(RoomStore store,string label)=>store.Seats.Single(s=>s.Label==label).Id;
    private static string[] Bindings(RoomStore store)=>store.Snapshot().Devices.OrderBy(d=>int.Parse(d.Name.Split('.')[^1]))
        .Select(d=>store.Seats.Single(s=>s.Id==d.SeatId).Label).ToArray();
    private static string[] Prepare(RoomStore store,int seats,int devices,bool match=true)
    {
        store.SaveLayout(new([Enumerable.Range(1,seats).Select(i=>(string?)$"B{i}").ToArray()]));
        var ids=Enumerable.Range(1,devices).Select(i=>store.ConnectAddress("192.168.1."+i)).ToArray();
        if(match)store.MatchSeatsInOrder();return ids;
    }

    [Fact]
    public void PreviewDoesNotMutateAndConfirmedShiftLeavesEarlierSeatsAndGapIntact()
    {
        using var store=Open();var ids=Prepare(store,8,6);
        var preview=store.PreviewSeatShift(ids[3],SeatId(store,"B5"));
        Assert.True(preview.OfferShift);Assert.True(preview.CanShift);Assert.Equal(2,preview.FollowingCount);
        Assert.Equal("B5",preview.Examples[0].From);Assert.Equal("B6",preview.Examples[0].To);
        Assert.Equal(new[]{"B1","B2","B3","B4","B5","B6"},Bindings(store));
        store.ShiftFollowingSeats(ids[3],new(SeatId(store,"B5"),preview.ExpectedState));
        Assert.Equal(new[]{"B1","B2","B3","B5","B6","B7"},Bindings(store));
        Assert.Equal("unbound",store.Snapshot().Seats.Single(s=>s.Label=="B4").Status);
    }

    [Fact]
    public void OnlyThisDeviceKeepsLaterBindingsAndStillAllowsTemporaryDuplicate()
    {
        using var store=Open();var ids=Prepare(store,8,6);
        var preview=store.PreviewSeatShift(ids[3],SeatId(store,"B5"));
        store.Assign(ids[3],SeatId(store,"B5"),preview.ExpectedState);
        Assert.Equal(new[]{"B1","B2","B3","B5","B5","B6"},Bindings(store));
    }

    [Fact]
    public void MultipleMissingComputersCanBeSkippedWithoutRemovingExistingGaps()
    {
        using var store=Open();var ids=Prepare(store,10,6);
        var first=store.PreviewSeatShift(ids[3],SeatId(store,"B5"));
        store.ShiftFollowingSeats(ids[3],new(SeatId(store,"B5"),first.ExpectedState));
        var next=store.PreviewSeatShift(ids[4],SeatId(store,"B7"));
        store.ShiftFollowingSeats(ids[4],new(SeatId(store,"B7"),next.ExpectedState));
        Assert.Equal(new[]{"B1","B2","B3","B5","B7","B8"},Bindings(store));
        var jump=store.PreviewSeatShift(ids[3],SeatId(store,"B7"));
        store.ShiftFollowingSeats(ids[3],new(SeatId(store,"B7"),jump.ExpectedState));
        Assert.Equal(new[]{"B1","B2","B3","B7","B9","B10"},Bindings(store));
    }

    [Theory]
    [InlineData(5,1,1)]
    [InlineData(6,0,2)]
    public void InsufficientCapacityShiftsWhatFitsAndRetainsOverflowBindings(int target,int shifted,int unchanged)
    {
        using var store=Open();var ids=Prepare(store,6,6);var before=Bindings(store);
        var preview=store.PreviewSeatShift(ids[3],SeatId(store,$"B{target}"));
        Assert.True(preview.OfferShift);Assert.True(preview.CanShift);Assert.Null(preview.Error);
        Assert.Equal(shifted,preview.ShiftableFollowingCount);Assert.Equal(unchanged,preview.UnchangedFollowingCount);
        Assert.Equal(before,Bindings(store));
        var result=store.ShiftFollowingSeats(ids[3],new(SeatId(store,$"B{target}"),preview.ExpectedState));
        var expected=target==5?new[]{"B1","B2","B3","B5","B6","B6"}:new[]{"B1","B2","B3","B6","B5","B6"};
        Assert.Equal(expected,Bindings(store));Assert.Equal(shifted+1,result.Changed);
        Assert.Equal("duplicate",store.Snapshot().Seats.Single(s=>s.Label=="B6").Status);
        Assert.Equal(6,store.Snapshot().Devices.Length);
    }

    [Fact]
    public void PartialShiftSkipsFullSeatsAndStillMovesLaterValidBindings()
    {
        using var store=Open();var ids=Prepare(store,6,6);
        store.Assign(ids[4],SeatId(store,"B6"));store.Assign(ids[5],SeatId(store,"B5"));
        var preview=store.PreviewSeatShift(ids[3],SeatId(store,"B5"));
        Assert.True(preview.CanShift);Assert.Equal(1,preview.ShiftableFollowingCount);Assert.Equal(1,preview.UnchangedFollowingCount);
        Assert.Equal("192.168.1.6",Assert.Single(preview.Examples).Ip);
        store.ShiftFollowingSeats(ids[3],new(SeatId(store,"B5"),preview.ExpectedState));
        Assert.Equal(new[]{"B1","B2","B3","B5","B6","B6"},Bindings(store));
    }

    [Fact]
    public void StaleConfirmationAfterSeatChangeOrNewArrivalIsRejected()
    {
        using var store=Open();var ids=Prepare(store,10,6);
        var preview=store.PreviewSeatShift(ids[3],SeatId(store,"B5"));
        store.Assign(ids[0],SeatId(store,"B10"));var before=Bindings(store);
        Assert.Throws<ArgumentException>(()=>store.ShiftFollowingSeats(ids[3],new(SeatId(store,"B5"),preview.ExpectedState)));
        Assert.Throws<ArgumentException>(()=>store.Assign(ids[3],SeatId(store,"B5"),preview.ExpectedState));
        Assert.Equal(before,Bindings(store));
        var next=store.PreviewSeatShift(ids[3],SeatId(store,"B5"));store.ConnectAddress("192.168.1.7");
        Assert.Throws<ArgumentException>(()=>store.ShiftFollowingSeats(ids[3],new(SeatId(store,"B5"),next.ExpectedState)));
    }

    [Fact]
    public void RestartRetainsSequentialModeAndOfflineDevicesShiftWhileTelemetryDoesNotInvalidatePreview()
    {
        string[] ids;
        using(var store=Open()){ids=Prepare(store,8,6);}
        using var restarted=Open();Assert.True(restarted.Snapshot().SequentialMatching);
        var preview=restarted.PreviewSeatShift(ids[3],SeatId(restarted,"B5"));
        restarted.Report(ids[3],new("boot",1,restarted.Snapshot().Session!.Id,"ok","mic",-65,-55,0));
        restarted.ShiftFollowingSeats(ids[3],new(SeatId(restarted,"B5"),preview.ExpectedState));
        Assert.Equal(new[]{"B1","B2","B3","B5","B6","B7"},Bindings(restarted));
        Assert.Equal(578.9,restarted.Snapshot().Seats.Single(s=>s.DeviceId==ids[3]).Score);
    }

    [Fact]
    public void BackwardEditsUnmatchedModeAndLastDeviceDoNotOfferShift()
    {
        using var store=Open();var ids=Prepare(store,8,6,false);
        Assert.False(store.PreviewSeatShift(ids[3],SeatId(store,"B5")).OfferShift);
        store.MatchSeatsInOrder();
        Assert.False(store.PreviewSeatShift(ids[3],SeatId(store,"B3")).OfferShift);
        Assert.False(store.PreviewSeatShift(ids[^1],SeatId(store,"B7")).OfferShift);
    }

    [Fact]
    public void ShiftCrossesLetterGroupsByNaturalOrder()
    {
        using var store=Open();store.SaveLayout(new([["B2","A10","B1","A2","A1"]]));
        var ids=Enumerable.Range(1,4).Select(i=>store.ConnectAddress("192.168.1."+i)).ToArray();store.MatchSeatsInOrder();
        var preview=store.PreviewSeatShift(ids[1],SeatId(store,"A10"));
        store.ShiftFollowingSeats(ids[1],new(SeatId(store,"A10"),preview.ExpectedState));
        Assert.Equal(new[]{"A1","A10","B1","B2"},Bindings(store));
    }

    [Fact]
    public void CollisionWithEarlierManualSeatPreventsChainButDoesNotPreventSingleEdit()
    {
        using var store=Open();var ids=Prepare(store,8,6);store.Assign(ids[0],SeatId(store,"B7"));
        var preview=store.PreviewSeatShift(ids[3],SeatId(store,"B5"));
        Assert.False(preview.CanShift);Assert.Contains("重复",preview.Error);
        store.Assign(ids[3],SeatId(store,"B5"),preview.ExpectedState);
        Assert.Equal("B5",Bindings(store)[3]);
    }

    [Fact]
    public async Task ApiRequiresConfirmationStateAndAllowsExplicitSingleChoice()
    {
        using var factory=new AppFactory(automaticIpEnrollment:true);
        var store=(RoomStore)factory.Services.GetService(typeof(RoomStore))!;var ids=Prepare(store,8,6);
        using var client=factory.Client();
        var path=$"/api/teacher/devices/{ids[3]}";var target=SeatId(store,"B5");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(path+"/seat-shift-preview",new SeatAssignment(target))).StatusCode);
        client.DefaultRequestHeaders.Add("X-RoomScope","1");
        var preview=(await (await client.PostAsJsonAsync(path+"/seat-shift-preview",new SeatAssignment(target))).Content.ReadFromJsonAsync<SeatShiftPreview>())!;
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync(path+"/shift-seats",new SeatAssignment(target))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync(path+"/shift-seats",new SeatAssignment(target,preview.ExpectedState))).StatusCode);
        Assert.Equal(new[]{"B1","B2","B3","B5","B6","B7"},Bindings(store));
    }

    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
