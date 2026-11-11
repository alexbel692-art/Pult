using System.Drawing.Drawing2D;
using Shturval.Common;

namespace Shturval.Viewer;

/// <summary>Карточка компьютера в списке.</summary>
sealed class ComputerCard : Control
{
    bool _hover, _selected;
    public Computer Computer { get; }

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public ComputerCard(Computer c)
    {
        Computer = c;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Size = new Size(LogicalToDeviceUnits(268), LogicalToDeviceUnits(92));
        Margin = new Padding(0, 0, LogicalToDeviceUnits(14), LogicalToDeviceUnits(14));
        Cursor = Cursors.Hand;
        BackColor = Theme.Bg;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        int U(int v) => LogicalToDeviceUnits(v);

        var cardBg = _hover || _selected ? Theme.SurfaceHover : Theme.Surface;
        using (var path = Theme.Rounded(new RectangleF(1, 1, Width - 3, Height - 3), U(12)))
        {
            using (var b = new SolidBrush(cardBg)) g.FillPath(b, path);
            using var pen = new Pen(_selected ? Theme.Accent : Theme.Border, _selected ? 2f : 1f);
            g.DrawPath(pen, path);
        }

        // плитка с монитором
        var tile = new RectangleF(U(16), U(22), U(46), U(46));
        using (var tp = Theme.Rounded(tile, U(10)))
        using (var tb = new SolidBrush(Theme.Field)) g.FillPath(tb, tp);

        using (var pen = new Pen(Theme.TextSoft, Math.Max(1.6f, U(2))))
        {
            pen.LineJoin = LineJoin.Round;
            pen.StartCap = pen.EndCap = LineCap.Round;
            float mx = tile.X + U(11), my = tile.Y + U(12), mw = U(24), mh = U(16);
            using (var screen = Theme.Rounded(new RectangleF(mx, my, mw, mh), U(3))) g.DrawPath(pen, screen);
            g.DrawLine(pen, mx + mw / 2, my + mh, mx + mw / 2, my + mh + U(5));
            g.DrawLine(pen, mx + mw / 2 - U(6), my + mh + U(5), mx + mw / 2 + U(6), my + mh + U(5));
        }

        // точка статуса
        var status = Theme.StatusColor(Computer.Status);
        float dot = U(14);
        var dr = new RectangleF(tile.Right - dot + U(3), tile.Bottom - dot + U(3), dot, dot);
        using (var ring = new SolidBrush(cardBg)) g.FillEllipse(ring, dr);
        dr.Inflate(-U(3), -U(3));
        using (var sb = new SolidBrush(status)) g.FillEllipse(sb, dr);

        // тексты
        int tx = U(76), tw = Width - tx - U(14);
        const TextFormatFlags F = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                                  TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        var addr = Computer.Port == Proto.DefaultPort ? Computer.Host : $"{Computer.Host}:{Computer.Port}";
        TextRenderer.DrawText(g, Computer.Name, Theme.CardTitle, new Rectangle(tx, U(16), tw, U(24)), Theme.Text, F);
        TextRenderer.DrawText(g, addr, Theme.Small, new Rectangle(tx, U(40), tw, U(18)), Theme.Muted, F);
        TextRenderer.DrawText(g, Theme.StatusText(Computer.Status), Theme.Small, new Rectangle(tx, U(60), tw, U(18)),
            Computer.Status == "" ? Theme.Muted : status, F);
    }
}

/// <summary>Шапка главного окна: логотип и название.</summary>
sealed class HeaderPanel : Panel
{
    public HeaderPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        Height = LogicalToDeviceUnits(74);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int U(int v) => LogicalToDeviceUnits(v);

        int logo = U(40);
        if (Theme.Logo != null) g.DrawImage(Theme.Logo, U(22), (Height - logo) / 2, logo, logo);

        TextRenderer.DrawText(g, "Штурвал", Theme.Title, new Point(U(74), U(13)), Theme.Text, TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, "Удалённое управление компьютерами в локальной сети", Theme.Small,
            new Point(U(76), U(44)), Theme.Muted, TextFormatFlags.NoPrefix);

        using var pen = new Pen(Theme.Border);
        g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}

/// <summary>Пункт боковой панели офисов.</summary>
sealed record OfficeItem(string? Key, string Title, int Count, int Online);

/// <summary>Список офисов в боковой панели.</summary>
sealed class OfficeList : ListBox
{
    public OfficeList()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = LogicalToDeviceUnits(40);
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Sidebar;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        IntegralHeight = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var it = (OfficeItem)Items[e.Index];
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int U(int v) => LogicalToDeviceUnits(v);
        bool sel = (e.State & DrawItemState.Selected) != 0;

        using (var bg = new SolidBrush(Theme.Sidebar)) g.FillRectangle(bg, e.Bounds);
        var r = new RectangleF(e.Bounds.X + 2, e.Bounds.Y + 3, e.Bounds.Width - 4, e.Bounds.Height - 6);
        if (sel)
        {
            using var path = Theme.Rounded(r, U(8));
            using var b = new SolidBrush(Theme.SurfaceHover);
            g.FillPath(b, path);
            using var accent = new SolidBrush(Theme.Accent);
            g.FillRectangle(accent, r.X, r.Y + U(9), U(3), r.Height - U(18));
        }

        var countText = it.Count == 0 ? "" : $"{it.Online}/{it.Count}";
        int countW = TextRenderer.MeasureText(countText, Theme.Small).Width;
        var titleRect = new Rectangle(e.Bounds.X + U(16), e.Bounds.Y, e.Bounds.Width - U(28) - countW, e.Bounds.Height);
        TextRenderer.DrawText(g, it.Title, Theme.Body, titleRect, sel ? Theme.Text : Theme.TextSoft,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, countText, Theme.Small,
            new Rectangle(e.Bounds.Right - countW - U(12), e.Bounds.Y, countW + U(4), e.Bounds.Height),
            it.Online > 0 ? Theme.Online : Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }
}
