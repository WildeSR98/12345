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
