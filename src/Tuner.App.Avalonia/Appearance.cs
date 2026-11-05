using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Tuner.Core.Config;

namespace Tuner.Cross;

/// <summary>
/// 外观管理（Avalonia 端）：主题走 RequestedThemeVariant（ThemeDictionaries 自动切换），
/// 强调色/字号/密度/圆角在 Application.Resources 直接覆写（优先于 ThemeDictionaries 的同名键）。
/// 与 WPF 端同 token 同预设；动效开关＝导航滑动胶囊的显隐与过渡（开：胶囊随指针流式滑动；关：Fluent 静态高亮，
/// 2026-10-24 用户指定补齐，此前"Avalonia 端不设项"的判定作废）。
/// </summary>
public static class Appearance
{
    /// <summary>强调色预设色板（与 WPF 端一致；""＝默认琥珀由主题字典提供）。</summary>
    public static readonly string[] AccentPresets =
    {
        "#E8A33D", "#4C8DFF", "#34D399", "#A78BFA", "#FB7185", "#22D3EE", "#FB923C", "#94A3B8",
    };

    public static void Apply(AppearanceSettings a)
    {
        if (Application.Current is not { } app)
            return;
        app.RequestedThemeVariant = a.Theme switch
        {
            "light" => ThemeVariant.Light,
            "system" => ThemeVariant.Default, // 跟随系统：由平台 ActualThemeVariant 决定
            _ => ThemeVariant.Dark,
        };
        ApplyAccent(a.Accent);
        ApplyScale(a.FontScale);
        ApplyDensity(a.Density);
        ApplyRadius(a.Radius);
    }

    private static void ApplyAccent(string hex)
    {
        var res = Application.Current!.Resources;
        var keys = new[] { "Accent", "AccentHover", "AccentPressed", "AccentSoft", "AccentFg", "TextOnAccent" };
        if (hex is not { Length: 7 } || hex[0] != '#')
        {
            foreach (var k in keys)
                res.Remove(k);
            return; // 默认琥珀：落回 ThemeDictionaries 的本主题值
        }
        bool light = Application.Current.ActualThemeVariant == ThemeVariant.Light;
        var c = Color.Parse(hex);
        var values = new[]
        {
            c,
            ShiftL(c, light ? 0.06 : 0.10),
            ShiftL(c, -0.14),
            // 柔和底＝基底色向强调色轻染（与 WPF 端同步修正混合方向）
            Blend(Color.Parse(light ? "#F4F6F9" : "#1F2530"), c, light ? 0.22 : 0.16),
            light ? ShiftL(c, -0.30) : ShiftL(c, 0.12),
            Luminance(c) > 0.35 ? Color.Parse("#241A05") : Colors.White,
        };
        for (int i = 0; i < keys.Length; i++)
            res[keys[i]] = new SolidColorBrush(values[i]);
    }

    private static void ApplyScale(string preset)
    {
        var v = preset switch
        {
            "small" => new[] { 10.0, 11, 11.5, 12, 12.5, 13, 12, 17 },
            "large" => new[] { 11.0, 13, 13.5, 14, 15, 15.5, 14.5, 19 },
            _ => new[] { 10.5, 12, 12.5, 13, 13.5, 14, 13, 18 },
        };
        var names = new[] { "Micro", "Small", "Base", "Body", "Title", "Stat", "Num", "Brand" };
        var res = Application.Current!.Resources;
        for (int i = 0; i < names.Length; i++)
            res["Fz." + names[i]] = v[i];
    }

    private static void ApplyDensity(string preset)
    {
        var compact = preset == "compact";
        var res = Application.Current!.Resources;
        res["Sp.Row"] = compact ? 8.0 : 12.0;
        res["Sp.Section"] = compact ? 10.0 : 14.0;
        res["M.Page"] = compact ? new Thickness(12, 8, 12, 8) : new Thickness(16, 12, 16, 10);
        res["M.Card"] = new Thickness(compact ? 14 : 20);
        res["M.Item"] = new Thickness(compact ? 9 : 12, compact ? 8 : 10, compact ? 9 : 12, compact ? 8 : 10);
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
        var res = Application.Current!.Resources;
        for (int i = 0; i < names.Length; i++)
            res["Rad." + names[i]] = new CornerRadius(v[i]);
    }

    // ---------- 颜色数学（与 WPF 端 Appearance.cs 同源） ----------

    private static Color ShiftL(Color c, double delta)
    {
        ToHsl(c, out var h, out var s, out var l);
        return FromHsl(h, s, Math.Clamp(l + delta, 0, 1));
    }

    private static Color Blend(Color a, Color b, double t) => new(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t),
        255);

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
        if (s == 0) { var v = (byte)(l * 255); return new Color(255, v, v, v); }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        return new Color(255, Hue(p, q, h + 1.0 / 3), Hue(p, q, h), Hue(p, q, h - 1.0 / 3));
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
}
