using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Shturval.Common;

namespace Shturval.Agent;

/// <summary>Окно установки агента — открывается по двойному щелчку на ShturvalAgent.exe.</summary>
static class SetupUi
{
    public static int Run()
    {
        if (!Installer.IsAdmin())
        {
            // перезапуск с правами администратора (появится запрос UAC)
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = "setup" });
            }
            catch (Win32Exception) { /* пользователь отказался */ }
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm());
        return 0;
    }
}

sealed class SetupForm : Form
{
    static readonly Color Bg = Color.FromArgb(0x1C, 0x20, 0x27);
    static readonly Color FieldBg = Color.FromArgb(0x26, 0x2B, 0x35);
    static readonly Color TextColor = Color.FromArgb(0xE9, 0xEC, 0xF2);
    static readonly Color Muted = Color.FromArgb(0x8A, 0x93, 0xA6);
    static readonly Color Accent = Color.FromArgb(0x4C, 0x8D, 0xFF);
    static readonly Color Green = Color.FromArgb(0x34, 0xD3, 0x99);
    static readonly Color Yellow = Color.FromArgb(0xF5, 0xB5, 0x44);
    static readonly Color Red = Color.FromArgb(0xF2, 0x61, 0x5B);

    readonly TextBox _password = Input(password: true);
    readonly TextBox _password2 = Input(password: true);
    readonly TextBox _allow = Input();
    readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, BorderStyle = BorderStyle.FixedSingle, BackColor = FieldBg, ForeColor = TextColor };
    readonly CheckBox _banner = new() { Text = "Показывать пользователю плашку при подключении", AutoSize = true, ForeColor = TextColor, Checked = true };
    readonly CheckBox _show = new() { Text = "Показать пароль", AutoSize = true, ForeColor = Muted };
    readonly Label _status = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 10f) };
    readonly Button _install = MakeButton("Установить и запустить", primary: true);
    readonly Button _remove = MakeButton("Удалить агент");
    readonly Button _close = MakeButton("Закрыть");
    bool _hasPassword;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public SetupForm()
    {
        Text = "Штурвал — агент";
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 9.75f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        HandleCreated += (_, _) => { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); int c = ColorTranslator.ToWin32(Bg); DwmSetWindowAttribute(Handle, 35, ref c, 4); };

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(24, 20, 24, 16), AutoSize = true };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // заголовок
        var title = new Label { Text = "Агент «Штурвал»", AutoSize = true, Font = new Font("Segoe UI Semibold", 15f), Margin = new Padding(0, 0, 0, 2) };
        var sub = new Label { Text = "Служба удалённого управления этим компьютером.\nЗапускается сама при включении ПК, до входа пользователя.", AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 0, 0, 12) };
        AddWide(root, title);
        AddWide(root, sub);
        AddWide(root, _status);
        _status.Margin = new Padding(0, 0, 0, 14);

        AddRow(root, "Пароль", _password);
        AddRow(root, "Повтор пароля", _password2);
        AddRow(root, "", _show);
        AddRow(root, "Разрешённые адреса", _allow);
        AddWide(root, new Label
        {
            Text = "IP сервера с консолью, через запятую можно несколько: 192.168.1.10, 10.0.0.0/24.\nПусто — подключаться можно с любого адреса сети.",
            AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(0, 0, 0, 8),
        });
        AddRow(root, "Порт", _port);
        AddWide(root, _banner);
        _banner.Margin = new Padding(0, 6, 0, 10);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0), WrapContents = false };
        buttons.Controls.Add(_install);
        buttons.Controls.Add(_close);
        buttons.Controls.Add(_remove);
        AddWide(root, buttons);

        Controls.Add(root);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(560, 0);

        _show.CheckedChanged += (_, _) => _password.UseSystemPasswordChar = _password2.UseSystemPasswordChar = !_show.Checked;
        _install.Click += async (_, _) => await InstallAsync();
        _remove.Click += async (_, _) => await RemoveAsync();
        _close.Click += (_, _) => Close();
        AcceptButton = _install;
        CancelButton = _close;

        LoadState();
    }

    void LoadState()
    {
        var cfg = AgentConfig.Load();
        _hasPassword = cfg.HasPassword;
        _allow.Text = string.Join(", ", cfg.Allow);
        _port.Value = cfg.Port;
        _banner.Checked = cfg.Banner;
        _password.PlaceholderText = _password2.PlaceholderText = _hasPassword ? "оставьте пустым, чтобы не менять" : "не короче 8 символов";

        string text; Color color;
        bool installed = Installer.ServiceExists();
        if (!installed) { text = "●  Не установлен"; color = Muted; }
        else
        {
            using var sc = new ServiceController(AgentService.Name);
            if (sc.Status == ServiceControllerStatus.Running) { text = $"●  Установлен и работает · порт {cfg.Port}"; color = Green; }
            else { text = "●  Установлен, но служба остановлена"; color = Yellow; }
        }
        _status.Text = text;
        _status.ForeColor = color;
        _install.Text = installed ? "Сохранить и перезапустить" : "Установить и запустить";
        _remove.Visible = installed;
        PerformLayout();
    }

    async Task InstallAsync()
    {
        var pw = _password.Text;
        if (pw != _password2.Text) { Warn("Пароли не совпадают."); return; }
        if (pw.Length == 0 && !_hasPassword) { Warn("Задайте пароль для подключения."); return; }
        if (pw.Length > 0 && pw.Length < 8) { Warn("Пароль должен быть не короче 8 символов."); return; }

        var allow = string.Join(",", _allow.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries));
        if (allow == "" && MessageBox.Show(this,
                "Разрешённые адреса не указаны — подключиться сможет любой компьютер сети, знающий пароль.\n\nПродолжить?",
                "Штурвал", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        var args = new List<string> { "--port", ((int)_port.Value).ToString() };
        if (pw.Length > 0) { args.Add("--password"); args.Add(pw); }
        args.Add("--allow"); args.Add(allow == "" ? "," : allow);   // "," — очистить список
        args.Add(_banner.Checked ? "--banner" : "--no-banner");

        var (code, output) = await RunCaptured(() => Installer.Install(args.ToArray()));
        if (code == 0)
        {
            _password.Clear(); _password2.Clear();
            MessageBox.Show(this, output, "Штурвал", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else Warn(output);
        LoadState();
    }

    async Task RemoveAsync()
    {
        if (MessageBox.Show(this, "Удалить агент с этого компьютера? Подключиться к нему будет нельзя.", "Штурвал",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var (_, output) = await RunCaptured(() => Installer.Uninstall(new[] { "--purge" }));
        MessageBox.Show(this, output, "Штурвал", MessageBoxButtons.OK, MessageBoxIcon.Information);
        LoadState();
    }

    async Task<(int, string)> RunCaptured(Func<int> action)
    {
        UseWaitCursor = true;
        _install.Enabled = _remove.Enabled = false;
        var old = Console.Out;
        var sw = new StringWriter();
        try
        {
            Console.SetOut(sw);
            int code = await Task.Run(() =>
            {
                try { return action(); }
                catch (Exception ex) { Console.WriteLine("Ошибка: " + ex.Message); return 1; }
            });
            return (code, sw.ToString().Trim());
        }
        finally
        {
            Console.SetOut(old);
            UseWaitCursor = false;
            _install.Enabled = _remove.Enabled = true;
        }
    }

    void Warn(string text) => MessageBox.Show(this, text, "Штурвал", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    // ---------- оформление ----------
    static TextBox Input(bool password = false) => new()
    {
        BorderStyle = BorderStyle.FixedSingle, BackColor = FieldBg, ForeColor = TextColor,
        UseSystemPasswordChar = password, Width = 300,
    };

    static Button MakeButton(string text, bool primary = false)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = primary ? Accent : FieldBg, ForeColor = primary ? Color.White : TextColor,
            Padding = new Padding(14, 6, 14, 6), Margin = new Padding(8, 0, 0, 0),
        };
        b.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(0x2F, 0x35, 0x42);
        b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(0x6B, 0xA1, 0xFF) : Color.FromArgb(0x2E, 0x34, 0x40);
        if (!primary) b.Margin = new Padding(8, 0, 0, 0);
        return b;
    }

    static void AddRow(TableLayoutPanel t, string label, Control c)
    {
        t.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Muted, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 16, 6) });
        c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        c.Margin = new Padding(0, 4, 0, 4);
        t.Controls.Add(c);
    }

    static void AddWide(TableLayoutPanel t, Control c)
    {
        t.Controls.Add(c);
        t.SetColumnSpan(c, 2);
    }
}
