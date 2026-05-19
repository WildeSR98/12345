using System.Text.RegularExpressions;

namespace ZapretManager.Service;

/// <summary>
/// Parses winws.exe arguments from general*.bat files.
/// Equivalent to Get-WinwsArgs from Service.psm1.
/// </summary>
public static class StrategyReader
{
    public static FileInfo[] GetStrategyFiles(string rootDir)
    {
        var strategiesDir = Path.Combine(rootDir, "strategies");
        if (!Directory.Exists(strategiesDir)) return Array.Empty<FileInfo>();

        return new DirectoryInfo(strategiesDir)
            .GetFiles("general*.bat")
            .OrderBy(f => Regex.Replace(f.Name, @"(\d+)",
                m => m.Value.PadLeft(8, '0')))
            .ToArray();
    }

    /// <summary>Parse winws.exe argument string from a bat file.</summary>
    public static string ParseArgs(string batPath, string binDir, string listsDir,
        string gameTcp, string gameUdp)
    {
        var binPath   = binDir.TrimEnd('\\')   + "\\";
        var listsPath = listsDir.TrimEnd('\\') + "\\";

        var lines = File.ReadAllLines(batPath, System.Text.Encoding.UTF8);
        bool capture = false;
        var parts = new System.Text.StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            bool continuation = line.EndsWith('^');
            if (continuation) line = line[..^1].TrimEnd();

            // Apply variable substitutions
            line = line
                .Replace("%BIN%", binPath)
                .Replace("%LISTS%", listsPath)
                .Replace("%~dp0bin\\", binPath)
                .Replace("%~dp0..\\bin\\", binPath)
                .Replace("%~dp0lists\\", listsPath)
                .Replace("%~dp0..\\lists\\", listsPath)
                .Replace("%GameFilterTCP%", gameTcp)
                .Replace("%GameFilterUDP%", gameUdp);

            if (!capture && Regex.IsMatch(line, @"winws\.exe""", RegexOptions.IgnoreCase))
            {
                capture = true;
                // Grab everything after winws.exe"
                var m = Regex.Match(line, @"winws\.exe""(.*)$", RegexOptions.IgnoreCase);
                if (m.Success) line = m.Groups[1].Value.Trim();
            }

            if (capture)
            {
                if (parts.Length > 0 && !string.IsNullOrWhiteSpace(line))
                    parts.Append(' ');
                parts.Append(line.Trim());

                if (!continuation) break; // end of the winws invocation
            }
        }

        return parts.ToString().Trim();
    }
}
