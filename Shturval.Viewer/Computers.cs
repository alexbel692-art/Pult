using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shturval.Common;

namespace Shturval.Viewer;

sealed class Computer
{
    public string Name { get; set; } = "";
    public string Office { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = Proto.DefaultPort;
    /// <summary>Пароль, зашифрованный DPAPI текущего пользователя Windows.</summary>
    public string SavedPassword { get; set; } = "";

    [JsonIgnore] public string Status { get; set; } = "";

    public string? GetPassword()
    {
        if (SavedPassword == "") return null;
        try { return Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(SavedPassword), false)); }
        catch { return null; }
    }

    public void SetPassword(string? password) =>
        SavedPassword = string.IsNullOrEmpty(password) ? "" : Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes(password), false));
}

static class ComputerStore
{
    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shturval", "computers.json");

    public static List<Computer> Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Computer>>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }

    public static void Save(List<Computer> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
    }
}

static class NetUtil
{
    public static async Task<bool> IsPortOpen(string host, int port, int timeoutMs)
    {
        using var tcp = new TcpClient();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await tcp.ConnectAsync(host, port, cts.Token); return true; }
        catch { return false; }
    }

    public static async Task<string> ResolveName(IPAddress ip)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(ip);
            if (await Task.WhenAny(lookup, Task.Delay(1500)) == lookup)
                return lookup.Result.HostName.Split('.')[0];
        }
        catch { }
        return ip.ToString();
    }

    public static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public static IPAddress FromUInt(uint v) =>
        new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
}
