using Noise.Core;
using Xunit;

namespace Noise.Tests;

public sealed class AcousticsTests
{
    [Fact]
    public void SineWaveHasExpectedRmsAndDrainDoesNotRetainSamples()
    {
        var accumulator = new AudioAccumulator();
        for (var i = 0; i < 16000; i++) accumulator.Add((float)(.5 * Math.Sin(2 * Math.PI * 1000 * i / 16000)));
        var summary = Assert.IsType<AudioSummary>(accumulator.Drain());
        Assert.InRange(summary.LevelDbfs, -9.032, -9.030);
        Assert.InRange(summary.PeakDbfs, -6.022, -6.020);
        Assert.Equal(16000, summary.SampleCount);
        Assert.Null(accumulator.Drain());
    }

    [Fact]
    public void DigitalSilenceIsFiniteAndNotAnActualDecibelMeasurement()
    {
        var accumulator = new AudioAccumulator();
        for (var i = 0; i < 1600; i++) accumulator.Add(0);
        Assert.Equal(-120, accumulator.Drain()!.LevelDbfs);
        Assert.Equal(0, Acoustics.Score(-60, -60));
        Assert.Equal(100, Acoustics.Score(-20, -60));
    }

    [Fact]
    public void SingleFaultyMicrophoneDoesNotDominateSixDeviceRegion()
    {
        var scores = new double[] { 20, 21, 22, 23, 24, 100 };
        Assert.InRange(Acoustics.Percentile(scores, .7), 23, 24);
    }

    [Fact]
    public void AlertRequiresContinuousNoiseAndClearsWithHysteresis()
    {
        var latch = new AlertLatch();
        var start = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++) Assert.False(latch.Update(80, start.AddSeconds(i), 70));
        Assert.True(latch.Update(80, start.AddSeconds(5), 70));
        Assert.True(latch.Update(60, start.AddSeconds(6), 70));
        Assert.True(latch.Update(40, start.AddSeconds(7), 70));
        Assert.True(latch.Update(40, start.AddSeconds(9), 70));
        Assert.False(latch.Update(40, start.AddSeconds(10), 70));
    }

    [Fact]
    public void MissingSamplesBreakAlertContinuity()
    {
        var latch = new AlertLatch(); var start = DateTimeOffset.UtcNow;
        latch.Update(90, start, 70);
        latch.Update(90, start.AddSeconds(2), 70);
        latch.Update(null, start.AddSeconds(3), 70);
        Assert.False(latch.Update(90, start.AddSeconds(6), 70));
        Assert.False(latch.Update(90, start.AddSeconds(10), 70));
    }
}
