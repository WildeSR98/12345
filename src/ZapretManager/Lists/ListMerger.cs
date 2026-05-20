namespace ZapretManager.Lists;

public record ListEntry(string Local, string Remote, string Stub, bool User);

public static class ListMerger
{
    /// <summary>
    /// Merge existing file lines with new downloaded lines.
    /// Existing lines have priority (preserved first), new lines added only if not already present.
    /// </summary>
    public static string[] Merge(string filePath, IEnumerable<string> newLines)
    {
        string[] oldLines = File.Exists(filePath)
            ? File.ReadAllLines(filePath, System.Text.Encoding.UTF8)
            : Array.Empty<string>();

        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var line in oldLines.Concat(newLines))
        {
            var trimmed = line.TrimEnd();
            var key     = trimmed.Trim();

            if (key.Length == 0 || key.StartsWith('#'))
            {
                // Preserve comments/blanks deduped by exact content
                if (seen.Add($"__cmt__{trimmed}")) result.Add(trimmed);
                continue;
            }
            if (seen.Add(key)) result.Add(trimmed);
        }
        return result.ToArray();
    }

    /// <summary>
    /// Replace local list with origin list, keeping only locally-added entries
    /// that are NOT in the origin. This ensures removed entries in origin
    /// get removed locally too.
    /// </summary>
    public static string[] ReplaceWithOrigin(string filePath, IEnumerable<string> originLines)
    {
        var originSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result    = new List<string>();
        var seen      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // First pass: collect all origin entries
        foreach (var line in originLines)
        {
            var key = line.TrimEnd().Trim();
            if (key.Length > 0 && !key.StartsWith('#'))
                originSet.Add(key);
        }

        // Add all origin lines (this is the new base)
        foreach (var line in originLines)
        {
            var trimmed = line.TrimEnd();
            var key     = trimmed.Trim();
            if (key.Length == 0 || key.StartsWith('#'))
            {
                if (seen.Add($"__cmt__{trimmed}")) result.Add(trimmed);
                continue;
            }
            if (seen.Add(key)) result.Add(trimmed);
        }

        // Add locally-added entries that are NOT in origin (user additions)
        if (File.Exists(filePath))
        {
            foreach (var line in File.ReadAllLines(filePath, System.Text.Encoding.UTF8))
            {
                var key = line.TrimEnd().Trim();
                if (key.Length == 0 || key.StartsWith('#')) continue;
                // Only keep if it's NOT in origin (user manually added it)
                if (!originSet.Contains(key) && seen.Add(key))
                    result.Add(line.TrimEnd());
            }
        }

        return result.ToArray();
    }

    public static void WriteUtf8(string path, string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(false));
    }
}
