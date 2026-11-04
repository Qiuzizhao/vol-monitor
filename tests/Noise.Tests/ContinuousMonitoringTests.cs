using Microsoft.Data.Sqlite;
using Noise.Core;
using Noise.Server;
using Xunit;

namespace Noise.Tests;

public sealed class ContinuousMonitoringTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "roomscope-continuous-" + Guid.NewGuid());
    private readonly ManualClock clock = new();
    private RoomStore store;
    public ContinuousMonitoringTests() => store = Open();
    private RoomStore Open() => new(directory, "Room", timeProvider: clock, continuousMonitoring: true);
    private string Enroll(char key, string seat)
    {
        store.SetEnrollmentWindow(true);
        var claim = new string(key, 64);
        var pending = store.RequestEnrollment(new("PC", new string(key, 64)), claim);
        store.ApproveEnrollments(new([new(pending.RequestId!, seat)]));
        return store.PollEnrollment(pending.RequestId!, claim).DeviceId!;
    }
    [Fact]
    public void StartupAndLateArrivalsImmediatelyMonitorWithoutCalibration()
    {
        Assert.Equal("running", store.Snapshot().Session!.Status);
        var id = Enroll('A', "seat-01");
        var command = store.Report(id, new("boot", 1, null, "idle", "mic"));
        Assert.Equal("running", command.State); Assert.Null(command.CalibrationId);
        store.Report(id, new("boot", 2, command.SessionId, "ok", "mic", -65, -55, 0));
        Assert.Equal(52.9, store.Snapshot().Seats[0].Score);
        clock.Advance(3600);
        var lateId = Enroll('B', "seat-02");
        var late = store.Report(lateId, new("boot", 1, null, "idle", "mic"));
        Assert.Equal("running", late.State); Assert.Null(late.CalibrationId);
        store.Report(lateId, new("boot", 2, late.SessionId, "ok", "mic", -65, -55, 0));
        Assert.Equal(52.9, store.Snapshot().Seats[1].Score);
    }

    [Fact]
    public void RestartAndMicrophoneChangeNeedNoBaselineAndKeepBinding()
    {
        var id = Enroll('A', "seat-01");
        var command = store.Report(id, new("boot", 1, null, "idle", "mic"));
        store.Dispose(); store = Open();
        var resumed = store.Report(id, new("new-boot", 1, command.SessionId, "idle", "other-mic"));
        Assert.Equal("running", resumed.State); Assert.NotEqual(command.SessionId, resumed.SessionId);
        Assert.Null(resumed.CalibrationId); Assert.Equal("01", resumed.Seat);
        store.Report(id, new("new-boot", 2, resumed.SessionId, "ok", "other-mic", -35, -25, 0));
        Assert.Equal(88.2, store.Snapshot().Seats[0].Score);
    }

    [Fact]
    public void SilenceShowsZeroAndWhisperImmediatelyAppearsButOfflineIsMissing()
    {
        var id = Enroll('A', "seat-01");
        var command = store.Report(id, new("boot", 1, null, "idle", "mic"));
        store.Report(id, new("boot", 2, command.SessionId, "no_signal", "mic", -120, -120, 0));
        Assert.Equal(0d, store.Snapshot().Seats[0].Score);
        clock.Advance(1);
        store.Report(id, new("boot", 3, command.SessionId, "ok", "mic", -85, -75, 0));
        Assert.Equal(29.4, store.Snapshot().Seats[0].Score);
        store.Report(id, new("boot", 4, command.SessionId, "no_signal", "mic", -120, -120, 0));
        Assert.Equal(0d, store.Snapshot().Seats[0].Score);
        clock.Advance(11);
        Assert.Null(store.Snapshot().Seats[0].Score);
        store.SaveLayout(new([["02"]]));
        Assert.Equal("idle", store.Report(id, new("boot", 5, command.SessionId, "idle", "mic")).State);
    }

    [Fact]
    public void LoudMicrophoneIsNotPinnedAtOneHundredAndRecoversWhenQuiet()
    {
        var id = Enroll('A', "seat-01");
        var command = store.Report(id, new("boot", 1, null, "idle", "mic"));
        store.Report(id, new("boot", 2, command.SessionId, "ok", "mic", -25, -15, 0));
        Assert.Equal(100d, store.Snapshot().Seats[0].Score);
        var previous = 100d;
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(1);
            store.Report(id, new("boot", i + 3, command.SessionId, "ok", "mic", -20 + i * 5, -10 + i * 3, 0));
            var score = store.Snapshot().Seats[0].Score!.Value;
            Assert.True(score > previous); previous = score;
        }
        // Normal quiet input must leave the high range; digital silence resets to zero.
        for (var i = 0; i < 10; i++)
        {
            clock.Advance(1);
            store.Report(id, new("boot", i + 7, command.SessionId, "ok", "mic", -85, -75, 0));
        }
        Assert.InRange(store.Snapshot().Seats[0].Score!.Value, 29, 32);
        store.Report(id, new("boot", 17, command.SessionId, "no_signal", "mic", -120, -120, 0));
        Assert.Equal(0d, store.Snapshot().Seats[0].Score);
    }

    [Fact]
    public void SharedSeatShowsDuplicateWithoutVolumeUntilBindingIsResolved()
    {
        var first = Enroll('A', "seat-01"); var second = Enroll('B', "seat-01");
        var session = store.Snapshot().Session!.Id;
        store.Report(first, new("boot", 1, session, "ok", "mic", -85, -75, 0), "192.168.1.2");
        store.Report(second, new("boot", 1, session, "ok", "mic", -20, -10, 0), "192.168.1.10");
        var seat = store.Snapshot().Seats[0];
        Assert.Equal("duplicate", seat.Status); Assert.Null(seat.Score); Assert.Null(seat.DeviceId);
        Assert.Null(seat.LevelDbfs);
        Assert.Equal("192.168.1.2、192.168.1.10", seat.DeviceName);
        Assert.Equal(2, store.Snapshot().Devices.Length);
        store.Report(second, new("boot", 2, session, "error", "mic"));
        Assert.Equal("duplicate", store.Snapshot().Seats[0].Status);
        Assert.Null(store.Snapshot().Seats[0].Score);
        clock.Advance(11);
        store.Report(first, new("boot", 2, session, "no_signal", "mic", -120, -120, 0));
        Assert.Equal("duplicate", store.Snapshot().Seats[0].Status);
        Assert.Null(store.Snapshot().Seats[0].Score);
        store.Assign(second, "seat-02");
        Assert.Equal(0d, store.Snapshot().Seats[0].Score);
        Assert.Equal(first, store.Snapshot().Seats[0].DeviceId);
        store.Assign(second, "seat-01");
        store.SaveLayout(new([["02"]]));
        Assert.All(store.Snapshot().Devices, d => Assert.Null(d.SeatId));
    }

    [Fact]
    public void LegacyUniqueIndexIsRemovedAndSharedBindingsSurviveRestartAndSwap()
    {
        var first = Enroll('A', "seat-01"); var second = Enroll('B', "seat-02");
        store.Dispose();
        using (var db = new SqliteConnection($"Data Source={Path.Combine(directory, "roomscope.db")}"))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE UNIQUE INDEX one_device_per_seat ON devices(seat_id) WHERE seat_id IS NOT NULL";
            cmd.ExecuteNonQuery();
        }
        store = Open(); store.Assign(second, "seat-01");
        store.Dispose(); store = Open();
        Assert.Equal(2, store.Snapshot().Devices.Length);
        Assert.All(store.Snapshot().Devices, d => Assert.Equal("seat-01", d.SeatId));
        Assert.Equal(first, store.Authenticate(new string('A', 64)));
        Assert.Equal(second, store.Authenticate(new string('B', 64)));
        store.Assign(first, "seat-02");
        Assert.Equal("seat-02", store.Snapshot().Devices.Single(d => d.Id == first).SeatId);
        Assert.Equal("seat-01", store.Snapshot().Devices.Single(d => d.Id == second).SeatId);
    }

    [Fact]
    public void AnOldHighCalibrationCannotSubtractWhispers()
    {
        var id = Enroll('A', "seat-01");
        var command = store.Report(id, new("boot", 1, null, "idle", "mic"));
        store.Calibrate();
        store.SaveCalibration(id, new(command.SessionId!, store.Snapshot().CalibrationId!, "mic", -40, 10));
        store.Report(id, new("boot", 2, command.SessionId, "ok", "mic", -65, -55, 0));
        Assert.Equal(52.9, store.Snapshot().Seats[0].Score);
    }
    public void Dispose()
    {
        store.Dispose(); SqliteConnection.ClearAllPools();
        var path = Path.GetFullPath(directory);
        if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("roomscope-continuous-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test directory.");
        Directory.Delete(path, true);
    }
}
