using System.Net;
using System.Text.RegularExpressions;

namespace ZapretManager.Lists;

public static class ListRepairer
{
    /// <summary>Remove duplicates, blank lines, and optionally overlapping CIDRs from all list files.</summary>
    public static void RepairAll(string listsDir, bool removeCidrOverlap = true)
    {
        if (!Directory.Exists(listsDir)) return;

        foreach (var file in Directory.EnumerateFiles(listsDir, "*.txt"))
        {
            // Skip user files
            if (file.Contains("-user", StringComparison.OrdinalIgnoreCase)) continue;

            var lines = File.ReadAllLines(file, System.Text.Encoding.UTF8);
            var cleaned = CleanLines(lines, removeCidrOverlap);
            ListMerger.WriteUtf8(file, cleaned);
        }
    }

    private static string[] CleanLines(string[] lines, bool removeCidrOverlap)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var networks = new List<(uint Net, uint Mask)>();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var key  = line.Trim();

            if (key.Length == 0) continue; // remove blank lines
            if (key.StartsWith('#'))        // preserve comments
            {
                if (seen.Add($"##{key}")) result.Add(line);
                continue;
            }

            if (!seen.Add(key)) continue; // dedup

            if (removeCidrOverlap && IsCidr(key, out var net, out var mask))
            {
                // Skip if already covered by a broader subnet
                if (networks.Any(n => (net & n.Mask) == n.Net)) continue;
                // Remove any more-specific subnets we already added
                networks.RemoveAll(n => (n.Net & mask) == net);
                networks.Add((net, mask));
            }

            result.Add(line);
        }
        return result.ToArray();
    }

    private static bool IsCidr(string s, out uint network, out uint mask)
    {
        network = mask = 0;
        var m = Regex.Match(s, @"^(\d+\.\d+\.\d+\.\d+)/(\d+)$");
        if (!m.Success) return false;

        if (!IPAddress.TryParse(m.Groups[1].Value, out var ip)) return false;
        if (!int.TryParse(m.Groups[2].Value, out var prefix) || prefix < 0 || prefix > 32)
            return false;

        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return false;

        network = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        mask    = prefix == 0 ? 0 : (0xFFFFFFFF << (32 - prefix));
        network &= mask;
        return true;
    }
}
