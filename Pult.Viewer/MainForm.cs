using Pult.Common;

namespace Pult.Viewer;

sealed class MainForm : Form
{
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
    };
    readonly List<Computer> _items;

    public MainForm()
    {
        Text = "Пульт — компьютеры";
        Size = new Size(820, 560);
        StartPosition = FormStartPosition.CenterScreen;

        _list.Columns.Add("Имя", 230);
        _list.Columns.Add("Адрес", 170);
        _list.Columns.Add("Офис", 160);
        _list.Columns.Add("Статус", 140);
        _list.DoubleClick += (_, _) => ConnectSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) ConnectSelected();
            else if (e.KeyCode == Keys.Delete) RemoveSelected();
        };

        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4) };
        bar.Items.Add(new ToolStripButton("Подключиться", null, (_, _) => ConnectSelected()) { Font = new Font(bar.Font, FontStyle.Bold) });
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add("Добавить", null, (_, _) => AddComputer());
        bar.Items.Add("Изменить", null, (_, _) => EditSelected());
        bar.Items.Add("Удалить", null, (_, _) => RemoveSelected());
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add("Найти в сети", null, (_, _) => Scan());
        bar.Items.Add("Проверить связь", null, (_, _) => CheckAll());

        Controls.Add(_list);
        Controls.Add(bar);

        _items = ComputerStore.Load();
        RefreshList();
        if (_items.Count > 0) CheckAll();
    }

    Computer? Selected => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as Computer : null;
    IEnumerable<string> Offices => _items.Select(c => c.Office);

    void RefreshList()
    {
        var selected = Selected;
        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Groups.Clear();
        foreach (var g in _items.GroupBy(c => string.IsNullOrWhiteSpace(c.Office) ? "Без офиса" : c.Office).OrderBy(g => g.Key))
        {
            var group = new ListViewGroup(g.Key, g.Key);
            _list.Groups.Add(group);
            foreach (var c in g.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var addr = c.Port == Proto.DefaultPort ? c.Host : $"{c.Host}:{c.Port}";
                var item = new ListViewItem(new[] { c.Name, addr, c.Office, c.Status }, group) { Tag = c };
                if (c.Status == "в сети") item.ForeColor = Color.DarkGreen;
                else if (c.Status == "недоступен") item.ForeColor = Color.Gray;
                _list.Items.Add(item);
                if (c == selected) item.Selected = true;
            }
        }
        _list.EndUpdate();
    }

    void Save() => ComputerStore.Save(_items);

    void AddComputer()
    {
        var c = new Computer { Office = Selected?.Office ?? "" };
        using var dlg = new ComputerDialog(c, Offices, isNew: true);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _items.Add(c);
        Save();
        RefreshList();
    }

    void EditSelected()
    {
        if (Selected is not { } c) return;
        using var dlg = new ComputerDialog(c, Offices, isNew: false);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Save();
        RefreshList();
    }

    void RemoveSelected()
    {
        if (Selected is not { } c) return;
        if (MessageBox.Show(this, $"Удалить «{c.Name}» из списка?", "Пульт", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        _items.Remove(c);
        Save();
        RefreshList();
    }

    void Scan()
    {
        using var dlg = new ScanDialog(Offices);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        int added = 0;
        foreach (var pc in dlg.Found)
        {
            if (_items.Any(c => c.Host == pc.Host && c.Port == pc.Port)) continue;
            pc.Status = "в сети";
            _items.Add(pc);
            added++;
        }
        Save();
        RefreshList();
        if (dlg.Found.Count > 0 && added == 0)
            MessageBox.Show(this, "Все найденные компьютеры уже есть в списке.", "Пульт");
    }

    async void CheckAll()
    {
        foreach (var c in _items) c.Status = "проверка…";
        RefreshList();
        await Task.WhenAll(_items.Select(async c =>
            c.Status = await NetUtil.IsPortOpen(c.Host, c.Port, 1500) ? "в сети" : "недоступен"));
        RefreshList();
    }

    async void ConnectSelected()
    {
        if (Selected is not { } c) return;

        var password = c.GetPassword();
        bool remember = false;
        if (password == null)
        {
            using var dlg = new PasswordDialog(c.Name);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            password = dlg.Password;
            remember = dlg.Remember;
        }

        UseWaitCursor = true;
        try
        {
            var client = await RemoteClient.ConnectAsync(c.Host, c.Port, password);
            if (remember) { c.SetPassword(password); Save(); }
            c.Status = "в сети";
            RefreshList();
            new RemoteForm(client, c).Show();
        }
        catch (AuthFailedException ex)
        {
            if (c.SavedPassword != "") { c.SetPassword(null); Save(); }
            MessageBox.Show(this, $"{c.Name}: {ex.Message}", "Пульт", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не удалось подключиться к {c.Name} ({c.Host}):\n{ex.Message}", "Пульт",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { UseWaitCursor = false; }
    }
}
