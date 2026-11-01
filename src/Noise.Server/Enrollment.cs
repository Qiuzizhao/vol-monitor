using Noise.Core;

namespace Noise.Server;

public sealed record PendingDevice(string Id, string Name, string HardwareFingerprint, DateTimeOffset CreatedAt,
    DateTimeOffset LastSeen, string? SuggestedSeatId, string? ExistingDeviceId, bool DuplicateHardware);
public sealed record EnrollmentOverview(EnrollmentWindow Window, PendingDevice[] Requests);

public sealed partial class RoomStore
{
    private DateTimeOffset? enrollmentClosesAt;
    private EnrollmentWindow Window() => new(enrollmentClosesAt > Now, enrollmentClosesAt);

    public EnrollmentWindow EnrollmentStatus() { lock (gate) return Window(); }
    public EnrollmentWindow SetEnrollmentWindow(bool open)
    {
        lock (gate) { enrollmentClosesAt = open ? Now.AddMinutes(10) : null; return Window(); }
    }

    private static bool Hex64(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    public EnrollmentState RequestEnrollment(EnrollmentRequest request, string claim)
    {
        lock (gate)
        {
            if (!Hex64(claim)) throw new UnauthorizedAccessException("接入凭证无效。");
            if (!Hex64(request.HardwareFingerprint) || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 64)
                throw new ArgumentException("设备名称或硬件标识无效。");
            var fingerprint = request.HardwareFingerprint.ToUpperInvariant();
            var hash = TeacherAccess.TokenHash(claim);
            var existing = Scalar("SELECT id FROM enrollment_requests WHERE claim_hash=$hash", ("$hash", hash)) as string;
            if (existing is not null)
            {
                var saved = Scalar("SELECT fingerprint FROM enrollment_requests WHERE id=$id", ("$id", existing)) as string;
                if (saved != fingerprint) throw new UnauthorizedAccessException("接入凭证与设备标识不一致。");
                return PollEnrollment(existing, claim);
            }
            if (!Window().Open) throw new ArgumentException("教师尚未开启批量接入，请稍后重试。");
            if (tokens.ContainsKey(hash)) throw new ArgumentException("此申请凭证已被使用，请重新生成独立凭证。");
            Exec("DELETE FROM enrollment_requests WHERE expires_at<$now AND status!='approved'", ("$now", Now.ToString("O")));
            if (Convert.ToInt32(Scalar("SELECT COUNT(*) FROM enrollment_requests WHERE status='pending'")) >= 500)
                throw new ArgumentException("待接入设备数量已达上限，请教师先处理现有申请。");
            var id = Guid.NewGuid().ToString("N");
            Exec("INSERT INTO enrollment_requests VALUES($id,$name,$fingerprint,$hash,'pending',NULL,$now,$now,$expiry)",
                ("$id", id), ("$name", request.Name.Trim()), ("$fingerprint", fingerprint), ("$hash", hash),
                ("$now", Now.ToString("O")), ("$expiry", Now.AddHours(24).ToString("O")));
            return new("pending", id, null);
        }
    }

    public EnrollmentState PollEnrollment(string id, string claim)
    {
        lock (gate)
        {
            if (!Hex64(claim)) throw new UnauthorizedAccessException("接入凭证无效。");
            var hash = TeacherAccess.TokenHash(claim);
            string status; string? deviceId; DateTimeOffset expires;
            using (var cmd = Command("SELECT status,device_id,expires_at FROM enrollment_requests WHERE id=$id AND claim_hash=$hash", ("$id", id), ("$hash", hash)))
            using (var reader = cmd.ExecuteReader())
            {
                if (!reader.Read()) throw new UnauthorizedAccessException("接入申请不存在，或凭证不匹配。");
                status = reader.GetString(0); deviceId = reader.IsDBNull(1) ? null : reader.GetString(1);
                expires = DateTimeOffset.Parse(reader.GetString(2));
            }
            if (status == "approved")
                return devices.ContainsKey(deviceId!) && tokens.GetValueOrDefault(hash) == deviceId
                    ? new("approved", id, deviceId) : new("revoked", id, null);
            if (expires <= Now) return new("expired", id, null);
            Exec("UPDATE enrollment_requests SET last_seen=$now WHERE id=$id", ("$now", Now.ToString("O")), ("$id", id));
            return new(status, id, null);
        }
    }

    public EnrollmentOverview EnrollmentRequests()
    {
        lock (gate)
        {
            var requests = new List<PendingDevice>();
            using (var cmd = Command("""
                SELECT r.id,r.name,r.fingerprint,r.created_at,r.last_seen,d.id,d.seat_id
                FROM enrollment_requests r LEFT JOIN device_hardware h ON h.fingerprint=r.fingerprint
                LEFT JOIN devices d ON d.id=h.device_id
                WHERE r.status='pending' AND r.expires_at>$now ORDER BY r.created_at,r.rowid
                """, ("$now", Now.ToString("O"))))
            using (var reader = cmd.ExecuteReader())
                while (reader.Read())
                {
                    var suggested = reader.IsDBNull(6) ? null : reader.GetString(6);
                    if (suggested is null && MachineIdentity.SeatNumber(reader.GetString(1)) is { } n && n <= Seats.Length)
                        suggested = Seats[n - 1].Id;
                    requests.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3)),
                        DateTimeOffset.Parse(reader.GetString(4)), suggested, reader.IsDBNull(5) ? null : reader.GetString(5), false));
                }
            var planned = new HashSet<string>();
            var suggestions = new Dictionary<string, string?>();
            // Existing hardware keeps its position; explicit machine numbers come
            // next. Other computers get unused seats, without granting access.
            foreach (var request in requests.OrderBy(r => r.ExistingDeviceId is not null ? 0 : r.SuggestedSeatId is not null ? 1 : 2))
            {
                bool Available(Seat seat) => !planned.Contains(seat.Id) &&
                    !devices.Values.Any(d => !d.Demo && d.SeatId == seat.Id && d.Id != request.ExistingDeviceId);
                var seat = Seats.FirstOrDefault(s => s.Id == request.SuggestedSeatId && Available(s)) ?? Seats.FirstOrDefault(Available);
                suggestions[request.Id] = seat?.Id;
                if (seat is not null) planned.Add(seat.Id);
            }
            return new(Window(), requests.Select(r => r with
            {
                SuggestedSeatId = suggestions[r.Id],
                DuplicateHardware = requests.Count(x => x.HardwareFingerprint == r.HardwareFingerprint) > 1
            }).ToArray());
        }
    }

    public void ApproveEnrollments(EnrollmentBatch batch)
    {
        lock (gate)
        {
            if (batch.Items is null || batch.Items.Length is < 1 or > 64) throw new ArgumentException("每次请选择 1～64 台设备。");
            if (batch.Items.Select(i => i.RequestId).Distinct().Count() != batch.Items.Length ||
                batch.Items.Select(i => i.SeatId).Distinct().Count() != batch.Items.Length)
                throw new ArgumentException("申请或座位有重复，请先检查映射。");
            var pending = EnrollmentRequests().Requests.ToDictionary(r => r.Id);
            var plan = new List<(PendingDevice Request, Device Device, string Hash)>();
            foreach (var item in batch.Items)
            {
                if (!pending.TryGetValue(item.RequestId, out var request)) throw new ArgumentException("部分申请已过期或已处理，请刷新。");
                if (!Seats.Any(s => s.Id == item.SeatId)) throw new ArgumentException("请选择有效座位。");
                if (plan.Any(p => p.Request.HardwareFingerprint == request.HardwareFingerprint)) throw new ArgumentException("同一硬件标识有多个申请，请核对后只批准其中一个。");
                if (devices.Values.Any(d => !d.Demo && d.SeatId == item.SeatId && d.Id != request.ExistingDeviceId))
                    throw new ArgumentException($"座位 {Seats.First(s => s.Id == item.SeatId).Label} 已被其他设备占用。");
                var hash = (string)Scalar("SELECT claim_hash FROM enrollment_requests WHERE id=$id", ("$id", item.RequestId))!;
                plan.Add((request, new Device { Id = Guid.NewGuid().ToString("N"), Name = request.Name, SeatId = item.SeatId }, hash));
            }
            using var transaction = db.BeginTransaction();
            foreach (var p in plan)
            {
                if (p.Request.ExistingDeviceId is { } old)
                {
                    Exec("DELETE FROM device_hardware WHERE device_id=$id", ("$id", old));
                    Exec("DELETE FROM devices WHERE id=$id", ("$id", old));
                }
                Exec("INSERT INTO devices(id,name,seat_id,token_hash) VALUES($id,$name,$seat,$hash)", ("$id", p.Device.Id),
                    ("$name", p.Device.Name), ("$seat", p.Device.SeatId), ("$hash", p.Hash));
                Exec("INSERT INTO device_hardware VALUES($fingerprint,$id)", ("$fingerprint", p.Request.HardwareFingerprint), ("$id", p.Device.Id));
                Exec("UPDATE enrollment_requests SET status='approved',device_id=$device WHERE id=$id", ("$device", p.Device.Id), ("$id", p.Request.Id));
            }
            transaction.Commit();
            foreach (var p in plan)
            {
                if (p.Request.ExistingDeviceId is { } old)
                {
                    devices.Remove(old);
                    foreach (var key in tokens.Where(t => t.Value == old).Select(t => t.Key).ToArray()) tokens.Remove(key);
                }
                devices[p.Device.Id] = p.Device; tokens[p.Hash] = p.Device.Id;
            }
        }
    }

    public void RejectEnrollment(string id)
    {
        lock (gate)
        {
            var status = Scalar("SELECT status FROM enrollment_requests WHERE id=$id", ("$id", id)) as string;
            if (status != "pending") throw new ArgumentException("申请不存在或已处理。");
            Exec("UPDATE enrollment_requests SET status='rejected' WHERE id=$id", ("$id", id));
        }
    }
}
