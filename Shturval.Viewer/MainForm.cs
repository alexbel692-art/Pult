using Shturval.Common;

namespace Shturval.Viewer;

sealed class MainForm : Form
{
    const string NoOffice = "Без офиса";

    readonly List<Computer> _items;
    readonly OfficeList _offices = new() { Dock = DockStyle.Fill };
    readonly FlowLayoutPanel _cards = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
    readonly TextBox _search = new() { PlaceholderText = "Поиск по имени или адресу" };
    readonly Label _summary = new() { Dock = DockStyle.Right, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleRight, Font = Theme.Small };
    readonly Label _empty = new() { Dock = DockStyle.Fill, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleCenter, Visible = false };
    readonly ContextMenuStrip _menu = new() { Renderer = new DarkRenderer(), Font = Theme.Body, ShowImageMargin = false };
    string? _office;             // null — все компьютеры
    ComputerCard? _selected;
    bool _rebuildingSidebar;

    public MainForm()
    {
        Theme.Apply(this);
        Text = "Штурвал";
        Size = new Size(LogicalToDeviceUnits(1120), LogicalToDeviceUnits(720));
        MinimumSize = new Size(LogicalToDeviceUnits(820), LogicalToDeviceUnits(520));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        int U(int v) => LogicalToDeviceUnits(v);

        // ---- шапка ----
        var header = new HeaderPanel { Dock = DockStyle.Top };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            AutoSize = true, BackColor = Theme.Bg, Padding = new Padding(0, U(19), U(20), 0),
        };
        var add = new FlatButton("+  Добавить", primary: true);
        var scan = new FlatButton("Найти в сети");
        var check = new FlatButton("Проверить связь");
        add.Click += (_, _) => AddComputer();
        scan.Click += (_, _) => Scan();
        check.Click += (_, _) => CheckAll();
        foreach (var b in new[] { add, scan, check })
        {
            b.Margin = new Padding(U(8), 0, 0, 0);
            actions.Controls.Add(b);
        }
        header.Controls.Add(actions);

        // ---- боковая панель ----
        var sidebar = new Panel { Dock = DockStyle.Left, Width = U(236), BackColor = Theme.Sidebar, Padding = new Padding(U(12), U(18), U(12), U(12)) };
        var section = new Label
        {
            Text = "ОФИСЫ", Dock = DockStyle.Top, Height = U(28), ForeColor = Theme.Muted,
            Font = Theme.Section, Padding = new Padding(U(6), 0, 0, 0),
        };
        sidebar.Controls.Add(_offices);
        sidebar.Controls.Add(section);
        _offices.SelectedIndexChanged += (_, _) =>
        {
            if (_rebuildingSidebar || _offices.SelectedItem is not OfficeItem it) return;
            _office = it.Key;
            RenderCards();
        };

        // ---- содержимое ----
        var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(U(24), U(16), U(10), U(10)) };
        var topRow = new Panel { Dock = DockStyle.Top, Height = U(54), BackColor = Theme.Bg };
        var searchField = new RoundedField(_search, Theme.Bg) { Dock = DockStyle.Left, Width = U(340) };
        var searchWrap = new Panel { Dock = DockStyle.Left, Width = U(340), Height = U(36), BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, U(18)) };
        searchWrap.Controls.Add(searchField);
        _summary.Width = U(300);
        _summary.Padding = new Padding(0, 0, U(14), U(18));
        topRow.Controls.Add(_summary);
        topRow.Controls.Add(searchWrap);
        _search.TextChanged += (_, _) => RenderCards();

        content.Controls.Add(_cards);
        content.Controls.Add(_empty);
        content.Controls.Add(topRow);

        Controls.Add(content);
        Controls.Add(sidebar);
        Controls.Add(header);

        // ---- меню карточки ----
        _menu.Items.Add("Подключиться", null, (_, _) => ConnectSelected());
        _menu.Items.Add("Изменить…", null, (_, _) => EditSelected());
        _menu.Items.Add("Удалить", null, (_, _) => RemoveSelected());
        _menu.Opening += (_, _) => { if (_menu.SourceControl is ComputerCard c) SelectCard(c); };

        KeyDown += (_, e) =>
        {
            if (_search.Focused) return;
            if (e.KeyCode == Keys.Enter) { ConnectSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = true; }
        };

        _items = ComputerStore.Load();
        RebuildSidebar();
        RenderCards();
        if (_items.Count > 0) CheckAll();
    }

    IEnumerable<string> Offices => _items.Select(c => c.Office);
    static string OfficeOf(Computer c) => string.IsNullOrWhiteSpace(c.Office) ? NoOffice : c.Office;

    // ---------- отрисовка ----------
    void RebuildSidebar()
    {
        _rebuildingSidebar = true;
        _offices.BeginUpdate();
        _offices.Items.Clear();
        _offices.Items.Add(new OfficeItem(null, "Все компьютеры", _items.Count, _items.Count(c => c.Status == "в сети")));
        foreach (var g in _items.GroupBy(OfficeOf).OrderBy(g => g.Key == NoOffice).ThenBy(g => g.Key))
            _offices.Items.Add(new OfficeItem(g.Key, g.Key, g.Count(), g.Count(c => c.Status == "в сети")));

        int index = 0;
        for (int i = 0; i < _offices.Items.Count; i++)
            if (((OfficeItem)_offices.Items[i]).Key == _office) index = i;
        if (index == 0) _office = null;
        _offices.SelectedIndex = index;
        _offices.EndUpdate();
        _rebuildingSidebar = false;
    }

    void RenderCards()
    {
        var query = _search.Text.Trim();
        var visible = _items
            .Where(c => _office == null || OfficeOf(c) == _office)
            .Where(c => query == "" || c.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                    || c.Host.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var keep = _selected?.Computer;
        _cards.SuspendLayout();
        foreach (Control c in _cards.Controls.Cast<Control>().ToList()) c.Dispose();
        _cards.Controls.Clear();
        _selected = null;

        foreach (var pc in visible)
        {
            var card = new ComputerCard(pc) { ContextMenuStrip = _menu };
            card.MouseDown += (_, _) => SelectCard(card);
            card.DoubleClick += (_, _) => { SelectCard(card); ConnectSelected(); };
            _cards.Controls.Add(card);
            if (pc == keep) SelectCard(card);
        }
        _cards.ResumeLayout();

        _empty.Text = _items.Count == 0
            ? "Компьютеров пока нет.\nНажмите «Найти в сети», чтобы найти агенты, или «Добавить»."
            : "Ничего не найдено.";
        _empty.Visible = visible.Count == 0;
        _cards.Visible = visible.Count > 0;
        UpdateSummary();
    }

    void UpdateSummary()
    {
        int online = _items.Count(c => c.Status == "в сети");
        _summary.Text = _items.Count == 0 ? "" : $"В сети {online} из {_items.Count}";
    }

    /// <summary>Перерисовать статусы без перестройки списка.</summary>
    void RefreshStatuses()
    {
        foreach (Control c in _cards.Controls) c.Invalidate();
        RebuildSidebar();
        UpdateSummary();
    }

    void SelectCard(ComputerCard card)
    {
        if (_selected != null && _selected != card) _selected.Selected = false;
        _selected = card;
        card.Selected = true;
        card.Focus();
    }

    // ---------- действия ----------
    void Save() => ComputerStore.Save(_items);

    void Changed()
    {
        Save();
        RebuildSidebar();
        RenderCards();
    }

    void AddComputer()
    {
        var c = new Computer { Office = _office == NoOffice ? "" : _office ?? "" };
        using var dlg = new ComputerDialog(c, Offices, isNew: true);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _items.Add(c);
        Changed();
        _ = CheckOne(c);
    }

    void EditSelected()
    {
        if (_selected?.Computer is not { } c) return;
        using var dlg = new ComputerDialog(c, Offices, isNew: false);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Changed();
    }

    void RemoveSelected()
    {
        if (_selected?.Computer is not { } c) return;
        if (MessageBox.Show(this, $"Удалить «{c.Name}» из списка?", "Штурвал", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _items.Remove(c);
        Changed();
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
        Changed();
        if (dlg.Found.Count > 0 && added == 0)
            MessageBox.Show(this, "Все найденные компьютеры уже есть в списке.", "Штурвал");
    }

    async void CheckAll()
    {
        foreach (var c in _items) c.Status = "проверка…";
        RefreshStatuses();
        await Task.WhenAll(_items.Select(CheckOne));
    }

    async Task CheckOne(Computer c)
    {
        c.Status = "проверка…";
        RefreshStatuses();
        c.Status = await NetUtil.IsPortOpen(c.Host, c.Port, 1500) ? "в сети" : "недоступен";
        RefreshStatuses();
    }

    async void ConnectSelected()
    {
        if (_selected?.Computer is not { } c) return;

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
            RefreshStatuses();
            new RemoteForm(client, c).Show();
        }
        catch (AuthFailedException ex)
        {
            if (c.SavedPassword != "") { c.SetPassword(null); Save(); }
            MessageBox.Show(this, $"{c.Name}: {ex.Message}", "Штурвал", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            c.Status = "недоступен";
            RefreshStatuses();
            MessageBox.Show(this, $"Не удалось подключиться к {c.Name} ({c.Host}):\n{ex.Message}", "Штурвал",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { UseWaitCursor = false; }
    }
}
