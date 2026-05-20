using System.Runtime.InteropServices;

namespace ZapretManager.Service;

/// <summary>Windows Service management via P/Invoke — no sc.exe dependency.</summary>
public static class WinServiceManager
{
    // ── P/Invoke ──────────────────────────────────────────────────────────────
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateService(IntPtr scm, string name, string displayName,
        uint access, uint type, uint start, uint error,
        string binPath, string? group, IntPtr tag, string? deps,
        string? account, string? password);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartService(IntPtr svc, uint argc, string[]? argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr svc);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr svc, uint ctrl, ref SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr svc, ref SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig2(IntPtr svc,
        uint infoLevel, ref SERVICE_DESCRIPTIONW desc);

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint Type, CurrentState, ControlsAccepted, Win32ExitCode,
                    ServiceExitCode, CheckPoint, WaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVICE_DESCRIPTIONW
    {
        public string Description;
    }

    private const uint SC_MANAGER_ALL = 0xF003F;
    private const uint SERVICE_ALL    = 0xF01FF;
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x10;
    private const uint SERVICE_AUTO_START         = 0x02;
    private const uint SERVICE_DEMAND_START       = 0x03;
    private const uint SERVICE_ERROR_NORMAL       = 0x01;
    private const uint SERVICE_CONTROL_STOP       = 0x01;

    public enum ServiceState { NotInstalled, Stopped, Starting, Running, Stopping, Unknown }

    // ── Public API ────────────────────────────────────────────────────────────

    public static bool Install(string name, string displayName, string description,
        string binPathWithArgs, bool autoStart = true)
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_ALL);
        if (scm == IntPtr.Zero) return false;
        try
        {
            // Delete old if exists
            var old = OpenService(scm, name, SERVICE_ALL);
            if (old != IntPtr.Zero)
            {
                var st = new SERVICE_STATUS();
                ControlService(old, SERVICE_CONTROL_STOP, ref st);
                System.Threading.Thread.Sleep(500);
                DeleteService(old);
                CloseServiceHandle(old);
                System.Threading.Thread.Sleep(300);
            }

            var startType = autoStart ? SERVICE_AUTO_START : SERVICE_DEMAND_START;
            var svc = CreateService(scm, name, displayName, SERVICE_ALL,
                SERVICE_WIN32_OWN_PROCESS, startType, SERVICE_ERROR_NORMAL,
                binPathWithArgs, null, IntPtr.Zero, null, null, null);

            if (svc == IntPtr.Zero) return false;

            // Set description (SERVICE_CONFIG_DESCRIPTION = 1)
            var desc = new SERVICE_DESCRIPTIONW { Description = description };
            ChangeServiceConfig2(svc, 1, ref desc);

            StartService(svc, 0, null);
            CloseServiceHandle(svc);
            Core.Logger.Ok($"Служба установлена: {name}");
            return true;
        }
        finally { CloseServiceHandle(scm); }
    }

    public static bool Remove(string name)
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_ALL);
        if (scm == IntPtr.Zero) return false;
        try
        {
            var svc = OpenService(scm, name, SERVICE_ALL);
            if (svc == IntPtr.Zero) return true; // already gone
            var st = new SERVICE_STATUS();
            ControlService(svc, SERVICE_CONTROL_STOP, ref st);
            System.Threading.Thread.Sleep(500);
            var ok = DeleteService(svc);
            CloseServiceHandle(svc);
            if (ok) Core.Logger.Ok($"Служба удалена: {name}");
            return ok;
        }
        finally { CloseServiceHandle(scm); }
    }

    public static ServiceState GetState(string name)
    {
        var scm = OpenSCManager(null, null, 0x0001);
        if (scm == IntPtr.Zero) return ServiceState.Unknown;
        try
        {
            var svc = OpenService(scm, name, 0x0004); // SERVICE_QUERY_STATUS
            if (svc == IntPtr.Zero) return ServiceState.NotInstalled;
            var st = new SERVICE_STATUS();
            if (!QueryServiceStatus(svc, ref st)) return ServiceState.Unknown;
            CloseServiceHandle(svc);
            return st.CurrentState switch
            {
                1 => ServiceState.Stopped,
                2 => ServiceState.Starting,
                3 => ServiceState.Stopping,
                4 => ServiceState.Running,
                _ => ServiceState.Unknown
            };
        }
        finally { CloseServiceHandle(scm); }
    }

    public static bool Start(string name)
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_ALL);
        if (scm == IntPtr.Zero) return false;
        try
        {
            var svc = OpenService(scm, name, SERVICE_ALL);
            if (svc == IntPtr.Zero) return false;
            var ok = StartService(svc, 0, null);
            CloseServiceHandle(svc);
            return ok;
        }
        finally { CloseServiceHandle(scm); }
    }

    public static bool Stop(string name)
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_ALL);
        if (scm == IntPtr.Zero) return false;
        try
        {
            var svc = OpenService(scm, name, SERVICE_ALL);
            if (svc == IntPtr.Zero) return false;
            var st = new SERVICE_STATUS();
            var ok = ControlService(svc, SERVICE_CONTROL_STOP, ref st);
            CloseServiceHandle(svc);
            return ok;
        }
        finally { CloseServiceHandle(scm); }
    }
}
