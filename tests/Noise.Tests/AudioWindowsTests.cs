using Noise.Core;
using Xunit;

namespace Noise.Tests;

public sealed class AudioWindowsTests
{
    [Fact]
    public void SlowUploadKeepsOnlyTheLatestOneSecondWindow()
    {
        var windows = new AudioWindows();
        for (var second = 1; second <= 4; second++)
            for (var chunk = 0; chunk < 10; chunk++) windows.AddEnergy(second * 48, .5, 4800, 0, 48000);
        var result = windows.Drain()!;
        Assert.Equal(48000, result.SampleCount);
        Assert.Equal(Acoustics.Dbfs(.04), result.LevelDbfs, 6);
        Assert.Null(windows.Drain());
    }
    [Fact]
    public void StopClearsOldWindowsAndPartialSamples()
    {
        var windows = new AudioWindows(); windows.AddEnergy(1, .5, 48000, 0, 48000);
        windows.Clear(); Assert.Null(windows.Drain());
        windows.AddEnergy(1, .5, 24000, 0, 48000); Assert.Null(windows.Drain());
    }
}
