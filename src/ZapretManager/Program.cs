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
        Console.Title = "Zapret Auto-Setup";

        RootDir  = DetectRootDir();
        BinDir   = Path.Combine(RootDir, "bin");
        ListsDir = Path.Combine(RootDir, "lists");
        UtilsDir = Path.Combine(RootDir, "utils");
        Directory.CreateDirectory(UtilsDir);

        Cfg = AppConfig.Load(RootDir);
        Logger.Init(RootDir, Cfg.Features.VerboseLogging);
        AdminHelper.RequireAdmin();

        if (args.Contains("--menu"))        { Console.Title = "Zapret Manager"; await RunMenuAsync(); return; }
        if (args.Contains("--remove"))      { await RunRemoveAsync(); return; }
        if (args.Contains("--reinstall"))   { await RunRemoveAsync(silent: true); await RunSetupAsync(args); return; }
        if (args.Contains("--test"))        { await RunTestAndInstallAsync(); return; }
        if (args.Contains("--diagnostics")) { await MenuDiagnostics(); MenuExportReport(); return; }

        // Default — run setup wizard (like autosetup.bat)
        await RunSetupAsync(args);
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
        var mgrVer  = GitHubUpdater.ReadManagerVersion(RootDir) ?? version;
        var coreVer = ReadLocalCoreVersion();
        var state   = WinServiceManager.GetState("zapret");
        var strategy = GetCurrentStrategy();
        var gf       = GameFilter.StatusLabel(UtilsDir);
        var ipset    = GetIpsetStatus();
        var updates  = File.Exists(Path.Combine(UtilsDir, "check_updates.enabled")) ? "вкл" : "выкл";
        var tgState  = IsTgProxyRunning() ? "запущен" : "остановлен";

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("\n  ╔══════════════════════════════════════════════════════════╗");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  ║  МЕНЕДЖЕР СЛУЖБЫ ZAPRET                                 ║");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  ║  Manager: v{mgrVer,-12}  Zapret Core: {coreVer,-14}║");
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
        ConsoleMenu.WriteInfo($"Текущие версии: Manager v{GitHubUpdater.ReadManagerVersion(RootDir) ?? "не определена"} | Zapret Core {ReadLocalCoreVersion()}");
        Console.WriteLine();

        // ── Этап 1: Проверка обновлений zapret-manager ──
        ConsoleMenu.WriteStep("Этап 1: Проверка обновлений zapret-manager...");
        ConsoleMenu.StartSpinner("Запрос к GitHub...");
        var (mgrRemote, mgrLocal, mgrDownloadUrl) = await GitHubUpdater.CheckManagerUpdateAsync(Cfg, RootDir);
        ConsoleMenu.StopSpinner();

        if (mgrRemote == null)
        {
            ConsoleMenu.WriteInfo("Не удалось проверить обновления manager (нет релизов)");
        }
        else if (mgrRemote == mgrLocal)
        {
            ConsoleMenu.WriteOk($"Zapret Manager актуален: v{mgrLocal}");
        }
        else
        {
            ConsoleMenu.WriteWarn($"Доступна новая версия manager: v{mgrRemote} (у вас: v{mgrLocal ?? "не определена"})");
            if (mgrDownloadUrl != null && ConsoleMenu.Confirm("Обновить zapret-manager?"))
            {
                var updated = await GitHubUpdater.UpdateManagerAsync(mgrDownloadUrl, RootDir, mgrRemote);
                if (updated)
                {
                    ConsoleMenu.WriteOk("Обновление запущено. Приложение будет перезапущено.");
                    Environment.Exit(0);
                    return;
                }
            }
        }

        // ── Этап 2: Проверка обновлений zapret core ──
        ConsoleMenu.WriteStep("Этап 2: Проверка обновлений zapret core (Flowseal)...");
        ConsoleMenu.StartSpinner("Запрос к GitHub...");
        var (remote, local) = await GitHubUpdater.CheckZapretCoreAsync(Cfg, RootDir);
        ConsoleMenu.StopSpinner();

        if (remote == null)
        {
            ConsoleMenu.WriteError("Не удалось получить версию zapret core с GitHub");
        }
        else if (remote == local)
        {
            ConsoleMenu.WriteOk($"Zapret core актуален: {local}");
        }
        else
        {
            ConsoleMenu.WriteWarn($"Доступна новая версия zapret core: {remote} (у вас: {local ?? "не установлена"})");
            if (ConsoleMenu.Confirm("Обновить файлы zapret core (bin, strategies, lists)?"))
            {
                await GitHubUpdater.UpdateZapretCoreFilesAsync(Cfg, RootDir);
            }
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

        // Check for interrupted test (ipset flag)
        IpsetTestHelper.CheckAndRestoreFlag(RootDir, ListsDir);

        // Select test type
        Console.WriteLine("   [1] Стандартные тесты (HTTP/TLS1.2/TLS1.3 + ping)");
        Console.WriteLine("   [2] DPI тесты (TCP 16-20 KB freeze)");
        var testType = ConsoleMenu.Prompt("Выберите тип теста", "1");

        var winws = Path.Combine(BinDir, "winws.exe");
        var files = StrategyReader.GetStrategyFiles(RootDir);
        if (files.Length == 0)
        {
            ConsoleMenu.WriteError("Стратегии general*.bat не найдены в папке strategies/");
            ConsoleMenu.PauseAny(); return;
        }

        // Select configs
        var selectedConfigs = StrategyTester.SelectConfigs(files);

        // Save winws snapshot
        var winwsSnapshot = WinWsSnapshot.Capture();

        // Save original ipset status for DPI tests
        var originalIpsetStatus = IpsetTestHelper.GetStatus(ListsDir);

        try
        {
            if (testType == "2")
            {
                // ── DPI тесты ──
                ConsoleMenu.WriteStep("Загрузка DPI suite...");
                var dpiTargets = await DpiChecker.GetSuiteAsync();
                if (dpiTargets.Count == 0)
                {
                    ConsoleMenu.WriteError("Suite недоступен");
                    ConsoleMenu.PauseAny(); return;
                }

                var curlPath = Path.Combine(BinDir, "curl.exe");
                if (!File.Exists(curlPath)) curlPath = "curl.exe";

                // Switch ipset to 'any' for accurate DPI tests
                if (originalIpsetStatus != "any")
                {
                    ConsoleMenu.WriteWarn($"IPSet в режиме '{originalIpsetStatus}'. Переключение в 'any' для точных DPI тестов...");
                    IpsetTestHelper.SwitchToAny(ListsDir);
                    IpsetTestHelper.SetFlag(RootDir);
                }

                var allDpiResults = new List<(string Config, List<DpiTargetResult> Results)>();

                ConsoleMenu.WriteWarn("Тесты займут несколько минут. Пожалуйста, подождите...");

                for (int i = 0; i < selectedConfigs.Length; i++)
                {
                    var file = selectedConfigs[i];
                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.WriteLine($"\n  [{i + 1}/{selectedConfigs.Length}] {file.Name}");
                    Console.WriteLine("  " + new string('─', 56));
                    Console.ResetColor();

                    Service.ProcessManager.KillAll();
                    await Task.Delay(500);

                    // Start config via cmd.exe
                    var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                        $"/c \"{file.FullName}\"") { WorkingDirectory = RootDir, WindowStyle = System.Diagnostics.ProcessWindowStyle.Minimized, UseShellExecute = true });
                    await Task.Delay(5000);

                    ConsoleMenu.WriteInfo("Выполнение DPI тестов...");
                    var dpiResults = await DpiChecker.RunSuiteAsync(dpiTargets, curlPath);
                    DpiChecker.PrintResults(dpiResults);
                    allDpiResults.Add((file.Name, dpiResults));

                    Service.ProcessManager.KillAll();
                    if (proc != null && !proc.HasExited) try { proc.Kill(); } catch { }
                    await Task.Delay(500);
                }

                // DPI Analytics
                ConsoleMenu.WriteHeader("DPI АНАЛИТИКА");
                string? bestDpi = null;
                int maxOk = 0;
                foreach (var (config, results) in allDpiResults)
                {
                    int ok = results.SelectMany(r => r.Lines).Count(l => l.Status == "OK");
                    int fail = results.SelectMany(r => r.Lines).Count(l => l.Status == "FAIL");
                    int blocked = results.SelectMany(r => r.Lines).Count(l => l.Status == "LIKELY_BLOCKED");
                    int unsup = results.SelectMany(r => r.Lines).Count(l => l.Status == "UNSUPPORTED");
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"   {config} : OK: {ok}, FAIL: {fail}, UNSUP: {unsup}, BLOCKED: {blocked}");
                    Console.ResetColor();
                    if (ok > maxOk) { maxOk = ok; bestDpi = config; }
                }
                if (bestDpi != null) ConsoleMenu.WriteOk($"Лучший конфиг: {bestDpi}");

                // Save DPI results
                StrategyTester.SaveDpiResults(RootDir, allDpiResults);
            }
            else
            {
                // ── Стандартные тесты ──
                var targets = StrategyTester.LoadTargets(RootDir, Cfg.Diagnostics.CheckTargets);
                if (targets.Count == 0)
                {
                    ConsoleMenu.WriteError("Нет целей для тестирования");
                    ConsoleMenu.PauseAny(); return;
                }

                var allResults = await StrategyTester.RunStandardTestsAsync(
                    RootDir, selectedConfigs, targets, winws);

                // Analytics
                var analytics = StrategyTester.ComputeAnalytics(allResults);
                StrategyTester.PrintAnalytics(analytics);

                // Save results
                StrategyTester.SaveStandardResults(RootDir, allResults, analytics);

                // Offer to install best
                var bestConfig = StrategyTester.GetBestConfig(analytics);
                if (bestConfig != null)
                {
                    var bestFile = selectedConfigs.FirstOrDefault(f => f.Name == bestConfig);
                    if (bestFile != null && ConsoleMenu.Confirm("Установить лучшую стратегию как службу?"))
                    {
                        var gf = GameFilter.Get(UtilsDir);
                        var bArgs = StrategyReader.ParseArgs(bestFile.FullName, BinDir, ListsDir, gf.Tcp, gf.Udp);
                        var wp = Path.Combine(BinDir, "winws.exe");
                        WinServiceManager.Install("zapret", "zapret", "Zapret DPI bypass",
                            $"\"{wp}\" {bArgs}");
                        ConsoleMenu.WriteOk("Служба установлена");
                    }
                }
            }
        }
        finally
        {
            // Restore ipset if it was switched
            if (originalIpsetStatus != "any")
            {
                ConsoleMenu.WriteInfo("Восстановление ipset...");
                IpsetTestHelper.Restore(ListsDir);
                IpsetTestHelper.RemoveFlag(RootDir);
            }

            // Restore winws snapshot
            Service.ProcessManager.KillAll();
            WinWsSnapshot.Restore(winwsSnapshot);
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

    // ── SETUP WIZARD (mirrors autosetup.ps1 flow) ─────────────────────────────
    static async Task RunSetupAsync(string[]? args = null)
    {
        bool silent = args?.Contains("--silent") == true;
        string? forcedStrategy = null;
        for (int i = 0; i < (args?.Length ?? 0) - 1; i++)
            if (args![i] == "--strategy") { forcedStrategy = args[i + 1]; break; }

        Console.Clear();
        Console.Title = "Zapret Auto-Setup";
        var mgrVer  = GitHubUpdater.ReadManagerVersion(RootDir) ?? Cfg.Project.Version;
        var coreVer = ReadLocalCoreVersion();
        ConsoleMenu.WriteHeader($"ZAPRET AUTO-SETUP");
        ConsoleMenu.WriteInfo($"Manager: v{mgrVer}  |  Zapret Core: {coreVer}");
        ConsoleMenu.WriteInfo($"Рабочая папка: {RootDir}");
        ConsoleMenu.WriteInfo($".NET: {Environment.Version}");

        // ── Фоновая проверка обновлений (оба этапа) ──
        var managerUpdateTask = GitHubUpdater.CheckManagerUpdateAsync(Cfg, RootDir);
        var coreUpdateTask = GitHubUpdater.CheckZapretCoreAsync(Cfg, RootDir);

        // Главное меню
        string mainOpt = "1";
        if (!silent)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine("  " + new string('═', 54));
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  ГЛАВНОЕ МЕНЮ");
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine("  " + new string('═', 54));
            Console.ResetColor();
            Console.WriteLine("    [1]  Установить / Обновить конфигурацию");
            Console.WriteLine("    [2]  Удалить zapret");
            Console.WriteLine("    [3]  Переустановить");
            Console.WriteLine("    [4]  Диагностика и отчёт");
            Console.WriteLine("    [5]  Тест стратегий и установка");
            Console.WriteLine("    [s]  Сервисное меню");
            Console.WriteLine();
            mainOpt = ConsoleMenu.Prompt("  Выберите (1/2/3/4/5/s, по умолчанию 1)", "1") ?? "1";
        }

        // ── Этап 1: Проверка обновлений zapret-manager ──
        try
        {
            var (mgrRemote, mgrLocal, mgrDownloadUrl) = await managerUpdateTask;
            if (mgrRemote != null && mgrRemote != mgrLocal)
            {
                ConsoleMenu.WriteWarn($"Доступна новая версия zapret-manager: v{mgrRemote} (у вас: v{mgrLocal ?? "не определена"})");
                if (!silent && mgrDownloadUrl != null && ConsoleMenu.Confirm("Обновить zapret-manager?"))
                {
                    var updated = await GitHubUpdater.UpdateManagerAsync(mgrDownloadUrl, RootDir, mgrRemote);
                    if (updated)
                    {
                        ConsoleMenu.WriteOk("Обновление запущено. Приложение будет перезапущено.");
                        Environment.Exit(0);
                        return;
                    }
                }
            }
            else if (mgrRemote != null)
            {
                ConsoleMenu.WriteOk($"Manager актуален: v{mgrLocal}");
            }
        }
        catch { }

        // ── Этап 2: Проверка обновлений zapret core ──
        try
        {
            var (remote, local) = await coreUpdateTask;
            if (remote != null && remote != local)
            {
                ConsoleMenu.WriteWarn($"Доступна новая версия zapret core: {remote} (у вас: {local ?? "не установлена"})");
                if (!silent && ConsoleMenu.Confirm("Обновить файлы zapret core (bin, strategies, lists)?"))
                {
                    await GitHubUpdater.UpdateZapretCoreFilesAsync(Cfg, RootDir);
                }
            }
            else if (remote != null)
            {
                ConsoleMenu.WriteOk($"Zapret core актуален: {local}");
            }
        }
        catch { }

        switch (mainOpt.ToLower())
        {
            case "2": await RunRemoveAsync(silent); return;
            case "3": await RunRemoveAsync(silent: true); break;
            case "4": await MenuDiagnostics(); MenuExportReport(); if (!silent) ConsoleMenu.PauseAny(); return;
            case "5": await RunTestAndInstallAsync(); return;
            case "s": await RunMenuAsync(); return;
        }

        // Инициализация пользовательских списков-заглушек
        Directory.CreateDirectory(ListsDir);
        foreach (var e in Cfg.Lists.Files.Where(e => e.User))
        {
            var p = Path.Combine(ListsDir, e.Local);
            if (!File.Exists(p)) ListMerger.WriteUtf8(p, new[] { e.Stub });
        }

        // Проверка файлов
        ConsoleMenu.WriteStep("Проверка необходимых файлов");
        var winwsExe = Path.Combine(BinDir, "winws.exe");
        if (!File.Exists(winwsExe))
        {
            ConsoleMenu.WriteError($"winws.exe не найден в {BinDir}");
            if (!silent) ConsoleMenu.PauseAny();
            return;
        }
        ConsoleMenu.WriteOk("winws.exe найден");
        if (File.Exists(Path.Combine(BinDir, "curl.exe")) || IsInPath("curl.exe"))
            ConsoleMenu.WriteOk("curl.exe найден");
        else
            ConsoleMenu.WriteWarn("curl.exe не найден — HTTP-тесты ограничены");

        // Конфликтующие службы
        ConsoleMenu.WriteStep("Проверка конфликтующих служб");
        var conflicts = ConflictDetector.FindConflicts(Cfg.Diagnostics.ConflictingServices);
        if (conflicts.Count > 0)
        {
            ConsoleMenu.WriteWarn($"Найдены: {string.Join(", ", conflicts)}");
            if (silent || ConsoleMenu.Confirm("Удалить автоматически?"))
                ConflictDetector.RemoveConflicts(conflicts);
        }
        else ConsoleMenu.WriteOk("Конфликтующих служб не найдено");

        // Game Filter
        if (!silent)
        {
            ConsoleMenu.WriteStep("Настройка Game Filter");
            ConsoleMenu.WriteInfo($"Текущий статус: {GameFilter.StatusLabel(UtilsDir)}");
            Console.WriteLine("   [1] Оставить как есть  [2] Отключить  [3] TCP+UDP  [4] Только TCP  [5] Только UDP");
            switch (ConsoleMenu.Prompt("Выберите (1..5)", "1"))
            {
                case "2": GameFilter.Set(UtilsDir, "disabled"); ConsoleMenu.WriteOk("Game Filter отключён"); break;
                case "3": GameFilter.Set(UtilsDir, "all");      ConsoleMenu.WriteOk("Game Filter: TCP+UDP"); break;
                case "4": GameFilter.Set(UtilsDir, "tcp");      ConsoleMenu.WriteOk("Game Filter: только TCP"); break;
                case "5": GameFilter.Set(UtilsDir, "udp");      ConsoleMenu.WriteOk("Game Filter: только UDP"); break;
                default:  ConsoleMenu.WriteInfo("Game Filter не изменён"); break;
            }
        }

        // TCP timestamps
        ConsoleMenu.WriteStep("Включение TCP timestamps");
        RunNetsh("interface tcp set global timestamps=enabled");
        ConsoleMenu.WriteOk("TCP timestamps включены");

        // Загрузка списков с GitHub
        ConsoleMenu.WriteStep("Загрузка списков IP и доменов с GitHub");
        await ListDownloader.DownloadAllAsync(
            Cfg.Lists.Files, ListsDir,
            "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main",
            (msg, ok) => { if (ok) ConsoleMenu.WriteOk(msg); else ConsoleMenu.WriteWarn(msg); });

        // Очистка списков
        ConsoleMenu.WriteStep("Очистка списков (дубликаты, пустые строки, невалидные IP)");
        ListRepairer.RepairAll(ListsDir, Cfg.Features.RemoveCidrOverlap);
        ConsoleMenu.WriteOk("Списки очищены");

        // Обновление hosts
        ConsoleMenu.WriteStep("Обновление файла hosts");
        var hostsUrl = Cfg.Repositories.ZapretCore.HostsService ?? "";
        if (!string.IsNullOrWhiteSpace(hostsUrl))
        {
            var hostsNeedsUpdate = await HostsUpdater.CheckAndUpdate(hostsUrl);
            if (hostsNeedsUpdate)
                ConsoleMenu.WriteWarn("Файл hosts требует обновления — открыт в Блокноте для ручного слияния");
            else
                ConsoleMenu.WriteOk("Файл hosts актуален");
        }
        else ConsoleMenu.WriteInfo("URL hosts не настроен в config.json — пропуск");

        // Синхронизация publish/lists с основной lists/
        SyncPublishLists();

        // Проверка доступности БЕЗ обхода
        ConsoleMenu.WriteStep("Проверка доступности сайтов БЕЗ обхода");
        ProcessManager.KillAll();
        var preResults = await AccessChecker.CheckAllAsync(Cfg.Diagnostics.CheckTargets);
        bool allOk = preResults.All(r => r.Reachable);
        foreach (var r in preResults)
        {
            if (r.Reachable) ConsoleMenu.WriteOk($"{r.Name}: доступен");
            else             ConsoleMenu.WriteWarn($"{r.Name}: недоступен (нужен обход)");
        }
        if (allOk && !silent)
        {
            ConsoleMenu.WriteOk("Все сайты доступны без обхода!");
            if (!ConsoleMenu.Confirm("Всё равно продолжить установку?")) { ConsoleMenu.PauseAny(); return; }
        }

        // Выбор стратегии
        var batFiles = StrategyReader.GetStrategyFiles(RootDir);
        if (batFiles.Length == 0)
        {
            ConsoleMenu.WriteError("Не найдены файлы general*.bat в папке strategies/");
            if (!silent) ConsoleMenu.PauseAny();
            return;
        }

        string? chosenBat = null;
        if (!string.IsNullOrWhiteSpace(forcedStrategy))
            chosenBat = batFiles.FirstOrDefault(f =>
                f.Name.Contains(forcedStrategy, StringComparison.OrdinalIgnoreCase))?.FullName;

        if (chosenBat == null && !silent)
        {
            Console.WriteLine();
            ConsoleMenu.WriteSeparator();
            ConsoleMenu.WriteInfo($"Найдено конфигов: {batFiles.Length}");
            ConsoleMenu.WriteSeparator();
            Console.WriteLine("  [1]  Тест стратегий — автоматический выбор лучшего конфига");
            Console.WriteLine("  [2]  Ручной выбор конфига из списка");
            Console.WriteLine("  [3]  Отмена");
            Console.WriteLine();
            var modeChoice = ConsoleMenu.Prompt("  Введите номер (1/2/3)", "1");
            switch (modeChoice)
            {
                case "1":
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.WriteLine("  ╔═══════════════════════════════════════════╗");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine("  ║  ТЕСТ СТРАТЕГИЙ                           ║");
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("  ║                                           ║");
                    Console.WriteLine("  ║  [1] Стандартный — HTTP/TLS/ping тесты    ║");
                    Console.WriteLine("  ║      (доступность сайтов, score)          ║");
                    Console.WriteLine("  ║                                           ║");
                    Console.WriteLine("  ║  [2] DPI тест — TCP 16-20 KB freeze       ║");
                    Console.WriteLine("  ║      (curl payload, паттерн блокировки)   ║");
                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.WriteLine("  ╚═══════════════════════════════════════════╝");
                    Console.ResetColor();
                    Console.WriteLine();
                    var testMode = ConsoleMenu.Prompt("  Выберите режим (1/2)", "1");

                    if (testMode == "1")
                    {
                        // Стандартный тест
                        var targets = StrategyTester.LoadTargets(RootDir, Cfg.Diagnostics.CheckTargets);
                        var selectedConfigs = StrategyTester.SelectConfigs(batFiles);
                        var allResults = await StrategyTester.RunStandardTestsAsync(RootDir, selectedConfigs, targets, winwsExe);
                        var analytics = StrategyTester.ComputeAnalytics(allResults);
                        StrategyTester.PrintAnalytics(analytics);
                        StrategyTester.SaveStandardResults(RootDir, allResults, analytics);
                        var bestName = StrategyTester.GetBestConfig(analytics);
                        if (bestName != null)
                        {
                            var bestFile = batFiles.FirstOrDefault(f => f.Name == bestName);
                            if (bestFile != null) { chosenBat = bestFile.FullName; ConsoleMenu.WriteOk($"Лучшая: {bestName}"); }
                        }
                    }
                    else if (testMode == "2")
                    {
                        // DPI тест
                        var selectedConfigs = StrategyTester.SelectConfigs(batFiles);
                        
                        // Backup ipset and switch to "any" for testing
                        IpsetTestHelper.SwitchToAny(ListsDir);
                        IpsetTestHelper.SetFlag(RootDir);
                        var winwsSnapshot = WinWsSnapshot.Capture();

                        // Load DPI suite
                        ConsoleMenu.WriteStep("Загрузка DPI suite...");
                        var dpiTargets = await DpiChecker.GetSuiteAsync();
                        if (dpiTargets.Count == 0)
                        {
                            ConsoleMenu.WriteError("Не удалось загрузить DPI suite");
                            IpsetTestHelper.Restore(ListsDir);
                            IpsetTestHelper.RemoveFlag(RootDir);
                            break;
                        }
                        ConsoleMenu.WriteOk($"Загружено {dpiTargets.Count} целей");

                        var curlPath = File.Exists(Path.Combine(BinDir, "curl.exe")) 
                            ? Path.Combine(BinDir, "curl.exe") : "curl.exe";

                        var allDpiResults = new List<(string Config, List<DpiTargetResult> Results)>();
                        ConsoleMenu.WriteWarn("DPI тесты займут несколько минут...");

                        for (int i = 0; i < selectedConfigs.Length; i++)
                        {
                            var file = selectedConfigs[i];
                            Console.ForegroundColor = ConsoleColor.DarkCyan;
                            Console.WriteLine($"\n  [{i + 1}/{selectedConfigs.Length}] {file.Name}");
                            Console.WriteLine("  " + new string('─', 56));
                            Console.ResetColor();

                            ProcessManager.KillAll();
                            await Task.Delay(500);

                            // Launch strategy via bat file
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                                $"/c \"{file.FullName}\"") { WorkingDirectory = RootDir, WindowStyle = System.Diagnostics.ProcessWindowStyle.Minimized, UseShellExecute = true });
                            await Task.Delay(5000);

                            var dpiResults = await DpiChecker.RunSuiteAsync(dpiTargets, curlPath);
                            allDpiResults.Add((file.Name, dpiResults));

                            ProcessManager.KillAll();
                            await Task.Delay(500);
                        }

                        // Restore
                        IpsetTestHelper.Restore(ListsDir);
                        IpsetTestHelper.RemoveFlag(RootDir);
                        WinWsSnapshot.Restore(winwsSnapshot);

                        // Вывод результатов и выбор лучшего
                        string? bestDpiConfig = null;
                        int bestDpiScore = -1;
                        foreach (var (config, results) in allDpiResults)
                        {
                            int okCount = results.Count(r => !r.WarnDetected);
                            int blocked = results.Count(r => r.WarnDetected);
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine($"\n  {config}: OK={okCount} BLOCKED={blocked} / {results.Count}");
                            Console.ResetColor();
                            foreach (var r in results)
                            {
                                var color = r.WarnDetected ? ConsoleColor.Red : ConsoleColor.Green;
                                Console.ForegroundColor = color;
                                var status = r.WarnDetected ? "LIKELY_BLOCKED" : "OK";
                                Console.WriteLine($"    {r.TargetId} ({r.Provider}): {status}");
                            }
                            Console.ResetColor();
                            if (okCount > bestDpiScore) { bestDpiScore = okCount; bestDpiConfig = config; }
                        }

                        if (bestDpiConfig != null)
                        {
                            var bestFile = batFiles.FirstOrDefault(f => f.Name == bestDpiConfig);
                            if (bestFile != null) { chosenBat = bestFile.FullName; ConsoleMenu.WriteOk($"Лучшая (DPI): {bestDpiConfig}"); }
                        }
                    }
                    break;
                case "2":
                    Console.WriteLine();
                    for (int i = 0; i < batFiles.Length; i++) Console.WriteLine($"   [{i + 1}] {batFiles[i].Name}");
                    var pick = ConsoleMenu.Prompt("  Введите номер конфига", "1");
                    if (int.TryParse(pick, out var pidx) && pidx >= 1 && pidx <= batFiles.Length)
                        chosenBat = batFiles[pidx - 1].FullName;
                    break;
                default:
                    ConsoleMenu.WriteInfo("Установка отменена");
                    ConsoleMenu.PauseAny(); return;
            }
        }

        if (chosenBat == null)
        {
            ConsoleMenu.WriteError("Конфиг не выбран");
            if (!silent) ConsoleMenu.PauseAny();
            return;
        }

        // Установка службы
        try
        {
            var gf2 = GameFilter.Get(UtilsDir);
            var wArgs = StrategyReader.ParseArgs(chosenBat, BinDir, ListsDir, gf2.Tcp, gf2.Udp);
            if (string.IsNullOrWhiteSpace(wArgs))
            {
                ConsoleMenu.WriteError($"Не удалось извлечь аргументы из {Path.GetFileName(chosenBat)}");
                ConsoleMenu.WriteInfo("Попробуйте другой конфиг или проверьте формат bat-файла");
                if (!silent) ConsoleMenu.PauseAny();
                return;
            }
            ConsoleMenu.WriteStep($"Установка службы Windows: {Path.GetFileName(chosenBat)}");
            Logger.Info($"Аргументы winws: {wArgs}");
            WinServiceManager.Install("zapret", "zapret", "Zapret DPI bypass", $"\"{winwsExe}\" {wArgs}");
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"System\CurrentControlSet\Services\zapret");
                key?.SetValue("zapret-discord-youtube", Path.GetFileNameWithoutExtension(chosenBat));
            }
            catch { }
            ConsoleMenu.WriteOk("Служба 'zapret' установлена и запущена");
        }
        catch (Exception ex)
        {
            ConsoleMenu.WriteError($"Ошибка установки службы: {ex.Message}");
            Logger.Error($"Service install failed: {ex}");
            if (!silent) ConsoleMenu.PauseAny();
            return;
        }

        // Пост-проверка
        if (!silent)
        {
            ConsoleMenu.WriteStep("Проверка доступности С обходом (ждём 5 сек...)");
            await Task.Delay(5000);
            var post = await AccessChecker.CheckAllAsync(Cfg.Diagnostics.CheckTargets);
            foreach (var r in post)
            {
                if (r.Reachable) ConsoleMenu.WriteOk($"{r.Name}: {r.Detail}");
                else             ConsoleMenu.WriteWarn($"{r.Name}: всё ещё недоступен");
            }
            int okCnt = post.Count(r => r.Reachable);
            Console.WriteLine();
            if (okCnt > post.Count / 2)  ConsoleMenu.WriteOk($"ZAPRET РАБОТАЕТ! {okCnt}/{post.Count} ресурсов доступны");
            else if (okCnt > 0)           ConsoleMenu.WriteWarn($"Частично: {okCnt}/{post.Count}. Попробуйте другую стратегию.");
            else                          ConsoleMenu.WriteError("Ни один ресурс не работает. Запустите --menu → 11 (тест стратегий).");

            ShowBalloon("Zapret", $"Служба установлена: {Path.GetFileNameWithoutExtension(chosenBat)}");
            Console.WriteLine();
            ConsoleMenu.WriteInfo("Дальнейшие команды:");
            ConsoleMenu.WriteInfo("  zapret-manager --menu       — сервисное меню");
            ConsoleMenu.WriteInfo("  zapret-manager --menu → 10 — диагностика");
            ConsoleMenu.WriteInfo("  zapret-manager --menu → 11 — тест всех конфигов");
            Console.WriteLine();
            ConsoleMenu.WriteInfo("Нажмите любую клавишу для выхода...");
            Console.ReadKey(true);
        }
        Logger.Info("=== Установка завершена ===");
    }

    static async Task RunRemoveAsync(bool silent = false)
    {
        Console.Clear();
        ConsoleMenu.WriteHeader("УДАЛЕНИЕ ZAPRET");
        ProcessManager.KillAll();
        foreach (var svc in new[] { "zapret", "WinDivert", "WinDivert14" })
        {
            WinServiceManager.Stop(svc);
            if (WinServiceManager.Remove(svc)) ConsoleMenu.WriteOk($"Служба удалена: {svc}");
            else ConsoleMenu.WriteInfo($"{svc}: не установлена");
        }
        ConsoleMenu.WriteOk("Ваши списки в папке lists/ сохранены.");
        if (!silent) ConsoleMenu.PauseAny();
        await Task.CompletedTask;
    }

    static async Task RunTestAndInstallAsync()
    {
        Console.Clear();
        ConsoleMenu.WriteHeader("ТЕСТ СТРАТЕГИЙ И УСТАНОВКА");
        var winwsExe = Path.Combine(BinDir, "winws.exe");
        var files = StrategyReader.GetStrategyFiles(RootDir);
        if (files.Length == 0)
        {
            ConsoleMenu.WriteError("Стратегии general*.bat не найдены");
            ConsoleMenu.PauseAny(); return;
        }

        var targets = StrategyTester.LoadTargets(RootDir, Cfg.Diagnostics.CheckTargets);
        var allResults = await StrategyTester.RunStandardTestsAsync(RootDir, files, targets, winwsExe);
        var analytics = StrategyTester.ComputeAnalytics(allResults);
        StrategyTester.PrintAnalytics(analytics);
        StrategyTester.SaveStandardResults(RootDir, allResults, analytics);

        var bestConfig = StrategyTester.GetBestConfig(analytics);
        if (bestConfig != null)
        {
            var bestFile = files.FirstOrDefault(f => f.Name == bestConfig);
            if (bestFile != null)
            {
                ConsoleMenu.WriteStep($"Установка лучшей стратегии: {bestConfig}");
                var gf = GameFilter.Get(UtilsDir);
                var wa = StrategyReader.ParseArgs(bestFile.FullName, BinDir, ListsDir, gf.Tcp, gf.Udp);
                WinServiceManager.Install("zapret", "zapret", "Zapret DPI bypass", $"\"{winwsExe}\" {wa}");
                ConsoleMenu.WriteOk("Служба zapret установлена!");
                await Task.Delay(5000);
                var post = await AccessChecker.CheckAllAsync(Cfg.Diagnostics.CheckTargets);
                foreach (var r in post)
                {
                    if (r.Reachable) ConsoleMenu.WriteOk($"{r.Name}: {r.Detail}");
                    else             ConsoleMenu.WriteWarn($"{r.Name}: недоступен");
                }
            }
        }
        else ConsoleMenu.WriteInfo("Стратегия для установки не выбрана");
        ConsoleMenu.PauseAny();
    }

    static void ShowBalloon(string title, string text)
    {
        try
        {
            var t = new System.Threading.Thread(() =>
            {
                using var icon = new System.Windows.Forms.NotifyIcon();
                icon.Icon = System.Drawing.SystemIcons.Information;
                icon.Visible = true;
                icon.BalloonTipTitle = title;
                icon.BalloonTipText  = text;
                icon.ShowBalloonTip(5000);
                System.Threading.Thread.Sleep(5500);
                icon.Visible = false;
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }
        catch { }
    }

    static bool IsInPath(string exe)
    {
        try
        {
            var r = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("where", exe)
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            r?.WaitForExit(1000);
            return r?.ExitCode == 0;
        }
        catch { return false; }
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

    static string ReadLocalCoreVersion()
    {
        var vf = Path.Combine(BinDir, "version.txt");
        if (File.Exists(vf))
        {
            var ver = File.ReadAllText(vf).Trim();
            if (!string.IsNullOrEmpty(ver)) return ver;
        }
        return "не установлен";
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

    /// <summary>Sync lists/ → publish/lists/ so the publish directory stays up to date.</summary>
    static void SyncPublishLists()
    {
        var publishLists = Path.Combine(RootDir, "publish", "lists");
        if (!Directory.Exists(publishLists)) return; // publish/ doesn't exist, skip

        try
        {
            foreach (var file in Directory.EnumerateFiles(ListsDir, "*.txt"))
            {
                var dest = Path.Combine(publishLists, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
            }
            Logger.Info("publish/lists/ синхронизирован с lists/");
        }
        catch (Exception ex) { Logger.Warn($"Синхронизация publish/lists не удалась: {ex.Message}"); }
    }
}
