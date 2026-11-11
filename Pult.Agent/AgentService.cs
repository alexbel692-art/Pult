using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using System.Text;
using static Pult.Agent.Native;

namespace Pult.Agent;

/// <summary>
/// Служба Windows (SYSTEM). Сама экран не снимает — служба живёт в сессии 0 и экрана не видит.
/// Она держит запущенным «помощника» (этот же exe с аргументом helper) в сессии,
/// которая сейчас на мониторе, и перезапускает его при смене пользователя.
/// </summary>
sealed class AgentService : ServiceBase
{
    public const string Name = "PultAgent";
    public const string CadEventName = @"Global\PultAgentCAD";

    Thread? _thread;
    volatile bool _stopping;
    EventWaitHandle? _cad;
    Process? _helper;
    uint _helperSession = uint.MaxValue;
    DateTime _lastStart;

    public AgentService()
    {
        ServiceName = Name;
        CanStop = true;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        _cad = new EventWaitHandle(false, EventResetMode.AutoReset, CadEventName);
        Installer.KillOtherInstances();
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.Start();
        Log.Write("служба запущена");
    }

    protected override void OnStop()
    {
        _stopping = true;
        _cad?.Set();
        _thread?.Join(3000);
        KillHelper();
        Log.Write("служба остановлена");
    }

    protected override void OnShutdown() => OnStop();

    void Loop()
    {
        while (!_stopping)
        {
            try { EnsureHelper(); }
            catch (Exception ex) { Log.Write("служба: " + ex.Message); }

            // ждём секунду или сигнал Ctrl+Alt+Del от помощника
            if (_cad!.WaitOne(1000) && !_stopping)
            {
                try { SendSAS(false); Log.Write("отправлен Ctrl+Alt+Del"); }
                catch (Exception ex) { Log.Write("SendSAS: " + ex.Message); }
            }
        }
    }

    void EnsureHelper()
    {
        uint session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF) return; // идёт переключение сессий

        if (_helper is { HasExited: false } && _helperSession == session) return;
        if (DateTime.UtcNow - _lastStart < TimeSpan.FromSeconds(5)) return; // не перезапускать слишком часто

        KillHelper();
        _lastStart = DateTime.UtcNow;
        int pid = StartInSession(Environment.ProcessPath!, "helper", session);
        _helper = Process.GetProcessById(pid);
        _helperSession = session;
        Log.Write($"помощник запущен: pid {pid}, сессия {session}");
    }

    void KillHelper()
    {
        try { if (_helper is { HasExited: false }) _helper.Kill(); } catch { }
        _helper?.Dispose();
        _helper = null;
    }

    /// <summary>Запустить процесс от SYSTEM, но в указанной пользовательской сессии.</summary>
    static int StartInSession(string exe, string args, uint sessionId)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out var own))
            throw new Win32Exception();
        try
        {
            if (!DuplicateTokenEx(own, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var token))
                throw new Win32Exception();
            try
            {
                if (!SetTokenInformation(token, TokenSessionId, ref sessionId, sizeof(uint)))
                    throw new Win32Exception();

                var si = new STARTUPINFO { cb = System.Runtime.InteropServices.Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
                var cmd = new StringBuilder($"\"{exe}\" {args}");
                if (!CreateProcessAsUser(token, null, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_NO_WINDOW,
                        IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out var pi))
                    throw new Win32Exception();

                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return pi.dwProcessId;
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(own); }
    }
}
