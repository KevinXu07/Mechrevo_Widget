using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MechrevoWidget;

/// <summary>控制面板弹窗:档位/狂暴子模式/指标卡片/自启,从任务栏胶囊弹出。</summary>
public partial class ControlPanel : Window
{
    private readonly MqttService _mqtt;
    private readonly string[] _order = { "office", "gaming", "turbo", "custom" };
    private readonly Dictionary<string, Button> _buttons = new();
    private readonly SolidColorBrush _segBrush = new(Theme.ModeColor["office"]);

    private string? _shownMode;          // 分段控件当前高亮
    private string? _pendingMode;        // 刚点击、尚未被 Fan/Status 确认的档位
    private DateTime _pendingUntil;
    private bool? _pendingSilent;
    private DateTime _pendingSubUntil;
    private bool _indicatorPlaced;
    private bool _connected;
    private bool _closing;
    private bool _uiReady;
    private double _anchorBottom = double.NaN; // 面板底边(DIP),高度变化时保持贴住任务栏

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    public ControlPanel(MqttService mqtt)
    {
        _mqtt = mqtt;
        InitializeComponent();
        _buttons["office"] = BtnOffice; _buttons["gaming"] = BtnGaming;
        _buttons["turbo"] = BtnTurbo; _buttons["custom"] = BtnCustom;
        SegIndicator.Background = _segBrush;
        AutoStartToggle.IsChecked = AppConfig.IsAutoStartEnabled();
        _uiReady = true;
    }

    // ───────────────────────── 弹出 / 收起 ─────────────────────────

    /// <summary>弹出:右边缘对齐托盘通知区左边缘,底边贴任务栏上缘,视觉上"长在托盘旁"。</summary>
    public void ShowAbove(IntPtr anchorHwnd)
    {
        IntPtr tray = WindowHelper.FindTray();
        IntPtr trayNotify = WindowHelper.FindTrayNotify(tray);
        var r = new RECT();
        if (trayNotify != IntPtr.Zero) GetWindowRect(trayNotify, out r);
        else GetWindowRect(tray, out r); // 退化:贴任务栏右侧

        double scale = GetDpiForWindow(anchorHwnd) / 96.0;
        if (scale <= 0) scale = 1.0;

        // SizeToContent 的高度要到布局后才知道,先手动量一次,避免首帧位置跳动
        // (不写 Height,否则会打断 SizeToContent)
        double h = 300;
        if (Content is FrameworkElement root)
        {
            root.Measure(new Size(Width, double.PositiveInfinity));
            h = root.DesiredSize.Height;
        }

        var wa = SystemParameters.WorkArea;
        // 窗口即卡片(阴影由 DWM 画在窗口外),间距直接算进窗口位置
        _anchorBottom = Math.Min(r.Top / scale - 8, wa.Bottom);
        Left = Math.Max(wa.Left, r.Left / scale - Width - 6);
        Top = Math.Max(wa.Top, _anchorBottom - h);

        Show();
        Activate();
        PlayEnter();
    }

    private void OnSourceInitialized(object sender, EventArgs e)
    {
        WindowHelper.EnableRoundedAcrylic(this);
        WindowHelper.HideFromAltTab(this);
    }

    /// <summary>狂暴子模式展开/收起导致高度变化时,保持底边不动、向上生长。</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.HeightChanged || double.IsNaN(_anchorBottom) || _closing) return;
        BeginAnimation(TopProperty, null); // 入场动画进行中也以最终位置为准
        Top =Math.Max(SystemParameters.WorkArea.Top, _anchorBottom - ActualHeight);
    }

    // 滑动动画移动的是整个窗口(Top),不是窗口里的卡片:
    // 亚克力背景跟着 HWND 走,只平移卡片会和背景错位。
    // 非分层窗口不支持整窗透明度,淡入淡出作用于内容;窗口出现/消失本身由 DWM 过渡动画处理。

    /// <summary>入场:淡入 + 上滑 10px,180ms ease-out。</summary>
    private void PlayEnter()
    {
        double top = Top;
        PanelCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = EaseOut });
        var rise = new DoubleAnimation(top + 10, top, TimeSpan.FromMilliseconds(180)) { EasingFunction = EaseOut };
        rise.Completed += (_, _) => { BeginAnimation(TopProperty, null); Top = top; };
        BeginAnimation(TopProperty, rise);
    }

    /// <summary>出场:淡出 + 下滑 8px,140ms ease-in,完成后关闭。可重复调用。</summary>
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        double top = Top;
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)) { EasingFunction = easeIn };
        fade.Completed += (_, _) => { try { Close(); } catch (InvalidOperationException) { } };
        PanelCard.BeginAnimation(OpacityProperty, fade);
        BeginAnimation(TopProperty, new DoubleAnimation(top + 8, TimeSpan.FromMilliseconds(140)) { EasingFunction = easeIn });
    }

    private void OnDeactivated(object sender, EventArgs e) => Dismiss();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Dismiss();
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    // ───────────────────────── 状态 ─────────────────────────

    public void ApplyConnection(bool connected)
    {
        _connected = connected;
        if (!connected) SetChip("未连接", Theme.Idle);
    }

    public void ApplyStatus(FanStatus s)
    {
        var mode = Theme.ModeOf(s);
        if (mode is null) { SetChip("切换中…", Theme.Idle); return; }

        // 刚点击的档位在被确认前,忽略旧状态,防止高亮来回跳
        if (_pendingMode is not null)
        {
            if (mode == _pendingMode || DateTime.Now > _pendingUntil) _pendingMode = null;
            else return;
        }

        SelectMode(mode, animate: _indicatorPlaced);

        bool? silent = s.Silent == "1" ? true : s.Extreme == "1" ? false : null;
        if (_pendingSilent is not null)
        {
            if (silent == _pendingSilent || DateTime.Now > _pendingSubUntil) _pendingSilent = null;
            else silent = _pendingSilent;
        }
        HighlightSub(mode == "turbo" ? silent : null);

        var label = Theme.ModeLabel[mode];
        if (mode == "turbo" && silent is bool sl) label += sl ? " · 静音" : " · 高能";
        SetChip(label, Theme.ModeColor[mode]);

        CpuSub.Text = s.CpuPl1 is string p1
            ? (s.CpuPl2 is string p2 && p2 != p1 ? $"PL1 {p1}W · PL2 {p2}W" : $"PL1 {p1}W")
            : "PL1 —";
        GpuSub.Text = s.GpuTgp is string tgp && tgp != "0" ? $"TGP {tgp}W" : "TGP —";
    }

    private void SetChip(string text, Color c)
    {
        StatusText.Text = text;
        ChipDot.Fill = Theme.Brush(c);
    }

    // ───────────────────────── 分段控件 ─────────────────────────

    private void SelectMode(string mode, bool animate)
    {
        _shownMode = mode;
        SubModePanel.Visibility = mode == "turbo" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (k, b) in _buttons)
            b.Foreground = Theme.Brush(k == mode ? Theme.TextOnAccent : Theme.TextDim);
        MoveIndicator(animate);
    }

    private void MoveIndicator(bool animate)
    {
        if (_shownMode is null || SegHost.ActualWidth <= 0) return;
        double colW = SegHost.ActualWidth / _order.Length;
        double x = Array.IndexOf(_order, _shownMode) * colW;
        var color = Theme.ModeColor[_shownMode];
        SegIndicator.Width = colW;

        if (animate)
        {
            var dur = TimeSpan.FromMilliseconds(220);
            SegX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, dur) { EasingFunction = EaseOut });
            _segBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, dur) { EasingFunction = EaseOut });
        }
        else
        {
            SegX.BeginAnimation(TranslateTransform.XProperty, null);
            _segBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            SegX.X = x;
            _segBrush.Color = color;
        }
        SegIndicator.Opacity = 1;
        _indicatorPlaced = true;
    }

    private void OnSegHostSizeChanged(object sender, SizeChangedEventArgs e) => MoveIndicator(animate: false);

    private void HighlightSub(bool? silent)
    {
        BtnTurboSilent.Background = silent == true ? Theme.Brush(Theme.SubSilent) : Brushes.Transparent;
        BtnTurboExtreme.Background = silent == false ? Theme.Brush(Theme.SubExtreme) : Brushes.Transparent;
        BtnTurboSilent.Foreground = Theme.Brush(silent == true ? Theme.TextOnAccent : Theme.TextDim);
        BtnTurboExtreme.Foreground = Theme.Brush(silent == false ? Theme.TextOnAccent : Theme.TextDim);
    }

    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } || key == _shownMode) return;
        if (!_connected) { SetChip("未连接", Theme.Idle); return; }
        _pendingMode = key;
        _pendingUntil = DateTime.Now.AddSeconds(4);
        SelectMode(key, animate: true);
        if (key != "turbo") HighlightSub(null);
        SetChip("切换中…", Theme.ModeColor[key]);
        _ = _mqtt.SwitchModeAsync(key);
    }

    private void OnTurboSubModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string sub }) return;
        bool silent = sub == "silent";
        _pendingSilent = silent;
        _pendingSubUntil = DateTime.Now.AddSeconds(4);
        HighlightSub(silent);
        _ = _mqtt.SwitchTurboSubModeAsync(extreme: !silent);
    }

    // ───────────────────────── 指标卡片 ─────────────────────────

    /// <summary>由 MainWindow 的 2 秒轮询驱动;t 为 null 表示 EC 不可读。</summary>
    public void UpdateTelemetry(EcReader.Telemetry? t, FanStatus? s)
    {
        EcHint.Visibility = t is null && EcReader.Instance.IsAvailable == false ? Visibility.Visible : Visibility.Collapsed;

        SetTempTile(CpuVal, CpuBar, t?.CpuTemp);
        // dGPU 休眠时 EC 报 0,当作无读数
        SetTempTile(GpuVal, GpuBar, t?.GpuTemp is int g && g > 0 ? g : null);

        // 风扇:大字显示较快的那台,两条细条分别是左/右风扇 PWM 占空比
        int? r1 = t?.Fan1Rpm, r2 = t?.Fan2Rpm;
        int? maxRpm = r1 is int a && r2 is int b ? Math.Max(a, b) : r1 ?? r2;
        FanVal.Text = maxRpm?.ToString() ?? "—";
        int? d1 = Duty(t?.Fan1Pwm), d2 = Duty(t?.Fan2Pwm);
        SetBar(Fan1Bar, d1 / 100.0);
        SetBar(Fan2Bar, d2 / 100.0);
        FanSub.Text = d1 is null && d2 is null ? "占空比 —" : $"占空比 {d1?.ToString() ?? "—"}% · {d2?.ToString() ?? "—"}%";
        FanVal.ToolTip = $"左 {r1?.ToString() ?? "—"} rpm · 右 {r2?.ToString() ?? "—"} rpm";

        if (t?.BattSoc is int soc and >= 0 and <= 100)
        {
            BattVal.Text = $"{soc}%";
            SetBar(BattBar, soc / 100.0, soc <= 20 ? Color.FromRgb(248, 113, 113) : Color.FromRgb(134, 239, 172));
        }
        else { BattVal.Text = "—"; SetBar(BattBar, null); }
        BattSub.Text = t?.BattCycles is int cyc ? $"循环 {cyc} 次" : "循环 —";
    }

    /// <summary>EC PWM 0-255 → 0-100%。</summary>
    private static int? Duty(int? raw) => raw is int v ? (int)Math.Round(Compat.Clamp(v, 0, 255) / 2.55) : null;

    private void SetTempTile(TextBlock val, Border bar, int? temp)
    {
        if (temp is int c)
        {
            val.Text = $"{c}°";
            val.Foreground = Theme.Brush(Theme.TempText(c));
            // 30°~100° 映射到进度条
            SetBar(bar, (c - 30) / 70.0, Theme.TempBar(c));
        }
        else
        {
            val.Text = "—";
            val.Foreground = Theme.Brush(Theme.TextDim);
            SetBar(bar, null);
        }
    }

    private static void SetBar(Border bar, double? frac, Color? color = null)
    {
        if (color is Color c) bar.Background = Theme.Brush(c);
        double target = frac is double f ? Compat.Clamp(f, 0.0, 1.0) : 0.0;
        var st = (ScaleTransform)bar.RenderTransform;
        if (st.IsFrozen) { st = new ScaleTransform(0, 1); bar.RenderTransform = st; }
        st.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(400)) { EasingFunction = EaseOut });
    }

    // ───────────────────────── 自启 ─────────────────────────

    private async void OnAutoStartClick(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        bool want = AutoStartToggle.IsChecked == true;
        AutoStartToggle.IsEnabled = false;
        bool ok = await Task.Run(() => AppConfig.SetAutoStart(want));
        AutoStartToggle.IsEnabled = true;
        if (!ok) AutoStartToggle.IsChecked = !want;
    }
}
