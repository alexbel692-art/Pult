using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using static Pult.Agent.Native;

namespace Pult.Agent;

record FrameData(int Width, int Height, List<(Rectangle Rect, byte[] Jpeg)> Tiles);

/// <summary>
/// Один поток, который захватывает экран и вводит мышь/клавиатуру.
/// Перед каждой операцией переключается на текущий рабочий стол —
/// так видно и экран блокировки, и окна UAC.
/// </summary>
sealed class DesktopWorker
{
    readonly BlockingCollection<Action> _queue = new();
    readonly ScreenCapture _capture = new();
    IntPtr _desktop;
    string _desktopName = "";

    public DesktopWorker()
    {
        new Thread(Loop) { IsBackground = true, Name = "desktop" }.Start();
    }

    void Loop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try { SyncDesktop(); action(); }
            catch (Exception ex) { Log.Write("desktop: " + ex.Message); }
        }
    }

    void SyncDesktop()
    {
        IntPtr h = OpenInputDesktop(0, false, GENERIC_ALL);
        if (h == IntPtr.Zero) return;
        string name = GetDesktopName(h);
        if (name == _desktopName) { CloseDesktop(h); return; }

        if (SetThreadDesktop(h))
        {
            if (_desktop != IntPtr.Zero) CloseDesktop(_desktop);
            _desktop = h;
            _desktopName = name;
            _capture.Reset();
            Log.Write($"рабочий стол: {name}");
        }
        else CloseDesktop(h);
    }

    public void Post(Action action)
    {
        try { _queue.Add(action); } catch (InvalidOperationException) { }
    }

    public Task<FrameData> CaptureAsync(bool full, int quality)
    {
        var tcs = new TaskCompletionSource<FrameData>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                var rects = _capture.Capture(full);
                var tiles = rects.Select(r => (r, _capture.EncodeJpeg(r, quality))).ToList();
                tcs.SetResult(new FrameData(_capture.Width, _capture.Height, tiles));
            }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }
}

/// <summary>Ввод мыши и клавиатуры. Вызывается только из потока DesktopWorker.</summary>
static class Input
{
    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4, KEYEVENTF_SCANCODE = 0x8;

    public static void MoveTo(int x, int y) =>
        SetCursorPos(GetSystemMetrics(SM_XVIRTUALSCREEN) + x, GetSystemMetrics(SM_YVIRTUALSCREEN) + y);

    public static void Button(int button, bool down, int x, int y)
    {
        MoveTo(x, y);
        (uint flags, uint data) = button switch
        {
            1 => (down ? 0x0002u : 0x0004u, 0u),
            2 => (down ? 0x0008u : 0x0010u, 0u),
            3 => (down ? 0x0020u : 0x0040u, 0u),
            4 => (down ? 0x0080u : 0x0100u, 1u),
            5 => (down ? 0x0080u : 0x0100u, 2u),
            _ => (0u, 0u),
        };
        if (flags != 0) Mouse(flags, data);
    }

    public static void Wheel(int delta, bool horizontal) =>
        Mouse(horizontal ? 0x1000u : 0x0800u, unchecked((uint)delta));

    public static void Key(ushort vk, ushort scan, bool down, bool extended)
    {
        var k = new KEYBDINPUT();
        if (scan != 0) { k.wScan = scan; k.dwFlags = KEYEVENTF_SCANCODE; }
        else k.wVk = vk;
        if (extended) k.dwFlags |= KEYEVENTF_EXTENDEDKEY;
        if (!down) k.dwFlags |= KEYEVENTF_KEYUP;
        Send(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = k } });
    }

    /// <summary>Напечатать текст (работает при любой раскладке и на экране входа).</summary>
    public static void TypeText(string text)
    {
        foreach (char ch in text)
        {
            if (ch == '\r') continue;
            if (ch == '\n') { Key(0x0D, 0, true, false); Key(0x0D, 0, false, false); continue; }
            Send(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } } });
            Send(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
        }
    }

    static void Mouse(uint flags, uint data) =>
        Send(new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data } } });

    static void Send(INPUT input) => SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
}
