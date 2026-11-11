using System.Security.Cryptography;
using System.Text;

namespace Shturval.Common;

public static class Proto
{
    public const int DefaultPort = 5650;
    public const int Pbkdf2Iterations = 200_000;
    public const int SaltLength = 16;
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("SHTV1");

    /// <summary>Ключ из пароля. Агент хранит только этот ключ, а не сам пароль.</summary>
    public static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
}

public enum MsgType : byte
{
    // агент -> консоль
    Frame = 1,          // int w, int h, ushort n, n * (ushort x, y, w, h, int len, jpeg)
    Notice = 2,         // текст UTF-8

    // консоль -> агент
    FrameRequest = 10,  // byte quality, bool full
    MouseMove = 11,     // int x, int y
    MouseButton = 12,   // byte button(1 L,2 R,3 M,4 X1,5 X2), bool down, int x, int y
    MouseWheel = 13,    // short delta, bool horizontal
    Key = 14,           // ushort vk, ushort scan, byte flags (1 = down, 2 = extended)
    CtrlAltDel = 15,
    TypeText = 16,      // текст UTF-8 — напечатать на удалённом ПК
}

public static class Packets
{
    public static byte[] Build(Action<BinaryWriter> write)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms)) write(w);
        return ms.ToArray();
    }

    public static BinaryReader Reader(byte[] payload) => new(new MemoryStream(payload));
}
