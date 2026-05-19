using System.Net.Http;
using System.Net.NetworkInformation;
using ZapretManager.Core;

namespace ZapretManager.Diagnostics;

public record AccessResult(string Name, string Type, bool Reachable, string Detail);

public static class AccessChecker
{
    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    }) { Timeout = TimeSpan.FromSeconds(7) };

    public static async Task<List<AccessResult>> CheckAllAsync(IList<Core.CheckTarget> targets)
    {
        var tasks = targets.Select(t => CheckOneAsync(t));
        return (await Task.WhenAll(tasks)).ToList();
    }

    private static async Task<AccessResult> CheckOneAsync(Core.CheckTarget t)
    {
        if (t.Type == "ping")
        {
            var (ok, ms) = await PingAsync(t.Host);
            return new(t.Name, "ping", ok, ok ? $"{ms} мс" : "Timeout");
        }
        else
        {
            var (ok, code) = await HttpCheckAsync(t.Url);
            return new(t.Name, "url", ok, ok ? $"HTTP {code}" : "ERROR");
        }
    }

    public static async Task<(bool Ok, long Ms)> PingAsync(string host, int count = 2)
    {
        using var ping = new Ping();
        try
        {
            long total = 0;
            int success = 0;
            for (int i = 0; i < count; i++)
            {
                var reply = await ping.SendPingAsync(host, 3000);
                if (reply.Status == IPStatus.Success) { total += reply.RoundtripTime; success++; }
            }
            return success > 0 ? (true, total / success) : (false, 0);
        }
        catch { return (false, 0); }
    }

    public static async Task<(bool Ok, int Code)> HttpCheckAsync(string url)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Head, url);
            var resp = await _http.SendAsync(req);
            var code = (int)resp.StatusCode;
            return (code is >= 200 and < 400, code);
        }
        catch { return (false, 0); }
    }
}
