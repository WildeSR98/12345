namespace ZapretManager.Core;

public static class Logger
{
    private static string? _logFile;
    private static bool _verbose;
    private static readonly object _lock = new();

    public static void Init(string rootDir, bool verbose = false)
    {
        _verbose = verbose;
        var logDir = Path.Combine(rootDir, "logs");
        Directory.CreateDirectory(logDir);
        var ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        _logFile = Path.Combine(logDir, $"zapret_{ts}.log");
        Write("INFO", "=== ZAPRET MANAGER started ===");
        Write("INFO", $"OS: {Environment.OSVersion} | .NET: {Environment.Version}");
    }

    public static void Write(string level, string message)
    {
        if (_logFile == null) return;
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        lock (_lock)
        {
            try { File.AppendAllText(_logFile, line + "\r\n", System.Text.Encoding.UTF8); }
            catch { /* ignore log errors */ }
        }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Ok(string msg) => Write("OK", msg);
    public static void Step(string msg) => Write("STEP", msg);
    public static void Debug(string msg) { if (_verbose) Write("DEBUG", msg); }
}
