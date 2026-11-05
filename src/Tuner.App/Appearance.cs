using System.Windows;
using System.Windows.Media;
using Tuner.Core.Config;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Tuner;

/// <summary>
/// 外观管理：主题（调色板字典热切换）＋强调色/字号/密度/圆角/动效在 Application.Resources 上覆写。
/// 所有 UI 消费端以 DynamicResource 引用这些键，覆写后全界面即时刷新（与语言热切换同机制）。
/// 强调色的 hover/pressed/soft/文字变体由 HSL 数学推导，深浅两主题分别合成。
/// </summary>
public static class Appearance
{
    /// <summary>动效总开关（代码侧动画路径读取；XAML 侧由 Dur.* 归零实现）。</summary>
    public static bool AnimationsEnabled { get; private set; } = true;

    /// <summary>当前实际主题（"dark"/"light"，已解析跟随系统）。</summary>
    public static string CurrentTheme { get; private set; } = "dark";

    /// <summary>外观变化通知（导航胶囊等代码取色者需要重取色/重校准）。</summary>
    public static event Action? Changed;

    /// <summary>外观预设色板（与外观页色板一致；""＝默认琥珀由调色板字典提供）。</summary>
    public static readonly string[] AccentPresets =
    {
        "#E8A33D", "#4C8DFF", "#34D399", "#A78BFA", "#FB7185", "#22D3EE", "#FB923C", "#94A3B8",
    };

    public static void Apply(AppearanceSettings a)
    {
        CurrentTheme = a.Theme == "system"
            ? (SystemUsesLight() ? "light" : "dark")
            : (a.Theme == "light" ? "light" : "dark");
        SwapPalette(CurrentTheme);
        ApplyAccent(a.Accent, CurrentTheme == "light");
        ApplyScale(a.FontScale);
        ApplyDensity(a.Density);
        ApplyRadius(a.Radius);
        AnimationsEnabled = a.Animations;
        if (_noAnimLoaded != !a.Animations)
        {
            _noAnimLoaded = !a.Animations;
            SwapThemeDictionary();
        }
        Changed?.Invoke();
    }

    private static bool _noAnimLoaded;

    /// <summary>动效开关热切换：整本替换 Theme.xaml / Theme.NoAnim.xaml（NoAnim 由 Theme 生成、全部时长归零）。
    /// 页面样式全部走 DynamicResource，换字典即全套模板重应用——真正的"关动效＝状态即时呈现"。
    /// 关键坑：BAML 字典按 URI 有实例缓存，直接 new ResourceDictionary{Source=同名 URI} 很可能拿回
    /// 上次的同一实例，WPF 视作"无变化"、样式引用不重算（实测表现：关动效后再开，动画永远回不来）。
    /// 因此这里把内容物化进一个全新的普通字典实例，保证每次切换都是真正的换血。</summary>
    private static void SwapThemeDictionary()
    {
        var res = Application.Current.Resources;
        var name = _noAnimLoaded ? "Theme.NoAnim.xaml" : "Theme.xaml";
        var dict = FreshDictionary("pack://application:,,,/" + name);
        for (int i = 0; i < res.MergedDictionaries.Count; i++)
        {
            var src = res.MergedDictionaries[i].Source?.OriginalString ?? "";
            if (src.EndsWith("Theme.NoAnim.xaml", StringComparison.OrdinalIgnoreCase)
                || src.EndsWith("/Theme.xaml", StringComparison.OrdinalIgnoreCase)
                || src.Equals("Theme.xaml", StringComparison.OrdinalIgnoreCase))
            {
                res.MergedDictionaries[i] = dict;
                return;
            }
        }
        res.MergedDictionaries.Add(dict);
    }

    /// <summary>替换合并字典中的调色板（深/浅），Theme.xaml 的 DynamicResource 引用自动重解析。</summary>
    private static void SwapPalette(string theme)
    {
        var res = Application.Current.Resources;
        var name = theme == "light" ? "Palette.Light.xaml" : "Palette.Dark.xaml";
        var dict = FreshDictionary("pack://application:,,,/Theme/" + name);
        for (int i = 0; i < res.MergedDictionaries.Count; i++)
        {
            var src = res.MergedDictionaries[i].Source?.OriginalString ?? "";
            if (src.EndsWith("Palette.Dark.xaml", StringComparison.OrdinalIgnoreCase)
                || src.EndsWith("Palette.Light.xaml", StringComparison.OrdinalIgnoreCase))
            {
                res.MergedDictionaries[i] = dict;
                return;
            }
        }
        res.MergedDictionaries.Add(dict);
    }

    /// <summary>按 URI 装载字典并物化成全新的普通字典实例（规避 BAML 同 URI 实例缓存——
    /// 塞回缓存实例时 WPF 判定"无变化"，深浅/动效来回切换的第二程会静默失效）。</summary>
    private static ResourceDictionary FreshDictionary(string packUri)
    {
        var loaded = new ResourceDictionary { Source = new Uri(packUri, UriKind.Absolute) };
        var fresh = new ResourceDictionary();
        foreach (var key in loaded.Keys)
            fresh[key] = loaded[key]; // 逐键取值＝物化（样式/模板每本字典各自实例化）
        return fresh;
    }

    // ---------- 强调色 ----------

    private static void ApplyAccent(string hex, bool light)
    {
        var res = Application.Current.Resources;
        var colorKeys = new[] { "AccentColor", "AccentHoverColor", "AccentPressedColor", "AccentSoftColor", "AccentFgColor", "TextOnAccentColor" };
        var brushKeys = new[] { "Accent", "AccentHover", "AccentPressed", "AccentSoft", "AccentFg", "TextOnAccent" };
        if (hex is not { Length: 7 } || hex[0] != '#')
        {
            // 默认琥珀：清空覆写，落回调色板字典的本主题值
            foreach (var k in colorKeys) res.Remove(k);
            foreach (var k in brushKeys) res.Remove(k);
            return;
        }
        var c = (Color)ColorConverter.ConvertFromString(hex);
        var hover = ShiftL(c, light ? 0.06 : 0.10);
        var pressed = ShiftL(c, -0.14);
        // 柔和底＝基底色向强调色轻染（与默认 #332B1C/#F3E7CE 的暗度对齐；
        // 早前写反成"强调色向基底 17%"，用户实选琥珀后选中胶囊变亮橙底+橙字不可读）
        var soft = light ? Blend(FromHex("#F4F6F9"), c, 0.22) : Blend(FromHex("#1F2530"), c, 0.16);
        var fg = light ? ShiftL(c, -0.30) : ShiftL(c, 0.12);
        var on = Luminance(c) > 0.35 ? FromHex("#241A05") : Colors.White;
        var colors = new[] { c, hover, pressed, soft, fg, on };
        for (int i = 0; i < colorKeys.Length; i++)
        {
            res[colorKeys[i]] = colors[i];
            res[brushKeys[i]] = new SolidColorBrush(colors[i]);
        }
    }

    private static Color FromHex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>色相/饱和度不变，亮度加减 delta（-1..1）。</summary>
    private static Color ShiftL(Color c, double delta)
    {
        ToHsl(c, out var h, out var s, out var l);
        return FromHsl(h, s, Math.Clamp(l + delta, 0, 1));
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    /// <summary>相对亮度（0..1）：决定强调色上的文字用深还是白。</summary>
    private static double Luminance(Color c) =>
        (0.2126 * c.R / 255) + (0.7152 * c.G / 255) + (0.0722 * c.B / 255);

    private static void ToHsl(Color c, out double h, out double s, out double l)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;
        if (max == min) { h = s = 0; return; }
        var d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
        else if (max == g) h = ((b - r) / d + 2) / 6;
        else h = ((r - g) / d + 4) / 6;
    }

    private static Color FromHsl(double h, double s, double l)
    {
        if (s == 0) { var v = (byte)(l * 255); return Color.FromRgb(v, v, v); }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        return Color.FromRgb(Hue(p, q, h + 1.0 / 3), Hue(p, q, h), Hue(p, q, h - 1.0 / 3));
        static byte Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            double v = t < 1.0 / 6 ? p + (q - p) * 6 * t
                : t < 0.5 ? q
                : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6
                : p;
            return (byte)Math.Round(v * 255);
        }
    }

    // ---------- 字号 / 密度 / 圆角 ----------

    private static void ApplyScale(string preset)
    {
        var v = preset switch
        {
            "small" => new[] { 10.0, 11, 11.5, 12, 12.5, 13, 12, 17 },
            "large" => new[] { 11.0, 13, 13.5, 14, 15, 15.5, 14.5, 19 },
            _ => new[] { 10.5, 12, 12.5, 13, 13.5, 14, 13, 18 },
        };
        var names = new[] { "Micro", "Small", "Base", "Body", "Title", "Stat", "Num", "Brand" };
        var res = Application.Current.Resources;
        for (int i = 0; i < names.Length; i++)
            res["Fz." + names[i]] = v[i];
    }

    private static void ApplyDensity(string preset)
    {
        var compact = preset == "compact";
        var res = Application.Current.Resources;
        res["M.Page"] = compact ? new Thickness(16, 10, 16, 8) : new Thickness(20, 14, 20, 12);
        res["M.Card"] = new Thickness(compact ? 14 : 20);
        res["M.Section"] = new Thickness(0, compact ? 10 : 14, 0, 0);
        res["M.Row"] = new Thickness(0, compact ? 7 : 10, 0, 0);
        res["M.Item"] = new Thickness(compact ? 8 : 11, compact ? 7 : 9, compact ? 8 : 11, compact ? 7 : 9);
    }

    private static void ApplyRadius(string preset)
    {
        var v = preset switch
        {
            "sharp" => new[] { 3.0, 4, 6, 8 },
            "round" => new[] { 6.0, 9, 11, 14 },
            _ => new[] { 5.0, 7, 9, 12 },
        };
        var names = new[] { "Chip", "Control", "Card", "Panel" };
        var res = Application.Current.Resources;
        for (int i = 0; i < names.Length; i++)
            res["Rad." + names[i]] = new CornerRadius(v[i]);
    }

    // ---------- 跟随系统（注册表；缺省视为深色） ----------

    private static bool SystemUsesLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int i && i == 1;
        }
        catch
        {
            return false;
        }
    }
}
