namespace TgWsProxy.Tray;

public class FirstRunForm : Form
{
    public FirstRunForm(string proxyUrl, Action<bool> onOk)
    {
        Text            = "TG WS Proxy";
        Size            = new(520, 380);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = Color.White;
        Font            = new Font("Segoe UI", 10);

        var tgBlue  = Color.FromArgb(51, 144, 236);
        var fieldBg = Color.FromArgb(240, 242, 245);
        int y = 24;

        // Accent bar + title
        var accent = new Panel { Left = 24, Top = y, Width = 4, Height = 28, BackColor = tgBlue };
        Controls.Add(accent);
        var title = new Label
        {
            Left = 36, Top = y, Width = 456, Height = 28, Font = new Font("Segoe UI", 13, FontStyle.Bold),
            Text = "Прокси запущен и работает в системном трее"
        };
        Controls.Add(title); y += 44;

        var lines = new[]
        {
            ("Как подключить Telegram Desktop:", true),
            ("  Автоматически:", true),
            ("  ПКМ по иконке в трее → «Открыть в Telegram»", false),
            ($"  Или ссылка: {proxyUrl}", false),
            ("  Вручную:", true),
            ("  Настройки → Продвинутые → Тип подключения → Прокси", false),
            ($"  SOCKS5 → 127.0.0.1:{proxyUrl.Split('=').Last()} (без логина/пароля)", false),
        };

        foreach (var (text, bold) in lines)
        {
            var lbl = new Label
            {
                Left = 24, Top = y, Width = 464, Height = 20, Text = text,
                Font = new Font("Segoe UI", 10, bold ? FontStyle.Bold : FontStyle.Regular)
            };
            Controls.Add(lbl); y += 22;
        }
        y += 8;

        var sep = new Panel { Left = 24, Top = y, Width = 464, Height = 1, BackColor = Color.FromArgb(214, 217, 220) };
        Controls.Add(sep); y += 14;

        var chk = new CheckBox { Left = 24, Top = y, Width = 380, Height = 24, Text = "Открыть прокси в Telegram сейчас", Checked = true };
        Controls.Add(chk); y += 36;

        var btn = new Button
        {
            Left = 24, Top = y, Width = 180, Height = 40, Text = "Начать",
            BackColor = tgBlue, ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 11, FontStyle.Bold), Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.Click += (_, _) => { onOk(chk.Checked); Close(); };
        Controls.Add(btn);

        FormClosed += (_, _) => onOk(false);
    }
}
