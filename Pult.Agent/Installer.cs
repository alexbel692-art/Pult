using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;

namespace Pult.Agent;

/// <summary>Установка, удаление и смена пароля агента из командной строки.</summary>
static class Installer
{
    static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pult Agent");
    static string InstalledExe => Path.Combine(InstallDir, "PultAgent.exe");
    const string FirewallRule = "Pult Agent";

    public static int Install(string[] args)
    {
        if (!IsAdmin()) { Console.WriteLine("Запустите от имени администратора."); return 1; }

        var cfg = AgentConfig.Load();
        string? password = null;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--password": password = args[++i]; break;
                    case "--allow": cfg.Allow = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); break;
                    case "--port": cfg.Port = int.Parse(args[++i]); break;
                    case "--no-banner": cfg.Banner = false; break;
                    case "--banner": cfg.Banner = true; break;
                    default: Console.WriteLine($"Неизвестный параметр: {args[i]}"); return 1;
                }
            }
        }
        catch (IndexOutOfRangeException) { Console.WriteLine("После параметра не указано значение."); return 1; }

        if (password != null)
        {
            if (password.Length < 8) { Console.WriteLine("Пароль должен быть не короче 8 символов."); return 1; }
            cfg.SetPassword(password);
        }
        if (!cfg.HasPassword) { Console.WriteLine("Укажите пароль: --password <пароль>"); return 1; }
        if (cfg.Allow.Count == 0) Console.WriteLine("ВНИМАНИЕ: --allow не задан, подключаться можно с любого адреса сети.");

        StopService();
        KillOtherInstances();

        Directory.CreateDirectory(InstallDir);
        var self = Path.GetFullPath(Environment.ProcessPath!);
        if (!string.Equals(self, Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
            File.Copy(self, InstalledExe, overwrite: true);

        cfg.Save();
        // папка с настройками — только SYSTEM и администраторы
        Run("icacls", AgentConfig.Dir, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F");

        var bin = $"\"{InstalledExe}\" service";
        if (ServiceExists())
            Run("sc.exe", "config", AgentService.Name, "binPath=", bin, "start=", "auto");
        else
            Run("sc.exe", "create", AgentService.Name, "binPath=", bin, "start=", "auto", "DisplayName=", "Pult Agent");
        Run("sc.exe", "description", AgentService.Name, "Пульт: удалённое управление по локальной сети");
        Run("sc.exe", "failure", AgentService.Name, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/5000");

        Run("netsh", "advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}");
        Run("netsh", "advfirewall", "firewall", "add", "rule", $"name={FirewallRule}", "dir=in", "action=allow",
            "protocol=TCP", $"localport={cfg.Port}", $"program={InstalledExe}", "profile=any",
            $"remoteip={(cfg.Allow.Count > 0 ? string.Join(",", cfg.Allow) : "any")}");

        // разрешить службе нажимать Ctrl+Alt+Del
        using (var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
        {
            int cur = key.GetValue("SoftwareSASGeneration") as int? ?? 0;
            if ((cur & 1) == 0) key.SetValue("SoftwareSASGeneration", cur | 1, RegistryValueKind.DWord);
        }

        using (var sc = new ServiceController(AgentService.Name)) sc.Start();

        Console.WriteLine($"Агент установлен и запущен. Порт {cfg.Port}, " +
                          $"доступ: {(cfg.Allow.Count > 0 ? string.Join(", ", cfg.Allow) : "любой адрес")}, " +
                          $"плашка: {(cfg.Banner ? "да" : "нет")}.");
        return 0;
    }

    public static int Uninstall(string[] args)
    {
        if (!IsAdmin()) { Console.WriteLine("Запустите от имени администратора."); return 1; }
        StopService();
        KillOtherInstances();
        Run("sc.exe", "delete", AgentService.Name);
        Run("netsh", "advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}");
        if (args.Contains("--purge"))
            try { Directory.Delete(AgentConfig.Dir, true); } catch { }
        Console.WriteLine($"Агент удалён. Папку {InstallDir} можно удалить вручную.");
        return 0;
    }

    public static int SetPassword(string[] args)
    {
        if (!IsAdmin()) { Console.WriteLine("Запустите от имени администратора."); return 1; }
        if (args.Length < 1 || args[0].Length < 8) { Console.WriteLine("Использование: PultAgent setpass <новый пароль, от 8 символов>"); return 1; }
        var cfg = AgentConfig.Load();
        cfg.SetPassword(args[0]);
        cfg.Save();
        KillOtherInstances(); // служба сама перезапустит помощника с новым паролем
        Console.WriteLine("Пароль изменён.");
        return 0;
    }

    /// <summary>Завершить все остальные процессы PultAgent (помощников).</summary>
    public static void KillOtherInstances()
    {
        foreach (var p in Process.GetProcessesByName("PultAgent"))
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId) continue;
                if (IsServiceProcess(p)) continue;
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
        }
    }

    static bool IsServiceProcess(Process p) => p.SessionId == 0;

    static void StopService()
    {
        try
        {
            using var sc = new ServiceController(AgentService.Name);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
        }
        catch (InvalidOperationException) { /* службы нет */ }
        catch (System.ServiceProcess.TimeoutException) { }
    }

    static bool ServiceExists() => ServiceController.GetServices().Any(s => s.ServiceName == AgentService.Name);

    static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    static void Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && !(file == "netsh" && args.Contains("delete")))
            Console.WriteLine($"  {file} {string.Join(' ', args)} → код {p.ExitCode}");
    }
}
