using System.Windows.Media;

namespace MechrevoWidget;

/// <summary>档位映射、配色、温度分级——胶囊和面板共用。</summary>
internal static class Theme
{
    public static readonly Dictionary<string, string> ModeByOperating =
        new() { ["0"] = "office", ["1"] = "gaming", ["2"] = "turbo", ["3"] = "custom" };

    public static readonly Dictionary<string, string> ModeLabel =
        new() { ["office"] = "办公", ["gaming"] = "均衡", ["turbo"] = "狂暴", ["custom"] = "自定义" };

    public static readonly Dictionary<string, Color> ModeColor = new()
    {
        ["office"] = Color.FromRgb(52, 211, 153),   // emerald
        ["gaming"] = Color.FromRgb(96, 165, 250),   // blue
        ["turbo"] = Color.FromRgb(167, 139, 250),   // violet
        ["custom"] = Color.FromRgb(226, 232, 240),  // slate-200
    };

    public static readonly Color Idle = Color.FromRgb(148, 163, 184);       // 未连接
    public static readonly Color SubSilent = Color.FromRgb(34, 211, 238);   // cyan
    public static readonly Color SubExtreme = Color.FromRgb(244, 114, 182); // pink
    public static readonly Color TextOnAccent = Color.FromRgb(17, 20, 26);
    public static readonly Color TextDim = Color.FromArgb(160, 226, 232, 240);

    // 温度分级:CPU 在办公档满载会稳在 ~71°(功耗墙),因此 75° 起才算"热"
    public const int WarmAt = 75;
    public const int HotAt = 88;

    private static readonly Color TempNormalText = Color.FromArgb(240, 255, 255, 255);
    private static readonly Color TempWarm = Color.FromRgb(251, 191, 36);   // amber
    private static readonly Color TempHot = Color.FromRgb(248, 113, 113);   // red
    private static readonly Color BarCool = Color.FromRgb(94, 234, 212);    // teal

    /// <summary>胶囊上的温度文字色:常温白,偏热琥珀,过热红。</summary>
    public static Color TempText(int t) => t >= HotAt ? TempHot : t >= WarmAt ? TempWarm : TempNormalText;

    /// <summary>面板进度条色:常温青绿,偏热琥珀,过热红。</summary>
    public static Color TempBar(int t) => t >= HotAt ? TempHot : t >= WarmAt ? TempWarm : BarCool;

    public static string? ModeOf(FanStatus? s) =>
        s?.OperatingMode is string om && ModeByOperating.TryGetValue(om, out var m) ? m : null;

    private static readonly Dictionary<Color, SolidColorBrush> _brushes = new();

    /// <summary>冻结的画刷缓存,避免每 2 秒新建一批 Brush。</summary>
    public static SolidColorBrush Brush(Color c)
    {
        if (!_brushes.TryGetValue(c, out var b))
        {
            b = new SolidColorBrush(c);
            b.Freeze();
            _brushes[c] = b;
        }
        return b;
    }
}
