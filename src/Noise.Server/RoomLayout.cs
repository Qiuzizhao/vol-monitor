using System.Globalization;
using Noise.Core;

namespace Noise.Server;

public sealed partial class RoomStore
{
    private void InitializeLayout()
    {
        if (Scalar("SELECT rows FROM room_layout WHERE id=1") is not null)
        {
            LayoutRows = Convert.ToInt32(Scalar("SELECT rows FROM room_layout WHERE id=1"));
            LayoutColumns = Convert.ToInt32(Scalar("SELECT columns FROM room_layout WHERE id=1"));
        }
        else
        {
            LayoutRows = Seats.Max(s => s.Row); LayoutColumns = Seats.Max(s => s.Column);
            Exec("INSERT INTO room_layout VALUES(1,$rows,$columns)", ("$rows", LayoutRows), ("$columns", LayoutColumns));
        }
    }

    private static string SeatKey(string label) =>
        int.TryParse(label, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? "N:" + number.ToString(CultureInfo.InvariantCulture) : "S:" + label.ToUpperInvariant();

    public void SaveLayout(LayoutRequest request)
    {
        lock (gate)
        {
            var cells = request.Cells;
            if (cells is null || cells.Length is < 1 or > 30 || cells[0] is null || cells[0].Length is < 1 or > 30 ||
                cells.Any(row => row is null || row.Length != cells[0].Length)) throw new ArgumentException("布局需为 1～30 行、1～30 列的完整表格。");
            var old = Seats.ToDictionary(s => SeatKey(s.Label));
            var used = new HashSet<string>(); var next = new List<Seat>();
            for (var row = 0; row < cells.Length; row++)
            for (var col = 0; col < cells[row].Length; col++)
            {
                var label = cells[row][col]?.Trim();
                if (string.IsNullOrEmpty(label)) continue;
                if (label.Length > 16 || label.Any(char.IsControl)) throw new ArgumentException("座位号最多 16 个字符。");
                var key = SeatKey(label);
                if (!used.Add(key)) throw new ArgumentException($"座位号 {label} 重复。");
                var id = old.TryGetValue(key, out var previous) ? previous.Id : "seat-" + Guid.NewGuid().ToString("N");
                var zone = Zones[Math.Min(2, row * 3 / cells.Length) * 2 + (col >= (cells[0].Length + 1) / 2 ? 1 : 0)].Id;
                next.Add(new(id, label, row + 1, col + 1, zone));
            }
            if (next.Count == 0) throw new ArgumentException("请至少填写一个座位号。");
            var ids = next.Select(s => s.Id).ToHashSet();
            var removedBindings = devices.Values.Where(d => !d.Demo && d.SeatId is not null && !ids.Contains(d.SeatId)).ToArray();
            using (var transaction = db.BeginTransaction())
            {
                Exec("DELETE FROM seats");
                foreach (var seat in next)
                    Exec("INSERT INTO seats VALUES($id,$label,$row,$col,$zone)", ("$id", seat.Id), ("$label", seat.Label),
                        ("$row", seat.Row), ("$col", seat.Column), ("$zone", seat.ZoneId));
                Exec("UPDATE room_layout SET rows=$rows,columns=$columns WHERE id=1", ("$rows", cells.Length), ("$columns", cells[0].Length));
                foreach (var device in removedBindings) Exec("UPDATE devices SET seat_id=NULL WHERE id=$id", ("$id", device.Id));
                transaction.Commit();
            }
            Seats = next.ToArray(); LayoutRows = cells.Length; LayoutColumns = cells[0].Length;
            foreach (var device in removedBindings) { device.SeatId = null; device.Level = device.Smoothed = null; device.LastSample = null; }
            foreach (var latch in latches.Values) latch.Reset();
        }
    }
}
