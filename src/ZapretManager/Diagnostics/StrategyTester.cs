using System.Diagnostics;
using System.Text.RegularExpressions;
using ZapretManager.Core;
using ZapretManager.UI;

namespace ZapretManager.Diagnostics;

/// <summary>Target loaded from utils/targets.txt or config.json.</summary>
public record TestTarget(string Name, string? Url, string PingTarget, bool IsPingOnly);

/// <summary>Result of a single curl test (HTTP/TLS1.2/TLS1.3).</summary>
public record CurlTestResult(string Label, string Status);
// Status: "OK", "ERROR", "UNSUP", "SSL"

/// <summary>Result for one target in a standard test.</summary>
public record TargetTestResult(string Name, List<CurlTestResult> HttpResults, string PingResult,
    bool IsUrl);

/// <summary>Per-config analytics.</summary>
public record ConfigAnalytics(string ConfigName, int HttpOk, int HttpError, int HttpUnsup,
    int PingOk, int PingFail);

/// <summary>Score for ranking configs.</summary>
public record StrategyScore(string Name, string FullPath, int Ok, int Fail);

/// <summary>
/// Full port of standard strategy testing from test zapret.ps1.
/// Tests each general*.bat by launching it via cmd.exe, then running
/// HTTP/TLS1.2/TLS1.3 + ping checks against targets from targets.txt.
/// </summary>
public static class StrategyTester
{
    private const int CurlTimeoutSeconds = 5;
    private const int MaxParallel = 8;

    // ── Target Loading ────────────────────────────────────────────────────────

    /// <summary>Load targets from utils/targets.txt, fallback to config.json.</summary>
    public static List<TestTarget> LoadTargets(string rootDir, IList<CheckTarget>? configTargets)
    {
        var targetsFile = Path.Combine(rootDir, "utils", "targets.txt");
        var targets = new List<TestTarget>();

        if (File.Exists(targetsFile))
        {
            var lines = File.ReadAllLines(targetsFile);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;

                // Format: KeyName = "https://host..." or KeyName = "PING:1.2.3.4"
                var match = Regex.Match(trimmed, @"^\s*(\w+)\s*=\s*""(.+)""\s*$");
                if (!match.Success) continue;

                var name = match.Groups[1].Value;
                var value = match.Groups[2].Value;

                if (value.StartsWith("PING:", StringComparison.OrdinalIgnoreCase))
                {
                    var pingTarget = value[5..].Trim();
                    targets.Add(new TestTarget(name, null, pingTarget, true));
                }
                else
                {
                    // Extract hostname for ping
                    var pingTarget = Regex.Replace(value, @"^https?://", "");
                    pingTarget = Regex.Replace(pingTarget, @"/.*$", "");
                    targets.Add(new TestTarget(name, value, pingTarget, false));
                }
            }

            if (targets.Count > 0)
            {
                ConsoleMenu.WriteInfo($"Загружено {targets.Count} целей из targets.txt");
                return targets;
            }
        }

        // Fallback to config.json targets
        if (configTargets != null)
        {
            foreach (var ct in configTargets)
            {
                if (ct.Type == "ping")
                    targets.Add(new TestTarget(ct.Name, null, ct.Host, true));
                else
                    targets.Add(new TestTarget(ct.Name, ct.Url, ct.Host, false));
            }
            ConsoleMenu.WriteInfo($"Загружено {targets.Count} целей из config.json");
        }

        return targets;
    }

    // ── Config Selection ──────────────────────────────────────────────────────

    /// <summary>Interactive config selection (all or custom subset).</summary>
    public static FileInfo[] SelectConfigs(FileInfo[] allFiles)
    {
        Console.WriteLine();
        ConsoleMenu.WriteInfo($"Найдено конфигов: {allFiles.Length}");
        Console.WriteLine("   [1] Все конфиги");
        Console.WriteLine("   [2] Выбранные конфиги");
        var mode = ConsoleMenu.Prompt("Выберите режим", "1");

        if (mode != "2") return allFiles;

        // Show list
        Console.WriteLine();
        for (int i = 0; i < allFiles.Length; i++)
            Console.WriteLine($"   [{i + 1}] {allFiles[i].Name}");

        while (true)
        {
            var input = ConsoleMenu.Prompt(
                "Введите номера (1,3,5 или 2-7 или 1,5-10,12). 0 = все", "0");
            if (input == "0") return allFiles;

            var selected = ParseConfigSelection(input ?? "", allFiles);
            if (selected.Length > 0)
            {
                ConsoleMenu.WriteOk($"Выбрано конфигов: {selected.Length}");
                return selected;
            }

            ConsoleMenu.WriteWarn("Неверный ввод. Попробуйте ещё раз.");
        }
    }

    private static FileInfo[] ParseConfigSelection(string input, FileInfo[] allFiles)
    {
        var indices = new HashSet<int>();
        var parts = input.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var rangeMatch = Regex.Match(part.Trim(), @"^(\d+)-(\d+)$");
            if (rangeMatch.Success)
            {
                int start = int.Parse(rangeMatch.Groups[1].Value);
                int end = int.Parse(rangeMatch.Groups[2].Value);
                if (start > end) continue;
                for (int i = Math.Max(start, 1); i <= Math.Min(end, allFiles.Length); i++)
                    indices.Add(i);
            }
            else if (int.TryParse(part.Trim(), out var n) && n >= 1 && n <= allFiles.Length)
            {
                indices.Add(n);
            }
        }

        return indices.OrderBy(i => i).Select(i => allFiles[i - 1]).ToArray();
    }

    // ── Standard Tests ────────────────────────────────────────────────────────

    /// <summary>Run standard tests for all selected configs.</summary>
    public static async Task<List<(string Config, List<TargetTestResult> Results)>>
        RunStandardTestsAsync(string rootDir, FileInfo[] configs, List<TestTarget> targets,
            string winwsExe)
    {
        var allResults = new List<(string Config, List<TargetTestResult> Results)>();
        var curlPath = FindCurl(rootDir);

        ConsoleMenu.WriteHeader($"СТАНДАРТНЫЕ ТЕСТЫ ({configs.Length} конфигов, {targets.Count} целей)");
        ConsoleMenu.WriteWarn("Тесты займут несколько минут. Пожалуйста, подождите...");

        for (int i = 0; i < configs.Length; i++)
        {
            var file = configs[i];
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"\n  [{i + 1}/{configs.Length}] {file.Name}");
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine("  " + new string('─', 56));
            Console.ResetColor();

            // Stop any running winws
            Service.ProcessManager.KillAll();
            await Task.Delay(500);

            // Start config via cmd.exe (like original)
            ConsoleMenu.WriteInfo("Запуск конфигурации...");
            var proc = Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c \"{file.FullName}\"")
            {
                WorkingDirectory = rootDir,
                WindowStyle = ProcessWindowStyle.Minimized,
                UseShellExecute = true
            });

            // Wait for initialization
            await Task.Delay(5000);

            // Run tests in parallel
            ConsoleMenu.WriteInfo("Выполнение тестов...");
            var results = await RunTargetTestsAsync(targets, curlPath);

            // Print results
            int maxNameLen = targets.Max(t => t.Name.Length);
            if (maxNameLen < 10) maxNameLen = 10;

            foreach (var target in targets)
            {
                var res = results.FirstOrDefault(r => r.Name == target.Name);
                if (res == null) continue;

                Console.Write($"  {target.Name.PadRight(maxNameLen)}    ");

                if (res.IsUrl && res.HttpResults.Count > 0)
                {
                    foreach (var hr in res.HttpResults)
                    {
                        Console.ForegroundColor = hr.Status switch
                        {
                            "OK" => ConsoleColor.Green,
                            "UNSUP" => ConsoleColor.Yellow,
                            "SSL" => ConsoleColor.Red,
                            _ => ConsoleColor.Red
                        };
                        Console.Write($" {hr.Label}:{hr.Status,-5}");
                    }
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(" | Ping: ");
                    Console.ForegroundColor = res.PingResult == "Timeout"
                        ? ConsoleColor.Yellow : ConsoleColor.Cyan;
                    Console.WriteLine(res.PingResult);
                }
                else
                {
                    // Ping-only target
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(" Ping: ");
                    Console.ForegroundColor = res.PingResult == "Timeout"
                        ? ConsoleColor.Red : ConsoleColor.Cyan;
                    Console.WriteLine(res.PingResult);
                }
                Console.ResetColor();
            }

            allResults.Add((file.Name, results));

            // Stop config
            Service.ProcessManager.KillAll();
            if (proc != null && !proc.HasExited)
                try { proc.Kill(); } catch { }
            await Task.Delay(500);
        }

        return allResults;
    }

    private static async Task<List<TargetTestResult>> RunTargetTestsAsync(
        List<TestTarget> targets, string curlPath)
    {
        using var sem = new SemaphoreSlim(MaxParallel);
        var tasks = targets.Select(t => RunSingleTargetTestAsync(t, curlPath, sem));
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    private static async Task<TargetTestResult> RunSingleTargetTestAsync(
        TestTarget target, string curlPath, SemaphoreSlim sem)
    {
        await sem.WaitAsync();
        try
        {
            var httpResults = new List<CurlTestResult>();

            if (target.Url != null)
            {
                var tests = new[]
                {
                    ("HTTP",   new[] { "--http1.1" }),
                    ("TLS1.2", new[] { "--tlsv1.2", "--tls-max", "1.2" }),
                    ("TLS1.3", new[] { "--tlsv1.3", "--tls-max", "1.3" })
                };

                foreach (var (label, extraArgs) in tests)
                {
                    var status = await RunCurlCheckAsync(curlPath, target.Url, extraArgs);
                    httpResults.Add(new CurlTestResult(label, status));
                }
            }

            // Ping
            var pingResult = await RunPingAsync(target.PingTarget);

            return new TargetTestResult(target.Name, httpResults, pingResult, target.Url != null);
        }
        finally { sem.Release(); }
    }

    private static async Task<string> RunCurlCheckAsync(string curlPath, string url,
        string[] extraArgs)
    {
        try
        {
            var args = new List<string>
                { "-I", "-s", "-m", CurlTimeoutSeconds.ToString(), "-o", "NUL", "-w",
                    "%{http_code}", "--show-error" };
            args.AddRange(extraArgs);
            args.Add(url);

            var psi = new ProcessStartInfo(curlPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CurlTimeoutSeconds + 5));
            await proc.WaitForExitAsync(cts.Token);

            // Check for SSL/DNS hijack
            if (Regex.IsMatch(stderr, @"Could not resolve host|certificate|SSL certificate problem|self[- ]?signed|certificate verify failed|unable to get local issuer certificate",
                RegexOptions.IgnoreCase))
                return "SSL";

            // Check for unsupported protocol
            if (proc.ExitCode == 35 || Regex.IsMatch(stderr,
                @"does not support|not supported|protocol\s+'.+'\s+not\s+supported|unsupported protocol|TLS.*not supported|Unrecognized option|Unknown option|unsupported option|unsupported feature|schannel",
                RegexOptions.IgnoreCase))
                return "UNSUP";

            return proc.ExitCode == 0 ? "OK" : "ERROR";
        }
        catch
        {
            return "ERROR";
        }
    }

    private static async Task<string> RunPingAsync(string host)
    {
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var tasks = new List<Task<System.Net.NetworkInformation.PingReply>>();
            for (int i = 0; i < 3; i++)
                tasks.Add(ping.SendPingAsync(host, 3000));

            var replies = await Task.WhenAll(tasks);
            var successReplies = replies
                .Where(r => r.Status == System.Net.NetworkInformation.IPStatus.Success)
                .ToList();

            if (successReplies.Count == 0) return "Timeout";

            var avg = successReplies.Average(r => r.RoundtripTime);
            return $"{avg:N0} ms";
        }
        catch
        {
            return "Timeout";
        }
    }

    // ── Analytics ─────────────────────────────────────────────────────────────

    public static List<ConfigAnalytics> ComputeAnalytics(
        List<(string Config, List<TargetTestResult> Results)> allResults)
    {
        var analytics = new List<ConfigAnalytics>();

        foreach (var (config, results) in allResults)
        {
            int httpOk = 0, httpError = 0, httpUnsup = 0, pingOk = 0, pingFail = 0;

            foreach (var res in results)
            {
                if (res.IsUrl)
                {
                    foreach (var hr in res.HttpResults)
                    {
                        switch (hr.Status)
                        {
                            case "OK": httpOk++; break;
                            case "UNSUP": httpUnsup++; break;
                            default: httpError++; break;
                        }
                    }
                }

                if (res.PingResult != "Timeout" && res.PingResult != "n/a")
                    pingOk++;
                else
                    pingFail++;
            }

            analytics.Add(new ConfigAnalytics(config, httpOk, httpError, httpUnsup, pingOk, pingFail));
        }

        return analytics;
    }

    public static string? GetBestConfig(List<ConfigAnalytics> analytics)
    {
        return analytics
            .OrderByDescending(a => a.HttpOk)
            .ThenByDescending(a => a.PingOk)
            .FirstOrDefault()?.ConfigName;
    }

    public static void PrintAnalytics(List<ConfigAnalytics> analytics)
    {
        ConsoleMenu.WriteHeader("АНАЛИТИКА");
        int maxLen = analytics.Max(a => a.ConfigName.Length);

        foreach (var a in analytics.OrderByDescending(a => a.HttpOk))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"   {a.ConfigName.PadRight(maxLen)} : " +
                $"HTTP OK: {a.HttpOk}, ERR: {a.HttpError}, UNSUP: {a.HttpUnsup}, " +
                $"Ping OK: {a.PingOk}, Fail: {a.PingFail}");
            Console.ResetColor();
        }

        var best = GetBestConfig(analytics);
        if (best != null)
        {
            Console.WriteLine();
            ConsoleMenu.WriteOk($"Лучший конфиг: {best}");
        }
    }

    // ── Results Saving ────────────────────────────────────────────────────────

    public static void SaveStandardResults(string rootDir,
        List<(string Config, List<TargetTestResult> Results)> allResults,
        List<ConfigAnalytics> analytics)
    {
        var resultsDir = Path.Combine(rootDir, "utils", "test results");
        Directory.CreateDirectory(resultsDir);

        var dateStr = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var resultFile = Path.Combine(resultsDir, $"test_results_{dateStr}.txt");

        var lines = new List<string>();
        foreach (var (config, results) in allResults)
        {
            lines.Add($"Config: {config} (Type: standard)");
            foreach (var res in results)
            {
                var http = string.Join(" ", res.HttpResults.Select(h => $"{h.Label}:{h.Status}"));
                lines.Add($"  {res.Name} : {http} | Ping: {res.PingResult}");
            }
            lines.Add("");
        }

        lines.Add("=== ANALYTICS ===");
        foreach (var a in analytics)
        {
            lines.Add($"{a.ConfigName} : HTTP OK: {a.HttpOk}, ERR: {a.HttpError}, " +
                $"UNSUP: {a.HttpUnsup}, Ping OK: {a.PingOk}, Fail: {a.PingFail}");
        }

        var best = GetBestConfig(analytics);
        if (best != null) lines.Add($"Best strategy: {best}");

        File.WriteAllLines(resultFile, lines, System.Text.Encoding.UTF8);
        ConsoleMenu.WriteOk($"Результаты сохранены: {resultFile}");
    }

    public static void SaveDpiResults(string rootDir,
        List<(string Config, List<DpiTargetResult> Results)> allResults)
    {
        var resultsDir = Path.Combine(rootDir, "utils", "test results");
        Directory.CreateDirectory(resultsDir);

        var dateStr = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var resultFile = Path.Combine(resultsDir, $"test_results_dpi_{dateStr}.txt");

        var lines = new List<string>();
        foreach (var (config, results) in allResults)
        {
            lines.Add($"Config: {config} (Type: dpi)");
            foreach (var res in results)
            {
                lines.Add($"  Target: [{res.Country}] {res.TargetId} ({res.Provider})");
                foreach (var l in res.Lines)
                {
                    lines.Add($"    {l.Label}: code={l.Code}  up={l.UpBytes}B  " +
                        $"down={l.DownBytes}B  time={l.Time:F1}s  status={l.Status}");
                }
            }
            lines.Add("");
        }

        File.WriteAllLines(resultFile, lines, System.Text.Encoding.UTF8);
        ConsoleMenu.WriteOk($"DPI результаты сохранены: {resultFile}");
    }

    // ── Legacy compatibility ─────────────────────────────────────────────────

    /// <summary>Simple score-based result for backward compatibility.</summary>
    public static List<StrategyScore> ToScores(
        List<(string Config, List<TargetTestResult> Results)> allResults, FileInfo[] files)
    {
        var scores = new List<StrategyScore>();
        foreach (var (config, results) in allResults)
        {
            int ok = results.Count(r =>
                (r.IsUrl && r.HttpResults.Any(h => h.Status == "OK")) ||
                (!r.IsUrl && r.PingResult != "Timeout"));
            int fail = results.Count - ok;
            var fi = files.FirstOrDefault(f => f.Name == config);
            scores.Add(new StrategyScore(config, fi?.FullName ?? "", ok, fail));
        }
        return scores;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FindCurl(string rootDir)
    {
        var localCurl = Path.Combine(rootDir, "bin", "curl.exe");
        if (File.Exists(localCurl)) return localCurl;

        // Check system PATH
        try
        {
            var p = Process.Start(new ProcessStartInfo("where", "curl.exe")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(1000);
            if (p?.ExitCode == 0) return "curl.exe";
        }
        catch { }

        return "curl.exe"; // Hope for the best
    }
}
