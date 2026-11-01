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
    private void Baseline(string id, DeviceCommand command, string mic = "mic") =>
        store.SaveCalibration(id, new(command.SessionId!, command.CalibrationId!, mic, -65, 10));

    [Fact]
    public void StartupAndLateArrivalsAutomaticallyMonitorAndCalibrateIndependently()
    {
        Assert.Equal("running", store.Snapshot().Session!.Status);
        var first = Enroll('A', "seat-01");
        var command = store.Report(first, new("boot", 1, null, "idle", "mic"));
        Assert.Equal("running", command.State); Assert.NotNull(command.CalibrationId);
        Baseline(first, command);
        clock.Advance(3600);
        var second = Enroll('B', "seat-02");
        var late = store.Report(second, new("boot", 1, null, "idle", "mic"));
        Assert.Equal("running", late.State); Assert.NotNull(late.CalibrationId);
        Assert.Throws<ArgumentException>(() => store.SaveCalibration(first, new(late.SessionId!, late.CalibrationId!, "mic", -65, 10)));
        Baseline(second, late);
        var existing = store.Report(first, new("boot", 2, command.SessionId, "ok", "mic", -35, -25, 0));
        Assert.Null(existing.CalibrationId);
        Assert.Equal(100d, store.Snapshot().Seats[0].Score);
    }

    [Fact]
    public void RestartResumesMonitoringAndReusesSavedBaselineAndBinding()
    {
        var id = Enroll('A', "seat-01");
        var command = store.Report(id, new("boot", 1, null, "idle", "mic"));
        Baseline(id, command);
        store.Dispose(); store = Open();
        var resumed = store.Report(id, new("new-boot", 1, command.SessionId, "idle", "mic"));
        Assert.Equal("running", resumed.State); Assert.NotEqual(command.SessionId, resumed.SessionId);
        Assert.Null(resumed.CalibrationId); Assert.Equal("01", resumed.Seat);
        store.Report(id, new("new-boot", 2, resumed.SessionId, "ok", "mic", -35, -25, 0));
        Assert.Equal(100d, store.Snapshot().Seats[0].Score);
    }

    [Fact]
    public void ExpiredCalibrationRetriesAndChangingMicrophoneRequestsANewBaseline()
    {
        var id = Enroll('A', "seat-01");
        var original = store.Report(id, new("boot", 1, null, "idle", "mic"));
        clock.Advance(21);
        Assert.Throws<ArgumentException>(() => Baseline(id, original));
        var retry = store.Report(id, new("boot", 2, original.SessionId, "idle", "mic"));
        Assert.NotEqual(original.CalibrationId, retry.CalibrationId);
        Baseline(id, retry);
        var changed = store.Report(id, new("boot", 3, retry.SessionId, "idle", "other-mic"));
        Assert.NotNull(changed.CalibrationId);
        Assert.NotEqual(retry.CalibrationId, changed.CalibrationId);
        Baseline(id, changed, "other-mic");
        store.SaveLayout(new([["02"]]));
        Assert.Equal("idle", store.Report(id, new("boot", 4, retry.SessionId, "idle", "other-mic")).State);
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
