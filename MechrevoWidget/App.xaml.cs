using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MechrevoWidget;

public partial class App : Application
{
    // 单实例:新启动的实例接管——先通知旧实例自己退出,不响应(旧版本)就直接结束它。
    // 这样换新版 exe 时双击运行即可,不用先手动关旧的。
    private const string MutexName = @"Local\MechrevoWidget.Instance";
    private const string QuitEventName = @"Local\MechrevoWidget.Quit";

    private Mutex? _mutex;
    private EventWaitHandle? _quitEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 软件渲染只给任务栏胶囊开(见 MainWindow.OnSourceInitialized);
        // 弹出面板走硬件渲染,DWM 亚克力背景需要硬件路径输出的透明像素。
        TakeOverPreviousInstance();
        ListenForQuit();
        base.OnStartup(e);
    }

    private void TakeOverPreviousInstance()
    {
        _mutex = new Mutex(false, MutexName);
        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);

        bool owned;
        try { owned = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }

        if (!owned)
        {
            // 新版实例在监听退出事件,礼貌请它走
            _quitEvent.Set();
            try { owned = _mutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { owned = true; }
            MechrevoWidget.MainWindow.Log(owned ? "takeover: previous instance quit" : "takeover: previous instance did not respond");
        }

        // 不认识互斥量的旧版本 / 没响应的实例:按进程名结束(exe 可能被改名成 MechrevoWidget.new.exe 等)
        var me = Compat.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id != me && p.ProcessName.StartsWith("MechrevoWidget", StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill();
                    p.WaitForExit(2000);
                    MechrevoWidget.MainWindow.Log($"takeover: killed {p.ProcessName} ({p.Id})");
                }
            }
            catch (Exception ex) { MechrevoWidget.MainWindow.Log("takeover: " + ex.Message); }
            finally { p.Dispose(); }
        }

        if (!owned)
        {
            try { _mutex.WaitOne(TimeSpan.FromSeconds(1)); }
            catch (AbandonedMutexException) { }
        }
    }

    /// <summary>后台等待"退出"信号(来自之后启动的新实例)。</summary>
    private void ListenForQuit()
    {
        var ev = _quitEvent!;
        var t = new Thread(() =>
        {
            try
            {
                ev.WaitOne();
                Dispatcher.BeginInvoke(() => Shutdown());
            }
            catch { }
        }) { IsBackground = true, Name = "QuitListener" };
        t.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
