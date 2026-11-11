using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Shturval.Common;

/// <summary>Шифрование средствами Windows (DPAPI) — для хранения ключей и паролей на диске.</summary>
public static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr h);

    const int UI_FORBIDDEN = 0x1, LOCAL_MACHINE = 0x4;

    /// <param name="machine">true — расшифровать сможет любой процесс этого ПК (для службы); false — только этот пользователь.</param>
    public static byte[] Protect(byte[] data, bool machine) => Run(data, true, machine);
    public static byte[] Unprotect(byte[] data, bool machine) => Run(data, false, machine);

    static byte[] Run(byte[] data, bool protect, bool machine)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new DATA_BLOB { cbData = data.Length, pbData = handle.AddrOfPinnedObject() };
            var output = new DATA_BLOB();
            int flags = UI_FORBIDDEN | (machine ? LOCAL_MACHINE : 0);
            bool ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, flags, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, flags, ref output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally { LocalFree(output.pbData); }
        }
        finally { handle.Free(); }
    }
}
