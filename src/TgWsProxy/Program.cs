using TgWsProxy.Tray;

// Single-instance guard
var procs = System.Diagnostics.Process.GetProcessesByName(
    System.Diagnostics.Process.GetCurrentProcess().ProcessName);
if (procs.Length > 1)
{
    MessageBox.Show("Приложение уже запущено.", "TG WS Proxy",
        MessageBoxButtons.OK, MessageBoxIcon.Information);
    return;
}

Application.SetHighDpiMode(HighDpiMode.SystemAware);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.Run(new TrayApp());