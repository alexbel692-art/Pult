using System.Security.Cryptography;
using System.Text;

namespace Shturval.Common;

public sealed class AuthFailedException(string message) : Exception(message);

/// <summary>
/// Зашифрованный канал поверх TCP.
/// Рукопожатие: агент шлёт соль и случайное число, консоль доказывает знание пароля через HMAC,
/// агент отвечает своим HMAC (взаимная проверка). Пароль по сети не передаётся.
/// Дальше все сообщения шифруются AES-256-GCM, ключи свои для каждого направления и каждой сессии.
/// </summary>
public sealed class SecureChannel : IDisposable
{
    const int NonceLength = 16, MacLength = 32, TagLength = 16;
    public const int MaxMessage = 64 * 1024 * 1024;

    readonly Stream _stream;
    readonly AesGcm _send, _recv;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    ulong _sendCounter, _recvCounter;

    SecureChannel(Stream stream, byte[] sendKey, byte[] recvKey)
    {
        _stream = stream;
        _send = new AesGcm(sendKey, TagLength);
        _recv = new AesGcm(recvKey, TagLength);
    }

    // ---------- сторона агента ----------
    public static async Task<SecureChannel> AcceptAsync(Stream s, byte[] key, byte[] salt, CancellationToken ct)
    {
        var serverNonce = RandomNumberGenerator.GetBytes(NonceLength);
        await s.WriteAsync(Concat(Proto.Magic, salt, serverNonce), ct);

        var reply = new byte[NonceLength + MacLength];
        await s.ReadExactlyAsync(reply, ct);
        var clientNonce = reply[..NonceLength];
        var proof = reply[NonceLength..];

        if (!Equal(proof, Mac(key, "client", serverNonce, clientNonce)))
        {
            await s.WriteAsync(new byte[] { 0 }, ct);
            throw new AuthFailedException("Неверный пароль");
        }

        await s.WriteAsync(Concat(new byte[] { 1 }, Mac(key, "server", serverNonce, clientNonce)), ct);
        var (c2s, s2c) = SessionKeys(key, serverNonce, clientNonce);
        return new SecureChannel(s, s2c, c2s);
    }

    // ---------- сторона консоли ----------
    public static async Task<SecureChannel> ConnectAsync(Stream s, string password, CancellationToken ct)
    {
        int m = Proto.Magic.Length;
        var hello = new byte[m + Proto.SaltLength + NonceLength];
        await s.ReadExactlyAsync(hello, ct);
        if (!StartsWithMagic(hello)) throw new InvalidDataException("На этом порту не агент «Штурвал»");

        var salt = hello[m..(m + Proto.SaltLength)];
        var serverNonce = hello[(m + Proto.SaltLength)..];
        var key = await Task.Run(() => Proto.DeriveKey(password, salt), ct);

        var clientNonce = RandomNumberGenerator.GetBytes(NonceLength);
        await s.WriteAsync(Concat(clientNonce, Mac(key, "client", serverNonce, clientNonce)), ct);

        var status = new byte[1];
        await s.ReadExactlyAsync(status, ct);
        if (status[0] != 1) throw new AuthFailedException("Неверный пароль");

        var serverProof = new byte[MacLength];
        await s.ReadExactlyAsync(serverProof, ct);
        if (!Equal(serverProof, Mac(key, "server", serverNonce, clientNonce)))
            throw new AuthFailedException("Агент не подтвердил пароль — возможна подмена");

        var (c2s, s2c) = SessionKeys(key, serverNonce, clientNonce);
        return new SecureChannel(s, c2s, s2c);
    }

    // ---------- обмен сообщениями ----------
    public async Task SendAsync(MsgType type, byte[] payload, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var frame = Seal(type, payload);
            await _stream.WriteAsync(frame, ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<(MsgType Type, byte[] Payload)> ReceiveAsync(CancellationToken ct = default)
    {
        var header = new byte[4];
        await _stream.ReadExactlyAsync(header, ct);
        int len = BitConverter.ToInt32(header, 0);
        if (len < 1 + TagLength || len > MaxMessage) throw new InvalidDataException("Некорректный пакет");
        var body = new byte[len];
        await _stream.ReadExactlyAsync(body, ct);
        return Open(body);
    }

    byte[] Seal(MsgType type, byte[] payload)
    {
        int plainLen = 1 + payload.Length;
        var frame = new byte[4 + plainLen + TagLength];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), plainLen + TagLength);
        var plain = new byte[plainLen];
        plain[0] = (byte)type;
        Buffer.BlockCopy(payload, 0, plain, 1, payload.Length);
        _send.Encrypt(Nonce(_sendCounter++), plain, frame.AsSpan(4, plainLen), frame.AsSpan(4 + plainLen, TagLength));
        return frame;
    }

    (MsgType, byte[]) Open(byte[] body)
    {
        int plainLen = body.Length - TagLength;
        var plain = new byte[plainLen];
        _recv.Decrypt(Nonce(_recvCounter++), body.AsSpan(0, plainLen), body.AsSpan(plainLen), plain);
        return ((MsgType)plain[0], plain[1..]);
    }

    // ---------- вспомогательное ----------
    static byte[] Nonce(ulong counter)
    {
        var n = new byte[12];
        BitConverter.TryWriteBytes(n.AsSpan(0, 8), counter);
        return n;
    }

    static byte[] Concat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)];
        int o = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
        return r;
    }

    static byte[] Mac(byte[] key, string label, byte[] sn, byte[] cn) =>
        HMACSHA256.HashData(key, Concat(Encoding.ASCII.GetBytes(label), sn, cn));

    static bool Equal(byte[] a, byte[] b) => CryptographicOperations.FixedTimeEquals(a, b);

    static bool StartsWithMagic(byte[] b) => b.AsSpan(0, Proto.Magic.Length).SequenceEqual(Proto.Magic);

    static (byte[] c2s, byte[] s2c) SessionKeys(byte[] key, byte[] sn, byte[] cn)
    {
        var salt = Concat(sn, cn);
        return (HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, salt, Encoding.ASCII.GetBytes("shturval c2s")),
                HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, salt, Encoding.ASCII.GetBytes("shturval s2c")));
    }

    public void Dispose()
    {
        _send.Dispose();
        _recv.Dispose();
        _stream.Dispose();
    }
}
