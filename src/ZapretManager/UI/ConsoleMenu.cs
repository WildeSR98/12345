namespace ZapretManager.UI;

public static class ConsoleMenu
{
    // ── Colors ─────────────────────────────────────────────────
    public static void WriteHeader(string title)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(new string('═', 60));
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  {title}");
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(new string('═', 60));
        Console.ResetColor();
        Logger.Step(title);
    }

    public static void WriteStep(string msg)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.Write(">> ");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(msg);
        Console.ResetColor();
        Logger.Step(msg);
    }

    public static void WriteOk(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"   [OK] {msg}");
        Console.ResetColor();
        Logger.Ok(msg);
    }

    public static void WriteWarn(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"   [!]  {msg}");
        Console.ResetColor();
        Logger.Warn(msg);
    }

    public static void WriteError(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"   [X]  {msg}");
        Console.ResetColor();
        Logger.Error(msg);
    }

    public static void WriteInfo(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"   [-]  {msg}");
        Console.ResetColor();
        Logger.Info(msg);
    }

    public static void WriteSeparator()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("   " + new string('─', 50));
        Console.ResetColor();
    }

    // ── Progress spinner ────────────────────────────────────────
    private static readonly char[] _spin = { '⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏' };
    private static int _spinIdx;
    private static CancellationTokenSource? _spinCts;
    private static Task? _spinTask;

    public static void StartSpinner(string msg)
    {
        _spinCts = new CancellationTokenSource();
        var tok = _spinCts.Token;
        _spinTask = Task.Run(async () =>
        {
            Console.CursorVisible = false;
            while (!tok.IsCancellationRequested)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"\r   {_spin[_spinIdx++ % _spin.Length]} {msg}  ");
                Console.ResetColor();
                await Task.Delay(80, tok).ConfigureAwait(false);
            }
        }, tok);
    }

    public static void StopSpinner(bool ok = true, string? result = null)
    {
        _spinCts?.Cancel();
        _spinTask?.Wait(500);
        Console.Write("\r" + new string(' ', 70) + "\r");
        Console.CursorVisible = true;
        if (result != null)
        {
            if (ok) WriteOk(result);
            else WriteError(result);
        }
        _spinCts = null;
        _spinTask = null;
    }

    // ── Progress bar ────────────────────────────────────────────
    public static void WriteProgress(string label, int current, int total)
    {
        const int barWidth = 30;
        var pct = total > 0 ? (double)current / total : 0;
        var filled = (int)(pct * barWidth);
        var bar = new string('█', filled) + new string('░', barWidth - filled);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"\r   [{bar}] {current}/{total}  {label.PadRight(30)}");
        Console.ResetColor();
        if (current >= total) Console.WriteLine();
    }

    // ── Input helpers ───────────────────────────────────────────
    public static string? Prompt(string prompt, string? defaultValue = null)
    {
        Console.ForegroundColor = ConsoleColor.White;
        if (defaultValue != null)
            Console.Write($"   {prompt} [{defaultValue}]: ");
        else
            Console.Write($"   {prompt}: ");
        Console.ResetColor();
        var input = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(input)) return defaultValue;
        return input;
    }

    public static bool Confirm(string prompt)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"   {prompt} [д/н]: ");
        Console.ResetColor();
        var key = Console.ReadKey(intercept: true);
        Console.WriteLine();
        return key.KeyChar == 'д' || key.KeyChar == 'Д' || key.KeyChar == 'y' || key.KeyChar == 'Y';
    }

    public static void PauseAny(string msg = "Нажмите любую клавишу для продолжения...")
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"\n   {msg}");
        Console.ResetColor();
        Console.ReadKey(true);
    }

    private static void Logger_Step(string msg) => Core.Logger.Step(msg);
    private static void Logger_Ok(string msg)   => Core.Logger.Ok(msg);
    private static void Logger_Warn(string msg) => Core.Logger.Warn(msg);
    private static void Logger_Error(string msg)=> Core.Logger.Error(msg);
    private static void Logger_Info(string msg) => Core.Logger.Info(msg);
}

// Make Logger accessible without namespace clash
file static class Logger
{
    public static void Step(string m)  => Core.Logger.Step(m);
    public static void Ok(string m)    => Core.Logger.Ok(m);
    public static void Warn(string m)  => Core.Logger.Warn(m);
    public static void Error(string m) => Core.Logger.Error(m);
    public static void Info(string m)  => Core.Logger.Info(m);
}
