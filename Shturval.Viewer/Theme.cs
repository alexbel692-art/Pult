using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Shturval.Viewer;

/// <summary>Цвета, шрифты и общие элементы тёмной темы.</summary>
static class Theme
{
    public static readonly Color Bg = Hex(0x111318);
    public static readonly Color Sidebar = Hex(0x16191F);
    public static readonly Color Surface = Hex(0x1C2027);
    public static readonly Color SurfaceHover = Hex(0x242934);
    public static readonly Color Field = Hex(0x262B35);
    public static readonly Color Border = Hex(0x2F3542);
    public static readonly Color Text = Hex(0xE9ECF2);
    public static readonly Color TextSoft = Hex(0xC3C9D6);
    public static readonly Color Muted = Hex(0x8A93A6);
    public static readonly Color Accent = Hex(0x4C8DFF);
    public static readonly Color AccentHover = Hex(0x6BA1FF);
    public static readonly Color AccentDown = Hex(0x3A76E0);
    public static readonly Color AccentDim = Hex(0x23324F);
    public static readonly Color Online = Hex(0x34D399);
    public static readonly Color Offline = Hex(0x5B6170);
    public static readonly Color Checking = Hex(0xF5B544);

    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font Small = new("Segoe UI", 8.75f);
    public static readonly Font Section = new("Segoe UI", 8f, FontStyle.Bold);
    public static readonly Font CardTitle = new("Segoe UI Semibold", 10.5f);
    public static readonly Font Title = new("Segoe UI Semibold", 15f);

    static Color Hex(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    static Icon? _icon;
    public static Icon? AppIcon
    {
        get
        {
            if (_icon != null) return _icon;
            try { _icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
            return _icon;
        }
    }

    static Image? _logo;
    public static Image? Logo
    {
        get
        {
            if (_logo != null) return _logo;
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png");
                if (s != null) _logo = Image.FromStream(s);
            }
            catch { }
            return _logo;
        }
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Color StatusColor(string status) => status switch
    {
        "в сети" => Online,
        "проверка…" => Checking,
        _ => Offline,
    };

    public static string StatusText(string status) => status switch
    {
        "в сети" => "В сети",
        "недоступен" => "Недоступен",
        "проверка…" => "Проверка…",
        _ => "Статус неизвестен",
    };

    // ---------- тёмный заголовок окна (Windows 10 20H1+ / 11) ----------
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(Form f, Color? back = null)
    {
        f.BackColor = back ?? Bg;
        f.ForeColor = Text;
        f.Font = Body;
        if (AppIcon != null) f.Icon = AppIcon;
        f.HandleCreated += (_, _) =>
        {
            int on = 1;
            DwmSetWindowAttribute(f.Handle, 20, ref on, 4);             // тёмный режим заголовка
            int caption = ColorTranslator.ToWin32(f.BackColor);
            DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);        // цвет заголовка (Windows 11)
        };
    }
}

/// <summary>Скруглённая рамка вокруг TextBox / NumericUpDown.</summary>
sealed class RoundedField : Panel
{
    readonly Control _inner;

    public RoundedField(Control inner, Color parentBack)
    {
        _inner = inner;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = parentBack;
        Height = LogicalToDeviceUnits(36);
        Padding = new Padding(LogicalToDeviceUnits(12), LogicalToDeviceUnits(8), LogicalToDeviceUnits(10), LogicalToDeviceUnits(4));

        switch (inner)
        {
            case TextBox tb: tb.BorderStyle = BorderStyle.None; break;
            case NumericUpDown nu: nu.BorderStyle = BorderStyle.None; break;
        }
        inner.BackColor = Theme.Field;
        inner.ForeColor = Theme.Text;
        inner.Font = Theme.Body;
        inner.Dock = DockStyle.Fill;
        inner.GotFocus += (_, _) => Invalidate();
        inner.LostFocus += (_, _) => Invalidate();
        Controls.Add(inner);
        Cursor = Cursors.IBeam;
        Click += (_, _) => inner.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        using var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), LogicalToDeviceUnits(8));
        using (var b = new SolidBrush(Theme.Field)) g.FillPath(b, path);
        using var pen = new Pen(_inner.Focused ? Theme.Accent : Theme.Border, _inner.Focused ? 1.5f : 1f);
        g.DrawPath(pen, path);
    }
}

/// <summary>Плоская кнопка со скруглёнными углами.</summary>
sealed class FlatButton : Button
{
    bool _hover, _down;
    public bool Primary { get; set; }

    public FlatButton(string text, bool primary = false)
    {
        Text = text;
        Primary = primary;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Font = Theme.Body;
        Height = LogicalToDeviceUnits(36);
        Width = TextRenderer.MeasureText(text, Font).Width + LogicalToDeviceUnits(36);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);

        Color bg = Primary
            ? (_down ? Theme.AccentDown : _hover ? Theme.AccentHover : Theme.Accent)
            : (_down ? Theme.Border : _hover ? Theme.SurfaceHover : Theme.Field);
        if (!Enabled) bg = Theme.Field;

        using var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), LogicalToDeviceUnits(8));
        using (var b = new SolidBrush(bg)) g.FillPath(b, path);
        if (!Primary || !Enabled)
            using (var pen = new Pen(Focused && ShowFocusCues ? Theme.Accent : Theme.Border)) g.DrawPath(pen, path);

        var color = !Enabled ? Theme.Muted : Primary ? Color.White : Theme.Text;
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Тёмная отрисовка панелей инструментов и контекстных меню.</summary>
sealed class DarkRenderer : ToolStripProfessionalRenderer
{
    public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }
        else
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, e.AffectedBounds.Bottom - 1, e.AffectedBounds.Right, e.AffectedBounds.Bottom - 1);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? Theme.Muted
            : e.Item is ToolStripButton { Checked: true } ? Theme.AccentHover
            : Theme.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        bool on = e.Item is ToolStripButton { Checked: true };
        if (!on && !e.Item.Selected && !e.Item.Pressed) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(1, 2, e.Item.Width - 2, e.Item.Height - 4);
        using var path = Theme.Rounded(r, 6);
        using var b = new SolidBrush(on ? Theme.AccentDim : Theme.SurfaceHover);
        g.FillPath(b, path);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Border);
        if (e.Vertical)
        {
            int x = e.Item.Width / 2;
            e.Graphics.DrawLine(pen, x, 6, x, e.Item.Height - 6);
        }
        else
        {
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Theme.Sidebar;
        public override Color ToolStripGradientMiddle => Theme.Sidebar;
        public override Color ToolStripGradientEnd => Theme.Sidebar;
        public override Color ToolStripBorder => Theme.Border;
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Theme.SurfaceHover;
        public override Color MenuItemSelected => Theme.SurfaceHover;
        public override Color MenuItemSelectedGradientBegin => Theme.SurfaceHover;
        public override Color MenuItemSelectedGradientEnd => Theme.SurfaceHover;
        public override Color MenuItemPressedGradientBegin => Theme.Field;
        public override Color MenuItemPressedGradientEnd => Theme.Field;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
    }
}
