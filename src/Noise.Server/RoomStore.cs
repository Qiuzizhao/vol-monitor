using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Noise.Core;

namespace Noise.Server;

public sealed partial class RoomStore : IDisposable
{
    private readonly object gate = new();
    private readonly SqliteConnection db;
    private readonly TimeProvider clock;
    private readonly Dictionary<string, Device> devices = new();
    private readonly Dictionary<string, string> tokens = new();
    private readonly Dictionary<string, AlertLatch> latches = new();
    private readonly Queue<TrendPoint> trend = new();
    private readonly List<Annotation> annotations = [];
    private ResearchSession? session;
    private string? calibrationId;
    private DateTimeOffset? calibrationUntil;
    private string pairingCode = "";
    private DateTimeOffset pairingExpires;
    private long lastBucket = -1;
    public string RoomName { get; }
    public Seat[] Seats { get; }
    public Zone[] Zones { get; } = [new("front-left", "前排左区"), new("front-right", "前排右区"),
        new("middle-left", "中排左区"), new("middle-right", "中排右区"),
        new("back-left", "后排左区"), new("back-right", "后排右区")];

    public RoomStore(string directory, string name, int rows = 6, int columns = 6, TimeProvider? timeProvider = null)
    {
        if (rows is < 3 or > 20 || columns is < 2 or > 16 || columns % 2 != 0)
            throw new ArgumentException("Rows must be 3–20; columns must be even and 2–16.");
        clock = timeProvider ?? TimeProvider.System;
        RoomName = name;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        db = new SqliteConnection($"Data Source={Path.Combine(directory, "roomscope.db")}");
        db.Open();
        Exec("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS seats(id TEXT PRIMARY KEY,label TEXT,row_no INTEGER,column_no INTEGER,zone_id TEXT);
            CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY,name TEXT,seat_id TEXT,token_hash TEXT,microphone_id TEXT);
            CREATE UNIQUE INDEX IF NOT EXISTS one_device_per_seat ON devices(seat_id) WHERE seat_id IS NOT NULL;
            CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY,name TEXT,mode TEXT,demo INTEGER,status TEXT,started_at TEXT,ended_at TEXT,algorithm TEXT,config TEXT);
            CREATE TABLE IF NOT EXISTS calibrations(id TEXT,device_id TEXT,microphone_id TEXT,baseline REAL,sample_count INTEGER,created_at TEXT,PRIMARY KEY(id,device_id));
            CREATE TABLE IF NOT EXISTS device_metrics(session_id TEXT,bucket INTEGER,device_id TEXT,seat_id TEXT,zone_id TEXT,score REAL,level REAL,peak REAL,clipping REAL,baseline_id TEXT,PRIMARY KEY(session_id,bucket,device_id));
            CREATE TABLE IF NOT EXISTS zone_metrics(session_id TEXT,bucket INTEGER,zone_id TEXT,score REAL,valid INTEGER,expected INTEGER,alert INTEGER,PRIMARY KEY(session_id,bucket,zone_id));
            CREATE TABLE IF NOT EXISTS annotations(id TEXT PRIMARY KEY,session_id TEXT,time TEXT,text TEXT,kind TEXT);
            CREATE TABLE IF NOT EXISTS device_hardware(fingerprint TEXT PRIMARY KEY,device_id TEXT UNIQUE);
            CREATE TABLE IF NOT EXISTS enrollment_requests(id TEXT PRIMARY KEY,name TEXT,fingerprint TEXT,claim_hash TEXT UNIQUE,status TEXT,device_id TEXT,created_at TEXT,last_seen TEXT,expires_at TEXT);
            """
        );
        // A restart never silently resumes microphone collection.
        Exec("UPDATE sessions SET status='ended',ended_at=$now WHERE status!='ended'", ("$now", Now.ToString("O")));
        if (Convert.ToInt32(Scalar("SELECT COUNT(*) FROM seats"), CultureInfo.InvariantCulture) == 0)
        {
            for (var r = 1; r <= rows; r++) for (var c = 1; c <= columns; c++)
            {
                var zone = Zones[Math.Min(2, (r - 1) * 3 / rows) * 2 + (c > columns / 2 ? 1 : 0)].Id;
                var index = (r - 1) * columns + c;
                Exec("INSERT INTO seats VALUES($id,$label,$r,$c,$z)", ("$id", $"seat-{index:00}"),
                    ("$label", $"{index:00}"), ("$r", r), ("$c", c), ("$z", zone));
            }
        }
        using (var cmd = Command("SELECT * FROM seats ORDER BY row_no,column_no"))
        using (var reader = cmd.ExecuteReader())
        {
            var seats = new List<Seat>();
            while (reader.Read()) seats.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4)));
            Seats = seats.ToArray();
        }
        using (var cmd = Command("SELECT id,name,seat_id,token_hash,microphone_id FROM devices"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
            {
                var d = new Device { Id = reader.GetString(0), Name = reader.GetString(1),
                    SeatId = reader.IsDBNull(2) ? null : reader.GetString(2), MicrophoneId = reader.IsDBNull(4) ? null : reader.GetString(4) };
                devices.Add(d.Id, d);
                tokens.Add(reader.GetString(3), d.Id);
            }
        foreach (var d in devices.Values) LoadBaseline(d);
        foreach (var z in Zones) latches[z.Id] = new();
    }

    private DateTimeOffset Now => clock.GetUtcNow();
    private SqliteCommand Command(string sql, params (string, object?)[] args)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    private void Exec(string sql, params (string, object?)[] args) { using var cmd = Command(sql, args); cmd.ExecuteNonQuery(); }
    private object? Scalar(string sql, params (string, object?)[] args) { using var cmd = Command(sql, args); return cmd.ExecuteScalar(); }

    public object Pairing(bool rotate = false)
    {
        lock (gate)
        {
            if (rotate || Now >= pairingExpires)
            { pairingCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)); pairingExpires = Now.AddMinutes(10); }
            return new { code = pairingCode, expiresAt = pairingExpires };
        }
    }

    public EnrollResult Enroll(EnrollRequest request)
    {
        lock (gate)
        {
            if (string.IsNullOrEmpty(pairingCode) || Now >= pairingExpires || request.PairingCode != pairingCode)
                throw new UnauthorizedAccessException("配对码无效或已过期。");
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 64) throw new ArgumentException("设备名称需要 1～64 个字符。");
            if (request.HardwareFingerprint is not null && !Hex64(request.HardwareFingerprint)) throw new ArgumentException("硬件标识无效。");
            var fingerprint = request.HardwareFingerprint?.ToUpperInvariant();
            if (fingerprint is not null && Scalar("SELECT device_id FROM device_hardware WHERE fingerprint=$fp", ("$fp", fingerprint)) is not null)
                throw new ArgumentException("此硬件已有登记，请在教师端使用批量接入确认替换，或先移除旧设备。");
            var id = Guid.NewGuid().ToString("N");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var hash = TeacherAccess.TokenHash(token);
            Exec("INSERT INTO devices(id,name,token_hash) VALUES($id,$name,$hash)", ("$id", id), ("$name", request.Name.Trim()), ("$hash", hash));
            if (fingerprint is not null) Exec("INSERT INTO device_hardware VALUES($fp,$id)", ("$fp", fingerprint), ("$id", id));
            devices[id] = new Device { Id = id, Name = request.Name.Trim() }; tokens[hash] = id;
            return new(id, token);
        }
    }

    public string? Authenticate(string token)
    {
        if (token.Length != 64) return null;
        lock (gate) return tokens.GetValueOrDefault(TeacherAccess.TokenHash(token));
    }

    public void Assign(string id, string seatId)
    {
        lock (gate)
        {
            if (!devices.TryGetValue(id, out var d) || d.Demo) throw new ArgumentException("设备不存在。");
            if (!Seats.Any(s => s.Id == seatId)) throw new ArgumentException("座位不存在。");
            if (devices.Values.Any(other => !other.Demo && other.Id != id && other.SeatId == seatId)) throw new ArgumentException("该座位已绑定其他设备。");
            d.SeatId = seatId;
            d.Level = d.Smoothed = null; d.LastSample = null;
            Exec("UPDATE devices SET seat_id=$seat WHERE id=$id", ("$seat", seatId), ("$id", id));
        }
    }

    public void DeleteDevice(string id)
    {
        lock (gate)
        {
            if (!devices.TryGetValue(id, out var d) || d.Demo) throw new ArgumentException("设备不存在。");
            Exec("DELETE FROM devices WHERE id=$id", ("$id", id));
            devices.Remove(id);
            Exec("DELETE FROM device_hardware WHERE device_id=$id", ("$id", id));
            foreach (var key in tokens.Where(x => x.Value == id).Select(x => x.Key).ToArray()) tokens.Remove(key);
        }
    }

    public ResearchSession Start(SessionRequest request)
    {
        lock (gate)
        {
            if (session is { Status: not "ended" }) throw new ArgumentException("请先结束当前实验。");
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100) throw new ArgumentException("实验名称需要 1～100 个字符。");
            if (request.Mode is not ("quiet" or "discussion")) throw new ArgumentException("课堂模式无效。");
            ClearLive();
            session = new(Guid.NewGuid().ToString("N"), request.Name.Trim(), request.Mode, request.Demo, "running", Now, null);
            Exec("INSERT INTO sessions VALUES($id,$name,$mode,$demo,$status,$start,NULL,$algorithm,$config)",
                ("$id", session.Id), ("$name", session.Name), ("$mode", session.Mode), ("$demo", session.Demo),
                ("$status", session.Status), ("$start", session.StartedAt.ToString("O")), ("$algorithm", Acoustics.AlgorithmVersion),
                ("$config", JsonSerializer.Serialize(new { alpha = .35, deadband = 3, span = 27, percentile = .7,
                    alertThreshold = request.Mode == "discussion" ? 85 : 70, alertSeconds = 5, clearSeconds = 3, bucketSeconds = 5 })));
            if (request.Demo)
                foreach (var seat in Seats)
                    devices["demo-" + seat.Id] = new Device { Id = "demo-" + seat.Id, Name = "模拟设备 " + seat.Label,
                        SeatId = seat.Id, Demo = true, Baseline = -65, BaselineId = "demo", MicrophoneId = "simulator" };
            return session;
        }
    }

    private void ClearLive()
    {
        foreach (var id in devices.Values.Where(d => d.Demo).Select(d => d.Id).ToArray()) devices.Remove(id);
        foreach (var d in devices.Values) { d.Level = d.Smoothed = d.Peak = null; d.LastSample = null; }
        trend.Clear(); annotations.Clear(); calibrationId = null; calibrationUntil = null; lastBucket = -1;
        foreach (var latch in latches.Values) latch.Reset();
    }

    public void Transition(string action)
    {
        lock (gate)
        {
            if (session is null || session.Status == "ended") throw new ArgumentException("当前没有进行中的实验。");
            var state = action switch { "pause" => "paused", "resume" => "running", "end" => "ended", _ => throw new ArgumentException("无效操作。") };
            session = session with { Status = state, EndedAt = state == "ended" ? Now : null };
            Exec("UPDATE sessions SET status=$status,ended_at=$end WHERE id=$id", ("$status", state), ("$end", session.EndedAt?.ToString("O")), ("$id", session.Id));
            calibrationId = null; calibrationUntil = null;
            foreach (var d in devices.Values) { d.Level = d.Smoothed = null; d.LastSample = null; }
            foreach (var latch in latches.Values) latch.Reset();
        }
    }

    public void Calibrate()
    {
        lock (gate)
        {
            if (session is not { Status: "running", Demo: false }) throw new ArgumentException("请先开始真实采集实验，再进行校准。");
            calibrationId = Guid.NewGuid().ToString("N"); calibrationUntil = Now.AddSeconds(20);
            AddAnnotation("发起安静基线校准（每台设备采集 10 秒）", "system");
        }
    }

    public DeviceCommand Report(string id, DeviceReport report)
    {
        lock (gate)
        {
            var d = devices[id];
            if (string.IsNullOrWhiteSpace(report.BootId) || report.BootId.Length > 64 || report.Seq < 0) throw new ArgumentException("设备序列无效。");
            if (d.BootId == report.BootId && report.Seq <= d.Seq) return GetCommand(d);
            d.BootId = report.BootId; d.Seq = report.Seq; d.LastSeen = Now;
            d.Status = report.CaptureStatus is "ok" or "idle" or "error" or "no_signal" or "calibrating" or "blocked" ? report.CaptureStatus : "error";
            if (report.MicrophoneId?.Length > 512) throw new ArgumentException("麦克风标识无效。");
            if (d.MicrophoneId != report.MicrophoneId && report.MicrophoneId is not null)
            {
                d.MicrophoneId = report.MicrophoneId; d.Baseline = null; d.BaselineId = null; d.Smoothed = null;
                Exec("UPDATE devices SET microphone_id=$mic WHERE id=$id", ("$mic", d.MicrophoneId), ("$id", d.Id));
                LoadBaseline(d);
            }
            if (report.LevelDbfs is { } level)
            {
                if (!double.IsFinite(level) || level is < -120 or > 0 || report.PeakDbfs is not { } peak ||
                    !double.IsFinite(peak) || peak is < -120 or > 0 || peak < level ||
                    report.ClippingRatio is not { } clipping || !double.IsFinite(clipping) || clipping is < 0 or > 1 ||
                    report.SampleDurationMs is < 500 or > 2000) throw new ArgumentException("音量统计超出有效范围。");
                if (session is { Status: "running", Demo: false } && report.SessionId == session.Id && d.SeatId is not null && report.CaptureStatus == "ok")
                {
                    var fresh = d.LastSample is not null && Now - d.LastSample <= TimeSpan.FromSeconds(3);
                    d.Level = level; d.Peak = peak; d.Clipping = clipping;
                    d.Smoothed = fresh && d.Smoothed is { } old ? .35 * level + .65 * old : level;
                    d.LastSample = Now;
                }
            }
            return GetCommand(d);
        }
    }

    private DeviceCommand GetCommand(Device d)
    {
        var active = session is { Status: "running", Demo: false } && d.SeatId is not null;
        var seat = Seats.FirstOrDefault(s => s.Id == d.SeatId);
        return new(session is { Demo: false, Status: not "ended" } ? session.Id : null,
            active ? "running" : "idle", active && Now <= calibrationUntil ? calibrationId : null,
            active ? calibrationUntil : null, seat?.Label, seat is null ? null : Zones.First(z => z.Id == seat.ZoneId).Name, Acoustics.AlgorithmVersion);
    }

    public void SaveCalibration(string id, CalibrationReport report)
    {
        lock (gate)
        {
            var d = devices[id];
            if (session is not { Status: "running", Demo: false } || report.SessionId != session.Id ||
                report.CalibrationId != calibrationId || Now > calibrationUntil || d.MicrophoneId != report.MicrophoneId ||
                report.SampleCount is < 10 or > 20 || !double.IsFinite(report.BaselineDbfs) || report.BaselineDbfs is < -110 or > -3)
                throw new ArgumentException("校准已过期，或采样无效，请检查麦克风后重试。");
            d.Baseline = report.BaselineDbfs; d.BaselineId = report.CalibrationId; d.Smoothed = null;
            Exec("INSERT OR REPLACE INTO calibrations VALUES($id,$device,$mic,$baseline,$samples,$time)",
                ("$id", report.CalibrationId), ("$device", id), ("$mic", report.MicrophoneId),
                ("$baseline", report.BaselineDbfs), ("$samples", report.SampleCount), ("$time", Now.ToString("O")));
        }
    }

    private void LoadBaseline(Device d)
    {
        using var cmd = Command("SELECT id,baseline FROM calibrations WHERE device_id=$id AND microphone_id=$mic ORDER BY created_at DESC LIMIT 1", ("$id", d.Id), ("$mic", d.MicrophoneId));
        using var reader = cmd.ExecuteReader();
        if (reader.Read()) { d.BaselineId = reader.GetString(0); d.Baseline = reader.GetDouble(1); }
    }

    private string Status(Device d, DateTimeOffset now)
    {
        if (d.LastSeen is null || now - d.LastSeen > TimeSpan.FromSeconds(10)) return "offline";
        if (now - d.LastSeen > TimeSpan.FromSeconds(3)) return "stale";
        if (session is not { Status: "running" }) return "idle";
        if (d.Status is "error" or "blocked" or "no_signal" or "calibrating") return d.Status;
        if (d.Baseline is null) return "uncalibrated";
        if (d.LastSample is null || now - d.LastSample > TimeSpan.FromSeconds(3)) return "stale";
        return "ok";
    }

    private SeatView[] SeatViews(DateTimeOffset now) => Seats.Select(seat =>
    {
        var d = devices.Values.FirstOrDefault(d => d.SeatId == seat.Id && d.Demo == (session?.Demo == true));
        if (d is null) return new SeatView(seat.Id, seat.Label, seat.Row, seat.Column, seat.ZoneId, null, null, "unbound", null, null, null, null, null);
        var status = Status(d, now);
        return new SeatView(seat.Id, seat.Label, seat.Row, seat.Column, seat.ZoneId, d.Id, d.Name, status,
            status == "ok" && d.Smoothed is { } level && d.Baseline is { } baseline ? Acoustics.Score(level, baseline) : null,
            d.Level, d.Baseline, d.LastSeen, d.Clipping);
    }).ToArray();

    private ZoneView[] ZoneViews(SeatView[] seats, DateTimeOffset now, bool advance) => Zones.Select(z =>
    {
        var group = seats.Where(s => s.ZoneId == z.Id).ToArray();
        var scores = group.Where(s => s.Score is not null).Select(s => s.Score!.Value).ToArray();
        double? score = scores.Length == 0 ? null : Math.Round(Acoustics.Percentile(scores, .7), 1);
        var valid = scores.Length;
        // Coverage below half the region, or fewer than three microphones, cannot trigger an alert.
        var limited = valid < 3 || valid < Math.Ceiling(group.Length / 2.0);
        var latch = latches[z.Id];
        if (advance)
        {
            var was = latch.Active;
            latch.Update(limited ? null : score, now, session?.Mode == "discussion" ? 85 : 70);
            if (!was && latch.Active) AddAnnotation($"{z.Name}出现持续噪声，指数 {score:0}", "alert");
        }
        return new ZoneView(z.Id, z.Name, score, valid, group.Length, limited, !limited && score is not null && latch.Active);
    }).ToArray();

    public Dashboard Snapshot()
    {
        lock (gate)
        {
            var now = Now; var seats = SeatViews(now);
            return new(RoomName, now, Acoustics.AlgorithmVersion, session, calibrationId,
                calibrationUntil, seats, ZoneViews(seats, now, false),
                devices.Values.Where(d => !d.Demo).Select(d => new DeviceView(d.Id, d.Name, d.SeatId, Status(d, now), d.MicrophoneId, d.Baseline, d.LastSeen)).ToArray(),
                trend.ToArray(), annotations.TakeLast(30).Reverse().ToArray());
        }
    }

    public void Tick()
    {
        lock (gate)
        {
            if (session is not { Status: "running" }) return;
            var now = Now;
            if (session.Demo)
            {
                var seconds = (now - session.StartedAt).TotalSeconds;
                var hot = ((int)(seconds / 30) + 2) % Zones.Length;
                foreach (var d in devices.Values.Where(d => d.Demo))
                {
                    var seat = Seats.First(s => s.Id == d.SeatId);
                    if (seat == Seats[^1]) continue; // Explicitly simulated offline terminal.
                    var index = Array.IndexOf(Seats, seat);
                    var zone = Array.FindIndex(Zones, z => z.Id == seat.ZoneId);
                    var score = zone == hot ? 83 + 8 * Math.Sin(seconds / 5 + index) : 22 + zone * 4 + 9 * Math.Sin(seconds / 7 + index);
                    d.Level = d.Smoothed = -65 + 3 + score * .27; d.Peak = Math.Min(0, d.Level.Value + 8);
                    d.LastSeen = d.LastSample = now; d.Status = "ok";
                }
            }
            var seats = SeatViews(now); var zones = ZoneViews(seats, now, true);
            var valid = seats.Where(s => s.Score is not null).Select(s => s.Score!.Value).ToArray();
            trend.Enqueue(new(now, valid.Length == 0 ? null : Math.Round(valid.Average(), 1), zones.ToDictionary(z => z.Id, z => z.Score)));
            while (trend.Count > 120) trend.Dequeue();
            var bucket = now.ToUnixTimeSeconds() / 5 * 5;
            if (bucket == lastBucket) return;
            lastBucket = bucket;
            using var transaction = db.BeginTransaction();
            foreach (var seat in seats.Where(s => s.Score is not null))
            {
                var d = devices[seat.DeviceId!];
                Exec("INSERT OR IGNORE INTO device_metrics VALUES($session,$bucket,$device,$seat,$zone,$score,$level,$peak,$clip,$baseline)",
                    ("$session", session.Id), ("$bucket", bucket), ("$device", d.Id), ("$seat", seat.Id), ("$zone", seat.ZoneId),
                    ("$score", seat.Score), ("$level", seat.LevelDbfs), ("$peak", d.Peak), ("$clip", d.Clipping), ("$baseline", d.BaselineId));
            }
            foreach (var zone in zones)
                Exec("INSERT OR IGNORE INTO zone_metrics VALUES($session,$bucket,$zone,$score,$valid,$expected,$alert)",
                    ("$session", session.Id), ("$bucket", bucket), ("$zone", zone.Id), ("$score", zone.Score),
                    ("$valid", zone.Valid), ("$expected", zone.Expected), ("$alert", zone.Alert));
            transaction.Commit();
        }
    }

    public void AddAnnotation(string text, string kind = "manual")
    {
        lock (gate)
        {
            if (session is null || session.Status == "ended") throw new ArgumentException("请先开始实验。");
            if (string.IsNullOrWhiteSpace(text) || text.Length > 200) throw new ArgumentException("标注需要 1～200 个字符。");
            var item = new Annotation(Guid.NewGuid().ToString("N"), session.Id, Now, text.Trim(), kind);
            annotations.Add(item);
            Exec("INSERT INTO annotations VALUES($id,$session,$time,$text,$kind)", ("$id", item.Id), ("$session", item.SessionId),
                ("$time", item.Time.ToString("O")), ("$text", item.Text), ("$kind", kind));
        }
    }

    public object[] History()
    {
        lock (gate)
        {
            using var cmd = Command("SELECT id,name,mode,demo,status,started_at,ended_at,algorithm,config FROM sessions ORDER BY started_at DESC LIMIT 100");
            using var reader = cmd.ExecuteReader(); var result = new List<object>();
            while (reader.Read()) result.Add(new { id = reader.GetString(0), name = reader.GetString(1), mode = reader.GetString(2),
                demo = reader.GetBoolean(3), status = reader.GetString(4), startedAt = reader.GetString(5), endedAt = reader.IsDBNull(6) ? null : reader.GetString(6),
                algorithm = reader.GetString(7), config = reader.GetString(8) });
            return result.ToArray();
        }
    }

    public string Export(string id)
    {
        lock (gate)
        {
            if (Scalar("SELECT id FROM sessions WHERE id=$id", ("$id", id)) is null) throw new ArgumentException("实验不存在。");
            using var cmd = Command("""
                SELECT m.session_id,s.name,s.demo,m.bucket,m.zone_id,m.score,m.valid,m.expected,m.alert,s.mode,s.algorithm,s.config
                FROM zone_metrics m JOIN sessions s ON s.id=m.session_id WHERE m.session_id=$id ORDER BY m.bucket,m.zone_id
                """, ("$id", id));
            using var reader = cmd.ExecuteReader();
            var output = new StringBuilder("\uFEFFsession_id,session_name,is_simulated,time_utc,zone,noise_index,valid_devices,expected_devices,alert,mode,algorithm,parameters\r\n");
            while (reader.Read())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (i > 0) output.Append(',');
                    var value = reader.IsDBNull(i) ? "" : i == 3 ? DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(i)).ToString("O") : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;
                    // Escape CSV and neutralize spreadsheet formula injection in user-supplied names.
                    if (value.Length > 0 && "=+-@\t\r\n".Contains(value[0])) value = "'" + value;
                    output.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
                }
                output.Append("\r\n");
            }
            return output.ToString();
        }
    }

    public object ExportExperiment(string id)
    {
        lock (gate)
        {
            using var cmd = Command("SELECT name,mode,demo,started_at,ended_at,algorithm,config FROM sessions WHERE id=$id", ("$id", id));
            object metadata;
            using (var reader = cmd.ExecuteReader())
            {
                if (!reader.Read()) throw new ArgumentException("实验不存在。");
                metadata = new { id, name = reader.GetString(0), mode = reader.GetString(1), demo = reader.GetBoolean(2), startedAt = reader.GetString(3),
                    endedAt = reader.IsDBNull(4) ? null : reader.GetString(4), algorithm = reader.GetString(5), config = JsonSerializer.Deserialize<object>(reader.GetString(6)) };
            }
            var notes = new List<object>();
            using (var notesCmd = Command("SELECT time,text,kind FROM annotations WHERE session_id=$id ORDER BY time", ("$id", id)))
            using (var reader = notesCmd.ExecuteReader())
                while (reader.Read()) notes.Add(new { time = reader.GetString(0), text = reader.GetString(1), kind = reader.GetString(2) });
            var metrics = new List<object>();
            using (var metricsCmd = Command("SELECT bucket,device_id,seat_id,zone_id,score,level,peak,clipping,baseline_id FROM device_metrics WHERE session_id=$id ORDER BY bucket", ("$id", id)))
            using (var reader = metricsCmd.ExecuteReader())
                while (reader.Read()) metrics.Add(new { time = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)), deviceId = reader.GetString(1),
                    seatId = reader.GetString(2), zoneId = reader.GetString(3), score = reader.GetDouble(4), levelDbfs = reader.GetDouble(5),
                    peakDbfs = reader.GetDouble(6), clippingRatio = reader.GetDouble(7), baselineId = reader.GetString(8) });
            var baselines = new List<object>();
            using (var baselineCmd = Command("""
                SELECT DISTINCT c.id,c.device_id,c.microphone_id,c.baseline,c.sample_count,c.created_at FROM calibrations c
                JOIN device_metrics m ON m.baseline_id=c.id AND m.device_id=c.device_id WHERE m.session_id=$id
                """, ("$id", id)))
            using (var reader = baselineCmd.ExecuteReader())
                while (reader.Read()) baselines.Add(new { id = reader.GetString(0), deviceId = reader.GetString(1), microphoneId = reader.GetString(2), baselineDbfs = reader.GetDouble(3), sampleCount = reader.GetInt32(4), createdAt = reader.GetString(5) });
            return new { metadata, seats = Seats, annotations = notes, deviceMetrics = metrics, calibrations = baselines };
        }
    }

    public void Dispose() => db.Dispose();
}
