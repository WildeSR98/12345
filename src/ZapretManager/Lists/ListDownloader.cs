using System.Net.Http;
using ZapretManager.Core;

namespace ZapretManager.Lists;

public static class ListDownloader
{
    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    }) { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task DownloadAllAsync(
        IList<ListFileEntry> entries,
        string listsDir,
        string repoBase,
        Action<string, bool>? onResult = null)
    {
        Directory.CreateDirectory(listsDir);

        // Resolve remote URLs
        var tasks = entries.Select(async entry =>
        {
            var localPath = Path.Combine(listsDir, entry.Local);

            // User-only file: create stub if missing
            if (entry.User)
            {
                if (!File.Exists(localPath))
                {
                    ListMerger.WriteUtf8(localPath, new[] { entry.Stub });
                    onResult?.Invoke($"{entry.Local}: создан пользовательский файл", true);
                }
                return;
            }

            // No remote: create stub if missing
            if (string.IsNullOrWhiteSpace(entry.Remote))
            {
                if (!File.Exists(localPath))
                {
                    ListMerger.WriteUtf8(localPath, new[] { entry.Stub });
                    onResult?.Invoke($"{entry.Local}: создана заглушка", true);
                }
                return;
            }

            var url = entry.Remote.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? entry.Remote
                : $"{repoBase}/{entry.Remote}";

            try
            {
                var content = await _http.GetStringAsync(url);
                var originLines = content.Split('\n').Select(l => l.TrimEnd('\r'));
                var updated = ListMerger.ReplaceWithOrigin(localPath, originLines);
                ListMerger.WriteUtf8(localPath, updated);
                onResult?.Invoke($"{entry.Local}: {updated.Length} строк", true);
            }
            catch (Exception ex)
            {
                Logger.Warn($"{entry.Local}: загрузка не удалась ({ex.Message})");
                if (!File.Exists(localPath))
                {
                    ListMerger.WriteUtf8(localPath, new[] { entry.Stub });
                    onResult?.Invoke($"{entry.Local}: используется заглушка", false);
                }
                else
                {
                    onResult?.Invoke($"{entry.Local}: используется существующий файл", true);
                }
            }
        });

        await Task.WhenAll(tasks);
    }
}
