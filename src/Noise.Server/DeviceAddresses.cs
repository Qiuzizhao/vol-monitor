using System.Net;

namespace Noise.Server;

public sealed partial class RoomStore
{
    private static string? NormalizeAddress(string? value)
    {
        if (value is null) return null;
        if (!IPAddress.TryParse(value, out var address)) throw new ArgumentException("设备 IP 无效。");
        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    private void UpdateDeviceAddress(Device device, string? value)
    {
        var address = NormalizeAddress(value);
        if (address is null || address == device.IpAddress) return;
        Exec("INSERT INTO device_addresses VALUES($id,$ip) ON CONFLICT(device_id) DO UPDATE SET ip_address=excluded.ip_address",
            ("$id", device.Id), ("$ip", address));
        device.IpAddress = address;
    }

    private void UpdateEnrollmentAddress(string id, string? value)
    {
        var address = NormalizeAddress(value);
        if (address is null) return;
        Exec("INSERT INTO enrollment_addresses VALUES($id,$ip) ON CONFLICT(request_id) DO UPDATE SET ip_address=excluded.ip_address WHERE enrollment_addresses.ip_address<>excluded.ip_address",
            ("$id", id), ("$ip", address));
    }
}
