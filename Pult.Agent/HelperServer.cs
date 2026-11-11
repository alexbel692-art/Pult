using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Pult.Common;

namespace Pult.Agent;

/// <summary>Сетевая часть агента: принимает подключения консоли. Работает в сессии пользователя.</summary>
sealed class HelperServer
{
    readonly AgentConfig _cfg;
    readonly byte[] _key, _salt;
    readonly DesktopWorker _worker = new();
    readonly Dictionary<IPAddress, (int Fails, DateTime BlockedUntil)> _fails = new();
    readonly object _sync = new();
    CancellationTokenSource? _current;

    public HelperServer(AgentConfig cfg)
    {
        _cfg = cfg;
        _key = cfg.GetKey();
        _salt = cfg.GetSalt();
    }

    public async Task RunAsync()
    {
        var listener = new TcpListener(IPAddress.Any, _cfg.Port);
        listener.Start();
        Log.Write($"помощник слушает порт {_cfg.Port}, сессия {Process.GetCurrentProcess().SessionId}");
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var ip = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

            if (!_cfg.IsAllowed(ip)) { Log.Write($"отклонено, адреса нет в списке: {ip}"); return; }
            if (IsBlocked(ip)) return;
            client.NoDelay = true;

            SecureChannel channel;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                channel = await SecureChannel.AcceptAsync(client.GetStream(), _key, _salt, timeout.Token);
            }
            catch (AuthFailedException) { RegisterFail(ip); Log.Write($"неверный пароль с {ip}"); return; }
            catch (Exception ex) when (ex is EndOfStreamException or IOException) { return; } // проверка «в сети» из консоли
            catch (Exception ex) { Log.Write($"рукопожатие с {ip}: {ex.Message}"); return; }

            lock (_fails) _fails.Remove(ip);

            // одновременно — одна сессия; новое подключение вытесняет старое
            var cts = new CancellationTokenSource();
            lock (_sync) { _current?.Cancel(); _current = cts; }

            Log.Write($"подключился {ip}");
            using (channel)
            {
                try { await new RemoteSession(channel, _worker, _cfg, ip).RunAsync(cts.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or EndOfStreamException) { }
                catch (Exception ex) { Log.Write($"сессия {ip}: {ex.Message}"); }
            }
            lock (_sync) { if (_current == cts) _current = null; }
            Log.Write($"отключился {ip}");
        }
    }

    bool IsBlocked(IPAddress ip)
    {
        lock (_fails) return _fails.TryGetValue(ip, out var f) && f.BlockedUntil > DateTime.UtcNow;
    }

    void RegisterFail(IPAddress ip)
    {
        lock (_fails)
        {
            _fails.TryGetValue(ip, out var f);
            f.Fails++;
            if (f.Fails >= 5) { f.BlockedUntil = DateTime.UtcNow.AddMinutes(5); f.Fails = 0; Log.Write($"{ip} заблокирован на 5 минут"); }
            _fails[ip] = f;
        }
    }
}

/// <summary>Одна сессия управления: приём команд и отправка кадров.</summary>
sealed class RemoteSession
{
    readonly SecureChannel _ch;
    readonly DesktopWorker _worker;
    readonly AgentConfig _cfg;
    readonly IPAddress _ip;
    readonly Channel<(int Quality, bool Full)> _requests =
        Channel.CreateBounded<(int, bool)>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    public RemoteSession(SecureChannel ch, DesktopWorker worker, AgentConfig cfg, IPAddress ip)
    {
        _ch = ch; _worker = worker; _cfg = cfg; _ip = ip;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var banner = _cfg.Banner ? new Banner() : null;
        banner?.Show($"К компьютеру подключён администратор ({_ip})");

        var frames = FrameLoopAsync(linked.Token);
        try { await ReceiveLoopAsync(linked.Token); }
        finally
        {
            linked.Cancel();
            try { await frames; } catch { }
            banner?.Close();
        }
    }

    async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            var (type, p) = await _ch.ReceiveAsync(ct);
            using var r = Packets.Reader(p);
            switch (type)
            {
                case MsgType.FrameRequest:
                {
                    int q = Math.Clamp((int)r.ReadByte(), 10, 95);
                    bool full = r.ReadBoolean();
                    _requests.Writer.TryWrite((q, full));
                    break;
                }
                case MsgType.MouseMove:
                {
                    int x = r.ReadInt32(), y = r.ReadInt32();
                    _worker.Post(() => Input.MoveTo(x, y));
                    break;
                }
                case MsgType.MouseButton:
                {
                    int b = r.ReadByte(); bool down = r.ReadBoolean();
                    int x = r.ReadInt32(), y = r.ReadInt32();
                    _worker.Post(() => Input.Button(b, down, x, y));
                    break;
                }
                case MsgType.MouseWheel:
                {
                    int delta = r.ReadInt16(); bool horizontal = r.ReadBoolean();
                    _worker.Post(() => Input.Wheel(delta, horizontal));
                    break;
                }
                case MsgType.Key:
                {
                    ushort vk = r.ReadUInt16(), scan = r.ReadUInt16(); byte f = r.ReadByte();
                    _worker.Post(() => Input.Key(vk, scan, (f & 1) != 0, (f & 2) != 0));
                    break;
                }
                case MsgType.TypeText:
                {
                    var text = Encoding.UTF8.GetString(p);
                    _worker.Post(() => Input.TypeText(text));
                    break;
                }
                case MsgType.CtrlAltDel:
                    SendCtrlAltDel();
                    break;
            }
        }
    }

    /// <summary>Кадр отправляется только по запросу консоли — так очередь не копится и задержка минимальна.</summary>
    async Task FrameLoopAsync(CancellationToken ct)
    {
        await foreach (var (quality, full) in _requests.Reader.ReadAllAsync(ct))
        {
            var sw = Stopwatch.StartNew();
            var frame = await _worker.CaptureAsync(full, quality);
            // ждём изменений до 1 с, чтобы не гонять пустые кадры
            while (frame.Tiles.Count == 0 && sw.ElapsedMilliseconds < 1000)
            {
                await Task.Delay(40, ct);
                frame = await _worker.CaptureAsync(false, quality);
            }

            var payload = Packets.Build(w =>
            {
                w.Write(frame.Width);
                w.Write(frame.Height);
                w.Write((ushort)frame.Tiles.Count);
                foreach (var (rect, jpeg) in frame.Tiles)
                {
                    w.Write((ushort)rect.X); w.Write((ushort)rect.Y);
                    w.Write((ushort)rect.Width); w.Write((ushort)rect.Height);
                    w.Write(jpeg.Length);
                    w.Write(jpeg);
                }
            });
            await _ch.SendAsync(MsgType.Frame, payload, ct);
        }
    }

    static void SendCtrlAltDel()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(AgentService.CadEventName);
            ev.Set();
        }
        catch (Exception ex) { Log.Write("Ctrl+Alt+Del недоступен (агент запущен не службой?): " + ex.Message); }
    }
}

/// <summary>Красная плашка в углу экрана, пока идёт подключение.</summary>
sealed class Banner
{
    BannerForm? _form;

    public void Show(string text)
    {
        var ready = new ManualResetEventSlim(); // не освобождаем: окно может открыться позже ожидания
        var t = new Thread(() =>
        {
            var f = new BannerForm(text);
            f.Shown += (_, _) => ready.Set();
            _form = f;
            Application.Run(f);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        ready.Wait(3000);
    }

    public void Close()
    {
        var f = _form;
        _form = null;
        try { if (f != null && f.IsHandleCreated) f.BeginInvoke(new Action(f.Close)); } catch { }
    }

    sealed class BannerForm : Form
    {
        public BannerForm(string text)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(190, 35, 35);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Controls.Add(new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Padding = new Padding(12, 8, 12, 8),
            });
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/ | 0x8 /*TOPMOST*/;
                return cp;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            PerformLayout();
            var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
            Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
        }
    }
}
