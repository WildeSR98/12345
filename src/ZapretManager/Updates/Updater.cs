using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using ZapretManager.Core;
using ZapretManager.UI;

namespace ZapretManager.Updates;

public static class GitHubUpdater
{
    private static readonly HttpClient _http = new();

    static GitHubUpdater()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("zapret-manager/2.0");
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

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

    private static string? ReadLocalVersion(string dir)
    {
        var vf = Path.Combine(dir, "version.txt");
        if (!File.Exists(vf)) return null;
        return File.ReadAllText(vf).Trim();
    }
}

/// <summary>
/// Downloads upstream Flowseal/zapret-discord-youtube archive and refreshes
/// our local <c>strategies/</c> folder. Upstream ships general*.bat in the
/// archive root with paths relative to that root (<c>%~dp0bin\</c>,
/// <c>%~dp0lists\</c>, <c>call service.bat ...</c>); our fork moved the bats
/// into a <c>strategies/</c> subfolder, so each line is rewritten to reach the
/// repo root via <c>%~dp0..\</c>. We only touch general*.bat and
/// bin/version.txt — everything else (bin/, lists/, service.bat) stays.
/// </summary>
public static class StrategyUpdater
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };

    static StrategyUpdater()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("zapret-manager/2.0");
    }

    public static async Task<int> UpdateFromUpstreamAsync(
        string rootDir, string? archiveUrl, string? remoteVersion = null)
    {
        if (string.IsNullOrWhiteSpace(archiveUrl))
            throw new InvalidOperationException("archive_url не задан в config.json (repositories.zapret_core.archive_url)");

        var strategiesDir = Path.Combine(rootDir, "strategies");
        var binDir        = Path.Combine(rootDir, "bin");
        Directory.CreateDirectory(strategiesDir);
        Directory.CreateDirectory(binDir);

        var tmpZip = Path.Combine(Path.GetTempPath(), $"zapret_upstream_{Guid.NewGuid():N}.zip");
        var tmpDir = Path.Combine(Path.GetTempPath(), $"zapret_upstream_{Guid.NewGuid():N}");
        try
        {
            Logger.Info($"Скачивание архива: {archiveUrl}");
            var bytes = await _http.GetByteArrayAsync(archiveUrl);
            await File.WriteAllBytesAsync(tmpZip, bytes);

            Directory.CreateDirectory(tmpDir);
            ZipFile.ExtractToDirectory(tmpZip, tmpDir);

            // GitHub archive wraps everything in a single top-level dir
            // (e.g. zapret-discord-youtube-main/). Pick it up.
            var topLevel = Directory.EnumerateDirectories(tmpDir).FirstOrDefault()
                ?? throw new InvalidOperationException("Архив пуст или повреждён");

            var upstreamBats = Directory.EnumerateFiles(topLevel, "general*.bat", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p)
                .ToList();
            if (upstreamBats.Count == 0)
                throw new InvalidOperationException("В архиве не найдено ни одного general*.bat");

            int updated = 0;
            foreach (var srcPath in upstreamBats)
            {
                var name        = Path.GetFileName(srcPath);
                var dstPath     = Path.Combine(strategiesDir, name);
                var srcContent  = await File.ReadAllTextAsync(srcPath, System.Text.Encoding.UTF8);
                var transformed = TransformForSubfolder(srcContent);

                // UTF-8 without BOM, CRLF (matches .gitattributes and existing files).
                await File.WriteAllTextAsync(dstPath, transformed,
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                updated++;
            }

            // Mark the new local version so subsequent CheckZapretCoreAsync
            // calls return Remote == Local.
            if (!string.IsNullOrWhiteSpace(remoteVersion))
            {
                await File.WriteAllTextAsync(Path.Combine(binDir, "version.txt"),
                    remoteVersion.Trim() + "\r\n",
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            Logger.Ok($"Обновлено стратегий из апстрима: {updated}");
            return updated;
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Rewrites the upstream .bat header so it works from a strategies/
    /// subfolder. The transformations exactly mirror the diff between
    /// upstream Flowseal/zapret-discord-youtube general*.bat files and the
    /// versions originally committed to this repo (c2cafe2,
    /// "refactor: move 19 general*.bat strategies to strategies/ subfolder").
    /// </summary>
    public static string TransformForSubfolder(string content)
    {
        // Normalise line endings before processing, then re-emit as CRLF.
        content = content.Replace("\r\n", "\n").Replace("\r", "\n");

        // 1. cd /d "%~dp0"            → cd /d "%~dp0.."
        content = content.Replace(
            "cd /d \"%~dp0\"\n",
            "cd /d \"%~dp0..\"\n");

        // 2. call service.bat <X>     → call "%~dp0..\service.bat" <X>
        //    (four lines: status_zapret, check_updates, load_game_filter, load_user_lists)
        content = System.Text.RegularExpressions.Regex.Replace(
            content,
            @"^call service\.bat ",
            @"call ""%~dp0..\service.bat"" ",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        // 3. set "BIN=%~dp0bin\"      → set "BIN=%~dp0..\bin\"
        content = content.Replace(
            "set \"BIN=%~dp0bin\\\"",
            "set \"BIN=%~dp0..\\bin\\\"");

        // 4. set "LISTS=%~dp0lists\"  → set "LISTS=%~dp0..\lists\"
        content = content.Replace(
            "set \"LISTS=%~dp0lists\\\"",
            "set \"LISTS=%~dp0..\\lists\\\"");

        // 5. cd /d %BIN%              → GameFilter defaults + cd /d "%BIN%"
        content = content.Replace(
            "cd /d %BIN%\n",
            "if not defined GameFilterTCP set \"GameFilterTCP=12\"\n"
          + "if not defined GameFilterUDP set \"GameFilterUDP=12\"\n"
          + "cd /d \"%BIN%\"\n");

        return content.Replace("\n", "\r\n");
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
