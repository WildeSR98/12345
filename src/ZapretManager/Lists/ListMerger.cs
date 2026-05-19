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

    public static void WriteUtf8(string path, string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(false));
    }
}
