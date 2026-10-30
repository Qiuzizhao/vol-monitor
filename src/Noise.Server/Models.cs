using Noise.Core;

namespace Noise.Server;

public sealed record Seat(string Id, string Label, int Row, int Column, string ZoneId);
public sealed record Zone(string Id, string Name);
public sealed class Device
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? SeatId { get; set; }
    public bool Demo { get; init; }
    public string? MicrophoneId { get; set; }
    public double? Baseline { get; set; }
    public string? BaselineId { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    public DateTimeOffset? LastSample { get; set; }
    public string Status { get; set; } = "offline";
    public string? BootId { get; set; }
    public long Seq { get; set; } = -1;
    public double? Level { get; set; }
    public double? Smoothed { get; set; }
    public double? Peak { get; set; }
    public double Clipping { get; set; }
}
public sealed record ResearchSession(string Id, string Name, string Mode, bool Demo,
    string Status, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);
public sealed record SeatView(string Id, string Label, int Row, int Column, string ZoneId,
    string? DeviceId, string? DeviceName, string Status, double? Score, double? LevelDbfs,
    double? BaselineDbfs, DateTimeOffset? LastSeen, double? ClippingRatio);
public sealed record ZoneView(string Id, string Name, double? Score, int Valid, int Expected,
    bool LimitedCoverage, bool Alert);
public sealed record DeviceView(string Id, string Name, string? SeatId, string Status,
    string? MicrophoneId, double? BaselineDbfs, DateTimeOffset? LastSeen);
public sealed record TrendPoint(DateTimeOffset Time, double? Average, Dictionary<string, double?> Zones);
public sealed record Annotation(string Id, string SessionId, DateTimeOffset Time, string Text, string Kind);
public sealed record Dashboard(string RoomName, DateTimeOffset ServerTime, string AlgorithmVersion,
    ResearchSession? Session, string? CalibrationId, DateTimeOffset? CalibrationUntil,
    SeatView[] Seats, ZoneView[] Zones, DeviceView[] Devices, TrendPoint[] Trend,
    Annotation[] Annotations);
