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

    /// <summary>已应用到自定义控制点上的拟合方式（"linear"＝折线直连，即未拟合），见 VolumeCurve.NormalizeFit。</summary>
    public string Fit = "linear";
}

/// <summary>
/// 音量曲线：把界面上的音量百分比（混音台推子位置、规则目标音量）映射为写给音频会话的实际振幅。
/// 线性最直观，但小音量段每格听感变化剧烈；平方/对数让等量拖动更接近等响；
/// 自定义按控制点求值，控制点之间默认折线直连，也可点击「拟合」用设置里的算法把折线平滑/拟合（全局一份）。
/// </summary>
public static class VolumeCurve
{
    /// <summary>对数曲线的动态范围（dB）：100% 对应 0dB（满幅），0% 为静音，每 1% 恒定 0.4dB。</summary>
    public const double LogRangeDb = 40.0;

    private static readonly double LogMaxLinear = Math.Pow(10.0, LogRangeDb / 20.0) - 1.0; // = 99

    /// <summary>「拟合」按钮可选的算法（设置页下拉同此列表顺序）。均只在点击「拟合」后才生效。</summary>
    public static readonly IReadOnlyList<string> FitMethods = new[]
    {
        "monotone", // PCHIP：保形保单调，局部，绝不过冲
        "akima",    // Akima：局部、抗过冲，比 PCHIP 少"平台感"
        "natural",  // 自然三次样条：全局 C² 最光滑，可能过冲
        "catmull",  // Catmull-Rom：局部 C¹ 样条，最圆润
        "poly3",    // 三次多项式最小二乘：整体趋势拟合，不穿过控制点
        "poly2",    // 二次多项式最小二乘：更保守的整体趋势
    };

    /// <summary>归一化曲线标识：非法值回退线性。</summary>
    public static string Normalize(string? curve) => curve switch
    {
        "square" => "square",
        "log" => "log",
        "custom" => "custom",
        _ => "linear",
    };

    /// <summary>
    /// 归一化「已应用的拟合方式」：非法值回退折线直连（= 未拟合）。
    /// "linear"＝控制点直连（折线，默认行为）；其余见 <see cref="FitMethods"/>。
    /// </summary>
    public static string NormalizeFit(string? fit) => fit switch
    {
        "monotone" => "monotone",
        "akima" => "akima",
        "natural" => "natural",
        "catmull" => "catmull",
        "poly3" => "poly3",
        "poly2" => "poly2",
        _ => "linear",
    };

    /// <summary>归一化设置页选择的「拟合算法」（不允许 linear：折线就是未拟合状态）。</summary>
    public static string NormalizeFitMethod(string? method) => method switch
    {
        "akima" => "akima",
        "natural" => "natural",
        "catmull" => "catmull",
        "poly3" => "poly3",
        "poly2" => "poly2",
        _ => "monotone",
    };

    /// <summary>是否为平滑/拟合方式（false＝折线直连）。</summary>
    public static bool IsFitted(string? fit) => NormalizeFit(fit) != "linear";

    /// <summary>全局曲线（MASTER 与全部应用）。</summary>
    public static CurveSpec ResolveGlobal(TunerConfig config) =>
        new()
        {
            Id = config.Settings.VolumeCurve,
            Custom = config.Settings.CustomCurve,
            Fit = NormalizeFit(config.Settings.CustomCurveFit),
        };

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
            ? CustomToVolume(spec.Custom, position, spec.Fit)
            : ToVolume(spec.Id, position);

    /// <summary>振幅(0..1) → 位置(0..1)：ToVolume 的逆映射（显示与恢复值换算用）。</summary>
    public static float ToPosition(CurveSpec spec, double volume) =>
        spec.Id == "custom" && spec.Custom.Count >= 2
            ? CustomToPosition(spec.Custom, volume, spec.Fit)
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

    /// <summary>自定义曲线：控制点之间按拟合方式求值，区间外钳制到端点响度。</summary>
    private static float CustomToVolume(IReadOnlyList<CurvePoint> pts, double position, string fit)
    {
        var p = Math.Clamp(position, 0.0, 1.0);
        if (p <= pts[0].X)
            return (float)Math.Clamp(pts[0].Y, 0.0, 1.0);
        int n = pts.Count;
        fit = NormalizeFit(fit);

        // 多项式最小二乘拟合：整体趋势，不穿过控制点（首尾响度按端点控制点对齐）
        if (fit is "poly2" or "poly3")
            return (float)Math.Clamp(PolyLeastSquares(pts, fit == "poly3" ? 3 : 2, p), 0.0, 1.0);

        // 平滑插值（三次 Hermite）：切线由拟合方式决定，段内只做一次多项式求值
        if (fit != "linear" && n >= 3)
        {
            var m = Tangents(pts, fit);
            for (int i = 1; i < n; i++)
            {
                if (p > pts[i].X)
                    continue;
                var a = pts[i - 1];
                var b = pts[i];
                double h = Math.Max(1e-9, b.X - a.X);
                double t = (p - a.X) / h;
                double t2 = t * t, t3 = t2 * t;
                double y = (2 * t3 - 3 * t2 + 1) * a.Y
                         + (t3 - 2 * t2 + t) * h * m[i - 1]
                         + (-2 * t3 + 3 * t2) * b.Y
                         + (t3 - t2) * h * m[i];
                return (float)Math.Clamp(y, 0.0, 1.0);
            }
            return (float)Math.Clamp(pts[^1].Y, 0.0, 1.0);
        }

        // 折线直连（默认行为）
        for (int i = 1; i < n; i++)
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

    /// <summary>
    /// 各控制点处的切线斜率（三次 Hermite 用）：
    /// monotone＝PCHIP（保形，极值点切线取 0，绝不冲出控制点取值范围）；
    /// akima＝Akima（局部加权，抗过冲且比 PCHIP 平缓）；
    /// natural＝自然三次样条（全局二阶连续，允许过冲）；
    /// catmull＝Catmull-Rom（中心差分，最圆润）。
    /// </summary>
    private static double[] Tangents(IReadOnlyList<CurvePoint> pts, string fit)
    {
        int n = pts.Count;
        var d = new double[n - 1]; // 各段斜率
        for (int i = 0; i < n - 1; i++)
            d[i] = (pts[i + 1].Y - pts[i].Y) / Math.Max(1e-9, pts[i + 1].X - pts[i].X);

        var m = new double[n];

        switch (fit)
        {
            case "natural":
                return NaturalTangents(pts, d);

            case "akima":
            {
                // 两端各外推两个虚拟段斜率，按四段局部加权求切线（标准 Akima 端点处理）
                var s = new double[n + 3];
                s[1] = 2 * d[0] - d[1];
                s[0] = 2 * s[1] - d[0];
                for (int i = 0; i < n - 1; i++)
                    s[i + 2] = d[i];
                s[n + 1] = 2 * d[^1] - d[^2];
                s[n + 2] = 2 * s[n + 1] - d[^1];
                for (int i = 0; i < n; i++)
                {
                    double dm2 = s[i], dm1 = s[i + 1], dp1 = s[i + 3];
                    double w1 = Math.Abs(dp1 - s[i + 2]);
                    double w2 = Math.Abs(dm1 - dm2);
                    m[i] = w1 + w2 < 1e-12 ? (dm1 + s[i + 2]) / 2 : (w1 * dm1 + w2 * s[i + 2]) / (w1 + w2);
                }
                return m;
            }

            case "monotone":
                m[0] = d[0];
                m[n - 1] = d[^1];
                for (int i = 1; i < n - 1; i++)
                {
                    // PCHIP 调和平均：相邻两段同号才给非零切线，异号（极值点）取 0 → 保单调
                    if (d[i - 1] * d[i] <= 0)
                    {
                        m[i] = 0;
                        continue;
                    }
                    double h0 = Math.Max(1e-9, pts[i].X - pts[i - 1].X);
                    double h1 = Math.Max(1e-9, pts[i + 1].X - pts[i].X);
                    m[i] = 3 * (h0 + h1) /
                           ((2 * h1 + h0) / d[i - 1] + (h1 + 2 * h0) / d[i]);
                }
                return m;

            default: // catmull
                m[0] = d[0];
                m[n - 1] = d[^1];
                for (int i = 1; i < n - 1; i++)
                    m[i] = (pts[i + 1].Y - pts[i - 1].Y) / Math.Max(1e-9, pts[i + 1].X - pts[i - 1].X);
                return m;
        }
    }

    /// <summary>自然三次样条切线：先解三对角方程求各点二阶导（端点二阶导为 0），再由二阶导回推一阶导。</summary>
    private static double[] NaturalTangents(IReadOnlyList<CurvePoint> pts, double[] d)
    {
        int n = pts.Count;
        var h = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
            h[i] = Math.Max(1e-9, pts[i + 1].X - pts[i].X);

        // 三对角方程（Thomas 消元）：M[0]=M[n-1]=0
        var lower = new double[n];
        var diag = new double[n];
        var upper = new double[n];
        var rhs = new double[n];
        for (int i = 1; i < n - 1; i++)
        {
            lower[i] = h[i - 1];
            diag[i] = 2 * (h[i - 1] + h[i]);
            upper[i] = h[i];
            rhs[i] = 6 * (d[i] - d[i - 1]);
        }
        diag[0] = diag[n - 1] = 1; // 端点：M=0

        for (int i = 1; i < n; i++)
        {
            double f = lower[i] / diag[i - 1];
            diag[i] -= f * upper[i - 1];
            rhs[i] -= f * rhs[i - 1];
        }
        var M = new double[n];
        M[n - 1] = rhs[n - 1] / diag[n - 1];
        for (int i = n - 2; i >= 0; i--)
            M[i] = (rhs[i] - upper[i] * M[i + 1]) / diag[i];

        var m = new double[n];
        for (int i = 0; i < n - 1; i++)
            m[i] = d[i] - h[i] * (2 * M[i] + M[i + 1]) / 6;
        m[n - 1] = d[n - 2] + h[n - 2] * (M[n - 2] + 2 * M[n - 1]) / 6;
        return m;
    }

    /// <summary>
    /// 最小二乘多项式拟合 y ≈ c₀ + c₁x + … + cₖxᵏ（正规方程 + 高斯消元）。
    /// 拟合线不穿过控制点，因此再把两个端点的偏差按线性权重摊回去，保证首尾仍等于端点控制点的响度
    /// （否则 0% 可能不是静音）。
    /// </summary>
    private static double PolyLeastSquares(IReadOnlyList<CurvePoint> pts, int degree, double p)
    {
        // 点数不足时降阶（2 点只能是直线），避免正规方程奇异
        degree = Math.Min(degree, pts.Count - 1);
        if (degree < 1)
            return Math.Clamp(pts[^1].Y, 0.0, 1.0);
        int k = degree + 1;
        var ata = new double[k, k];
        var atb = new double[k];
        foreach (var pt in pts)
        {
            var basis = new double[k];
            basis[0] = 1;
            for (int j = 1; j < k; j++)
                basis[j] = basis[j - 1] * pt.X;
            for (int r = 0; r < k; r++)
            {
                for (int c = 0; c < k; c++)
                    ata[r, c] += basis[r] * basis[c];
                atb[r] += basis[r] * pt.Y;
            }
        }
        var coeff = Solve(ata, atb, k);
        if (coeff is null)
            return Math.Clamp(pts[^1].Y, 0.0, 1.0); // 退化（点太少）：回退端点

        double Eval(double x)
        {
            double y = 0, xp = 1;
            for (int j = 0; j < k; j++)
            {
                y += coeff[j] * xp;
                xp *= x;
            }
            return y;
        }

        double q = Eval(p);
        double q0 = Eval(0), q1 = Eval(1);
        return q + (1 - p) * (pts[0].Y - q0) + p * (pts[^1].Y - q1);
    }

    /// <summary>小规模线性方程组（高斯消元带主元）；奇异时返回 null。</summary>
    private static double[]? Solve(double[,] a, double[] b, int n)
    {
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++)
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                    pivot = r;
            if (Math.Abs(a[pivot, col]) < 1e-12)
                return null;
            if (pivot != col)
            {
                for (int c = 0; c < n; c++)
                    (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                (b[col], b[pivot]) = (b[pivot], b[col]);
            }
            for (int r = col + 1; r < n; r++)
            {
                double f = a[r, col] / a[col, col];
                if (f == 0)
                    continue;
                for (int c = col; c < n; c++)
                    a[r, c] -= f * a[col, c];
                b[r] -= f * b[col];
            }
        }
        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double sum = b[r];
            for (int c = r + 1; c < n; c++)
                sum -= a[r, c] * x[c];
            x[r] = sum / a[r, r];
        }
        return x;
    }

    /// <summary>
    /// 自定义曲线逆映射（振幅 → 位置）：任意拟合方式都适用——先细扫定位响度穿越的子区间，
    /// 再二分逼近；曲线完全不含该响度时落到最近端点。
    /// </summary>
    private static float CustomToPosition(IReadOnlyList<CurvePoint> pts, double volume, string fit)
    {
        var v = Math.Clamp(volume, 0.0, 1.0);
        fit = NormalizeFit(fit);
        if (fit == "linear")
            return LinearCustomToPosition(pts, v); // 折线可解析反解，保持原有精度与开销

        const int samples = 64;
        double prevP = 0;
        double prevY = CustomToVolume(pts, 0, fit);
        if (Math.Abs(prevY - v) < 1e-7)
            return 0f;
        for (int i = 1; i <= samples; i++)
        {
            double p = i / (double)samples;
            double y = CustomToVolume(pts, p, fit);
            if ((prevY - v) * (y - v) <= 0)
            {
                double lo = prevP, hi = p, loY = prevY;
                for (int k = 0; k < 30; k++)
                {
                    double mid = (lo + hi) / 2;
                    double midY = CustomToVolume(pts, mid, fit);
                    if ((loY - v) * (midY - v) <= 0)
                        hi = mid;
                    else
                    {
                        lo = mid;
                        loY = midY;
                    }
                }
                return (float)Math.Clamp((lo + hi) / 2, 0.0, 1.0);
            }
            prevP = p;
            prevY = y;
        }
        // 该响度不在曲线取值范围内：贴到更近的一端
        double y0 = CustomToVolume(pts, 0, fit), y1 = CustomToVolume(pts, 1, fit);
        return Math.Abs(y0 - v) <= Math.Abs(y1 - v) ? 0f : 1f;
    }

    /// <summary>折线自定义曲线逆映射：取第一段覆盖该响度的区间线性反解；超出两端时落到极值点位置。</summary>
    private static float LinearCustomToPosition(IReadOnlyList<CurvePoint> pts, double volume)
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
