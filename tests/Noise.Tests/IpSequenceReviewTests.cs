using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Noise.Server;
using Xunit;

namespace Noise.Tests;

public sealed class IpSequenceReviewTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"roomscope-ip-review-"+Guid.NewGuid());
    private readonly ManualClock clock=new();
    private RoomStore Open()=>new(directory,"Room",timeProvider:clock,continuousMonitoring:true,automaticIpEnrollment:true);

    [Fact]
    public void ListsSingleAndRangeGapsInNumericOrderWithoutChangingSeats()
    {
        using var store=Open();
        foreach(var ip in new[]{"192.168.1.10","192.168.1.2","192.168.1.6","::ffff:192.168.1.4"})store.ConnectAddress(ip);
        var before=store.Snapshot().Devices.Select(d=>(d.Id,d.SeatId)).ToArray();
        var review=store.PreviewSequentialMatch();
        Assert.Equal(3,review.CheckedPairs);Assert.Equal(3,review.Gaps.Length);
        Assert.Equal(new IpSequenceGap("192.168.1.2","192.168.1.4","192.168.1.3","192.168.1.3",1),review.Gaps[0]);
        Assert.Equal(new IpSequenceGap("192.168.1.6","192.168.1.10","192.168.1.7","192.168.1.9",3),review.Gaps[2]);
        Assert.True(review.CanMatch);Assert.False(store.SequentialMatching);
        Assert.Equal(before,store.Snapshot().Devices.Select(d=>(d.Id,d.SeatId)).ToArray());
    }

    [Fact]
    public void KnownOfflineIpsAreReportedSeparatelyAndDoNotBecomeMissingHoles()
    {
        using var store=Open();foreach(var n in new[]{101,102,103})store.ConnectAddress("192.168.1."+n);
        clock.Advance(11);store.ConnectAddress("192.168.1.101");store.ConnectAddress("192.168.1.103");
        var review=store.PreviewSequentialMatch();
        Assert.Empty(review.Gaps);Assert.Equal(new[]{"192.168.1.102"},review.OfflineIps);
        Assert.Equal(3,store.ConfirmSequentialMatch(new(review.ExpectedState)).Matched);
    }

    [Fact]
    public void DoesNotInventMissingHostsAtRangeEndsBetweenGroupsOrForIpv6()
    {
        using var store=Open();
        foreach(var ip in new[]{"10.0.1.200","10.0.1.201","10.0.2.10","fd00::1","fd00::10"})store.ConnectAddress(ip);
        var review=store.PreviewSequentialMatch();
        Assert.Empty(review.Gaps);Assert.Equal(1,review.CheckedPairs);Assert.Equal(2,review.AddressGroups);
        Assert.Equal(2,review.UncheckedAddresses);Assert.True(review.CanMatch);
    }

    [Fact]
    public void LargeGapStaysOneRangeInsteadOfEnumeratingManyMachines()
    {
        using var store=Open();store.ConnectAddress("10.0.1.1");store.ConnectAddress("10.0.1.254");
        var gap=Assert.Single(store.PreviewSequentialMatch().Gaps);
        Assert.Equal(252,gap.Count);Assert.Equal("10.0.1.2",gap.MissingFrom);Assert.Equal("10.0.1.253",gap.MissingTo);
    }

    [Fact]
    public void ConfirmationIsInvalidatedWhenPreviouslyMissingIpArrives()
    {
        using var store=Open();store.ConnectAddress("192.168.1.10");store.ConnectAddress("192.168.1.12");
        var review=store.PreviewSequentialMatch();Assert.Single(review.Gaps);
        store.ConnectAddress("192.168.1.11");
        var before=store.Snapshot().Devices.Select(d=>(d.Id,d.SeatId)).ToArray();
        Assert.Contains("重新检查",Assert.Throws<ArgumentException>(()=>store.ConfirmSequentialMatch(new(review.ExpectedState))).Message);
        Assert.Equal(before,store.Snapshot().Devices.Select(d=>(d.Id,d.SeatId)).ToArray());
        var fresh=store.PreviewSequentialMatch();Assert.Empty(fresh.Gaps);
        Assert.Equal(3,store.ConfirmSequentialMatch(new(fresh.ExpectedState)).Matched);
    }

    [Fact]
    public void NoDevicesOrTooFewSeatsCannotBeConfirmed()
    {
        using var store=Open();Assert.False(store.PreviewSequentialMatch().CanMatch);
        store.SaveLayout(new([["A1"]]));store.ConnectAddress("192.168.1.10");store.ConnectAddress("192.168.1.12");
        var review=store.PreviewSequentialMatch();Assert.False(review.CanMatch);Assert.Contains("不足",review.Error);
        Assert.Throws<ArgumentException>(()=>store.ConfirmSequentialMatch(new(review.ExpectedState)));
    }

    [Fact]
    public void DashboardShowsLiveGapsWithoutMatchingAndClearsThemWhenMissingIpsConnect()
    {
        using(var store=Open())
        {
            Assert.Empty(store.Snapshot().IpGaps);
            store.ConnectAddress("192.168.1.101");store.ConnectAddress("192.168.1.104");
            var snapshot=store.Snapshot();
            Assert.Equal(store.PreviewSequentialMatch().Gaps,snapshot.IpGaps);
            Assert.Equal(2,Assert.Single(snapshot.IpGaps).Count);
            Assert.False(snapshot.SequentialMatching);
            store.ConnectAddress("192.168.1.102");
            var gap=Assert.Single(store.Snapshot().IpGaps);
            Assert.Equal("192.168.1.103",gap.MissingFrom);Assert.Equal(1,gap.Count);
            clock.Advance(11);
            Assert.Equal(gap,Assert.Single(store.Snapshot().IpGaps));
            store.ConnectAddress("192.168.1.103");
            Assert.Empty(store.Snapshot().IpGaps);
        }
        using var reopened=Open();
        Assert.Empty(reopened.Snapshot().IpGaps);
        Assert.False(reopened.SequentialMatching);
    }

    [Fact]
    public async Task ApiRejectsBypassingReviewAndRequiresCsrfForPreview()
    {
        using var factory=new AppFactory(automaticIpEnrollment:true);using var client=factory.Client();
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsync("/api/teacher/devices/match-preview",null)).StatusCode);
        client.DefaultRequestHeaders.Add("X-RoomScope","1");
        Assert.False((await client.PostAsync("/api/teacher/devices/match-seats",null)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/teacher/devices/match-seats",new MatchConfirmation(""))).StatusCode);
        var review=await (await client.PostAsync("/api/teacher/devices/match-preview",null)).Content.ReadFromJsonAsync<SequentialMatchPreview>();
        Assert.NotNull(review);Assert.False(review.CanMatch);
    }

    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
