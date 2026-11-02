using System.Text.Json;

namespace Noise.Agent;

internal sealed class GuardState
{
    private readonly object gate = new();
    private readonly string file;
    private readonly HashSet<string> paused;
    public GuardState(string file)
    {
        this.file = file;
        paused = File.Exists(file) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(file)) ?? [] : [];
    }
    public bool IsPaused(string key) { lock (gate) return paused.Contains(key); }
    public bool ShouldRestart(string key, bool supervisorAlive) => !IsPaused(key) && !supervisorAlive;
    public void Pause(string key)
        => Change(key, true);
    public void Resume(string key)
        => Change(key, false);
    private void Change(string key, bool pause)
    {
        lock (gate)
        {
            var next = new HashSet<string>(paused);
            if (pause) next.Add(key); else next.Remove(key);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(next));
            File.Move(file + ".tmp", file, true);
            paused.Clear(); paused.UnionWith(next);
        }
    }
}
