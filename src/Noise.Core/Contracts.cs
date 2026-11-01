namespace Noise.Core;

public record DeviceReport(
    string BootId, long Seq, string? SessionId, string CaptureStatus,
    string? MicrophoneId = null, double? LevelDbfs = null, double? PeakDbfs = null,
    double? ClippingRatio = null, int SampleDurationMs = 1000);
public record DeviceCommand(string? SessionId, string State, string? CalibrationId,
    DateTimeOffset? CalibrationUntil, string? Seat, string? Zone, string AlgorithmVersion);
public record CalibrationReport(string SessionId, string CalibrationId, string MicrophoneId,
    double BaselineDbfs, int SampleCount);
public record SessionRequest(string Name, string Mode = "quiet", bool Demo = false);
public record AnnotationRequest(string Text);
public record SeatAssignment(string SeatId);
public record EnrollmentRequest(string Name, string HardwareFingerprint);
public record EnrollmentState(string Status, string? RequestId, string? DeviceId);
public record EnrollmentWindow(bool Open, DateTimeOffset? ClosesAt);
public record EnrollmentApproval(string RequestId, string SeatId);
public record EnrollmentBatch(EnrollmentApproval[] Items);
public record LayoutRequest(string?[][] Cells);
