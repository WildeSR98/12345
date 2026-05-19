using System.Diagnostics;
using System.Text.Json;
using ZapretManager.Core;
using ZapretManager.UI;

namespace ZapretManager.Diagnostics;

public record DpiTarget(string Id, string Provider, string Country, string Host);

public record DpiLineResult(string Label, string Code, long UpBytes, long DownBytes, double Time, string Status);

public record DpiTargetResult(string TargetId, string Provider, string Country,
    List<DpiLineResult> Lines, bool WarnDetected);

/// <summary>DPI TCP 16-20 KB freeze detection — ported from test zapret.ps1</summary>
public static class DpiChecker
{
    private static readonly string SuiteUrl =
        "https://hyperion-cs.github.io/dpi-checkers/ru/tcp-16-20/suite.v2.json";

    private static readonly (string Label, string[] Args)[] Tests =
    {
        ("HTTP",   new[] { "--http1.1" }),
        ("TLS1.2", new[] { "--tlsv1.2", "--tls-max", "1.2" }),
        ("TLS1.3", new[] { "--tlsv1.3", "--tls-max", "1.3" })
    };

    public static async Task<List<DpiTarget>> GetSuiteAsync()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = await http.GetStringAsync(SuiteUrl);
            var doc = JsonDocument.Parse(json);
            var results = new List<DpiTarget>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                results.Add(new(
                    el.GetProperty("id").GetString() ?? "",
                    el.GetProperty("provider").GetString() ?? "",
                    el.GetProperty("country").GetString() ?? "",
                    el.GetProperty("host").GetString() ?? ""));
            }
            return results;
        }
        catch (Exception ex)
        {
            Logger.Warn($"DPI suite fetch failed: {ex.Message}");
            return new();
        }
    }

    public static async Task<List<DpiTargetResult>> RunSuiteAsync(
        List<DpiTarget> targets, string curlPath,
        int timeoutSec = 5, int rangeBytes = 65536, int maxParallel = 8)
    {
        var rangeSpec = $"0-{rangeBytes - 1}";
        Logger.Info($"DPI тест: {targets.Count} targets, range={rangeSpec}, timeout={timeoutSec}s");

        // Generate random payload
        var payload = new byte[rangeBytes];
        Random.Shared.NextBytes(payload);
        var payloadFile = Path.GetTempFileName();
        await File.WriteAllBytesAsync(payloadFile, payload);

        try
        {
            using var sem = new SemaphoreSlim(maxParallel);
            var tasks = targets.Select(t => RunTargetAsync(t, payloadFile, curlPath,
                rangeSpec, timeoutSec, sem));
            return (await Task.WhenAll(tasks)).ToList();
        }
        finally { File.Delete(payloadFile); }
    }

    private static async Task<DpiTargetResult> RunTargetAsync(DpiTarget target,
        string payloadFile, string curlPath, string rangeSpec,
        int timeoutSec, SemaphoreSlim sem)
    {
        await sem.WaitAsync();
        try
        {
            var lines = new List<DpiLineResult>();
            bool warned = false;

            foreach (var (label, args) in Tests)
            {
                var curlArgs = new List<string>
                {
                    "--range", rangeSpec,
                    "-m", timeoutSec.ToString(),
                    "-w", "%{http_code} %{size_upload} %{size_download} %{time_total}",
                    "-o", "NUL", "-X", "POST",
                    "--data-binary", $"@{payloadFile}", "-s"
                };
                curlArgs.AddRange(args);
                curlArgs.Add($"https://{target.Host}");

                var (code, up, down, time, exitCode) = await RunCurlAsync(curlPath, curlArgs, timeoutSec + 5);

                string status;
                if (code == "UNSUP") status = "UNSUPPORTED";
                else if (exitCode != 0 || code is "ERR" or "NA") status = "FAIL";
                else status = "OK";

                // 16-20KB freeze pattern
                if (up > 0 && down == 0 && time >= timeoutSec && exitCode != 0)
                {
                    status = "LIKELY_BLOCKED";
                    warned = true;
                }

                lines.Add(new(label, code, up, down, time, status));
            }

            return new(target.Id, target.Provider, target.Country, lines, warned);
        }
        finally { sem.Release(); }
    }

    private static async Task<(string Code, long Up, long Down, double Time, int Exit)>
        RunCurlAsync(string curlPath, List<string> args, int timeoutSec)
    {
        try
        {
            var psi = new ProcessStartInfo(curlPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            using var cts  = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));

            var stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            var text = stdout.Trim();
            var parts = text.Split(' ');
            if (parts.Length >= 4
                && int.TryParse(parts[0], out var httpCode)
                && long.TryParse(parts[1], out var up)
                && long.TryParse(parts[2], out var down)
                && double.TryParse(parts[3], System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var t))
            {
                return (httpCode.ToString(), up, down, t, proc.ExitCode);
            }

            // Check for unsupported
            var err = await proc.StandardError.ReadToEndAsync();
            if (proc.ExitCode == 35 || err.Contains("not supported") || err.Contains("Unrecognized option"))
                return ("UNSUP", 0, 0, 0, proc.ExitCode);

            return ("ERR", 0, 0, 0, proc.ExitCode);
        }
        catch { return ("ERR", 0, 0, 0, -1); }
    }

    public static void PrintResults(List<DpiTargetResult> results)
    {
        bool anyWarn = false;
        foreach (var r in results)
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"\n=== [{r.Country}][{r.Provider}] {r.TargetId} ===");
            Console.ResetColor();

            foreach (var l in r.Lines)
            {
                Console.ForegroundColor = l.Status switch
                {
                    "OK"           => ConsoleColor.Green,
                    "UNSUPPORTED"  => ConsoleColor.Yellow,
                    "LIKELY_BLOCKED" => ConsoleColor.Yellow,
                    _              => ConsoleColor.Red
                };
                Console.WriteLine(
                    $"  [{l.Label}] code={l.Code} " +
                    $"up={l.UpBytes}B down={l.DownBytes}B time={l.Time:F1}s status={l.Status}");
                if (l.Status == "LIKELY_BLOCKED")
                    Console.WriteLine("    ⚠ Паттерн 16-20KB freeze — цензор режет стратегию");
                Console.ResetColor();
            }

            if (r.WarnDetected) anyWarn = true;
            else ConsoleMenu.WriteOk("Нет паттерна 16-20KB freeze для этого хоста");
        }

        Console.WriteLine();
        if (anyWarn) ConsoleMenu.WriteWarn("Обнаружена возможная блокировка DPI TCP 16-20 на некоторых хостах");
        else ConsoleMenu.WriteOk("Паттерн 16-20KB freeze не обнаружен");
    }
}
