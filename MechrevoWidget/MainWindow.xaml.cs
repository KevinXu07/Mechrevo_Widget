using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MechrevoWidget;

public sealed partial class MainWindow : Window
{
    private readonly MqttService _mqtt = new();
    private ControlPanel? _panel;
    private FanStatus? _lastStatus;
    private EcReader.Telemetry? _lastTele;
    private bool _connected;

    private const int BarHeight = 30;
    private const int BarMargin = 2;
    private int BarWidth => Math.Max(80, (int)Math.Ceiling(Card.ActualWidth));

    internal static void Log(string msg)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "mechrevo_widget.log"), $"{DateTime.Now:HH:mm:ss.fff} [ui] {msg}\n"); }
        catch { }
    }

    public MainWindow()
    {
        InitializeComponent();
        _mqtt.StatusUpdated += s => Dispatcher.InvokeAsync(() => ApplyStatus(s));
        _mqtt.ConnectionChanged += c => Dispatcher.InvokeAsync(() => ApplyConnection(c));
        _mqtt.Start();
        AppConfig.MigrateAutoStart();
        StartTelemetryPoll();
        Closed += (_, _) =>
        {
            try { _mqtt.Dispose(); } catch { }
            try { EcReader.Instance.Dispose(); } catch { }
        };
    }

    // ───────────────────────── 右键菜单 ─────────────────────────

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        _panel?.Dismiss();
        var mode = _connected ? Theme.ModeOf(_lastStatus) : null;
        foreach (var (item, key) in new[] { (MenuOffice, "office"), (MenuGaming, "gaming"), (MenuTurbo, "turbo"), (MenuCustom, "custom") })
        {
            item.Tag = Theme.Brush(Theme.ModeColor[key]);
            item.IsChecked = key == mode;
            item.IsEnabled = _connected;
        }
        MenuAutoStart.IsChecked = AppConfig.IsAutoStartEnabled();
    }

    private void OnMenuMode(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { CommandParameter: string key })
            _ = _mqtt.SwitchModeAsync(key);
    }

    private async void OnMenuAutoStart(object sender, RoutedEventArgs e)
    {
        bool want = MenuAutoStart.IsChecked;
        bool ok = await Task.Run(() => AppConfig.SetAutoStart(want));
        if (!ok) Log($"menu: autostart {(want ? "enable" : "disable")} failed");
    }

    private void OnMenuReembed(object sender, RoutedEventArgs e)
    {
        if (Embed()) Log("menu: re-embedded");
    }

    private void OnMenuOpenLog(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(Path.GetTempPath(), "mechrevo_widget.log");
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, "");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log("menu: open log: " + ex.Message); }
    }

    private void OnMenuExit(object sender, RoutedEventArgs e)
    {
        Log("menu: exit");
        _panel?.Close();
        Application.Current.Shutdown();
    }

    private void OnSourceInitialized(object sender, EventArgs e)
    {
        // 胶囊 SetParent 挂到任务栏(子窗口)后,WPF 硬件渲染内容不会被 DWM 合成,
        // 只给这个窗口切软件渲染(弹出面板仍走硬件,DWM 亚克力需要)。
        if (System.Windows.Interop.HwndSource.FromHwnd(WindowHelper.GetHwnd(this)) is { CompositionTarget: { } ct })
            ct.RenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // 不再叠 accent blur:那层模糊永远是直角矩形;任务栏本身已是模糊背景,
        // 胶囊只画半透明圆角底色(分层窗口,圆角外完全透明)即可。
        EmbedOrRetry();
        StartKeepAlive();
        Card.SizeChanged += (_, _) => Embed(); // 内容变宽(如 "—" → "71°")时同步 HWND 尺寸
    }

    private bool Embed() => WindowHelper.EmbedInTaskbar(this, BarWidth, BarHeight, BarMargin);

    private void EmbedOrRetry(int attempt = 0)
    {
        if (Embed())
        {
            Log("embed: ok");
            return;
        }
        if (attempt >= 5) { Log("embed: gave up"); return; }
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        t.Tick += (_, _) => { t.Stop(); EmbedOrRetry(attempt + 1); };
        t.Start();
    }

    /// <summary>
    /// 每 5 秒检查:被 Explorer 重启甩掉了就重新挂;
    /// 托盘图标增减导致 TrayNotifyWnd 左缘移动时也跟着重新定位。
    /// </summary>
    private void StartKeepAlive()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (!WindowHelper.IsInTaskbar(this))
                {
                    if (Embed()) Log("keepalive: re-embedded");
                }
                else if (WindowHelper.NeedsReposition(this, BarWidth, BarMargin))
                {
                    Embed(); // 仅重新定位
                }
            }
            catch (Exception ex) { Log("keepalive: " + ex); }
        };
        timer.Start();
    }

    /// <summary>
    /// 唯一的 EC 采样点:每 2 秒在后台线程读一次,结果同时喂给胶囊和(打开时的)面板。
    /// 驱动不可用时静默退化为 "—"。
    /// </summary>
    private void StartTelemetryPoll()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += async (_, _) => await PollOnceAsync();
        timer.Start();
        _ = PollOnceAsync();
    }

    private async Task PollOnceAsync()
    {
        EcReader.Telemetry? t = await Task.Run(() =>
        {
            var ec = EcReader.Instance;
            return ec.Open() ? ec.Read() : (EcReader.Telemetry?)null;
        });
        _lastTele = t;
        SetTemp(CpuTemp, t?.CpuTemp);
        SetTemp(GpuTemp, t?.GpuTemp is int g && g > 0 ? g : null); // dGPU 休眠时 EC 报 0
        UpdateTooltip();
        _panel?.UpdateTelemetry(t, _lastStatus);
    }

    private static void SetTemp(System.Windows.Controls.TextBlock tb, int? v)
    {
        tb.Text = v is int t ? $"{t}°" : "—";
        tb.Foreground = Theme.Brush(v is int t2 ? Theme.TempText(t2) : Theme.TextDim);
    }

    private void UpdateTooltip()
    {
        var mode = Theme.ModeOf(_lastStatus);
        var lines = new List<string>();
        if (!_connected) lines.Add("未连接 GCUBridge");
        else if (mode is not null)
        {
            var name = Theme.ModeLabel[mode];
            if (mode == "turbo") name += _lastStatus!.Silent == "1" ? " · 静音" : " · 高能";
            lines.Add("档位:" + name);
            if (_lastStatus!.CpuPl1 is string p1) lines.Add($"CPU PL1 {p1}W" + (_lastStatus.GpuTgp is string tgp ? $" · GPU TGP {tgp}W" : ""));
        }
        if (_lastTele is { } t && (t.Fan1Rpm is int || t.Fan2Rpm is int))
            lines.Add($"风扇 {t.Fan1Rpm?.ToString() ?? "—"} / {t.Fan2Rpm?.ToString() ?? "—"} rpm");
        if (EcReader.Instance.IsAvailable == false && _lastTele is null)
            lines.Add("温度不可读(需管理员运行)");
        BarTip.Content = string.Join("\n", lines);
    }

    private void OnBarClick(object sender, RoutedEventArgs e)
    {
        if (_panel is { IsLoaded: true })
        {
            _panel.Dismiss();
            _panel = null;
            return;
        }
        try
        {
            var hwnd = WindowHelper.GetHwnd(this);
            var p = new ControlPanel(_mqtt);
            p.Closed += (_, _) => { if (_panel == p) _panel = null; };
            _panel = p;
            p.ApplyConnection(_connected);
            if (_lastStatus is not null) p.ApplyStatus(_lastStatus);
            p.UpdateTelemetry(_lastTele, _lastStatus);
            p.ShowAbove(hwnd);
            _ = _mqtt.QueryStatusAsync(); // 打开时顺手刷新一次 PL/TGP
            Log("panel: shown");
        }
        catch (Exception ex) { Log("panel: " + ex); }
    }

    private void ApplyStatus(FanStatus s)
    {
        try
        {
            _lastStatus = s;
            SetDot(Theme.ModeOf(s) is string m ? Theme.ModeColor[m] : Theme.Idle);
            UpdateTooltip();
            _panel?.ApplyStatus(s);
        }
        catch (Exception ex) { Log("ApplyStatus: " + ex); }
    }

    private void ApplyConnection(bool connected)
    {
        _connected = connected;
        SetDot(connected && Theme.ModeOf(_lastStatus) is string m ? Theme.ModeColor[m] : Theme.Idle);
        UpdateTooltip();
        _panel?.ApplyConnection(connected);
    }

    private void SetDot(System.Windows.Media.Color c)
    {
        StatusDot.Fill = Theme.Brush(c);
        StatusHalo.Fill = Theme.Brush(c);
    }
}
