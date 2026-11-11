using System.ServiceProcess;

namespace Shturval.Agent;

static class Program
{
    static int Main(string[] args)
    {
        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        switch (cmd)
        {
            case "service":   // так запускает Windows
                ServiceBase.Run(new AgentService());
                return 0;
            case "helper":    // так запускает служба, в сессии пользователя
            case "debug":     // ручной запуск для отладки, без службы
                return RunHelper();
            case "install":
                return Installer.Install(args[1..]);
            case "uninstall":
                return Installer.Uninstall(args[1..]);
            case "setpass":
                return Installer.SetPassword(args[1..]);
            default:
                Console.WriteLine("""
                    Штурвал — агент удалённого управления

                      ShturvalAgent install --password <пароль> [--allow 192.168.1.10,10.0.0.0/24] [--port 5650] [--no-banner]
                      ShturvalAgent setpass <новый пароль>
                      ShturvalAgent uninstall [--purge]
                      ShturvalAgent debug        запуск без службы (для проверки)

                    Запускать от имени администратора.
                    """);
                return 0;
        }
    }

    static int RunHelper()
    {
        Native.SetProcessDpiAwarenessContext(Native.DPI_PER_MONITOR_AWARE_V2);
        var cfg = AgentConfig.Load();
        if (!cfg.HasPassword)
        {
            Log.Write("помощник: пароль не задан, выполните install");
            Console.WriteLine("Пароль не задан. Выполните: ShturvalAgent install --password ...");
            return 2;
        }
        try
        {
            new HelperServer(cfg).RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write("помощник: " + ex);
            Console.WriteLine(ex.Message);
            return 1;
        }
    }
}
