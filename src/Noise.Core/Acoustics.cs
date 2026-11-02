namespace Noise.Core;

public static class Acoustics
{
    public const string AlgorithmVersion = "rms-direct-p70-v3";
    public const double InputFloorDbfs = -110;
    public const double InputCeilingDbfs = -25;
    public static double InputScore(double level) => Math.Round(Math.Clamp(
        (level - InputFloorDbfs) / (InputCeilingDbfs - InputFloorDbfs) * 100, 0, 100), 1);
    public const double DeadbandDb = 1;
    public const double ScoreSpanDb = 18;

    public static double Dbfs(double meanSquare) =>
        Math.Clamp(10 * Math.Log10(Math.Max(meanSquare, 1e-12)), -120, 0);

    public static double Score(double level, double baseline) =>
        Math.Round(Math.Clamp((level - baseline - DeadbandDb) / ScoreSpanDb * 100, 0, 100), 1);

    public static double Percentile(IEnumerable<double> source, double p)
    {
        var values = source.Order().ToArray();
        if (values.Length == 0) throw new ArgumentException("No samples.", nameof(source));
        var position = Math.Clamp(p, 0, 1) * (values.Length - 1);
        var lower = (int)position;
        return values[lower] + (values[(int)Math.Ceiling(position)] - values[lower]) * (position - lower);
    }
}

/// <summary>Constant-memory aggregation. Audio samples are never retained.</summary>
public sealed class AudioAccumulator
{
    private readonly object gate = new();
    private double squares, peak;
    private long count, clipped;

    public void AddEnergy(double sumSquares, double maximum, long samples, long clippingCount)
    {
        lock (gate)
        {
            squares += sumSquares;
            peak = Math.Max(peak, maximum);
            count += samples;
            clipped += clippingCount;
        }
    }

    public void Add(float sample)
    {
        if (!float.IsFinite(sample)) return;
        double value = Math.Clamp(sample, -1, 1);
        lock (gate)
        {
            squares += value * value;
            peak = Math.Max(peak, Math.Abs(value));
            count++;
            if (Math.Abs(value) >= .99) clipped++;
        }
    }

    public AudioSummary? Drain()
    {
        lock (gate)
        {
            if (count == 0) return null;
            var result = new AudioSummary(Acoustics.Dbfs(squares / count),
                Acoustics.Dbfs(peak * peak), (double)clipped / count, count);
            squares = peak = 0;
            count = clipped = 0;
            return result;
        }
    }
}

public record AudioSummary(double LevelDbfs, double PeakDbfs, double ClippingRatio, long SampleCount);

public sealed class AlertLatch
{
    private DateTimeOffset? highSince, lowSince, last;
    public bool Active { get; private set; }

    public bool Update(double? score, DateTimeOffset now, double threshold)
    {
        // Missing data interrupts continuity. It must never count as a quiet measurement.
        if (score is null || last is not null && now - last > TimeSpan.FromSeconds(3)) Reset();
        last = now;
        if (score is null) return Active;
        if (!Active)
        {
            lowSince = null;
            highSince = score >= threshold ? highSince ?? now : null;
            if (highSince is not null && now - highSince >= TimeSpan.FromSeconds(5)) Active = true;
        }
        else
        {
            highSince = null;
            lowSince = score < threshold - 20 ? lowSince ?? now : null;
            if (lowSince is not null && now - lowSince >= TimeSpan.FromSeconds(3)) Reset();
        }
        return Active;
    }

    public void Reset() { highSince = lowSince = last = null; Active = false; }
}
