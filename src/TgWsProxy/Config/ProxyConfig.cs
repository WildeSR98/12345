using System.Text.Json;
using System.Text.Json.Serialization;

namespace TgWsProxy.Config;

public class ProxyConfig
{
    [JsonPropertyName("port")]    public int Port    { get; set; } = 1080;
    [JsonPropertyName("dc_ip")]   public List<string> DcIp { get; set; } = new() { "2:149.154.167.220", "4:149.154.167.220" };
    [JsonPropertyName("verbose")] public bool Verbose { get; set; }

    private static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TgWsProxy");
    public static readonly string ConfigPath = Path.Combine(AppDir, "config.json");
    public static readonly string LogPath    = Path.Combine(AppDir, "proxy.log");
    public static readonly string FirstRunMarker = Path.Combine(AppDir, ".first_run_done");

    public static void EnsureDir() => Directory.CreateDirectory(AppDir);

    public static ProxyConfig Load()
    {
        EnsureDir();
        if (!File.Exists(ConfigPath)) return new();
        try
        {
            var json = File.ReadAllText(ConfigPath, System.Text.Encoding.UTF8);
            return JsonSerializer.Deserialize<ProxyConfig>(json) ?? new();
        }
        catch { return new(); }
    }

    public void Save()
    {
        EnsureDir();
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json, System.Text.Encoding.UTF8);
    }

    /// <summary>Parse "dc:ip" list into dictionary.</summary>
    public Dictionary<int, string> ParseDcIp()
    {
        var result = new Dictionary<int, string>();
        foreach (var entry in DcIp)
        {
            var parts = entry.Split(':', 2);
            if (parts.Length == 2 && int.TryParse(parts[0], out var dc))
                result[dc] = parts[1].Trim();
        }
        return result;
    }
}
