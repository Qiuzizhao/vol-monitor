using System.Text.Json;
using Noise.Core;
using Noise.Server;
using Xunit;

namespace Noise.Tests;

public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(int seconds) => now = now.AddSeconds(seconds);
}

public sealed class RoomStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "roomscope-test-" + Guid.NewGuid());
    private readonly ManualClock clock = new();
    private readonly RoomStore store;
    public RoomStoreTests() => store = new(directory, "Test room", timeProvider: clock);
    private EnrollResult Enroll(string name = "PC-01")
    {
        var pair = JsonSerializer.SerializeToElement(store.Pairing());
        return store.Enroll(new(pair.GetProperty("code").GetString()!, name));
    }
    private (EnrollResult Device, ResearchSession Session) StartReal()
    {
        var d = Enroll(); store.Assign(d.DeviceId, "seat-01");
        var session = store.Start(new("Real experiment"));
        store.Report(d.DeviceId, new("boot", 1, session.Id, "ok", "mic-1", -60, -50, 0));
        return (d, session);
    }
    private void Calibrate(string id, string sessionId)
    {
        store.Calibrate();
        var s = store.Snapshot();
        store.SaveCalibration(id, new(sessionId, s.CalibrationId!, "mic-1", -65, 10));
    }

    [Fact]
    public void UnboundAndUncalibratedDevicesNeverProduceScores()
    {
        var d = Enroll(); var session = store.Start(new("Test"));
        var command = store.Report(d.DeviceId, new("boot", 1, session.Id, "idle", "mic-1"));
        Assert.Equal("idle", command.State);
        store.Assign(d.DeviceId, "seat-01");
        store.Report(d.DeviceId, new("boot", 2, session.Id, "ok", "mic-1", -30, -20, 0));
        var seat = store.Snapshot().Seats[0];
        Assert.Equal("uncalibrated", seat.Status);
        Assert.Null(seat.Score);
        Assert.All(store.Snapshot().Zones, z => Assert.Null(z.Score));
    }

    [Fact]
    public void BaselineProducesScoreButOfflineIsMissingNotZero()
    {
        var (d, session) = StartReal(); Calibrate(d.DeviceId, session.Id);
        store.Report(d.DeviceId, new("boot", 2, session.Id, "ok", "mic-1", -35, -25, 0));
        Assert.Equal(100d, store.Snapshot().Seats[0].Score);
        Assert.True(store.Snapshot().Zones[0].LimitedCoverage);
        Assert.False(store.Snapshot().Zones[0].Alert);
        clock.Advance(4);
        Assert.Equal("stale", store.Snapshot().Seats[0].Status);
        Assert.Null(store.Snapshot().Zones[0].Score);
        clock.Advance(7);
        Assert.Equal("offline", store.Snapshot().Seats[0].Status);
        Assert.Null(store.Snapshot().Seats[0].Score);
    }

    [Fact]
    public void MicrophoneChangeInvalidatesBaselineAndDuplicatePacketsAreIgnored()
    {
        var (d, session) = StartReal(); Calibrate(d.DeviceId, session.Id);
        store.Report(d.DeviceId, new("boot", 2, session.Id, "ok", "mic-1", -35, -25, 0));
        store.Report(d.DeviceId, new("boot", 2, session.Id, "ok", "mic-1", -65, -50, 0));
        Assert.Equal(100d, store.Snapshot().Seats[0].Score);
        store.Report(d.DeviceId, new("boot", 3, session.Id, "ok", "mic-2", -35, -25, 0));
        Assert.Equal("uncalibrated", store.Snapshot().Seats[0].Status);
        Assert.Null(store.Snapshot().Seats[0].Score);
    }

    [Fact]
    public void PausingStopsCommandsAndResumingDoesNotReuseOldLevels()
    {
        var (d, session) = StartReal(); Calibrate(d.DeviceId, session.Id);
        store.Transition("pause");
        Assert.Equal("idle", store.Report(d.DeviceId, new("boot", 2, session.Id, "ok", "mic-1", -35, -25, 0)).State);
        Assert.Null(store.Snapshot().Seats[0].Score);
        store.Transition("resume");
        Assert.Null(store.Snapshot().Seats[0].Score);
        Assert.Equal("running", store.Report(d.DeviceId, new("boot", 3, session.Id, "idle", "mic-1")).State);
    }

    [Fact]
    public void DemoNeverCommandsRealDevicesToCaptureAndExportsAreMarked()
    {
        var real = Enroll(); store.Assign(real.DeviceId, "seat-01");
        var demo = store.Start(new("=formula-test", Demo: true)); store.Tick();
        var command = store.Report(real.DeviceId, new("boot", 1, demo.Id, "idle"));
        Assert.Equal("idle", command.State); Assert.Null(command.SessionId);
        Assert.Equal(35, store.Snapshot().Seats.Count(s => s.Score is not null));
        var csv = store.Export(demo.Id);
        Assert.Contains("is_simulated", csv);
        Assert.Contains("'=formula-test", csv);
        var export = JsonSerializer.SerializeToElement(store.ExportExperiment(demo.Id));
        Assert.True(export.GetProperty("metadata").GetProperty("demo").GetBoolean());
        Assert.NotEmpty(export.GetProperty("deviceMetrics").EnumerateArray());
        store.Transition("end"); store.Start(new("New real experiment"));
        Assert.All(store.Snapshot().Seats, s => Assert.Null(s.Score));
        Assert.Empty(store.Snapshot().Trend);
    }

    [Fact]
    public void ExpiredPairingAndRevokedTokensAreRejected()
    {
        var pair = JsonSerializer.SerializeToElement(store.Pairing());
        clock.Advance(601);
        Assert.Throws<UnauthorizedAccessException>(()=>store.Enroll(new(pair.GetProperty("code").GetString()!, "PC")));
        var device = Enroll();
        Assert.Equal(device.DeviceId, store.Authenticate(device.Token));
        store.DeleteDevice(device.DeviceId);
        Assert.Null(store.Authenticate(device.Token));
    }

    [Fact]
    public void SeatCannotBeDoubleBoundAndInvalidSamplesAreRejected()
    {
        var (d, session) = StartReal(); var other = Enroll("PC-02");
        Assert.Throws<ArgumentException>(()=>store.Assign(other.DeviceId,"seat-01"));
        Assert.Throws<ArgumentException>(()=>store.Report(d.DeviceId,new("boot",2,session.Id,"ok","mic-1",double.NaN,-20,0)));
        Assert.Throws<ArgumentException>(()=>store.Report(d.DeviceId,new("boot",3,session.Id,"ok","mic-1",-30,-50,0)));
    }

    [Fact]
    public void StaleOrIncorrectSessionCannotInjectMeasurements()
    {
        var (d, session) = StartReal(); Calibrate(d.DeviceId, session.Id);
        store.Report(d.DeviceId,new("boot",2,"wrong-session","ok","mic-1",-20,-10,0));
        Assert.NotEqual(100d, store.Snapshot().Seats[0].Score);
    }

    public void Dispose() { store.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
}
