namespace ZapretManager.Service;

public record GameFilterPorts(string Tcp, string Udp, string Mode);

public static class GameFilter
{
    public static GameFilterPorts Get(string utilsDir)
    {
        var flag = Path.Combine(utilsDir, "game_filter.enabled");
        if (!File.Exists(flag)) return new("12", "12", "disabled");

        var mode = File.ReadAllText(flag).Trim().ToLowerInvariant();
        return mode switch
        {
            "all" => new("1024-65535", "1024-65535", "all"),
            "tcp" => new("1024-65535", "12", "tcp"),
            "udp" => new("12", "1024-65535", "udp"),
            _     => new("12", "12", "disabled")
        };
    }

    public static void Set(string utilsDir, string mode)
    {
        Directory.CreateDirectory(utilsDir);
        var flag = Path.Combine(utilsDir, "game_filter.enabled");
        if (mode == "disabled")
        {
            if (File.Exists(flag)) File.Delete(flag);
        }
        else
        {
            File.WriteAllText(flag, mode, System.Text.Encoding.ASCII);
        }
    }

    public static string StatusLabel(string utilsDir)
    {
        var p = Get(utilsDir);
        return p.Mode switch
        {
            "disabled" => "выкл",
            "all"      => "TCP + UDP",
            "tcp"      => "только TCP",
            "udp"      => "только UDP",
            _          => "?"
        };
    }
}
