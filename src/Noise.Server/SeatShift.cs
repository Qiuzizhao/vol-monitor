using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Noise.Core;

namespace Noise.Server;

public sealed record SeatShiftExample(string Ip, string From, string To);
public sealed record SeatShiftPreview(string From, string To, int FollowingCount, string ExpectedState,
    bool OfferShift, bool CanShift, string? Error, SeatShiftExample[] Examples);

public sealed partial class RoomStore
{
    private string SeatState() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        SequentialMatching,
        Devices = devices.Values.Where(d => !d.Demo).OrderBy(d => d.Id).Select(d => new { d.Id, d.SeatId, d.IpAddress }),
        Seats = Seats.OrderBy(s => s.Id).Select(s => new { s.Id, s.Label })
    }))));

    private void CheckSeatState(string? expected)
    {
        if (expected is not null && expected != SeatState())
            throw new ArgumentException("座位安排或学生机列表已变化，请重新选择座位后确认。");
    }

    private (Device[] Students, Seat[] Seats, int Index, int From, int To) ShiftContext(string id, string seatId)
    {
        var students = devices.Values.Where(d => !d.Demo).ToArray();
        if (students.Any(d => !System.Net.IPAddress.TryParse(d.IpAddress, out _))) throw new ArgumentException("部分学生机还没有有效 IP。");
        students = students.OrderBy(d => AddressOrder(d.IpAddress!), StringComparer.Ordinal).ThenBy(d => d.Id, StringComparer.Ordinal).ToArray();
        var index = Array.FindIndex(students, d => d.Id == id);
        if (index < 0) throw new ArgumentException("设备不存在。");
        var seats = Seats.OrderBy(s => s.Label, NaturalSeatOrder.Instance).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();
        var target = Array.FindIndex(seats, s => s.Id == seatId);
        if (target < 0) throw new ArgumentException("座位不存在。");
        return (students, seats, index, Array.FindIndex(seats, s => s.Id == students[index].SeatId), target);
    }

    private List<(Device Device, Seat Seat)> ShiftPlan(string id, string seatId)
    {
        var context = ShiftContext(id, seatId);
        var (students, seats, index, from, to) = context;
        if (!SequentialMatching || from < 0 || to <= from || index == students.Length - 1)
            throw new ArgumentException("当前修改无需顺延后续座位。");
        var offset = to - from;
        var plan = new List<(Device Device, Seat Seat)>();
        for (var i = index; i < students.Length; i++)
        {
            var old = Array.FindIndex(seats, s => s.Id == students[i].SeatId);
            if (old < 0) throw new ArgumentException("部分后续学生机尚未分配座位，请先调整或仅修改本机。");
            if (old + offset >= seats.Length) throw new ArgumentException("后续座位不足，无法全部顺延；请增加座位或仅修改本机。");
            plan.Add((students[i], seats[old + offset]));
        }
        var targets = plan.Select(p => p.Seat.Id).ToHashSet();
        if (targets.Count != plan.Count || students.Take(index).Any(d => d.SeatId is not null && targets.Contains(d.SeatId)))
            throw new ArgumentException("顺延后会出现座位重复，请先调整或仅修改本机。");
        return plan;
    }

    public SeatShiftPreview PreviewSeatShift(string id, string seatId)
    {
        lock (gate)
        {
            var (students, seats, index, from, to) = ShiftContext(id, seatId);
            var following = students.Length - index - 1;
            var offer = SequentialMatching && from >= 0 && to > from && following > 0;
            var state = SeatState(); var fromLabel = from < 0 ? "未分配" : seats[from].Label;
            if (!offer) return new(fromLabel, seats[to].Label, following, state, false, false, null, []);
            try
            {
                var plan = ShiftPlan(id, seatId);
                return new(fromLabel, seats[to].Label, following, state, true, true, null,
                    plan.Skip(1).Take(3).Select(p => new SeatShiftExample(p.Device.IpAddress!, seats.Single(s => s.Id == p.Device.SeatId).Label, p.Seat.Label)).ToArray());
            }
            catch (ArgumentException e) { return new(fromLabel, seats[to].Label, following, state, true, false, e.Message, []); }
        }
    }

    public SequentialMatchResult ShiftFollowingSeats(string id, SeatAssignment request)
    {
        lock (gate)
        {
            if (request.ExpectedState is null) throw new ArgumentException("请先确认顺延范围。");
            CheckSeatState(request.ExpectedState);
            var plan = ShiftPlan(id, request.SeatId);
            using var transaction = db.BeginTransaction();
            foreach (var p in plan) Exec("UPDATE devices SET seat_id=$seat WHERE id=$id", ("$seat", p.Seat.Id), ("$id", p.Device.Id));
            transaction.Commit();
            foreach (var p in plan) p.Device.SeatId = p.Seat.Id;
            return new(plan.Count, plan.Count);
        }
    }
}
