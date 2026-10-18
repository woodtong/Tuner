namespace Tuner.Core.Config;

/// <summary>自定义曲线控制点（0..1 归一化坐标，按 X 升序）。</summary>
public sealed class CurvePoint
{
    public CurvePoint() { }
    public CurvePoint(double x, double y) { X = x; Y = y; }

    /// <summary>音量百分比（0..1）。</summary>
    public double X { get; set; }

    /// <summary>实际响度/振幅（0..1）。</summary>
    public double Y { get; set; }
}

/// <summary>生效曲线：预设标识 + 自定义控制点（Id=="custom" 时使用）。由配置解析而来，只读使用。</summary>
public sealed class CurveSpec
{
    public static readonly CurveSpec Linear = new() { Id = "linear" };

    public string Id = "linear";
    public IReadOnlyList<CurvePoint> Custom = Array.Empty<CurvePoint>();
}

/// <summary>
/// 音量曲线：把界面上的音量百分比（混音台推子位置、规则目标音量）映射为写给音频会话的实际振幅。
/// 线性最直观，但小音量段每格听感变化剧烈；平方/对数让等量拖动更接近等响；自定义按控制点分段线性插值（全局一份）。
/// </summary>
public static class VolumeCurve
{
    /// <summary>对数曲线的动态范围（dB）：100% 对应 0dB（满幅），0% 为静音，每 1% 恒定 0.4dB。</summary>
    public const double LogRangeDb = 40.0;

    private static readonly double LogMaxLinear = Math.Pow(10.0, LogRangeDb / 20.0) - 1.0; // = 99

    /// <summary>归一化曲线标识：非法值回退线性。</summary>
    public static string Normalize(string? curve) => curve switch
    {
        "square" => "square",
        "log" => "log",
        "custom" => "custom",
        _ => "linear",
    };

    /// <summary>全局曲线（MASTER 与全部应用）。</summary>
    public static CurveSpec ResolveGlobal(TunerConfig config) =>
        new() { Id = config.Settings.VolumeCurve, Custom = config.Settings.CustomCurve };

    /// <summary>控制点规整：钳制 0..1、按 X 排序、合并近重合点、补齐首尾端点。</summary>
    public static List<CurvePoint> NormalizePoints(IEnumerable<CurvePoint>? points)
    {
        var pts = new List<CurvePoint>();
        if (points is not null)
            foreach (var p in points)
                pts.Add(new CurvePoint(Math.Clamp(p.X, 0.0, 1.0), Math.Clamp(p.Y, 0.0, 1.0)));
        pts.Sort((a, b) => a.X.CompareTo(b.X));
        for (int i = pts.Count - 1; i > 0; i--)
            if (pts[i].X - pts[i - 1].X < 1e-4)
                pts.RemoveAt(i);
        if (pts.Count == 0)
            return pts;
        if (pts[0].X > 1e-4)
            pts.Insert(0, new CurvePoint(0.0, pts[0].Y));
        if (pts[^1].X < 1 - 1e-4)
            pts.Add(new CurvePoint(1.0, pts[^1].Y));
        return pts;
    }

    /// <summary>位置(0..1) → 振幅(0..1)。</summary>
    public static float ToVolume(CurveSpec spec, double position) =>
        spec.Id == "custom" && spec.Custom.Count >= 2
            ? CustomToVolume(spec.Custom, position)
            : ToVolume(spec.Id, position);

    /// <summary>振幅(0..1) → 位置(0..1)：ToVolume 的逆映射（显示与恢复值换算用）。</summary>
    public static float ToPosition(CurveSpec spec, double volume) =>
        spec.Id == "custom" && spec.Custom.Count >= 2
            ? CustomToPosition(spec.Custom, volume)
            : ToPosition(spec.Id, volume);

    /// <summary>位置(0..1) → 振幅(0..1)（预设曲线）。</summary>
    public static float ToVolume(string? curve, double position)
    {
        var p = Math.Clamp(position, 0.0, 1.0);
        return curve switch
        {
            "square" => (float)(p * p),
            "log" => (float)((Math.Pow(10.0, p * LogRangeDb / 20.0) - 1.0) / LogMaxLinear),
            _ => (float)p,
        };
    }

    /// <summary>振幅(0..1) → 位置(0..1)（预设曲线，ToVolume 的精确逆映射）。</summary>
    public static float ToPosition(string? curve, double volume)
    {
        var v = Math.Clamp(volume, 0.0, 1.0);
        return curve switch
        {
            "square" => (float)Math.Sqrt(v),
            "log" => (float)(Math.Log10(v * LogMaxLinear + 1.0) * 20.0 / LogRangeDb),
            _ => (float)v,
        };
    }

    /// <summary>自定义曲线：控制点间分段线性插值，区间外钳制到端点响度。</summary>
    private static float CustomToVolume(IReadOnlyList<CurvePoint> pts, double position)
    {
        var p = Math.Clamp(position, 0.0, 1.0);
        if (p <= pts[0].X)
            return (float)Math.Clamp(pts[0].Y, 0.0, 1.0);
        for (int i = 1; i < pts.Count; i++)
        {
            if (p > pts[i].X)
                continue;
            var a = pts[i - 1];
            var b = pts[i];
            var t = (p - a.X) / Math.Max(1e-9, b.X - a.X);
            return (float)Math.Clamp(a.Y + (b.Y - a.Y) * t, 0.0, 1.0);
        }
        return (float)Math.Clamp(pts[^1].Y, 0.0, 1.0);
    }

    /// <summary>自定义曲线逆映射：取第一段覆盖该响度的区间线性反解；超出两端时落到极值点位置。</summary>
    private static float CustomToPosition(IReadOnlyList<CurvePoint> pts, double volume)
    {
        var v = Math.Clamp(volume, 0.0, 1.0);
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];
            var lo = Math.Min(a.Y, b.Y);
            var hi = Math.Max(a.Y, b.Y);
            if (v < lo || v > hi)
                continue;
            if (Math.Abs(b.Y - a.Y) < 1e-9)
                return (float)a.X; // 平段：取段起点
            var t = (v - a.Y) / (b.Y - a.Y);
            return (float)Math.Clamp(a.X + (b.X - a.X) * t, 0.0, 1.0);
        }
        CurvePoint best = pts[0];
        foreach (var pt in pts)
            if (v >= 0.5 ? pt.Y > best.Y : pt.Y < best.Y)
                best = pt; // 严格比较：取首个极值点，平段结果确定
        return (float)best.X;
    }
}
