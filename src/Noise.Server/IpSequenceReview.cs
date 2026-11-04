using System.Net;

namespace Noise.Server;

public sealed record IpSequenceGap(string Before, string After, string MissingFrom, string MissingTo, int Count);
public sealed record MatchConfirmation(string ExpectedState);
public sealed record SequentialMatchPreview(int Students, int Seats, int CheckedPairs, int AddressGroups,
    int UncheckedAddresses, IpSequenceGap[] Gaps, string[] OfflineIps, bool CanMatch, string? Error, string ExpectedState);

public sealed partial class RoomStore
{
    public SequentialMatchPreview PreviewSequentialMatch()
    {
        lock (gate)
        {
            var students = devices.Values.Where(d => !d.Demo).ToArray();
            var sequence = CheckIpSequence(students);
            var error = students.Length == 0 ? "暂无学生机。" : students.Any(d => !IPAddress.TryParse(d.IpAddress, out _))
                ? "部分学生机还没有有效 IP，请等待连接后再匹配。" : students.Length > Seats.Length
                ? $"座位不足：共有 {students.Length} 台学生机、{Seats.Length} 个座位，请先增加座位。" : null;
            var offline = students.Where(d => Status(d, Now) == "offline").Select(d => d.IpAddress ?? "未连接")
                .OrderBy(ip => IPAddress.TryParse(ip, out _) ? AddressOrder(ip) : ip, StringComparer.Ordinal).ToArray();
            return new(students.Length, Seats.Length, sequence.CheckedPairs, sequence.AddressGroups, sequence.UncheckedAddresses,
                sequence.Gaps, offline, error is null, error, SeatState());
        }
    }

    private static (IpSequenceGap[] Gaps, int CheckedPairs, int AddressGroups, int UncheckedAddresses) CheckIpSequence(IEnumerable<Device> students)
    {
        var ipv4 = new List<byte[]>(); var uncheckedCount = 0;
        foreach (var student in students)
        {
            if (!IPAddress.TryParse(student.IpAddress, out var ip)) { uncheckedCount++; continue; }
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var bytes = ip.GetAddressBytes();
            if (bytes.Length == 4) ipv4.Add(bytes); else uncheckedCount++;
        }
        // Without a configured classroom IP range, only infer internal gaps
        // within the same first three octets. Never guess leading/trailing hosts.
        var groups = ipv4.GroupBy(b => $"{b[0]}.{b[1]}.{b[2]}").OrderBy(g => Convert.ToHexString(g.First()), StringComparer.Ordinal).ToArray();
        var gaps = new List<IpSequenceGap>(); var checkedPairs = 0;
        foreach (var group in groups)
        {
            var hosts = group.Select(b => (int)b[3]).Distinct().Order().ToArray();
            for (var i = 1; i < hosts.Length; i++)
            {
                checkedPairs++;
                var before = hosts[i - 1]; var after = hosts[i];
                if (after - before > 1)
                    gaps.Add(new($"{group.Key}.{before}", $"{group.Key}.{after}", $"{group.Key}.{before + 1}", $"{group.Key}.{after - 1}", after - before - 1));
            }
        }
        return (gaps.ToArray(), checkedPairs, groups.Length, uncheckedCount);
    }

    public SequentialMatchResult ConfirmSequentialMatch(MatchConfirmation request)
    {
        lock (gate)
        {
            if (string.IsNullOrEmpty(request.ExpectedState)) throw new ArgumentException("请先检查 IP 连号情况后确认。");
            if (request.ExpectedState != SeatState()) throw new ArgumentException("学生机列表或座位安排已变化，请重新检查后确认。");
            return MatchSeatsInOrder();
        }
    }
}
