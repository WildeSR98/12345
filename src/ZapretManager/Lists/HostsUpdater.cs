using System.Net.Http;
using ZapretManager.Core;

namespace ZapretManager.Lists;

public static class HostsUpdater
{
    private static readonly string HostsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"drivers\etc\hosts");

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<bool> CheckAndUpdate(string hostsUrl)
    {
        string newContent;
        try
        {
            newContent = await _http.GetStringAsync(hostsUrl);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось скачать hosts: {ex.Message}");
            return false;
        }

        var newLines = newContent.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        if (newLines.Length == 0) return false;

        var existingContent = File.Exists(HostsPath)
            ? File.ReadAllText(HostsPath, System.Text.Encoding.UTF8)
            : "";

        var needsUpdate = newLines.Any(l => !existingContent.Contains(l, StringComparison.OrdinalIgnoreCase));

        if (!needsUpdate)
        {
            Logger.Ok("Файл hosts актуален");
            return false;
        }

        // Open both files for user to merge manually
        var tempPath = Path.Combine(Path.GetTempPath(), "zapret_hosts_new.txt");
        await File.WriteAllTextAsync(tempPath, newContent);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "notepad.exe", Arguments = $"\"{tempPath}\"", UseShellExecute = true
        });
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe", Arguments = $"/select,\"{HostsPath}\"", UseShellExecute = true
        });

        Logger.Warn("Файл hosts требует обновления — открыт в Блокноте");
        return true;
    }
}
