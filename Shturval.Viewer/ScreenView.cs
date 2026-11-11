using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Shturval.Viewer;

/// <summary>Показывает экран удалённого ПК и перехватывает мышь и клавиатуру.</summary>
sealed class ScreenView : Control
{
    readonly object _sync = new();
    Bitmap? _fb;
    bool _fit = true;

    /// <summary>Клавиша: vk, scan, нажата, расширенная.</summary>
    public event Action<ushort, ushort, bool, bool>? KeyEvent;
    public event Action<int, int>? RemoteMouseMove;
    public event Action<int, bool, int, int>? RemoteMouseButton; // кнопка, нажата, x, y
    public event Action<int, bool>? RemoteWheel;                 // delta, горизонтально
    public event Action? RemoteSizeChanged;

    public ScreenView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.Selectable | ControlStyles.Opaque, true);
        TabStop = true;
        BackColor = Theme.Bg;
    }

    public bool Fit
    {
        get => _fit;
        set { _fit = value; Invalidate(); }
    }

    public Size RemoteSize
    {
        get { lock (_sync) return _fb?.Size ?? Size.Empty; }
    }

    // ---------- приём кадров (из фонового потока) ----------
    public void EnsureSize(int w, int h)
    {
        bool changed = false;
        lock (_sync)
        {
            if (_fb == null || _fb.Width != w || _fb.Height != h)
            {
                _fb?.Dispose();
                _fb = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                changed = true;
            }
        }
        if (changed) UiAsync(() => RemoteSizeChanged?.Invoke());
    }

    public void DrawTile(Image tile, Rectangle dest)
    {
        lock (_sync)
        {
            if (_fb == null) return;
            using var g = Graphics.FromImage(_fb);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(tile, dest);
        }
    }

    public void InvalidateRemote(Rectangle remoteDirty) => UiAsync(() => Invalidate(ToLocal(remoteDirty)));

    void UiAsync(Action a)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
    }

    // ---------- отрисовка ----------
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        lock (_sync)
        {
            if (_fb == null)
            {
                g.Clear(BackColor);
                TextRenderer.DrawText(g, "Получение изображения…", Theme.Body, ClientRectangle, Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            var dest = DestRect();
            using (var bg = new SolidBrush(BackColor))
            {
                using var outside = new Region(ClientRectangle);
                outside.Exclude(dest);
                g.FillRegion(bg, outside);
            }
            bool scaled = dest.Size != _fb.Size;
            g.InterpolationMode = scaled ? InterpolationMode.Bilinear : InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_fb, dest);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    /// <summary>Где на контроле рисуется удалённый экран. Вызывать под _sync.</summary>
    Rectangle DestRect()
    {
        if (_fb == null) return Rectangle.Empty;
        if (!_fit) return new Rectangle(0, 0, _fb.Width, _fb.Height);
        double s = Math.Min((double)ClientSize.Width / _fb.Width, (double)ClientSize.Height / _fb.Height);
        if (s <= 0) s = 1;
        int w = Math.Max(1, (int)(_fb.Width * s)), h = Math.Max(1, (int)(_fb.Height * s));
        return new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
    }

    Rectangle ToLocal(Rectangle r)
    {
        lock (_sync)
        {
            if (_fb == null) return ClientRectangle;
            var d = DestRect();
            double sx = (double)d.Width / _fb.Width, sy = (double)d.Height / _fb.Height;
            var local = new Rectangle(d.X + (int)(r.X * sx), d.Y + (int)(r.Y * sy), (int)Math.Ceiling(r.Width * sx), (int)Math.Ceiling(r.Height * sy));
            local.Inflate(2, 2);
            return local;
        }
    }

    bool ToRemote(Point p, out int x, out int y)
    {
        lock (_sync)
        {
            x = y = 0;
            if (_fb == null) return false;
            var d = DestRect();
            x = Math.Clamp((int)((p.X - d.X) * (double)_fb.Width / d.Width), 0, _fb.Width - 1);
            y = Math.Clamp((int)((p.Y - d.Y) * (double)_fb.Height / d.Height), 0, _fb.Height - 1);
            return true;
        }
    }

    // ---------- мышь ----------
    Point _lastSent = new(-1, -1);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!ToRemote(e.Location, out int x, out int y)) return;
        if (x == _lastSent.X && y == _lastSent.Y) return;
        _lastSent = new Point(x, y);
        RemoteMouseMove?.Invoke(x, y);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        base.OnMouseDown(e);
        if (ToRemote(e.Location, out int x, out int y)) RemoteMouseButton?.Invoke(ButtonCode(e.Button), true, x, y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (ToRemote(e.Location, out int x, out int y)) RemoteMouseButton?.Invoke(ButtonCode(e.Button), false, x, y);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        RemoteWheel?.Invoke(e.Delta, false);
    }

    static int ButtonCode(MouseButtons b) => b switch
    {
        MouseButtons.Left => 1,
        MouseButtons.Right => 2,
        MouseButtons.Middle => 3,
        MouseButtons.XButton1 => 4,
        MouseButtons.XButton2 => 5,
        _ => 0,
    };

    // ---------- клавиатура: все клавиши уходят на удалённый ПК ----------
    protected override bool IsInputKey(Keys keyData) => true;
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) => false;
    protected override bool ProcessDialogKey(Keys keyData) => false;

    protected override void WndProc(ref Message m)
    {
        const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_CHAR = 0x102, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105,
                  WM_SYSCHAR = 0x106, WM_MOUSEHWHEEL = 0x20E;
        switch (m.Msg)
        {
            case WM_KEYDOWN or WM_SYSKEYDOWN or WM_KEYUP or WM_SYSKEYUP:
            {
                long lp = m.LParam.ToInt64();
                var vk = (ushort)m.WParam.ToInt64();
                var scan = (ushort)((lp >> 16) & 0xFF);
                bool extended = ((lp >> 24) & 1) != 0;
                bool down = m.Msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                KeyEvent?.Invoke(vk, scan, down, extended);
                return; // не отдаём Windows: Alt, F10 и т.п. не трогают окно консоли
            }
            case WM_CHAR or WM_SYSCHAR:
                return;
            case WM_MOUSEHWHEEL:
                RemoteWheel?.Invoke((short)((m.WParam.ToInt64() >> 16) & 0xFFFF), true);
                return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) lock (_sync) { _fb?.Dispose(); _fb = null; }
        base.Dispose(disposing);
    }
}
