namespace TgWsProxy.Proxy;

public class ProxyLogger
{
    private readonly string _logPath;
    private readonly bool _verbose;
    private readonly object _lock = new();

    public ProxyLogger(string logPath, bool verbose)
    {
        _logPath = logPath;
        _verbose = verbose;
        // Clear old log on start
        try { if (File.Exists(logPath)) File.Delete(logPath); } catch { }
        Write("INFO", "=== TG WS Proxy started ===");
    }

    public void Info(string msg)  => Write("INFO",  msg);
    public void Warn(string msg)  => Write("WARN",  msg);
    public void Error(string msg) => Write("ERROR", msg);
    public void Debug(string msg) { if (_verbose) Write("DEBUG", msg); }

    private void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {level,-5}  {msg}";
        lock (_lock)
        {
            try { File.AppendAllText(_logPath, line + "\r\n", System.Text.Encoding.UTF8); }
            catch { }
        }
    }

    public void Open() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_logPath) { UseShellExecute = true });
}
