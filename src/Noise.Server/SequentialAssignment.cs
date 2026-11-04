using System.Net;

namespace Noise.Server;

public sealed record SequentialMatchResult(int Matched, int Changed);

public sealed partial class RoomStore
{
    public bool SequentialMatching { get; private set; }
    public SequentialMatchResult MatchSeatsInOrder()
    {
        lock (gate)
        {
            var students = devices.Values.Where(d => !d.Demo).ToArray();
            if (students.Any(d => !IPAddress.TryParse(d.IpAddress, out _)))
                throw new ArgumentException("部分学生机还没有有效 IP，请等待连接后再匹配。");
            if (students.Length > Seats.Length)
                throw new ArgumentException($"座位不足：共有 {students.Length} 台学生机、{Seats.Length} 个座位，请先增加座位。");
            var ordered = students.OrderBy(d => AddressOrder(d.IpAddress!), StringComparer.Ordinal).ThenBy(d => d.Id, StringComparer.Ordinal).ToArray();
            var seats = Seats.OrderBy(s => s.Label, NaturalSeatOrder.Instance).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();
            var changes = ordered.Select((device, i) => (Device: device, Seat: seats[i]))
                .Where(p => p.Device.SeatId != p.Seat.Id).ToArray();
            using var transaction = db.BeginTransaction();
            foreach (var p in changes)
                Exec("UPDATE devices SET seat_id=$seat WHERE id=$id", ("$seat", p.Seat.Id), ("$id", p.Device.Id));
            Exec("INSERT INTO room_matching VALUES(1,1) ON CONFLICT(id) DO UPDATE SET enabled=1");
            transaction.Commit();
            SequentialMatching = true;
            foreach (var p in changes) p.Device.SeatId = p.Seat.Id;
            return new(ordered.Length, changes.Length);
        }
    }

    private static string AddressOrder(string value)
    {
        var ip = IPAddress.Parse(value); if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var bytes = ip.GetAddressBytes(); return (bytes.Length == 4 ? "4:" : "6:") + Convert.ToHexString(bytes);
    }
}

internal sealed class NaturalSeatOrder : IComparer<string>
{
    public static readonly NaturalSeatOrder Instance = new();
    public int Compare(string? x, string? y)
    {
        x = x?.Trim() ?? ""; y = y?.Trim() ?? "";
        int a = 0, b = 0;
        while (a < x.Length && b < y.Length)
        {
            if (char.IsAsciiDigit(x[a]) && char.IsAsciiDigit(y[b]))
            {
                int ae = a, be = b;
                while (ae < x.Length && char.IsAsciiDigit(x[ae])) ae++;
                while (be < y.Length && char.IsAsciiDigit(y[be])) be++;
                int az = a, bz = b;
                while (az < ae && x[az] == '0') az++;
                while (bz < be && y[bz] == '0') bz++;
                var comparison = (ae - az).CompareTo(be - bz);
                if (comparison == 0) comparison = x.AsSpan(az, ae - az).SequenceCompareTo(y.AsSpan(bz, be - bz));
                if (comparison != 0) return comparison;
                a = ae; b = be;
            }
            else
            {
                var comparison = char.ToUpperInvariant(x[a]).CompareTo(char.ToUpperInvariant(y[b]));
                if (comparison != 0) return comparison;
                a++; b++;
            }
        }
        return (x.Length - a).CompareTo(y.Length - b);
    }
}
