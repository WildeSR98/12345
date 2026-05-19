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

    // ── SETUP WIZARD (mirrors autosetup.ps1 flow) ─────────────────────────────
    static async Task RunSetupAsync(string[]? args = null)
    {
        bool silent = args?.Contains("--silent") == true;
        string? forcedStrategy = null;
        for (int i = 0; i < (args?.Length ?? 0) - 1; i++)
            if (args![i] == "--strategy") { forcedStrategy = args[i + 1]; break; }

        Console.Clear();
        Console.Title = "Zapret Auto-Setup";
        ConsoleMenu.WriteHeader("ZAPRET AUTO-SETUP v2.1");
        ConsoleMenu.WriteInfo($"Рабочая папка: {RootDir}");
        ConsoleMenu.WriteInfo($".NET: {Environment.Version}");

        // Фоновая проверка обновлений
        var updateTask = GitHubUpdater.CheckZapretCoreAsync(Cfg, RootDir);

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

        // Результат фоновой проверки обновлений
        try
        {
            var (remote, local) = await updateTask;
            if (remote != null && remote != local)
            {
                ConsoleMenu.WriteWarn($"Доступна новая версия zapret: {remote} (у вас: {local ?? "?"})");
                if (!silent && ConsoleMenu.Confirm("Обновить стратегии из апстрима?"))
                {
                    try
                    {
                        var archiveUrl = Cfg.Repositories.ZapretCore.ArchiveUrl;
                        var count = await StrategyUpdater.UpdateFromUpstreamAsync(RootDir, archiveUrl, remote);
                        ConsoleMenu.WriteOk($"Стратегии обновлены до {remote} (файлов: {count})");
                    }
                    catch (Exception ex)
                    {
                        ConsoleMenu.WriteError($"Не удалось обновить стратегии: {ex.Message}");
                        Logger.Error($"StrategyUpdater: {ex}");
                    }
                }
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
        if (!string.IsNullOrWhiteSpace(hostsUrl)) await HostsUpdater.CheckAndUpdate(hostsUrl);
        else ConsoleMenu.WriteInfo("URL hosts не настроен в config.json — пропуск");

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
            Console.WriteLine("  [1]  Быстрый тест — протестировать ВСЕ конфиги и выбрать лучший");
            Console.WriteLine("  [2]  Ручной выбор конфига из списка");
            Console.WriteLine("  [3]  Отмена");
            Console.WriteLine();
            var modeChoice = ConsoleMenu.Prompt("  Введите номер (1/2/3)", "1");
            switch (modeChoice)
            {
                case "1":
                    var scores = await StrategyTester.RunAllAsync(RootDir, Cfg.Diagnostics.CheckTargets, winwsExe);
                    StrategyTester.PrintSummary(scores);
                    var best = StrategyTester.GetBest(scores);
                    if (best != null) { chosenBat = best.FullPath; ConsoleMenu.WriteOk($"Лучшая: {best.Name}"); }
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
        var gf2 = GameFilter.Get(UtilsDir);
        var wArgs = StrategyReader.ParseArgs(chosenBat, BinDir, ListsDir, gf2.Tcp, gf2.Udp);
        ConsoleMenu.WriteStep($"Установка службы Windows: {Path.GetFileName(chosenBat)}");
        WinServiceManager.Install("zapret", "zapret", "Zapret DPI bypass", $"\"{winwsExe}\" {wArgs}");
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"System\CurrentControlSet\Services\zapret");
            key?.SetValue("zapret-discord-youtube", Path.GetFileNameWithoutExtension(chosenBat));
        }
        catch { }
        ConsoleMenu.WriteOk("Служба 'zapret' установлена и запущена");

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
        var scores = await StrategyTester.RunAllAsync(RootDir, Cfg.Diagnostics.CheckTargets, winwsExe);
        StrategyTester.PrintSummary(scores);
        var best = StrategyTester.GetBest(scores);
        if (best != null)
        {
            ConsoleMenu.WriteStep($"Установка лучшей стратегии: {best.Name}");
            var gf = GameFilter.Get(UtilsDir);
            var wa = StrategyReader.ParseArgs(best.FullPath, BinDir, ListsDir, gf.Tcp, gf.Udp);
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
