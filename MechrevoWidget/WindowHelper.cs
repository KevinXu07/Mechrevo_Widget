using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MechrevoWidget;

/// <summary>
/// Win32 interop:把窗口嵌入任务栏系统托盘通知区左侧,
/// 用 SetParent(SetParent 对 Shell_TrayWnd 这类系统窗口是有效且成熟的用法,
/// 例如 AutoHotkey 等工具也这么挂自定义区)。
/// </summary>
internal static class WindowHelper
{
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint GA_PARENT = 1;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private const int GWL_STYLE = -16;
    private const int WS_CHILD = 0x40000000;
    private const int WS_POPUP = unchecked((int)0x80000000);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool MapWindowPoints(IntPtr hWndFrom, IntPtr hWndTo, ref RECT lpPoints, uint cPoints);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    public static IntPtr GetHwnd(Window window) => new WindowInteropHelper(window).EnsureHandle();

    /// <summary>任务栏(Shell_TrayWnd)hwnd;找不到返回 Zero。</summary>
    public static IntPtr FindTray() => FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);

    /// <summary>系统托盘通知区(TrayNotifyWnd)hwnd;找不到返回 Zero。</summary>
    public static IntPtr FindTrayNotify(IntPtr shellTray) =>
        shellTray == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(shellTray, IntPtr.Zero, "TrayNotifyWnd", null);

    /// <summary>
    /// 把窗口挂到任务栏并放在托盘通知区左侧边缘(系统原生控件视觉位置,紧贴 TrayNotifyWnd)。
    /// 坐标全部用设备像素;宽高在父窗口坐标系下对齐托盘垂直居中。
    /// </summary>
    public static bool EmbedInTaskbar(Window window, int widthDip, int heightDip, int gapDip)
    {
        var hwnd = GetHwnd(window);
        IntPtr shellTray = FindTray();
        if (shellTray == IntPtr.Zero) return false;
        IntPtr trayNotify = FindTrayNotify(shellTray);

        var trayRect = new RECT();
        GetWindowRect(shellTray, out trayRect);
        int trayH = trayRect.Bottom - trayRect.Top;

        double scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;
        int w = (int)Math.Round(widthDip * scale);
        int h = (int)Math.Round(heightDip * scale);
        int gap = (int)Math.Round(gapDip * scale);

        int style = GetWindowLong(hwnd, GWL_STYLE);
        style = (style & ~WS_POPUP) | WS_CHILD;
        SetWindowLong(hwnd, GWL_STYLE, style);
        SetParent(hwnd, shellTray);

        int x, y;
        if (trayNotify != IntPtr.Zero)
        {
            var tnRect = new RECT();
            GetWindowRect(trayNotify, out tnRect);
            var pt = new RECT { Left = tnRect.Left, Top = tnRect.Top };
            MapWindowPoints(IntPtr.Zero, shellTray, ref pt, 2);
            // 紧贴托盘通知区左边缘
            x = pt.Left - w - gap;
        }
        else
        {
            x = (trayRect.Right - trayRect.Left) - w - gap;
        }
        y = Math.Max(0, (trayH - h) / 2);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOACTIVATE | SWP_NOZORDER);
        return true;
    }

    /// <summary>
    /// 已挂载时,检查胶囊右缘是否仍紧贴 TrayNotifyWnd 左缘、宽度是否与内容一致
    /// (托盘图标增减 / 胶囊内容变宽都会让它错位)。
    /// </summary>
    public static bool NeedsReposition(Window window, int widthDip, int gapDip)
    {
        var hwnd = GetHwnd(window);
        IntPtr shellTray = FindTray();
        IntPtr trayNotify = FindTrayNotify(shellTray);
        if (shellTray == IntPtr.Zero || trayNotify == IntPtr.Zero) return false;

        double scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;
        int w = (int)Math.Round(widthDip * scale);
        int gap = (int)Math.Round(gapDip * scale);

        GetWindowRect(trayNotify, out var tn);
        GetWindowRect(hwnd, out var me);
        int expectedRight = tn.Left - gap;
        return Math.Abs(me.Right - expectedRight) > 1 || Math.Abs((me.Right - me.Left) - w) > 1;
    }

    /// <summary>窗口当前是否挂在任务栏下(用 GA_PARENT 判断,GetParent 对 WS_POPUP 有假象)。</summary>
    public static bool IsInTaskbar(Window window)
    {
        IntPtr shellTray = FindTray();
        if (shellTray == IntPtr.Zero) return false;
        return GetAncestor(GetHwnd(window), GA_PARENT) == shellTray;
    }

    /// <summary>开启系统级 backdrop blur(磨砂玻璃,Win10/11 的 SetWindowCompositionAttribute)。</summary>
    public static void EnableBlur(Window window)
    {
        var hwnd = GetHwnd(window);
        var accent = new AccentPolicy { AccentState = 3 /* ACCENT_ENABLE_BLURBEHIND */ };
        var data = new WindowCompositionAttributeData
        {
            Attribute = 19, // WCA_ACCENT_POLICY
            SizeOfData = Marshal.SizeOf<AccentPolicy>(),
            Data = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>())
        };
        try
        {
            Marshal.StructureToPtr(accent, data.Data, false);
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(data.Data); }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;

    private const int WS_SYSMENU = 0x00080000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_MAXIMIZEBOX = 0x00010000;

    /// <summary>
    /// 带边框样式的窗口默认会出现在 Alt+Tab 里,标成工具窗口隐藏掉;
    /// 同时去掉系统菜单/最小化/最大化样式——玻璃框扩到整个客户区后,
    /// 只要窗口有 WS_SYSMENU,DWM 就会在右上角画出关闭按钮(✕)。
    /// </summary>
    public static void HideFromAltTab(Window window)
    {
        var hwnd = GetHwnd(window);
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
        int style = GetWindowLong(hwnd, GWL_STYLE);
        SetWindowLong(hwnd, GWL_STYLE, style & ~(WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX));
        // 样式改动要 FRAMECHANGED 才会让 DWM 重新计算非客户区
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_FRAMECHANGED = 0x0020;

    // ───────────── Win11 原生圆角亚克力 ─────────────
    // 旧方案(AllowsTransparency 分层窗口 + accent blur)的模糊层永远是 HWND 矩形:
    // WPF 的 CornerRadius 只裁内容,SetWindowRgn 对分层窗口的模糊也不生效。
    // 正解是让 DWM 自己画:非分层窗口 + 把玻璃框扩到整个客户区 + 系统背景(亚克力)+ 圆角偏好,
    // 圆角、模糊、1px 描边、阴影全部由 DWM 合成,天然一致。

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;          // 8px 圆角
    private const int DWMSBT_TRANSIENTWINDOW = 3; // 亚克力(弹出层用的那种)

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

    /// <summary>
    /// 给顶层、非分层(AllowsTransparency=False)窗口开启 DWM 圆角亚克力。
    /// 需在 SourceInitialized 里调用;窗口 Background 要设 Transparent。
    /// 返回 false 表示系统不支持(Win10 / 早期 Win11),已退回直角 accent blur。
    /// </summary>
    public static bool EnableRoundedAcrylic(Window window)
    {
        var hwnd = GetHwnd(window);

        // WPF 默认用黑色清屏;改成透明,DWM 背景才透得出来
        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
            target.BackgroundColor = System.Windows.Media.Colors.Transparent;

        var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref m);

        int on = 1, round = DWMWCP_ROUND, backdrop = DWMSBT_TRANSIENTWINDOW;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        if (hr != 0)
        {
            MechrevoWidget.MainWindow.Log($"dwm backdrop unsupported (0x{hr:X8}), fallback to accent blur");
            EnableBlur(window);
            return false;
        }
        return true;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int AccentState, AccentFlags, GradientColor, AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }
}
