using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZapretManager.Core;
using ZapretManager.Lists;
using ZapretManager.UI;

namespace ZapretManager.Updates;

public static class GitHubUpdater
{
    private static readonly HttpClient _http = new();

    static GitHubUpdater()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("zapret-manager/2.0");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    // ── Manager self-update (WildeSR98/12345) ─────────────────────────────────

    /// <summary>Check for manager updates via GitHub Releases API.</summary>
    public static async Task<(string? RemoteVersion, string? LocalVersion, string? DownloadUrl)>
        CheckManagerUpdateAsync(AppConfig cfg, string rootDir)
    {
        var releaseApi = cfg.Repositories.Scripts12345.ReleaseApi;
        if (string.IsNullOrWhiteSpace(releaseApi)) return (null, null, null);

        // Local version
        var localVersion = ReadManagerVersion(rootDir);

        // Remote version from GitHub Releases
        string? remoteVersion = null;
        string? downloadUrl = null;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, releaseApi);
            req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                { NoCache = true };
            var resp = await _http.SendAsync(req);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Logger.Info("GitHub Releases не найдены для WildeSR98/12345");
                return (null, localVersion, null);
            }

            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            remoteVersion = root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');

            // Find zip asset
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }

            // Fallback to zipball_url
            if (downloadUrl == null && root.TryGetProperty("zipball_url", out var zipball))
                downloadUrl = zipball.GetString();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Проверка обновлений manager не удалась: {ex.Message}");
        }

        return (remoteVersion, localVersion, downloadUrl);
    }

    /// <summary>Download and apply manager update via bat-script updater.</summary>
    public static async Task<bool> UpdateManagerAsync(string downloadUrl, string rootDir,
        string newVersion)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"zapret_update_{Guid.NewGuid():N}");
        var zipPath = Path.Combine(Path.GetTempPath(), $"zapret_update_{Guid.NewGuid():N}.zip");

        try
        {
            // Download
            ConsoleMenu.StartSpinner("Скачивание обновления manager...");
            var response = await _http.GetAsync(downloadUrl);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(zipPath, bytes);
            ConsoleMenu.StopSpinner(true, $"Загружено {bytes.Length / 1024} KB");

            // Extract
            ConsoleMenu.StartSpinner("Распаковка...");
            Directory.CreateDirectory(tempDir);
            ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);
            ConsoleMenu.StopSpinner(true, "Распаковано");

            // Find the actual content directory (GitHub archives have a nested folder)
            var extractedDir = tempDir;
            var subdirs = Directory.GetDirectories(tempDir);
            if (subdirs.Length == 1) extractedDir = subdirs[0];

            // Look for publish/ folder inside extracted archive
            var publishSrc = Path.Combine(extractedDir, "publish");
            if (!Directory.Exists(publishSrc))
            {
                // Maybe it's a flat release zip, use extractedDir directly
                publishSrc = extractedDir;
            }

            // Files to protect (never overwrite)
            var protectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "config.json"
            };
            var protectedPatterns = new[] { "-user.txt", "game_filter.enabled",
                "check_updates.enabled", "manager_version.txt" };

            // Generate update.bat
            var batPath = Path.Combine(Path.GetTempPath(), $"zapret_update_{Guid.NewGuid():N}.bat");
            var exePath = Path.Combine(rootDir, "zapret-manager.exe");

            // Build exclusion list for xcopy
            var batContent = $"""
                @echo off
                chcp 65001 >nul
                echo Ожидание завершения zapret-manager...
                timeout /t 3 /nobreak >nul

                echo Копирование файлов обновления...
                
                rem Copy files from publish source, skip protected files
                for /r "{publishSrc}" %%f in (*) do (
                    set "relpath=%%f"
                    set "relpath=!relpath:{publishSrc}\=!"
                    set "skip=0"
                    echo %%~nxf | findstr /i "config.json" >nul && set "skip=1"
                    echo %%~nxf | findstr /i "\-user.txt" >nul && set "skip=1"
                    echo %%~nxf | findstr /i "game_filter.enabled" >nul && set "skip=1"
                    echo %%~nxf | findstr /i "check_updates.enabled" >nul && set "skip=1"
                    echo %%~nxf | findstr /i "manager_version.txt" >nul && set "skip=1"
                    if !skip!==0 (
                        copy /y "%%f" "{rootDir}\%%~nxf" >nul 2>&1
                    )
                )

                rem Update version file
                echo {newVersion}> "{Path.Combine(rootDir, "utils", "manager_version.txt")}"
                
                echo Обновление завершено. Перезапуск...
                timeout /t 1 /nobreak >nul
                start "" "{exePath}" --menu
                
                rem Cleanup
                rd /s /q "{tempDir}" >nul 2>&1
                del /q "{zipPath}" >nul 2>&1
                del "%~f0"
                """;

            await File.WriteAllTextAsync(batPath, batContent, System.Text.Encoding.UTF8);

            ConsoleMenu.WriteOk("Обновление подготовлено. Перезапуск приложения...");

            // Launch bat and exit
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/c \"{batPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = rootDir
            });

            return true; // Caller should exit the app
        }
        catch (Exception ex)
        {
            ConsoleMenu.StopSpinner(false, $"Ошибка обновления: {ex.Message}");
            Logger.Error($"UpdateManagerAsync failed: {ex}");

            // Cleanup on failure
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }

            return false;
        }
    }

    // ── Zapret core file update (Flowseal/zapret-discord-youtube) ─────────────

    /// <summary>Check zapret core version (Flowseal/zapret-discord-youtube).</summary>
    public static async Task<(string? Remote, string? Local)> CheckZapretCoreAsync(
        AppConfig cfg, string rootDir)
    {
        var versionUrl = cfg.Repositories.ZapretCore.VersionUrl;
        if (string.IsNullOrWhiteSpace(versionUrl)) return (null, null);

        string? remote = null;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, versionUrl);
            req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                { NoCache = true };
            var resp = await _http.SendAsync(req);
            remote = (await resp.Content.ReadAsStringAsync()).Trim();
        }
        catch (Exception ex) { Logger.Warn($"Проверка версии zapret core не удалась: {ex.Message}"); }

        // Local version from bin/version.txt if exists
        var localVer = ReadLocalVersion(Path.Combine(rootDir, "bin"));
        return (remote, localVer);
    }

    /// <summary>Download and update zapret core files (bin, strategies, lists).</summary>
    public static async Task<bool> UpdateZapretCoreFilesAsync(AppConfig cfg, string rootDir)
    {
        var archiveUrl = cfg.Repositories.ZapretCore.ArchiveUrl;
        if (string.IsNullOrWhiteSpace(archiveUrl))
        {
            ConsoleMenu.WriteError("URL архива zapret core не настроен в config.json");
            return false;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"zapret_core_{Guid.NewGuid():N}");
        var zipPath = Path.Combine(Path.GetTempPath(), $"zapret_core_{Guid.NewGuid():N}.zip");

        try
        {
            // Create backup
            ConsoleMenu.WriteStep("Создание резервной копии...");
            BackupManager.Create(rootDir);

            // Download archive
            ConsoleMenu.StartSpinner("Скачивание файлов zapret core...");
            var response = await _http.GetAsync(archiveUrl);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(zipPath, bytes);
            ConsoleMenu.StopSpinner(true, $"Загружено {bytes.Length / 1024 / 1024} MB");

            // Extract
            ConsoleMenu.StartSpinner("Распаковка...");
            Directory.CreateDirectory(tempDir);
            ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);
            ConsoleMenu.StopSpinner(true, "Распаковано");

            // Find nested folder (GitHub archives: zapret-discord-youtube-main/)
            var extractedDir = tempDir;
            var subdirs = Directory.GetDirectories(tempDir);
            if (subdirs.Length == 1) extractedDir = subdirs[0];

            int updated = 0;

            // 1. Update bin/ (winws.exe, dll, sys, bin patterns)
            ConsoleMenu.WriteStep("Обновление bin/ (winws.exe, драйверы, паттерны)...");
            var srcBin = Path.Combine(extractedDir, "bin");
            var dstBin = Path.Combine(rootDir, "bin");
            if (Directory.Exists(srcBin))
            {
                Directory.CreateDirectory(dstBin);
                foreach (var file in Directory.EnumerateFiles(srcBin))
                {
                    var dest = Path.Combine(dstBin, Path.GetFileName(file));
                    File.Copy(file, dest, overwrite: true);
                    updated++;
                }
                ConsoleMenu.WriteOk($"bin/: {updated} файлов обновлено");
            }

            // 2. Update strategies/ (general*.bat with path adaptation)
            ConsoleMenu.WriteStep("Обновление strategies/ (general*.bat)...");
            var dstStrategies = Path.Combine(rootDir, "strategies");
            Directory.CreateDirectory(dstStrategies);
            int stratCount = 0;
            foreach (var file in Directory.EnumerateFiles(extractedDir, "general*.bat"))
            {
                var content = await File.ReadAllTextAsync(file, System.Text.Encoding.UTF8);
                // Adapt paths: original uses %~dp0bin\ / %~dp0lists\ (root-relative)
                // Our strategies/ folder is one level deeper, so we need %~dp0..\bin\ etc.
                content = AdaptStrategyPaths(content);
                var dest = Path.Combine(dstStrategies, Path.GetFileName(file));
                await File.WriteAllTextAsync(dest, content, System.Text.Encoding.UTF8);
                stratCount++;
            }
            ConsoleMenu.WriteOk($"strategies/: {stratCount} стратегий обновлено");

            // 3. Update lists/ (replace with origin, keep user additions)
            ConsoleMenu.WriteStep("Обновление lists/ (замена из оригинала + сохранение пользовательских)...");
            var srcLists = Path.Combine(extractedDir, "lists");
            var dstLists = Path.Combine(rootDir, "lists");
            if (Directory.Exists(srcLists))
            {
                Directory.CreateDirectory(dstLists);
                int listCount = 0;
                foreach (var file in Directory.EnumerateFiles(srcLists, "*.txt"))
                {
                    var fileName = Path.GetFileName(file);
                    // Skip user files
                    if (fileName.Contains("-user", StringComparison.OrdinalIgnoreCase)) continue;

                    var destPath = Path.Combine(dstLists, fileName);
                    var originLines = (await File.ReadAllTextAsync(file))
                        .Split('\n').Select(l => l.TrimEnd('\r'));
                    var listUpdated = ListMerger.ReplaceWithOrigin(destPath, originLines);
                    ListMerger.WriteUtf8(destPath, listUpdated);
                    listCount++;
                }
                ConsoleMenu.WriteOk($"lists/: {listCount} списков обновлено");
            }

            // 4. Update version.txt
            var srcVersion = Path.Combine(extractedDir, ".service", "version.txt");
            if (File.Exists(srcVersion))
            {
                var version = (await File.ReadAllTextAsync(srcVersion)).Trim();
                Directory.CreateDirectory(dstBin);
                await File.WriteAllTextAsync(Path.Combine(dstBin, "version.txt"), version);
                ConsoleMenu.WriteOk($"Версия zapret core обновлена: {version}");
            }

            // 5. Update ipset from .service/ipset-service.txt
            var srcIpset = Path.Combine(extractedDir, ".service", "ipset-service.txt");
            if (File.Exists(srcIpset))
            {
                var ipsetDest = Path.Combine(dstLists, "ipset-all.txt");
                var originIpset = (await File.ReadAllTextAsync(srcIpset))
                    .Split('\n').Select(l => l.TrimEnd('\r'));
                var updatedIpset = ListMerger.ReplaceWithOrigin(ipsetDest, originIpset);
                ListMerger.WriteUtf8(ipsetDest, updatedIpset);
                ConsoleMenu.WriteOk($"ipset-all.txt обновлён ({updatedIpset.Length} строк)");
            }

            // 6. Update hosts from .service/hosts
            var srcHosts = Path.Combine(extractedDir, ".service", "hosts");
            if (File.Exists(srcHosts))
            {
                ConsoleMenu.WriteStep("Проверка файла hosts из .service/hosts...");
                var hostsContent = await File.ReadAllTextAsync(srcHosts);
                var hostsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"drivers\etc\hosts");
                var existingHosts = File.Exists(hostsPath) 
                    ? File.ReadAllText(hostsPath) : "";
                var hostsLines = hostsContent.Split('\n')
                    .Select(l => l.TrimEnd('\r').Trim())
                    .Where(l => l.Length > 0);
                if (hostsLines.Any(l => !existingHosts.Contains(l, StringComparison.OrdinalIgnoreCase)))
                    ConsoleMenu.WriteWarn("Файл hosts требует обновления — используйте пункт меню 8");
                else
                    ConsoleMenu.WriteOk("Файл hosts актуален");
            }

            // 7. Sync publish/lists if publish dir exists
            var publishLists = Path.Combine(rootDir, "publish", "lists");
            if (Directory.Exists(publishLists))
            {
                foreach (var file in Directory.EnumerateFiles(dstLists, "*.txt"))
                {
                    var dest = Path.Combine(publishLists, Path.GetFileName(file));
                    File.Copy(file, dest, overwrite: true);
                }
                ConsoleMenu.WriteOk("publish/lists/ синхронизирован");
            }

            ConsoleMenu.WriteOk("Файлы zapret core успешно обновлены!");
            ConsoleMenu.WriteWarn("Перезапустите службу zapret для применения изменений");

            return true;
        }
        catch (Exception ex)
        {
            ConsoleMenu.StopSpinner(false, $"Ошибка: {ex.Message}");
            Logger.Error($"UpdateZapretCoreFilesAsync failed: {ex}");
            return false;
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>Check 12345 script updates via commit API.</summary>
    public static async Task<(string? Remote, string? Local)> CheckScriptsAsync(AppConfig cfg)
    {
        var commitApi = cfg.Repositories.Scripts12345.CommitApi;
        if (string.IsNullOrWhiteSpace(commitApi)) return (null, null);

        try
        {
            var json = await _http.GetStringAsync(commitApi);
            var doc  = JsonDocument.Parse(json);
            var sha  = doc.RootElement[0].GetProperty("sha").GetString()?[..7];
            return (sha, null);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Проверка обновлений скриптов не удалась: {ex.Message}");
            return (null, null);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? ReadLocalVersion(string dir)
    {
        var vf = Path.Combine(dir, "version.txt");
        if (!File.Exists(vf)) return null;
        return File.ReadAllText(vf).Trim();
    }

    public static string? ReadManagerVersion(string rootDir)
    {
        var vf = Path.Combine(rootDir, "utils", "manager_version.txt");
        if (!File.Exists(vf)) return null;
        return File.ReadAllText(vf).Trim();
    }

    public static void WriteManagerVersion(string rootDir, string version)
    {
        var utilsDir = Path.Combine(rootDir, "utils");
        Directory.CreateDirectory(utilsDir);
        File.WriteAllText(Path.Combine(utilsDir, "manager_version.txt"), version);
    }

    /// <summary>
    /// Adapt paths in strategy bat files for the strategies/ subfolder.
    /// Original uses %~dp0bin\ (root), we need %~dp0..\bin\ (one level up).
    /// </summary>
    private static string AdaptStrategyPaths(string content)
    {
        // Add parent dir navigation for cd and call
        content = content.Replace("cd /d \"%~dp0\"", "cd /d \"%~dp0..\"");
        content = content.Replace("call \"%~dp0service.bat\"", "call \"%~dp0..\\service.bat\"");

        // Adapt BIN and LISTS variable definitions
        content = content.Replace("set \"BIN=%~dp0bin\\\"", "set \"BIN=%~dp0..\\bin\\\"");
        content = content.Replace("set \"LISTS=%~dp0lists\\\"", "set \"LISTS=%~dp0..\\lists\\\"");

        // Adapt inline path references
        content = content.Replace("%~dp0bin\\", "%~dp0..\\bin\\");
        content = content.Replace("%~dp0lists\\", "%~dp0..\\lists\\");

        return content;
    }
}

public static class BackupManager
{
    public static string Create(string rootDir, int keepCount = 5)
    {
        var backupDir = Path.Combine(rootDir, "backups");
        Directory.CreateDirectory(backupDir);

        var ts   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var dest = Path.Combine(backupDir, $"backup_{ts}");
        Directory.CreateDirectory(dest);

        var patterns = new[] { "bin", "lists", "strategies", "utils" };
        foreach (var pattern in patterns)
        {
            var src = Path.Combine(rootDir, pattern);
            if (!Directory.Exists(src) && !File.Exists(src)) continue;

            var isDir = Directory.Exists(src);
            if (isDir)
                CopyDir(src, Path.Combine(dest, pattern));
            else
                File.Copy(src, Path.Combine(dest, pattern), overwrite: true);
        }

        // config.json
        var cfg = Path.Combine(rootDir, "config.json");
        if (File.Exists(cfg)) File.Copy(cfg, Path.Combine(dest, "config.json"), true);

        Logger.Ok($"Backup создан: {dest}");

        // Rotation
        Rotate(backupDir, keepCount);

        return dest;
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            // Skip user lists and logs
            if (file.Contains("-user", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Contains("\\logs\\", StringComparison.OrdinalIgnoreCase)) continue;

            var rel  = Path.GetRelativePath(src, file);
            var dest = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static void Rotate(string backupDir, int keepCount)
    {
        var dirs = new DirectoryInfo(backupDir)
            .GetDirectories("backup_*")
            .OrderByDescending(d => d.CreationTime)
            .Skip(keepCount);

        foreach (var d in dirs)
        {
            try { d.Delete(recursive: true); Logger.Info($"Старый backup удалён: {d.Name}"); }
            catch { }
        }
    }
}
