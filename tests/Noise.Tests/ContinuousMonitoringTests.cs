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
