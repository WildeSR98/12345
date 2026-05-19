using System.ServiceProcess;
using ZapretManager.Core;

namespace ZapretManager.Diagnostics;

public static class ReportExporter
{
    public static string Export(string rootDir)
    {
        var logDir = Path.Combine(rootDir, "logs");
        Directory.CreateDirectory(logDir);
        var outPath = Path.Combine(logDir, $"diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

        var lines = new List<string>
        {
            "ZAPRET DIAGNOSTICS REPORT",
            $"Generated: {DateTime.Now}",
            $".NET: {Environment.Version}  OS: {Environment.OSVersion}",
            new string('=', 60),
            "",
            "--- SERVICES ---"
        };

        foreach (var svc in new[] { "zapret", "WinDivert", "WinDivert14" })
        {
            var state = Service.WinServiceManager.GetState(svc);
            lines.Add($"{svc}: {state}");
        }

        lines.Add("");
        lines.Add("--- PROCESSES ---");
        var winws = System.Diagnostics.Process.GetProcessesByName("winws");
        lines.Add(winws.Length > 0
            ? $"winws.exe: RUNNING (PID={string.Join(",", winws.Select(p => p.Id))})"
            : "winws.exe: NOT RUNNING");

        lines.Add("");
        lines.Add("--- CONFLICTS ---");
        var conflicts = ConflictDetector.FindConflicts();
        lines.Add($"Found: {(conflicts.Count > 0 ? string.Join(", ", conflicts) : "none")}");

        lines.Add("");
        lines.Add("--- FILES ---");
        var binExe = Path.Combine(rootDir, "bin", "winws.exe");
        lines.Add(File.Exists(binExe)
            ? $"winws.exe: EXISTS ({new System.IO.FileInfo(binExe).Length} bytes)"
            : "winws.exe: MISSING");

        lines.Add("");
        lines.Add("--- STRATEGY (from registry) ---");
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"System\CurrentControlSet\Services\zapret");
            var strat = key?.GetValue("zapret-discord-youtube")?.ToString();
            lines.Add($"Installed: {strat ?? "none"}");
        }
        catch { lines.Add("Installed: unknown"); }

        lines.Add("");
        lines.Add("--- RECENT LOG ---");
        var logs = Directory.Exists(Path.Combine(rootDir, "logs"))
            ? new DirectoryInfo(Path.Combine(rootDir, "logs"))
                .GetFiles("zapret_*.log")
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault()
            : null;
        if (logs != null)
            lines.AddRange(File.ReadLines(logs.FullName).TakeLast(20));
        else
            lines.Add("No logs found");

        File.WriteAllLines(outPath, lines, new System.Text.UTF8Encoding(false));
        Logger.Ok($"Отчёт сохранён: {outPath}");
        return outPath;
    }
}
