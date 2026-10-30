using System.Security.Cryptography;
using System.Text.Json;
using Noise.Core;
using Noise.Server;
using Xunit;

namespace Noise.Tests;

public sealed class EnrollmentTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "roomscope-image-" + Guid.NewGuid());
    private readonly ManualClock clock = new();
    private readonly RoomStore store;
    public EnrollmentTests() => store = new(directory, "Image test", timeProvider: clock);
    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string Hardware(string mac = "001122334455") => MachineIdentity.Fingerprint("7d852f61-33eb-4c3f-a19a-a1d17e7f0902", [mac]);
    private (EnrollmentState Request, string Claim) Request(string name = "PC01", string? hardware = null)
    {
        var claim = Secret();
        return (store.RequestEnrollment(new(name, hardware ?? Hardware()), claim), claim);
    }
    private string Approve(EnrollmentState request, string claim, string seat = "seat-01")
    {
        store.ApproveEnrollments(new([new(request.RequestId!, seat)]));
        return store.PollEnrollment(request.RequestId!, claim).DeviceId!;
    }

    [Fact]
    public void ClonedMachineNamesAndSmbiosIdsStillHaveDistinctNicFingerprints()
    {
        Assert.NotEqual(Hardware("001122334455"), Hardware("001122334466"));
        Assert.Equal(MachineIdentity.Fingerprint("", ["00:11:22:33:44:55", "AA-BB-CC-DD-EE-FF"]),
            MachineIdentity.Fingerprint(null, ["aabbccddeeff", "001122334455", "001122334455"]));
        Assert.False(MachineIdentity.Matches(null, Hardware()));
        Assert.False(MachineIdentity.Matches(Hardware(), Hardware("001122334466")));
        Assert.Throws<ArgumentException>(() => MachineIdentity.Fingerprint(Guid.NewGuid().ToString(), ["000000000000"]));
    }

    [Theory]
    [InlineData("PC01", 1)] [InlineData("stu-002", 2)] [InlineData("学生机03", 3)]
    [InlineData("MASTER-PC", null)] [InlineData("PC00", null)]
    public void SeatSuggestionsUseExplicitNumberedMachineNames(string name, int? number) => Assert.Equal(number, MachineIdentity.SeatNumber(name));

    [Fact]
    public void ClosedWindowRejectsNewRequestsAndPendingKeyCannotSendTelemetry()
    {
        Assert.Throws<ArgumentException>(() => Request());
        store.SetEnrollmentWindow(true);
        var (request, claim) = Request();
        Assert.Equal("pending", request.Status);
        Assert.Null(store.Authenticate(claim));
        Assert.Empty(store.Snapshot().Devices);
        store.SetEnrollmentWindow(false);
        Assert.Equal("pending", store.PollEnrollment(request.RequestId!, claim).Status);
        Assert.Throws<ArgumentException>(() => Request("PC02", Hardware("001122334466")));
    }

    [Fact]
    public void NewManualPairingAlsoRetainsHardwareSeatForLaterImageEnrollment()
    {
        var code = JsonSerializer.SerializeToElement(store.Pairing()).GetProperty("code").GetString()!;
        var paired = store.Enroll(new(code, "PC01", Hardware())); store.Assign(paired.DeviceId, "seat-01");
        store.SetEnrollmentWindow(true); var (request, claim) = Request();
        Assert.Equal(paired.DeviceId, Assert.Single(store.EnrollmentRequests().Requests).ExistingDeviceId);
        Approve(request, claim);
        Assert.Null(store.Authenticate(paired.Token));
    }

    [Fact]
    public void RequestsAreIdempotentAndRequireTheirOwnSecret()
    {
        store.SetEnrollmentWindow(true); var (request, claim) = Request();
        Assert.Equal(request.RequestId, store.RequestEnrollment(new("PC01", Hardware()), claim).RequestId);
        Assert.Single(store.EnrollmentRequests().Requests);
        Assert.Throws<UnauthorizedAccessException>(() => store.PollEnrollment(request.RequestId!, Secret()));
        Assert.Throws<UnauthorizedAccessException>(() => store.RequestEnrollment(new("PC02", Hardware("001122334466")), claim));
    }

    [Fact]
    public void BatchApprovalCreatesIndependentCredentialsAndSeatBindings()
    {
        store.SetEnrollmentWindow(true);
        var (first, key1) = Request(); var (second, key2) = Request("PC02", Hardware("001122334466"));
        var pending = store.EnrollmentRequests().Requests;
        Assert.Equal("seat-01", pending[0].SuggestedSeatId); Assert.Equal("seat-02", pending[1].SuggestedSeatId);
        store.ApproveEnrollments(new([new(first.RequestId!, "seat-01"), new(second.RequestId!, "seat-02")]));
        var id1 = store.PollEnrollment(first.RequestId!, key1).DeviceId!;
        var id2 = store.PollEnrollment(second.RequestId!, key2).DeviceId!;
        Assert.NotEqual(id1, id2); Assert.Equal(id1, store.Authenticate(key1)); Assert.Equal(id2, store.Authenticate(key2));
        Assert.Equal(2, store.Snapshot().Devices.Length); Assert.Empty(store.EnrollmentRequests().Requests);
        Assert.Equal("seat-01", store.Snapshot().Devices.First(d => d.Id == id1).SeatId);
    }

    [Fact]
    public void ReimagingPreservesSeatSuggestionButNeedsApprovalAndRevokesOldToken()
    {
        store.SetEnrollmentWindow(true);
        var (first, oldKey) = Request(); var oldId = Approve(first, oldKey);
        var (replacement, newKey) = Request("RENAMED-PC");
        var pending = Assert.Single(store.EnrollmentRequests().Requests);
        Assert.Equal(oldId, pending.ExistingDeviceId); Assert.Equal("seat-01", pending.SuggestedSeatId);
        Assert.Null(store.Authenticate(newKey)); Assert.Equal(oldId, store.Authenticate(oldKey));
        var newId = Approve(replacement, newKey);
        Assert.NotEqual(oldId, newId); Assert.Null(store.Authenticate(oldKey)); Assert.Equal(newId, store.Authenticate(newKey));
        Assert.Equal("revoked", store.PollEnrollment(first.RequestId!, oldKey).Status);
        Assert.Single(store.Snapshot().Devices); Assert.Null(store.Snapshot().Devices[0].BaselineDbfs);
    }

    [Fact]
    public void ConflictingBatchDoesNotPartiallyApproveOrOverwriteSeats()
    {
        store.SetEnrollmentWindow(true);
        var (first, key1) = Request(); var (second, key2) = Request("PC02", Hardware("001122334466"));
        Assert.Throws<ArgumentException>(() => store.ApproveEnrollments(new([new(first.RequestId!, "seat-01"), new(second.RequestId!, "seat-01")])));
        Assert.Empty(store.Snapshot().Devices); Assert.Null(store.Authenticate(key1)); Assert.Null(store.Authenticate(key2));
        var id = Approve(first, key1);
        Assert.Throws<ArgumentException>(() => Approve(second, key2));
        Assert.Equal(id, Assert.Single(store.Snapshot().Devices).Id);
    }

    [Fact]
    public void DuplicateHardwareRequestsAreVisibleAndCannotBeApprovedTogether()
    {
        store.SetEnrollmentWindow(true);
        var (first, _) = Request(); var (second, _) = Request("CLONE");
        Assert.All(store.EnrollmentRequests().Requests, r => Assert.True(r.DuplicateHardware));
        Assert.Throws<ArgumentException>(() => store.ApproveEnrollments(new([new(first.RequestId!, "seat-01"), new(second.RequestId!, "seat-02")])));
        Assert.Empty(store.Snapshot().Devices);
    }

    [Fact]
    public void RejectionAndExpiryNeverGrantCredentials()
    {
        store.SetEnrollmentWindow(true);
        var (rejected, key) = Request(); store.RejectEnrollment(rejected.RequestId!);
        Assert.Equal("rejected", store.PollEnrollment(rejected.RequestId!, key).Status); Assert.Null(store.Authenticate(key));
        var (expired, key2) = Request("PC02", Hardware("001122334466"));
        clock.Advance(601); Assert.False(store.EnrollmentStatus().Open);
        clock.Advance(86400); Assert.Equal("expired", store.PollEnrollment(expired.RequestId!, key2).Status);
        Assert.Throws<ArgumentException>(() => Approve(expired, key2));
    }

    [Fact]
    public void RestartRetainsPendingRequestsButClosesAdmissionWindow()
    {
        store.SetEnrollmentWindow(true); var (request, claim) = Request();
        using var restarted = new RoomStore(directory, "Restarted", timeProvider: clock);
        Assert.False(restarted.EnrollmentStatus().Open);
        Assert.Equal("pending", restarted.PollEnrollment(request.RequestId!, claim).Status);
        restarted.ApproveEnrollments(new([new(request.RequestId!, "seat-01")]));
        Assert.NotNull(restarted.Authenticate(claim));
    }

    public void Dispose() { store.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
}
