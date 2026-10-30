using System.Management;
using System.Net.NetworkInformation;
using Noise.Core;

namespace Noise.Agent;

public static class HardwareIdentity
{
    public static string Read()
    {
        string? uuid = null;
        var wired = new List<string>(); var other = new List<string>();
        try
        {
            using var products = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct");
            using var productRows = products.Get();
            foreach (var row in productRows) { using (row) { uuid = row["UUID"] as string; } break; }
            using var adapters = new ManagementObjectSearcher("SELECT MACAddress,AdapterTypeID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE");
            using var adapterRows = adapters.Get();
            foreach (var row in adapterRows)
                using (row)
                    if (row["MACAddress"] is string mac)
                        (Convert.ToInt32(row["AdapterTypeID"]) == 0 ? wired : other).Add(mac);
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
        if (wired.Count == 0 && other.Count == 0)
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n =>
                         n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
                         !n.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase)))
                (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? wired : other).Add(nic.GetPhysicalAddress().ToString());
        return MachineIdentity.Fingerprint(uuid, wired.Count > 0 ? wired : other);
    }
}
