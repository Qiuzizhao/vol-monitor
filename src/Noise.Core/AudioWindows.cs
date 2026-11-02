namespace Noise.Core;

// Keep the latest complete window even when uploads are delayed. Never retain PCM.
public sealed class AudioWindows
{
    private readonly object gate = new();
    private readonly AudioAccumulator current = new();
    private long samples;
    private AudioSummary? latest;
    public void AddEnergy(double squares, double peak, long count, long clipped, int windowSamples)
    {
        lock (gate)
        {
            current.AddEnergy(squares, peak, count, clipped); samples += count;
            if (samples >= windowSamples) { latest = current.Drain(); samples = 0; }
        }
    }
    public AudioSummary? Drain() { lock (gate) { var value = latest; latest = null; return value; } }
    public void Clear() { lock (gate) { latest = null; samples = 0; current.Drain(); } }
}
