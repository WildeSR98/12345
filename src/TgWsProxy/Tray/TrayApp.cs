using TgWsProxy.Config;
using TgWsProxy.Proxy;

namespace TgWsProxy.Tray;

public class TrayApp : ApplicationContext
{
    private NotifyIcon _tray = null!;
    private ProxyConfig _cfg;
    private ProxyLogger _log = null!;
    private Thread? _proxyThread;
    private CancellationTokenSource? _proxyCts;
    private bool _exiting;

    public TrayApp()
    {
        ProxyConfig.EnsureDir();
        _cfg = ProxyConfig.Load();
        _cfg.Save();
        _log = new ProxyLogger(ProxyConfig.LogPath, _cfg.Verbose);
        _log.Info($"Config: port={_cfg.Port} dc_ip=[{string.Join(", ", _cfg.DcIp)}]");

        BuildTray();
        StartProxy();
        ShowFirstRun();
    }

    // ── Proxy thread ──────────────────────────────────────────────────────────
    private void StartProxy()
    {
        if (_proxyThread?.IsAlive == true) return;
        _proxyCts = new CancellationTokenSource();
        var token = _proxyCts.Token;
        var dcOpt = _cfg.ParseDcIp();
        _proxyThread = new Thread(() =>
        {
            var server = new Socks5Server(_cfg.Port, dcOpt, _log);
            try { server.RunAsync(token).GetAwaiter().GetResult(); }
            catch (Exception ex) { _log.Error($"Proxy crashed: {ex.Message}"); }
        }) { IsBackground = true, Name = "proxy" };
        _proxyThread.Start();
        _log.Info($"Proxy started on port {_cfg.Port}");
    }

    private void StopProxy()
    {
        _proxyCts?.Cancel();
        _proxyThread?.Join(2000);
        _proxyThread = null;
        _log.Info("Proxy stopped");
    }

    private void RestartProxy()
    {
        _log.Info("Restarting proxy...");
        StopProxy();
        Thread.Sleep(300);
        StartProxy();
        UpdateMenu();
    }

    // ── Tray ──────────────────────────────────────────────────────────────────
    private void BuildTray()
    {
        _tray = new NotifyIcon
        {
            Icon    = MakeIcon(),
            Text    = "TG WS Proxy",
            Visible = true
        };
        _tray.MouseDoubleClick += (_, _) => OpenInTelegram();
        UpdateMenu();
    }

    private void UpdateMenu()
    {
        var running = _proxyThread?.IsAlive == true;
        _tray.ContextMenuStrip?.Dispose();
        var menu = new ContextMenuStrip();
        menu.Items.Add($"Открыть в Telegram (:{_cfg.Port})", null, (_, _) => OpenInTelegram());
        menu.Items[0].Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(running ? "Перезапустить прокси" : "Запустить прокси", null,
            (_, _) => Task.Run(RestartProxy));
        if (running)
            menu.Items.Add("Остановить прокси", null, (_, _) => { StopProxy(); UpdateMenu(); });
        menu.Items.Add("Настройки...", null, (_, _) =>
        {
            var form = new SettingsForm(_cfg, newCfg =>
            {
                _cfg = newCfg;
                _log = new ProxyLogger(ProxyConfig.LogPath, _cfg.Verbose);
                RestartProxy();
            });
            form.Show();
        });
        menu.Items.Add("Открыть логи", null, (_, _) => _log.Open());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.Text = $"TG WS Proxy{(running ? "" : " [остановлен]")}";
    }

    // ── Actions ───────────────────────────────────────────────────────────────
    private void OpenInTelegram()
    {
        var url = $"tg://socks?server=127.0.0.1&port={_cfg.Port}";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch
        {
            CopyToClipboard(url);
            MessageBox.Show(
                $"Не удалось открыть Telegram автоматически.\n\nСсылка скопирована в буфер обмена:\n{url}",
                "TG WS Proxy", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static void CopyToClipboard(string text)
    {
        var t = new Thread(() => { try { Clipboard.SetText(text); } catch { } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
    }

    private void ShowFirstRun()
    {
        if (File.Exists(ProxyConfig.FirstRunMarker)) return;
        var url = $"tg://socks?server=127.0.0.1&port={_cfg.Port}";
        var form = new FirstRunForm(url, openTg =>
        {
            File.WriteAllText(ProxyConfig.FirstRunMarker, "done");
            if (openTg) OpenInTelegram();
        });
        form.Show();
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        StopProxy();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }

    // ── Icon ──────────────────────────────────────────────────────────────────
    private static Icon MakeIcon()
    {
        // Try to load from embedded resource
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".ico"));
        if (name != null)
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream != null) try { return new Icon(stream); } catch { }
        }

        // Generate: blue circle with 'T'
        var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.FillEllipse(new SolidBrush(Color.FromArgb(51, 144, 236)), 1, 1, 30, 30);
        using var font = new Font("Arial", 18, FontStyle.Bold);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("T", font, Brushes.White, new RectangleF(0, 0, 32, 32), sf);
        return Icon.FromHandle(bmp.GetHicon());
    }
}
