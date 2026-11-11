using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Pult.Common;

namespace Pult.Viewer;

static class Dialogs
{
    public static TableLayoutPanel Grid(params (string Label, Control Control)[] rows)
    {
        var t = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (label, control) in rows)
        {
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 10, 6) });
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 3, 0, 3);
            t.Controls.Add(control);
        }
        return t;
    }

    /// <summary>Оформить форму как диалог с кнопками OK/Отмена. Возвращает кнопку OK.</summary>
    public static Button Setup(Form f, string title, TableLayoutPanel body, int rows, string okText = "OK")
    {
        f.Text = title;
        f.FormBorderStyle = FormBorderStyle.FixedDialog;
        f.MaximizeBox = f.MinimizeBox = false;
        f.ShowInTaskbar = false;
        f.StartPosition = FormStartPosition.CenterParent;

        var ok = new Button { Text = okText, DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(90, 0) };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 0) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        f.Controls.Add(body);
        f.Controls.Add(buttons);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        f.ClientSize = new Size(460, 24 + rows * 34 + 50);
        return ok;
    }
}

sealed class ComputerDialog : Form
{
    public ComputerDialog(Computer c, IEnumerable<string> offices, bool isNew)
    {
        var name = new TextBox { Text = c.Name };
        var host = new TextBox { Text = c.Host };
        var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = c.Port };
        var office = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Text = c.Office };
        office.Items.AddRange(offices.Where(o => o != "").Distinct().OrderBy(o => o).Cast<object>().ToArray());
        var password = new TextBox
        {
            UseSystemPasswordChar = true,
            PlaceholderText = c.SavedPassword != "" ? "сохранён — оставьте пустым, чтобы не менять" : "необязательно — спросит при подключении",
        };

        var grid = Dialogs.Grid(("Имя", name), ("IP или имя ПК", host), ("Порт", port), ("Офис", office), ("Пароль", password));
        var ok = Dialogs.Setup(this, isNew ? "Новый компьютер" : "Изменить компьютер", grid, 5);
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(host.Text))
            {
                MessageBox.Show(this, "Укажите адрес компьютера.", "Пульт");
                DialogResult = DialogResult.None;
                return;
            }
            c.Host = host.Text.Trim();
            c.Name = string.IsNullOrWhiteSpace(name.Text) ? c.Host : name.Text.Trim();
            c.Port = (int)port.Value;
            c.Office = office.Text.Trim();
            if (password.Text != "") c.SetPassword(password.Text);
        };
    }
}

sealed class PasswordDialog : Form
{
    readonly TextBox _password = new() { UseSystemPasswordChar = true };
    readonly CheckBox _remember = new() { Text = "Запомнить для этого компьютера", AutoSize = true };

    public string Password => _password.Text;
    public bool Remember => _remember.Checked;

    public PasswordDialog(string computerName)
    {
        var grid = Dialogs.Grid(("Пароль", _password), ("", _remember));
        Dialogs.Setup(this, $"Подключение к {computerName}", grid, 2, "Подключить");
    }
}

/// <summary>Поиск агентов в диапазоне адресов.</summary>
sealed class ScanDialog : Form
{
    public List<Computer> Found { get; } = new();

    public ScanDialog(IEnumerable<string> offices)
    {
        var (from, to) = GuessRange();
        var start = new TextBox { Text = from };
        var end = new TextBox { Text = to };
        var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = Proto.DefaultPort };
        var office = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        office.Items.AddRange(offices.Where(o => o != "").Distinct().OrderBy(o => o).Cast<object>().ToArray());
        var password = new TextBox { UseSystemPasswordChar = true, PlaceholderText = "необязательно — общий пароль для найденных" };
        var status = new Label { AutoSize = true, Text = "" };

        var grid = Dialogs.Grid(("Начало диапазона", start), ("Конец диапазона", end), ("Порт", port),
                                ("Офис для найденных", office), ("Пароль", password), ("", status));
        var ok = Dialogs.Setup(this, "Поиск компьютеров в сети", grid, 6, "Искать");
        ok.DialogResult = DialogResult.None;

        ok.Click += async (_, _) =>
        {
            if (!IPAddress.TryParse(start.Text.Trim(), out var a) || !IPAddress.TryParse(end.Text.Trim(), out var b)
                || a.AddressFamily != AddressFamily.InterNetwork || b.AddressFamily != AddressFamily.InterNetwork)
            {
                MessageBox.Show(this, "Укажите IPv4-адреса начала и конца.", "Пульт");
                return;
            }
            uint first = NetUtil.ToUInt(a), last = NetUtil.ToUInt(b);
            if (last < first || last - first > 4096)
            {
                MessageBox.Show(this, "Диапазон должен быть не больше 4096 адресов.", "Пульт");
                return;
            }

            ok.Enabled = false;
            int total = (int)(last - first + 1), done = 0, p = (int)port.Value;
            var found = new ConcurrentBag<(uint Ip, Computer Pc)>();
            using var gate = new SemaphoreSlim(128);

            var tasks = Enumerable.Range(0, total).Select(async i =>
            {
                await gate.WaitAsync();
                try
                {
                    uint v = first + (uint)i;
                    var ip = NetUtil.FromUInt(v);
                    if (await NetUtil.IsPortOpen(ip.ToString(), p, 800))
                    {
                        var pc = new Computer { Host = ip.ToString(), Port = p, Office = office.Text.Trim(), Name = await NetUtil.ResolveName(ip) };
                        if (password.Text != "") pc.SetPassword(password.Text);
                        found.Add((v, pc));
                    }
                }
                finally
                {
                    gate.Release();
                    int d = Interlocked.Increment(ref done);
                    if (d % 16 == 0 || d == total)
                        status.Text = $"Проверено {d} из {total}, найдено {found.Count}";
                }
            }).ToList();

            await Task.WhenAll(tasks);
            Found.AddRange(found.OrderBy(x => x.Ip).Select(x => x.Pc));
            MessageBox.Show(this, $"Найдено агентов: {Found.Count}", "Пульт");
            DialogResult = DialogResult.OK;
        };
    }

    static (string, string) GuessRange()
    {
        try
        {
            var ip = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            if (ip != null)
            {
                var b = ip.GetAddressBytes();
                return ($"{b[0]}.{b[1]}.{b[2]}.1", $"{b[0]}.{b[1]}.{b[2]}.254");
            }
        }
        catch { }
        return ("192.168.1.1", "192.168.1.254");
    }
}
