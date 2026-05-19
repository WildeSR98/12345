using TgWsProxy.Config;
using TgWsProxy.Proxy;

namespace TgWsProxy.Tray;

public class SettingsForm : Form
{
    private readonly ProxyConfig _cfg;
    private TextBox _portBox = null!;
    private TextBox _dcBox = null!;
    private CheckBox _verboseBox = null!;
    private Action<ProxyConfig>? _onSave;

    private static readonly Color TgBlue   = Color.FromArgb(51, 144, 236);
    private static readonly Color FieldBg   = Color.FromArgb(240, 242, 245);
    private static readonly Color BorderCol = Color.FromArgb(214, 217, 220);

    public SettingsForm(ProxyConfig cfg, Action<ProxyConfig>? onSave = null)
    {
        _cfg    = cfg;
        _onSave = onSave;
        InitForm();
    }

    private void InitForm()
    {
        Text            = "TG WS Proxy — Настройки";
        Size            = new(440, 420);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = Color.White;
        Font            = new Font("Segoe UI", 10);

        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 20) };
        Controls.Add(panel);

        int y = 20;

        // Port
        AddLabel(panel, "Порт прокси", 14, y); y += 26;
        _portBox = AddTextBox(panel, _cfg.Port.ToString(), 14, y, 120, 36); y += 50;

        // DC-IP
        AddLabel(panel, "DC → IP маппинги (формат DC:IP, по одному на строке)", 14, y); y += 26;
        _dcBox = new TextBox
        {
            Left = 14, Top = y, Width = 380, Height = 110,
            Multiline = true, ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 10),
            BackColor = FieldBg, BorderStyle = BorderStyle.FixedSingle,
            Text = string.Join(Environment.NewLine, _cfg.DcIp)
        };
        panel.Controls.Add(_dcBox); y += 118;

        // Verbose
        _verboseBox = new CheckBox
        {
            Left = 14, Top = y, Width = 380, Height = 26,
            Text = "Подробное логирование (verbose)",
            Checked = _cfg.Verbose, FlatStyle = FlatStyle.Flat
        };
        panel.Controls.Add(_verboseBox); y += 36;

        // Info
        var info = new Label
        {
            Left = 14, Top = y, Width = 380, Height = 22,
            Text = "Изменения вступят в силу после перезапуска прокси.",
            ForeColor = Color.Gray, Font = new Font("Segoe UI", 9)
        };
        panel.Controls.Add(info); y += 32;

        // Buttons
        var btnSave = MakeButton("Сохранить", 14, y, 160, 38, TgBlue, Color.White);
        btnSave.Click += OnSave;
        panel.Controls.Add(btnSave);

        var btnCancel = MakeButton("Отмена", 182, y, 120, 38, FieldBg, Color.Black);
        btnCancel.Click += (_, _) => Close();
        panel.Controls.Add(btnCancel);

        Controls[0].Controls.AddRange(new Control[] { });
    }

    private void OnSave(object? s, EventArgs e)
    {
        if (!int.TryParse(_portBox.Text.Trim(), out var port) || port < 1 || port > 65535)
        { MessageBox.Show("Порт должен быть числом 1-65535", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        var lines = _dcBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        foreach (var line in lines)
        {
            var parts = line.Split(':', 2);
            if (parts.Length != 2 || !int.TryParse(parts[0], out _))
            { MessageBox.Show($"Неверный формат: {line}\nОжидается DC:IP.Address", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        }

        var newCfg = new ProxyConfig { Port = port, DcIp = lines, Verbose = _verboseBox.Checked };
        newCfg.Save();

        if (MessageBox.Show("Настройки сохранены. Перезапустить прокси?", Text,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            _onSave?.Invoke(newCfg);
        }
        Close();
    }

    private static Label AddLabel(Control parent, string text, int x, int y)
    {
        var lbl = new Label { Left = x, Top = y, Width = 380, Height = 20, Text = text };
        parent.Controls.Add(lbl);
        return lbl;
    }

    private static TextBox AddTextBox(Control parent, string text, int x, int y, int w, int h)
    {
        var tb = new TextBox
        {
            Left = x, Top = y, Width = w, Height = h, Text = text,
            BackColor = FieldBg, BorderStyle = BorderStyle.FixedSingle
        };
        parent.Controls.Add(tb);
        return tb;
    }

    private static Button MakeButton(string text, int x, int y, int w, int h,
        Color bg, Color fg)
    {
        var btn = new Button
        {
            Left = x, Top = y, Width = w, Height = h, Text = text,
            BackColor = bg, ForeColor = fg, FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 0;
        return btn;
    }
}
