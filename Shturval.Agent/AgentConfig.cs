using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Shturval.Common;

namespace Shturval.Agent;

/// <summary>Настройки агента: C:\ProgramData\Shturval\agent.json (доступ только SYSTEM и администраторам).</summary>
sealed class AgentConfig
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Shturval");
    static string FilePath => Path.Combine(Dir, "agent.json");

    public int Port { get; set; } = Proto.DefaultPort;
    /// <summary>С каких адресов можно подключаться: "192.168.1.10", "192.168.1.0/24". Пусто — с любых.</summary>
    public List<string> Allow { get; set; } = new();
    /// <summary>Показывать пользователю плашку «подключён администратор».</summary>
    public bool Banner { get; set; } = true;
    public string Salt { get; set; } = "";
    public string KeyProtected { get; set; } = "";

    public static AgentConfig Load() =>
        File.Exists(FilePath) ? JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(FilePath)) ?? new() : new();

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void SetPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(Proto.SaltLength);
        var key = Proto.DeriveKey(password, salt);
        Salt = Convert.ToBase64String(salt);
        KeyProtected = Convert.ToBase64String(Dpapi.Protect(key, machine: true));
    }

    public bool HasPassword => KeyProtected != "" && Salt != "";
    public byte[] GetSalt() => Convert.FromBase64String(Salt);
    public byte[] GetKey() => Dpapi.Unprotect(Convert.FromBase64String(KeyProtected), machine: true);

    public bool IsAllowed(IPAddress ip)
    {
        if (Allow.Count == 0) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return Allow.Any(rule => Matches(rule.Trim(), ip));
    }

    static bool Matches(string rule, IPAddress ip)
    {
        var parts = rule.Split('/');
        if (!IPAddress.TryParse(parts[0], out var net)) return false;
        if (parts.Length == 1) return net.Equals(ip);
        if (!int.TryParse(parts[1], out int bits) || net.AddressFamily != ip.AddressFamily) return false;

        var a = net.GetAddressBytes();
        var b = ip.GetAddressBytes();
        for (int i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            int mask = bits >= 8 ? 0xFF : (0xFF << (8 - bits)) & 0xFF;
            if ((a[i] & mask) != (b[i] & mask)) return false;
        }
        return true;
    }
}

static class Log
{
    static readonly object Sync = new();
    static string FilePath => Path.Combine(AgentConfig.Dir, "agent.log");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AgentConfig.Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(FilePath, FilePath + ".old", true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch { /* лог не должен ронять агент */ }
    }
}
