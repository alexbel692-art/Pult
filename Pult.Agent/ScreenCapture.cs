using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static Pult.Agent.Native;

namespace Pult.Agent;

/// <summary>
/// Снимает весь виртуальный экран (все мониторы) и находит изменившиеся участки,
/// чтобы по сети шли только они.
/// </summary>
sealed class ScreenCapture : IDisposable
{
    const int Tile = 64;
    static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    Bitmap? _bmp;
    byte[]? _cur, _prev;
    int _w, _h;

    public int Width => _w;
    public int Height => _h;

    public void Reset() => _prev = null;

    /// <summary>Снять экран. Возвращает изменившиеся прямоугольники (или весь экран).</summary>
    public List<Rectangle> Capture(bool full)
    {
        int ox = GetSystemMetrics(SM_XVIRTUALSCREEN), oy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN), h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (w <= 0 || h <= 0) return new();

        if (_bmp == null || w != _w || h != _h)
        {
            _bmp?.Dispose();
            _bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            _w = w; _h = h;
            _cur = new byte[w * h * 4];
            _prev = null;
        }

        using (var g = Graphics.FromImage(_bmp))
        {
            IntPtr dst = g.GetHdc();
            try
            {
                IntPtr src = GetDC(IntPtr.Zero);
                if (src != IntPtr.Zero)
                {
                    BitBlt(dst, 0, 0, w, h, src, ox, oy, SRCCOPY | CAPTUREBLT);
                    ReleaseDC(IntPtr.Zero, src);
                }
                DrawCursor(dst, ox, oy);
            }
            finally { g.ReleaseHdc(dst); }
        }

        var data = _bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, _cur!, y * w * 4, w * 4);
        }
        finally { _bmp.UnlockBits(data); }

        var rects = full || _prev == null ? new List<Rectangle> { new(0, 0, w, h) } : Diff();

        // меняем буферы местами, чтобы не выделять память каждый кадр
        var old = _prev ?? new byte[w * h * 4];
        _prev = _cur;
        _cur = old;
        return rects;
    }

    List<Rectangle> Diff()
    {
        var result = new List<Rectangle>();
        int stride = _w * 4;
        var cur = _cur.AsSpan();
        var prev = _prev.AsSpan();

        for (int ty = 0; ty < _h; ty += Tile)
        {
            int th = Math.Min(Tile, _h - ty);
            int runStart = -1;
            for (int tx = 0; tx < _w; tx += Tile)
            {
                int tw = Math.Min(Tile, _w - tx);
                bool changed = false;
                for (int y = ty; y < ty + th && !changed; y++)
                {
                    int off = y * stride + tx * 4;
                    if (!cur.Slice(off, tw * 4).SequenceEqual(prev.Slice(off, tw * 4))) changed = true;
                }

                if (changed) { if (runStart < 0) runStart = tx; }
                else if (runStart >= 0) { result.Add(new Rectangle(runStart, ty, tx - runStart, th)); runStart = -1; }
            }
            if (runStart >= 0) result.Add(new Rectangle(runStart, ty, _w - runStart, th));
        }
        return result;
    }

    public byte[] EncodeJpeg(Rectangle r, int quality)
    {
        using var part = _bmp!.Clone(r, PixelFormat.Format24bppRgb);
        using var ms = new MemoryStream();
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
        part.Save(ms, Jpeg, ep);
        return ms.ToArray();
    }

    static void DrawCursor(IntPtr hdc, int ox, int oy)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;

        int hx = 0, hy = 0;
        if (GetIconInfo(ci.hCursor, out var ii))
        {
            hx = ii.xHotspot; hy = ii.yHotspot;
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
        DrawIconEx(hdc, ci.pt.X - ox - hx, ci.pt.Y - oy - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3 /*DI_NORMAL*/);
    }

    public void Dispose() => _bmp?.Dispose();
}
