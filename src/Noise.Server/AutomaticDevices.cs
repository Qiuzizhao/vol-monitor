using System.Security.Cryptography;

namespace Noise.Server;

public sealed partial class RoomStore
{
    // Only the connection's observed address is passed here, never a client-supplied IP.
    public string ConnectAddress(string? ipAddress)
    {
        lock (gate)
        {
            var address = NormalizeAddress(ipAddress) ?? throw new ArgumentException("未识别到学生机 IP。");
            var device = devices.Values.FirstOrDefault(d => !d.Demo && d.IpAddress == address);
            if (device is null)
            {
                var id = Guid.NewGuid().ToString("N");
                var hash = TeacherAccess.TokenHash(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
                var seat = Seats.FirstOrDefault(s => !devices.Values.Any(d => !d.Demo && d.SeatId == s.Id));
                device = new Device { Id = id, Name = address, IpAddress = address, SeatId = seat?.Id };
                using var transaction = db.BeginTransaction();
                Exec("INSERT INTO devices(id,name,seat_id,token_hash) VALUES($id,$name,$seat,$hash)",
                    ("$id", id), ("$name", address), ("$seat", device.SeatId), ("$hash", hash));
                Exec("INSERT INTO device_addresses VALUES($id,$ip)", ("$id", id), ("$ip", address));
                transaction.Commit();
                devices.Add(id, device); tokens[hash] = id;
            }
            device.LastSeen = Now;
            return device.Id;
        }
    }

    private void MergeDevicesByAddress()
    {
        foreach (var device in devices.Values.Where(d => d.IpAddress is not null).ToArray())
            UpdateDeviceAddress(device, NormalizeAddress(device.IpAddress));
        foreach (var group in devices.Values.Where(d => !d.Demo && d.IpAddress is not null)
                     .GroupBy(d => d.IpAddress).Where(g => g.Count() > 1).ToArray())
        {
            // Prefer an existing seat binding when upgrading a database with duplicate IPs.
            var keep = group.OrderBy(d => d.SeatId is null).First();
            using var transaction = db.BeginTransaction();
            foreach (var duplicate in group.Where(d => d.Id != keep.Id))
            {
                Exec("UPDATE enrollment_requests SET device_id=$keep WHERE device_id=$old", ("$keep", keep.Id), ("$old", duplicate.Id));
                DeleteDevice(duplicate.Id);
            }
            transaction.Commit();
        }
    }
}
