using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Noise.Core;

namespace Noise.Agent;

public sealed class AudioCapture : IDisposable
{
    private readonly object lifecycle = new();
    private WasapiCapture? capture;
    private MMDevice? device;
    private readonly AudioAccumulator accumulator = new();
    public string? Error { get; private set; }
    public int SamplesPerSecond { get; private set; }
    public bool Running => capture is not null;

    public static (string Id, string Name)[] ListDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        var items = new List<(string, string)>();
        foreach (var d in collection)
        { items.Add((d.ID, d.FriendlyName)); d.Dispose(); }
        return items.ToArray();
    }

    public void Start(string deviceId)
    {
        lock (lifecycle) StartCore(deviceId);
    }

    private void StartCore(string deviceId)
    {
        Stop(); Error = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(deviceId);
            capture = new WasapiCapture(device, false, 100);
            var format = capture.WaveFormat;
            var floating = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
            if ((!floating && format.BitsPerSample is not (16 or 24 or 32)) || floating && format.BitsPerSample != 32)
                throw new NotSupportedException("麦克风格式不受支持，请使用 16/24/32 位 PCM 或 32 位浮点。");
            SamplesPerSecond = format.SampleRate * format.Channels;
            var bytesPerSample = format.BitsPerSample / 8;
            capture.DataAvailable += (_, e) =>
            {
                double squares = 0, maximum = 0; long count = 0, clipped = 0;
                var data = e.Buffer.AsSpan(0, e.BytesRecorded);
                for (var i = 0; i + bytesPerSample <= data.Length; i += bytesPerSample)
                {
                    double value;
                    if (floating) value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data[i..]));
                    else if (bytesPerSample == 2) value = BinaryPrimitives.ReadInt16LittleEndian(data[i..]) / 32768d;
                    else if (bytesPerSample == 3)
                    {
                        var signed = (data[i] | data[i + 1] << 8 | data[i + 2] << 16) << 8 >> 8;
                        value = signed / 8388608d;
                    }
                    else value = BinaryPrimitives.ReadInt32LittleEndian(data[i..]) / 2147483648d;
                    if (!double.IsFinite(value)) continue;
                    value = Math.Clamp(value, -1, 1);
                    squares += value * value; maximum = Math.Max(maximum, Math.Abs(value)); count++;
                    if (Math.Abs(value) >= .99) clipped++;
                }
                accumulator.AddEnergy(squares, maximum, count, clipped);
            };
            capture.RecordingStopped += (_, e) => { if (e.Exception is not null) Error = e.Exception.Message; };
            capture.StartRecording();
        }
        catch { Stop(); throw; }
    }

    public AudioSummary? Drain() => accumulator.Drain();
    public static string DefaultDeviceId()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var selected = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        return selected.ID;
    }
    public void Stop()
    {
        lock (lifecycle) StopCore();
    }

    private void StopCore()
    {
        if (capture is not null)
        {
            try { capture.StopRecording(); } catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
            capture.Dispose(); capture = null;
        }
        device?.Dispose(); device = null;
        accumulator.Drain();
    }
    public void Dispose() => Stop();
}
