using System.Diagnostics;

namespace ZapretManager.Service;

public static class ProcessManager
{
    public static bool IsRunning(string processName = "winws")
        => Process.GetProcessesByName(processName).Length > 0;

    public static void KillAll(string processName = "winws")
    {
        foreach (var p in Process.GetProcessesByName(processName))
        {
            try { p.Kill(); p.WaitForExit(2000); }
            catch { /* ignore */ }
        }
        Thread.Sleep(500);
    }

    public static Process? StartMinimized(string exePath, string args, string workDir)
    {
        var psi = new ProcessStartInfo(exePath, args)
        {
            WorkingDirectory = workDir,
            WindowStyle      = ProcessWindowStyle.Minimized,
            UseShellExecute  = true
        };
        return Process.Start(psi);
    }

    /// <summary>Get command line of a running process by name.</summary>
    public static IEnumerable<(int Pid, string CmdLine, string ExePath)> GetProcessInfo(string name)
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            string cmd = "", exe = "";
            try { exe = p.MainModule?.FileName ?? ""; } catch { }
            yield return (p.Id, cmd, exe);
        }
    }
}
