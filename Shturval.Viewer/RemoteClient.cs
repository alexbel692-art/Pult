using System.Net.Sockets;
using System.Threading.Channels;
using Shturval.Common;

namespace Shturval.Viewer;

/// <summary>Соединение консоли с агентом. Исходящие сообщения идут строго по очереди через один поток.</summary>
sealed class RemoteClient : IDisposable
{
    readonly TcpClient _tcp;
    readonly SecureChannel _channel;
    readonly CancellationTokenSource _cts = new();
    readonly Channel<(MsgType, byte[])> _outgoing =
        Channel.CreateUnbounded<(MsgType, byte[])>(new UnboundedChannelOptions { SingleReader = true });

    RemoteClient(TcpClient tcp, SecureChannel channel)
    {
        _tcp = tcp;
        _channel = channel;
        _ = Task.Run(SendLoopAsync);
    }

    public static async Task<RemoteClient> ConnectAsync(string host, int port, string password)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(host, port, cts.Token);
            var channel = await SecureChannel.ConnectAsync(tcp.GetStream(), password, cts.Token);
            return new RemoteClient(tcp, channel);
        }
        catch (OperationCanceledException)
        {
            tcp.Dispose();
            throw new TimeoutException("Компьютер не отвечает");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public void Post(MsgType type, byte[]? payload = null) =>
        _outgoing.Writer.TryWrite((type, payload ?? Array.Empty<byte>()));

    public Task<(MsgType Type, byte[] Payload)> ReceiveAsync() => _channel.ReceiveAsync(_cts.Token);

    async Task SendLoopAsync()
    {
        try
        {
            await foreach (var (type, payload) in _outgoing.Reader.ReadAllAsync(_cts.Token))
                await _channel.SendAsync(type, payload, _cts.Token);
        }
        catch { /* соединение закрыто — ошибку покажет цикл приёма */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _outgoing.Writer.TryComplete();
        _channel.Dispose();
        _tcp.Dispose();
    }
}
