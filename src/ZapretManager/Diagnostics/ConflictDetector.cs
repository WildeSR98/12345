using System.ServiceProcess;

namespace ZapretManager.Diagnostics;

public static class ConflictDetector
{
    private static readonly string[] KnownConflicts =
        { "GoodbyeDPI", "discordfix_zapret", "winws1", "winws2" };

    public static List<string> FindConflicts(IEnumerable<string>? extra = null)
    {
        var names = KnownConflicts.Concat(extra ?? Enumerable.Empty<string>()).Distinct();
        var found = new List<string>();
        foreach (var name in names)
        {
            try
            {
                using var svc = new ServiceController(name);
                _ = svc.Status; // throws if not found
                found.Add(name);
            }
            catch { /* service does not exist */ }
        }
        return found;
    }

    public static void RemoveConflicts(IEnumerable<string> services)
    {
        foreach (var name in services)
        {
            Service.WinServiceManager.Stop(name);
            Service.WinServiceManager.Remove(name);
        }
        // Also remove WinDivert
        foreach (var d in new[] { "WinDivert", "WinDivert14" })
        {
            Service.WinServiceManager.Stop(d);
            Service.WinServiceManager.Remove(d);
        }
    }
}
