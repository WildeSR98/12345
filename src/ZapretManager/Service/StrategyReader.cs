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

            // Skip empty lines and comments
            if (line.TrimStart().StartsWith("::") || line.TrimStart().StartsWith("rem ") || 
                line.TrimStart().StartsWith("@") || line.TrimStart().StartsWith("chcp") ||
                line.TrimStart().StartsWith("cd /d") || line.TrimStart().StartsWith("call") ||
                line.TrimStart().StartsWith("echo") || line.TrimStart().StartsWith("if not"))
            {
                if (!capture) continue;
            }

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

            // Look for winws.exe in the line (handles both "path\winws.exe" and plain winws.exe)
            if (!capture && line.Contains("winws.exe", StringComparison.OrdinalIgnoreCase))
            {
                capture = true;
                // Extract everything after winws.exe" (quoted path) or winws.exe (unquoted)
                var m = Regex.Match(line, @"winws\.exe""?\s+(.*)$", RegexOptions.IgnoreCase);
                if (m.Success)
                    line = m.Groups[1].Value.Trim();
                else
                    continue; // winws.exe found but no args on this line
            }

            if (capture)
            {
                var trimmed = line.Trim();
                // Skip set/start/cd commands that might be captured
                if (trimmed.StartsWith("set ", StringComparison.OrdinalIgnoreCase)) continue;

                if (parts.Length > 0 && !string.IsNullOrWhiteSpace(trimmed))
                    parts.Append(' ');
                parts.Append(trimmed);

                if (!continuation) break; // end of the winws invocation
            }
        }

        var result = parts.ToString().Trim();
        Core.Logger.Info($"ParseArgs [{Path.GetFileName(batPath)}]: length={result.Length}");
        return result;
    }
}
