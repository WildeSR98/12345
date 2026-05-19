using ZapretManager.Core;
using ZapretManager.UI;
using ZapretManager.Service;
using ZapretManager.Lists;
using ZapretManager.Diagnostics;
using ZapretManager.Updates;

namespace ZapretManager;

class Program
{
    static string RootDir = "";
    static string BinDir  = "";
    static string ListsDir = "";
    static string UtilsDir = "";
    static AppConfig Cfg = new();

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.Title = "Zapret Manager";

        // Detect root dir: search for strategies/ starting from exe dir, walking up
        RootDir  = DetectRootDir();
        BinDir   = Path.Combine(RootDir, "bin");
        ListsDir = Path.Combine(RootDir, "lists");
        UtilsDir = Path.Combine(RootDir, "utils");

        Cfg = AppConfig.Load(RootDir);
        Logger.Init(RootDir, Cfg.Features.VerboseLogging);

        AdminHelper.RequireAdmin();

        if (args.Length > 0 && args[0] == "--setup")
        {
            await RunSetupAsync();
            return;
        }

        await RunMenuAsync();
    }

    // ── MAIN MENU ─────────────────────────────────────────────────────────────
    static async Task RunMenuAsync()
    {
        while (true)
        {
            Console.Clear();
            PrintMenuHeader();

            Console.Write("   Выберите вариант (0-13): ");
            var choice = Console.ReadLine()?.Trim();

            switch (choice)
            {
                case "1":  await MenuInstallService();   break;
                case "2":  await MenuRemoveServices();   break;
                case "3":  MenuServiceStatus();           break;
                case "4":  MenuGameFilter();              break;
                case "5":  MenuIpsetSwitch();             break;
                case "6":  MenuToggleUpdates();           break;
                case "7":  await MenuUpdateIpset();       break;
                case "8":  await MenuUpdateHosts();       break;
                case "9":  await MenuCheckUpdates();      break;
                case "10": await MenuDiagnostics();       break;
                case "11": await MenuRunTests();          break;
                case "12": MenuExportReport();            break;
                case "13": MenuTgProxy();                 break;
                case "0":  return;
            }
        }
    }

    static void PrintMenuHeader()
    {
        var version = Cfg.Project.Version;
        var state   = WinServiceManager.GetState("zapret");
        var strategy = GetCurrentStrategy();
        var gf       = GameFilter.StatusLabel(UtilsDir);
        var ipset    = GetIpsetStatus();
        var updates  = File.Exists(Path.Combine(UtilsDir, "check_updates.enabled")) ? "вкл" : "выкл";
        var tgState  = IsTgProxyRunning() ? "запущен" : "остановлен";

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("\n  ╔══════════════════════════════════════════════════════════╗");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  ║  МЕНЕДЖЕР СЛУЖБЫ ZAPRET v{version,-34}║");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  ║  Служба: {state,-18}  Стратегия: {strategy,-16}║");
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("  ╚══════════════════════════════════════════════════════════╝");
        Console.ResetColor();

        Console.WriteLine("\n  :: СЛУЖБА");
        Console.WriteLine("     1. Установить службу");
        Console.WriteLine("     2. Удалить службы");
        Console.WriteLine("     3. Проверить статус");
        Console.WriteLine("\n  :: НАСТРОЙКИ");
        Console.WriteLine($"     4. Игровой фильтр          [{gf}]");
        Console.WriteLine($"     5. IPSet фильтр            [{ipset}]");
        Console.WriteLine($"     6. Автопроверка обновлений [{updates}]");
        Console.WriteLine("\n  :: ОБНОВЛЕНИЯ");
        Console.WriteLine("     7. Обновить список IPSet");
        Console.WriteLine("     8. Обновить файл Hosts");
        Console.WriteLine("     9. Проверить обновления");
        Console.WriteLine("\n  :: ИНСТРУМЕНТЫ");
        Console.WriteLine("     10. Запустить диагностику");
        Console.WriteLine("     11. Запустить тесты стратегий");
        Console.WriteLine("     12. Экспорт отчёта");
        Console.WriteLine($"     13. TG WS Proxy             [{tgState}]");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("\n  ─────────────────────────────────────────────────────────");
        Console.ResetColor();
        Console.WriteLine("      0. Выход\n");
    }

    // ── MENU ACTIONS ──────────────────────────────────────────────────────────

    static async Task MenuInstallService()
    {
        Console.Clear();
        ConsoleMenu.WriteHeader("УСТАНОВКА СЛУЖБЫ");

        var files = StrategyReader.GetStrategyFiles(RootDir);
        if (files.Length == 0)
        {
            ConsoleMenu.WriteError("Стратегии не найдены в папке strategies/");
            ConsoleMenu.PauseAny(); return;
        }

        Console.WriteLine("\n   Выберите стратегию:\n");
        for (int i = 0; i < files.Length; i++)
            Console.WriteLine($"     {i + 1,2}. {files[i].Name}");

        var input = ConsoleMenu.Prompt("\n   Номер стратегии");
        if (!int.TryParse(input, out var idx) || idx < 1 || idx > files.Length)
        { ConsoleMenu.WriteError("Неверный выбор"); ConsoleMenu.PauseAny(); return; }

        var bat    = files[idx - 1];
        var gf     = GameFilter.Get(UtilsDir);
        var winws  = Path.Combine(BinDir, "winws.exe");
        var batArgs = StrategyReader.ParseArgs(bat.FullName, BinDir, ListsDir, gf.Tcp, gf.Udp);

        ConsoleMenu.WriteStep($"Устанавливаю службу: {bat.Name}");

        // Enable TCP timestamps
        RunNetsh("interface tcp set global timestamps=enabled");

        var binPath = $"\"{winws}\" {batArgs}";
        var ok = WinServiceManager.Install("zapret", "zapret", "Zapret DPI bypass", binPath);

        if (ok)
        {
            // Save strategy name to registry
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                    @"System\CurrentControlSet\Services\zapret");
                key?.SetValue("zapret-discord-youtube",
                    Path.GetFileNameWithoutExtension(bat.Name));
            }
            catch { }
            ConsoleMenu.WriteOk($"Служба zapret установлена со стратегией: {bat.Name}");
        }
        else ConsoleMenu.WriteError("Не удалось установить службу");

        ConsoleMenu.PauseAny();
    }

    static async Task MenuRemoveServices()
    {
        Console.Clear();
        ConsoleMenu.WriteHeader("УДАЛЕНИЕ СЛУЖБ");
        ProcessManager.KillAll();
        foreach (var svc in new[] { "zapret", "WinDivert", "WinDivert14" })
        {
            WinServiceManager.Stop(svc);
            if (WinServiceManager.Remove(svc))
                ConsoleMenu.WriteOk($"Служба удалена: {svc}");
        }
        ConsoleMenu.PauseAny();
        await Task.CompletedTask;
    }

    static void MenuServiceStatus()
    {
        Console.Clear();
        ConsoleMenu.WriteHeader("СТАТУС СЛУЖБ");
        foreach (var svc in new[] { "zapret", "WinDivert", "WinDivert14" })
        {
            var st = WinServiceManager.GetState(svc);
            if (st == WinServiceManager.ServiceState.Running)
                ConsoleMenu.WriteOk($"{svc}: {st}");
            else if (st == WinServiceManager.ServiceState.NotInstalled)
                ConsoleMenu.WriteInfo($"{svc}: не установлена");
            else
                ConsoleMenu.WriteWarn($"{svc}: {st}");
        }
        Console.WriteLine();
        var strategy = GetCurrentStrategy();
        ConsoleMenu.WriteInfo($"Стратегия: {strategy}");
        ConsoleMenu.PauseAny();
    }

    static void MenuGameFilter()
    {
        Console.Clear();
        ConsoleMenu.WriteHeader("ИГРОВОЙ ФИЛЬТР");
        Console.WriteLine("   0. Отключить");
        Console.WriteLine("   1. TCP + UDP");
        Console.WriteLine("   2. Только TCP");
        Console.WriteLine("   3. Только UDP");
        var ch = ConsoleMenu.Prompt("Выберите вариант (0-3)", "0");
        var mode = ch switch { "1" => "all", "2" => "tcp", "3" => "udp", _ => "disabled" };
        GameFilter.Set(UtilsDir, mode);
        ConsoleMenu.WriteOk($"Игровой фильтр: {GameFilter.StatusLabel(UtilsDir)}");
        ConsoleMenu.WriteWarn("Перезапустите zapret для применения изменений");
        ConsoleMenu.PauseAny();
    }

    static void MenuIpsetSwitch()
    {
        Console.Clear();
        ConsoleMenu.WriteHeader("IPSET ФИЛЬТР");
        var listFile   = Path.Combine(ListsDir, "ipset-all.txt");
        var backupFile = listFile + ".backup";
        var status     = GetIpsetStatus();
        ConsoleMenu.WriteInfo($"Текущий режим: {status}");
        Console.WriteLine("   Переключение: loaded → none → any → loaded");

        switch (status)
        {
            case "loaded":
                if (File.Exists(backupFile)) File.Delete(backupFile);
                File.Move(listFile, backupFile);
                File.WriteAllText(listFile, "203.0.113.113/32\r\n");
                ConsoleMenu.WriteOk("Переключено в режим 'none'");
                break;
            case "none":
                File.WriteAllText(listFile, "\r\n");
                ConsoleMenu.WriteOk("Переключено в режим 'any'");
                break;
            case "any":
                if (File.Exists(backupFile))
                {
                    File.Delete(listFile);
                    File.Move(backupFile, listFile);
                    ConsoleMenu.WriteOk("Переключено в режим 'loaded'");
                }
                else ConsoleMenu.WriteError("Нет резервной копии. Сначала обновите список IPSet.");
                break;
        }
        ConsoleMenu.PauseAny();
    }

    static void MenuToggleUpdates()
    {
        var flag = Path.Combine(UtilsDir, "check_updates.enabled");
        if (File.Exists(flag)) { File.Delete(flag); ConsoleMenu.WriteOk("Автопроверка отключена"); }
        else { File.WriteAllText(flag, "ВКЛЮЧЕНО"); ConsoleMenu.WriteOk("Автопроверка включена"); }
        ConsoleMenu.PauseAny();
    }

    static async Task MenuUpdateIpset()
    {
        Console.Clear();
        ConsoleMenu.WriteHeader("ОБНОВЛЕНИЕ IPSET");
        var url      = Cfg.Repositories.ZapretCore.IpsetService ?? "";
        var listFile = Path.Combine(ListsDir, "ipset-all.txt");
        ConsoleMenu.StartSpinner("Скачивание ipset-all.txt...");
        try
        {
            using var http = new System.Net.Http.HttpClient();
            var content = await http.GetStringAsync(url);
            var newLines = content.Split('\n').Select(l => l.TrimEnd('\r'));
            var merged   = ListMerger.Merge(listFile, newLines);
            ListMerger.WriteUtf8(listFile, merged);
            ConsoleMenu.StopSpinner(true, $"ipset-all.txt обновлён ({merged.Length} строк)");
        }
        catch (Exception ex)
        {
            ConsoleMenu.StopSpinner(false, $"Ошибка: {ex.Message}");
        }
        ConsoleMenu.PauseAny();
    }

    static async Task MenuUpdateHosts()
    {
        Console.Clear();
        ConsoleMenu.WriteHeader("ОБНОВЛЕНИЕ HOSTS");
        var url = Cfg.Repositories.ZapretCore.HostsService ?? "";
        ConsoleMenu.StartSpinner("Проверка файла hosts...");
        var needsUpdate = await HostsUpdater.CheckAndUpdate(url);
        ConsoleMenu.StopSpinner(!needsUpdate, needsUpdate
            ? "Требуется обновление — открыт в Блокноте"
            : "Файл hosts актуален");
        ConsoleMenu.PauseAny();
    }

    static async Task MenuCheckUpdates()
    {
        Console.Clear();
        ConsoleMenu.WriteHeader("ПРОВЕРКА ОБНОВЛЕНИЙ");
        ConsoleMenu.StartSpinner("Запрос к GitHub...");
        var (remote, local) = await GitHubUpdater.CheckZapretCoreAsync(Cfg, RootDir);
        ConsoleMenu.StopSpinner();

        if (remote == null) { ConsoleMenu.WriteError("Не удалось получить версию с GitHub"); }
        else if (remote == local) { ConsoleMenu.WriteOk($"Установлена последняя версия: {local}"); }
        else
        {
            ConsoleMenu.WriteWarn($"Доступна новая версия: {remote} (установлена: {local ?? "неизвестна"})");
            var dlPage = Cfg.Repositories.ZapretCore.DownloadPage ?? "";
            if (!string.IsNullOrWhiteSpace(dlPage) && ConsoleMenu.Confirm("Открыть страницу загрузки?"))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlPage) { UseShellExecute = true });
        }
        ConsoleMenu.PauseAny();
    }

    static async Task MenuDiagnostics()
    {
        Console.Clear();
        ConsoleMenu.WriteHeader("ДИАГНОСТИКА");

        var conflicts = ConflictDetector.FindConflicts(Cfg.Diagnostics.ConflictingServices);
        if (conflicts.Count > 0)
            ConsoleMenu.WriteWarn($"Конфликтующие службы: {string.Join(", ", conflicts)}");
        else
            ConsoleMenu.WriteOk("Конфликтующих служб не найдено");

        ConsoleMenu.WriteStep("Проверка доступности ресурсов...");
        var results = await AccessChecker.CheckAllAsync(Cfg.Diagnostics.CheckTargets);
        foreach (var r in results)
        {
            if (r.Reachable) ConsoleMenu.WriteOk($"{r.Name}: {r.Detail}");
            else             ConsoleMenu.WriteWarn($"{r.Name}: недоступен");
        }
        ConsoleMenu.PauseAny();
    }

    static async Task MenuRunTests()
    {
        Console.Clear();
        ConsoleMenu.WriteHeader("ТЕСТЫ СТРАТЕГИЙ");
        Console.WriteLine("   [1] Стандартные тесты (HTTP/ping)");
        Console.WriteLine("   [2] DPI тест (TCP 16-20 KB freeze)");
        var mode = ConsoleMenu.Prompt("Выберите тип теста", "1");

        var winws = Path.Combine(BinDir, "winws.exe");

        if (mode == "2")
        {
            ConsoleMenu.WriteStep("Загрузка DPI suite...");
            var targets = await DpiChecker.GetSuiteAsync();
            if (targets.Count == 0) { ConsoleMenu.WriteError("Suite недоступен"); ConsoleMenu.PauseAny(); return; }

            var curlPath = Path.Combine(BinDir, "curl.exe");
            if (!File.Exists(curlPath)) curlPath = "curl.exe";

            var files = StrategyReader.GetStrategyFiles(RootDir);
            Console.WriteLine($"\n   Выберите конфиги (через запятую, 0=все):");
            for (int i = 0; i < files.Length; i++) Console.WriteLine($"   {i+1}. {files[i].Name}");
            var sel = ConsoleMenu.Prompt("Номера", "0");
            var selected = sel == "0" ? files : ParseSelection(sel, files);

            ConsoleMenu.WriteStep("Запуск DPI тестов...");
            var results = await DpiChecker.RunSuiteAsync(targets, curlPath);
            DpiChecker.PrintResults(results);
        }
        else
        {
            var scores = await StrategyTester.RunAllAsync(RootDir, Cfg.Diagnostics.CheckTargets, winws);
            StrategyTester.PrintSummary(scores);
            var best = StrategyTester.GetBest(scores);
            if (best != null)
            {
                ConsoleMenu.WriteOk($"Лучшая стратегия: {best.Name} (✓{best.Ok} ✗{best.Fail})");
                if (ConsoleMenu.Confirm("Установить лучшую стратегию как службу?"))
                {
                    var gf    = GameFilter.Get(UtilsDir);
                    var bArgs = StrategyReader.ParseArgs(best.FullPath, BinDir, ListsDir, gf.Tcp, gf.Udp);
                    var wp    = Path.Combine(BinDir, "winws.exe");
                    WinServiceManager.Install("zapret", "zapret", "Zapret DPI bypass",
                        $"\"{wp}\" {bArgs}");
                    ConsoleMenu.WriteOk("Служба установлена");
                }
            }
        }
        ConsoleMenu.PauseAny();
    }

    static void MenuExportReport()
    {
        Console.Clear();
        ConsoleMenu.WriteHeader("ЭКСПОРТ ОТЧЁТА");
        var path = ReportExporter.Export(RootDir);
        ConsoleMenu.WriteOk($"Отчёт: {path}");
        if (ConsoleMenu.Confirm("Открыть файл?"))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        ConsoleMenu.PauseAny();
    }

    static void MenuTgProxy()
    {
        Console.Clear();
        ConsoleMenu.WriteHeader("TG WS PROXY");
        var exePath = Path.Combine(RootDir, "tg-ws-proxy.exe");
        if (!File.Exists(exePath)) { ConsoleMenu.WriteError("tg-ws-proxy.exe не найден"); ConsoleMenu.PauseAny(); return; }

        var running = IsTgProxyRunning();
        if (running)
        {
            ConsoleMenu.WriteInfo("Прокси запущен");
            Console.WriteLine("   [1] Остановить   [0] Назад");
            var ch = ConsoleMenu.Prompt("Выбор", "0");
            if (ch == "1")
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("tg-ws-proxy"))
                    try { p.Kill(); } catch { }
                ConsoleMenu.WriteOk("Прокси остановлен");
            }
        }
        else
        {
            ConsoleMenu.WriteInfo("Прокси не запущен");
            Console.WriteLine("   [1] Запустить   [0] Назад");
            var ch = ConsoleMenu.Prompt("Выбор", "0");
            if (ch == "1")
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath)
                    { UseShellExecute = true });
                ConsoleMenu.WriteOk("Прокси запущен");
            }
        }
        ConsoleMenu.PauseAny();
    }

    // ── SETUP WIZARD ─────────────────────────────────────────────────────────
    static async Task RunSetupAsync()
    {
        Console.Clear();
        ConsoleMenu.WriteHeader("АВТОУСТАНОВКА ZAPRET");
        Logger.Init(RootDir);

        ConsoleMenu.WriteStep("Проверка конфликтующих служб...");
        var conflicts = ConflictDetector.FindConflicts(Cfg.Diagnostics.ConflictingServices);
        if (conflicts.Count > 0)
        {
            ConsoleMenu.WriteWarn($"Найдены конфликты: {string.Join(", ", conflicts)}");
            if (ConsoleMenu.Confirm("Удалить конфликтующие службы?"))
                ConflictDetector.RemoveConflicts(conflicts);
        }
        else ConsoleMenu.WriteOk("Конфликтов не найдено");

        ConsoleMenu.WriteStep("Загрузка списков...");
        int done = 0;
        await ListDownloader.DownloadAllAsync(
            Cfg.Lists.Files, ListsDir,
            "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main",
            (msg, ok) => { if (ok) ConsoleMenu.WriteOk(msg); else ConsoleMenu.WriteWarn(msg); });

        ConsoleMenu.WriteStep("Очистка списков...");
        ListRepairer.RepairAll(ListsDir, Cfg.Features.RemoveCidrOverlap);
        ConsoleMenu.WriteOk("Списки очищены");

        ConsoleMenu.WriteStep("Проверка доступности без обхода...");
        var preResults = await AccessChecker.CheckAllAsync(Cfg.Diagnostics.CheckTargets);
        foreach (var r in preResults)
        {
            if (!r.Reachable) ConsoleMenu.WriteWarn($"{r.Name}: недоступен (нужен обход)");
            else ConsoleMenu.WriteOk($"{r.Name}: доступен");
        }

        ConsoleMenu.WriteStep("Выбор стратегии");
        var files = StrategyReader.GetStrategyFiles(RootDir);
        Console.WriteLine("   [1] Авто-тест всех стратегий (рекомендуется)");
        Console.WriteLine("   [2] Выбрать вручную");
        var modeChoice = ConsoleMenu.Prompt("Режим", "1");

        string? selectedBat = null;
        if (modeChoice == "1")
        {
            var winws = Path.Combine(BinDir, "winws.exe");
            var scores = await StrategyTester.RunAllAsync(RootDir, Cfg.Diagnostics.CheckTargets, winws);
            StrategyTester.PrintSummary(scores);
            var best = StrategyTester.GetBest(scores);
            if (best != null) { selectedBat = best.FullPath; ConsoleMenu.WriteOk($"Лучшая: {best.Name}"); }
        }
        else
        {
            for (int i = 0; i < files.Length; i++) Console.WriteLine($"   {i+1,2}. {files[i].Name}");
            var inp = ConsoleMenu.Prompt("Номер стратегии", "1");
            if (int.TryParse(inp, out var idx) && idx >= 1 && idx <= files.Length)
                selectedBat = files[idx - 1].FullName;
        }

        if (selectedBat == null) { ConsoleMenu.WriteError("Стратегия не выбрана"); ConsoleMenu.PauseAny(); return; }

        ConsoleMenu.WriteStep("Установка службы...");
        var gf   = GameFilter.Get(UtilsDir);
        var wExe = Path.Combine(BinDir, "winws.exe");
        var bArgs = StrategyReader.ParseArgs(selectedBat, BinDir, ListsDir, gf.Tcp, gf.Udp);
        RunNetsh("interface tcp set global timestamps=enabled");
        WinServiceManager.Install("zapret", "zapret", "Zapret DPI bypass", $"\"{wExe}\" {bArgs}");

        ConsoleMenu.WriteStep("Пост-проверка...");
        await Task.Delay(5000);
        var post = await AccessChecker.CheckAllAsync(Cfg.Diagnostics.CheckTargets);
        int okCnt = post.Count(r => r.Reachable);
        Console.WriteLine();
        if (okCnt > post.Count / 2) ConsoleMenu.WriteOk($"ZAPRET РАБОТАЕТ! {okCnt}/{post.Count} ресурсов доступны");
        else if (okCnt > 0) ConsoleMenu.WriteWarn($"Частично: {okCnt}/{post.Count}. Попробуйте другую стратегию.");
        else ConsoleMenu.WriteError("Ни один ресурс недоступен. Запустите тест стратегий из меню.");

        ConsoleMenu.PauseAny();
    }

    // ── HELPERS ───────────────────────────────────────────────────────────────
    static string GetCurrentStrategy()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"System\CurrentControlSet\Services\zapret");
            return key?.GetValue("zapret-discord-youtube")?.ToString() ?? "не установлена";
        }
        catch { return "?"; }
    }

    static string GetIpsetStatus()
    {
        var f = Path.Combine(ListsDir, "ipset-all.txt");
        if (!File.Exists(f)) return "none";
        var lines = File.ReadAllLines(f).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        if (lines.Length == 0) return "any";
        if (lines.Any(l => l.Trim() == "203.0.113.113/32")) return "none";
        return "loaded";
    }

    static bool IsTgProxyRunning()
        => System.Diagnostics.Process.GetProcessesByName("tg-ws-proxy").Length > 0;

    static void RunNetsh(string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netsh", args)
                { CreateNoWindow = true, UseShellExecute = false };
            System.Diagnostics.Process.Start(psi)?.WaitForExit(3000);
        }
        catch { }
    }

    /// <summary>
    /// Walk up from exe directory to find the root that contains strategies/ folder.
    /// Falls back to exe directory if not found.
    /// </summary>
    static string DetectRootDir()
    {
        var dir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var candidate = dir;
        for (int i = 0; i < 5; i++)
        {
            if (Directory.Exists(Path.Combine(candidate, "strategies")))
                return candidate;
            var parent = Path.GetDirectoryName(candidate);
            if (parent == null || parent == candidate) break;
            candidate = parent;
        }
        // Not found via walk-up — use exe dir (strategies/ should be copied there by build)
        return dir;
    }

    static FileInfo[] ParseSelection(string input, FileInfo[] files)
    {
        var result = new List<FileInfo>();
        foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out var n) && n >= 1 && n <= files.Length)
                result.Add(files[n - 1]);
        }
        return result.ToArray();
    }
}
