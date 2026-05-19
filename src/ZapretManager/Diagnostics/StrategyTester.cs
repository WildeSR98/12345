using ZapretManager.Core;
using ZapretManager.UI;

namespace ZapretManager.Diagnostics;

public record StrategyScore(string Name, string FullPath, int Ok, int Fail);

/// <summary>
/// Auto-tests all general*.bat strategies via a temporary Windows service.
/// Equivalent to the foreach-bat loop in test zapret.ps1.
/// </summary>
public static class StrategyTester
{
    private const string TestServiceName = "zapret_test";

    public static async Task<List<StrategyScore>> RunAllAsync(
        string rootDir,
        IList<CheckTarget> targets,
        string winwsExe)
    {
        var files = Service.StrategyReader.GetStrategyFiles(rootDir);
        if (files.Length == 0)
        {
            ConsoleMenu.WriteError("Стратегии general*.bat не найдены в папке strategies/");
            return new();
        }

        var results = new List<StrategyScore>();
        var binDir   = Path.Combine(rootDir, "bin");
        var listsDir = Path.Combine(rootDir, "lists");
        var utilsDir = Path.Combine(rootDir, "utils");
        var gf = Service.GameFilter.Get(utilsDir);

        ConsoleMenu.WriteHeader($"АВТО-ТЕСТ СТРАТЕГИЙ ({files.Length} конфигов)");
        ConsoleMenu.WriteWarn("Тесты займут несколько минут. Пожалуйста, подождите...");

        for (int i = 0; i < files.Length; i++)
        {
            var file = files[i];
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"\n  [{i + 1}/{files.Length}] {file.Name}");
            Console.ResetColor();

            // Parse args from bat
            var args = Service.StrategyReader.ParseArgs(
                file.FullName, binDir, listsDir, gf.Tcp, gf.Udp);

            // Stop any running winws
            Service.ProcessManager.KillAll();

            // Install and start temporary service
            var binPath = $"\"{winwsExe}\" {args}";
            Service.WinServiceManager.Remove(TestServiceName);
            await Task.Delay(300);
            Service.WinServiceManager.Install(TestServiceName, "Zapret Test", "Temporary test", binPath, autoStart: false);
            await Task.Delay(3000); // Let it initialize

            // Run access checks
            var accessResults = await AccessChecker.CheckAllAsync(targets);
            int ok   = accessResults.Count(r => r.Reachable);
            int fail = accessResults.Count(r => !r.Reachable);

            // Print results
            foreach (var r in accessResults)
            {
                if (r.Reachable) ConsoleMenu.WriteOk($"{r.Name}: {r.Detail}");
                else             ConsoleMenu.WriteWarn($"{r.Name}: недоступен");
            }

            results.Add(new(file.Name, file.FullName, ok, fail));

            // Stop and remove test service
            Service.WinServiceManager.Remove(TestServiceName);
            Service.ProcessManager.KillAll();
            await Task.Delay(500);
        }

        return results;
    }

    public static StrategyScore? GetBest(List<StrategyScore> scores)
        => scores.OrderByDescending(s => s.Ok).ThenBy(s => s.Fail).FirstOrDefault();

    public static void PrintSummary(List<StrategyScore> scores)
    {
        ConsoleMenu.WriteHeader("РЕЗУЛЬТАТЫ ТЕСТИРОВАНИЯ");
        int maxLen = scores.Max(s => s.Name.Length);
        foreach (var s in scores.OrderByDescending(s => s.Ok))
        {
            Console.ForegroundColor = s.Ok > s.Fail ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"   {s.Name.PadRight(maxLen)}  ✓{s.Ok}  ✗{s.Fail}");
            Console.ResetColor();
        }
    }
}
