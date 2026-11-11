using System.Text;
using Pult.Common;

namespace Pult.Viewer;

/// <summary>Окно управления одним компьютером.</summary>
sealed class RemoteForm : Form
{
    readonly RemoteClient _client;
    readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(32, 32, 32) };
    readonly ScreenView _view = new();
    readonly ToolStripButton _viewOnly = new("Только просмотр") { CheckOnClick = true };
    readonly ToolStripButton _fit = new("По размеру окна") { CheckOnClick = true, Checked = true };
    readonly ToolStripComboBox _quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 110 };
    readonly ToolStripLabel _stats = new() { Alignment = ToolStripItemAlignment.Right, ForeColor = Color.Gray };
    readonly HashSet<(ushort Vk, ushort Scan, bool Ext)> _pressed = new();
    bool _closed, _needFull = true;
    int _frames; long _bytes;
    readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 1000 };

    static readonly int[] QualityValues = { 40, 65, 85 };

    public RemoteForm(RemoteClient client, Computer pc)
    {
        _client = client;
        Text = $"{pc.Name} ({pc.Host}) — Пульт";
        Size = new Size(1280, 820);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = false;

        _quality.Items.AddRange(new object[] { "Качество: низкое", "Качество: среднее", "Качество: высокое" });
        _quality.SelectedIndex = 1;
        _quality.SelectedIndexChanged += (_, _) => { _needFull = true; _view.Focus(); };

        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4) };
        bar.Items.Add("Ctrl+Alt+Del", null, (_, _) => { if (!_viewOnly.Checked) _client.Post(MsgType.CtrlAltDel); _view.Focus(); });
        bar.Items.Add("Win", null, (_, _) => { SendKey(0x5B, 0x5B, true, true); SendKey(0x5B, 0x5B, false, true); _view.Focus(); });
        bar.Items.Add("Вставить текст", null, (_, _) => PasteText());
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(_viewOnly);
        bar.Items.Add(_fit);
        bar.Items.Add(_quality);
        bar.Items.Add("Обновить", null, (_, _) => { _needFull = true; _view.Focus(); });
        bar.Items.Add(_stats);

        _fit.CheckedChanged += (_, _) => { ApplyFit(); _view.Focus(); };
        _viewOnly.CheckedChanged += (_, _) => { ReleaseKeys(); _view.Focus(); };

        _scroll.Controls.Add(_view);
        Controls.Add(_scroll);
        Controls.Add(bar);
        ApplyFit();

        _view.RemoteSizeChanged += ApplyFit;
        _view.RemoteMouseMove += (x, y) =>
        {
            if (!_viewOnly.Checked) _client.Post(MsgType.MouseMove, Packets.Build(w => { w.Write(x); w.Write(y); }));
        };
        _view.RemoteMouseButton += (b, down, x, y) =>
        {
            if (_viewOnly.Checked || b == 0) return;
            _client.Post(MsgType.MouseButton, Packets.Build(w => { w.Write((byte)b); w.Write(down); w.Write(x); w.Write(y); }));
        };
        _view.RemoteWheel += (delta, horizontal) =>
        {
            if (!_viewOnly.Checked) _client.Post(MsgType.MouseWheel, Packets.Build(w => { w.Write((short)delta); w.Write(horizontal); }));
        };
        _view.KeyEvent += (vk, scan, down, ext) => SendKey(vk, scan, down, ext);
        _view.LostFocus += (_, _) => ReleaseKeys();
        Deactivate += (_, _) => ReleaseKeys();

        _statsTimer.Tick += (_, _) =>
        {
            _stats.Text = $"{_frames} кадр/с · {_bytes * 8 / 1_000_000.0:0.0} Мбит/с";
            _frames = 0; _bytes = 0;
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _view.Focus();
        _statsTimer.Start();
        _ = ReceiveLoopAsync();
    }

    void ApplyFit()
    {
        _view.Fit = _fit.Checked;
        if (_fit.Checked)
        {
            _scroll.AutoScroll = false;
            _view.Dock = DockStyle.Fill;
        }
        else
        {
            _view.Dock = DockStyle.None;
            var size = _view.RemoteSize;
            _view.Location = Point.Empty;
            _view.Size = size.IsEmpty ? _scroll.ClientSize : size;
            _scroll.AutoScroll = true;
        }
    }

    // ---------- кадры ----------
    async Task ReceiveLoopAsync()
    {
        try
        {
            RequestFrame();
            while (true)
            {
                var (type, payload) = await _client.ReceiveAsync();
                if (type != MsgType.Frame) continue;
                _bytes += payload.Length;
                _frames++;
                await Task.Run(() => ApplyFrame(payload));
                RequestFrame();
            }
        }
        catch (Exception ex)
        {
            if (_closed) return;
            _closed = true;
            MessageBox.Show(this, $"Соединение разорвано: {ex.Message}", "Пульт", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
        }
    }

    void RequestFrame()
    {
        int q = QualityValues[Math.Max(0, _quality.SelectedIndex)];
        _client.Post(MsgType.FrameRequest, new[] { (byte)q, (byte)(_needFull ? 1 : 0) });
        _needFull = false;
    }

    void ApplyFrame(byte[] payload)
    {
        using var r = Packets.Reader(payload);
        int w = r.ReadInt32(), h = r.ReadInt32();
        int count = r.ReadUInt16();
        _view.EnsureSize(w, h);

        var dirty = Rectangle.Empty;
        for (int i = 0; i < count; i++)
        {
            var rect = new Rectangle(r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16());
            var jpeg = r.ReadBytes(r.ReadInt32());
            using var ms = new MemoryStream(jpeg);
            using var tile = new Bitmap(ms);
            _view.DrawTile(tile, rect);
            dirty = dirty.IsEmpty ? rect : Rectangle.Union(dirty, rect);
        }
        if (!dirty.IsEmpty) _view.InvalidateRemote(dirty);
    }

    // ---------- клавиатура ----------
    void SendKey(ushort vk, ushort scan, bool down, bool ext)
    {
        if (_viewOnly.Checked) return;
        if (down) _pressed.Add((vk, scan, ext)); else _pressed.Remove((vk, scan, ext));
        byte flags = (byte)((down ? 1 : 0) | (ext ? 2 : 0));
        _client.Post(MsgType.Key, Packets.Build(w => { w.Write(vk); w.Write(scan); w.Write(flags); }));
    }

    /// <summary>Отпустить зажатые клавиши, если консоль потеряла фокус (иначе на ПК «залипнет» Alt или Ctrl).</summary>
    void ReleaseKeys()
    {
        foreach (var (vk, scan, ext) in _pressed.ToList())
            _client.Post(MsgType.Key, Packets.Build(w => { w.Write(vk); w.Write(scan); w.Write((byte)(ext ? 2 : 0)); }));
        _pressed.Clear();
    }

    void PasteText()
    {
        if (_viewOnly.Checked) return;
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        if (text.Length == 0) { MessageBox.Show(this, "В буфере обмена нет текста.", "Пульт"); return; }
        if (text.Length > 10_000) text = text[..10_000];
        _client.Post(MsgType.TypeText, Encoding.UTF8.GetBytes(text));
        _view.Focus();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closed = true;
        _statsTimer.Dispose();
        ReleaseKeys();
        _client.Dispose();
        base.OnFormClosed(e);
    }
}
