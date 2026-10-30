using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Noise.Core;

public static partial class MachineIdentity
{
    /// <summary>Hardware matching aid, not a secret or hardware attestation.</summary>
    public static string Fingerprint(string? productUuid, IEnumerable<string> physicalMacs)
    {
        var uuid = Guid.TryParse(productUuid, out var parsed) && parsed != Guid.Empty &&
            parsed != new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff") ? parsed.ToString("N") : "";
        var macs = physicalMacs.Select(s => s.Replace(":", "").Replace("-", "").ToUpperInvariant())
            .Where(s => MacPattern().IsMatch(s) && s != "000000000000" && s != "FFFFFFFFFFFF")
            .Distinct().Order(StringComparer.Ordinal).ToArray();
        // A physical NIC is required: some classroom images and VMs duplicate SMBIOS UUIDs.
        if (macs.Length == 0) throw new ArgumentException("无法读取物理网卡标识，请检查网卡和 WMI 服务后重试。");
        var input = "roomscope-hardware-v1|" + uuid + "|" + string.Join(',', macs);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    public static bool Matches(string? saved, string current) => !string.IsNullOrEmpty(saved) &&
        string.Equals(saved, current, StringComparison.Ordinal);

    public static int? SeatNumber(string name)
    {
        var match = SeatPattern().Match(name.Trim());
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) && n > 0 ? n : null;
    }

    [GeneratedRegex("^[0-9A-F]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex MacPattern();
    [GeneratedRegex("^(?:PC|STU|学生机)[-_ ]?([0-9]{1,4})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeatPattern();
}
