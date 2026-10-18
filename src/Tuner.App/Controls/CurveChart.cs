using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Tuner.Core.Config;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Tuner.Controls;

/// <summary>
/// 音量曲线图：X=音量百分比（推子位置），Y=实际响度（振幅）。
/// 预设曲线按函数采样绘制；custom 模式绘制控制点折线并支持编辑：
/// 拖动控制点、双击空白处加点、右键删点（首尾端点 X 固定，仅可上下拖）。
/// 编辑就地修改 Points 列表并通过 CurveEdited 通知（由外部写回配置）。
/// </summary>
public sealed class CurveChart : FrameworkElement
{
    public static readonly DependencyProperty CurveIdProperty = DependencyProperty.Register(
        nameof(CurveId), typeof(string), typeof(CurveChart),
        new FrameworkPropertyMetadata("linear", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<CurvePoint>), typeof(CurveChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsEditableProperty = DependencyProperty.Register(
        nameof(IsEditable), typeof(bool), typeof(CurveChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>当前绘制的曲线标识：linear / square / log / custom。</summary>
    public string CurveId
    {
        get => (string)GetValue(CurveIdProperty);
        set => SetValue(CurveIdProperty, value);
    }

    /// <summary>custom 模式的控制点（编辑经 CurveEdited 通知外部写回）。</summary>
    public IReadOnlyList<CurvePoint>? Points
    {
        get => (IReadOnlyList<CurvePoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <summary>是否允许编辑控制点。</summary>
    public bool IsEditable
    {
        get => (bool)GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    /// <summary>控制点被编辑（拖动/新增/删除）后触发。</summary>
    public event Action? CurveEdited;

    private static readonly Brush PlotBg = Freeze(new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x1B)));
    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x23, 0x29, 0x36)));
    private static readonly Brush AccentBrushC = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D)));
    private static readonly Brush FaintBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7E, 0x88, 0x9D)));
    private static readonly Pen PlotBorderPen = FreezePen(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3B))), 1));
    private static readonly Pen GridPen = FreezePen(new Pen(GridBrush, 1));
    private static readonly Pen CurvePen = FreezePen(new Pen(AccentBrushC, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
    private static readonly Pen PointRimPen = FreezePen(new Pen(PlotBg, 1.5));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    private static Pen FreezePen(Pen p) { p.Freeze(); return p; }

    private const double MarginL = 34, MarginR = 10, MarginT = 18, MarginB = 20;
    private const double HitRadius = 9, MinStepX = 0.01;

    private int _dragIndex = -1;

    private Rect PlotRect => new(MarginL, MarginT,
        Math.Max(10, ActualWidth - MarginL - MarginR), Math.Max(10, ActualHeight - MarginT - MarginB));

    private Point ToScreen(double x, double y)
    {
        var r = PlotRect;
        return new Point(r.X + x * r.Width, r.Bottom - y * r.Height);
    }

    private CurvePoint ToData(Point s)
    {
        var r = PlotRect;
        return new CurvePoint((s.X - r.X) / r.Width, (r.Bottom - s.Y) / r.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var r = PlotRect;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.DrawRoundedRectangle(PlotBg, PlotBorderPen, r, 4, 4);

        // 曲线主体（裁剪在绘图区内）
        dc.PushClip(new RectangleGeometry(r));
        for (int i = 1; i < 5; i++)
        {
            var t = i / 5.0;
            var gx = r.X + t * r.Width;
            var gy = r.Bottom - t * r.Height;
            dc.DrawLine(GridPen, new Point(gx, r.Top), new Point(gx, r.Bottom));
            dc.DrawLine(GridPen, new Point(r.Left, gy), new Point(r.Right, gy));
        }
        var pts = Points;
        if (CurveId == "custom" && pts is { Count: >= 2 })
            DrawPolyline(dc, pts, CurvePen);
        else
            DrawPreset(dc, CurveId, CurvePen);
        dc.Pop();

        // 控制点（custom 且可编辑时；不足 2 个点也画出来，便于从空白起步）
        if (CurveId == "custom" && pts is { Count: >= 1 } && IsEditable)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                var c = ToScreen(pts[i].X, pts[i].Y);
                var rad = i == _dragIndex ? 6 : 4.5;
                dc.DrawEllipse(AccentBrushC, PointRimPen, c, rad, rad);
            }
        }

        // 坐标刻度与轴名
        foreach (var (v, label) in new[] { (0.0, "0"), (0.5, "50"), (1.0, "100") })
        {
            var yt = Text(label, 9, FaintBrush, dpi);
            yt.TextAlignment = TextAlignment.Right;
            dc.DrawText(yt, new Point(r.Left - 5, r.Bottom - v * r.Height - yt.Height / 2));
            var xt = Text(label, 9, FaintBrush, dpi);
            xt.TextAlignment = TextAlignment.Center;
            dc.DrawText(xt, new Point(r.Left + v * r.Width, r.Bottom + 4));
        }
        var yName = Text("实际响度%", 9.5, FaintBrush, dpi);
        dc.DrawText(yName, new Point(0, 0));
        var xName = Text("音量%", 9.5, FaintBrush, dpi);
        dc.DrawText(xName, new Point(ActualWidth - xName.Width - 2, 0)); // 右上角：与 Y 轴名对称，避开底部"100"刻度
    }

    private void DrawPreset(DrawingContext dc, string curveId, Pen pen)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            const int samples = 64;
            var first = ToScreen(0, VolumeCurve.ToVolume(curveId, 0));
            ctx.BeginFigure(first, false, false);
            for (int i = 1; i <= samples; i++)
            {
                var t = i / (double)samples;
                ctx.LineTo(ToScreen(t, VolumeCurve.ToVolume(curveId, t)), true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private void DrawPolyline(DrawingContext dc, IReadOnlyList<CurvePoint> pts, Pen pen)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(ToScreen(pts[0].X, pts[0].Y), false, false);
            for (int i = 1; i < pts.Count; i++)
                ctx.LineTo(ToScreen(pts[i].X, pts[i].Y), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private FormattedText Text(string s, double size, Brush brush, double pixelsPerDip) =>
        new(s, System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal,
                FontWeights.Normal, FontStretches.Normal),
            size, brush, pixelsPerDip);

    // ---------- 编辑交互 ----------

    /// <summary>整块区域可命中：裸 FrameworkElement 默认对鼠标透明，不覆写则收不到任何输入事件。</summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters) =>
        new PointHitTestResult(this, parameters.HitPoint);

    private bool EditableCustom => IsEditable && CurveId == "custom" && Points is not null;

    private int HitPoint(Point pos)
    {
        if (Points is null)
            return -1;
        double best = HitRadius * HitRadius;
        int idx = -1;
        for (int i = 0; i < Points.Count; i++)
        {
            var s = ToScreen(Points[i].X, Points[i].Y);
            var d = (s.X - pos.X) * (s.X - pos.X) + (s.Y - pos.Y) * (s.Y - pos.Y);
            if (d <= best)
            {
                best = d;
                idx = i;
            }
        }
        return idx;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!EditableCustom)
            return;
        var pos = e.GetPosition(this);
        var hit = HitPoint(pos);
        if (hit >= 0)
        {
            _dragIndex = hit;
            CaptureMouse();
            e.Handled = true;
        }
        else if (e.ClickCount == 2 && Points is List<CurvePoint> list)
        {
            // 双击空白处加点：X 落在相邻两点之间（保持 1% 最小间隔）
            var d = ToData(pos);
            int insert = list.Count;
            for (int i = 0; i < list.Count; i++)
                if (list[i].X > d.X)
                {
                    insert = i;
                    break;
                }
            var lo = insert > 0 ? list[insert - 1].X + MinStepX : 0.0;
            var hi = insert < list.Count ? list[insert].X - MinStepX : 1.0;
            if (lo <= hi)
            {
                list.Insert(insert, new CurvePoint(Math.Clamp(d.X, lo, Math.Max(lo, hi)), Math.Clamp(d.Y, 0, 1)));
                InvalidateVisual();
                CurveEdited?.Invoke();
            }
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (_dragIndex >= 0 && IsMouseCaptured && Points is List<CurvePoint> list && _dragIndex < list.Count)
        {
            var d = ToData(pos);
            var p = list[_dragIndex];
            p.Y = Math.Clamp(d.Y, 0, 1);
            bool endpoint = _dragIndex == 0 || _dragIndex == list.Count - 1;
            if (!endpoint)
            {
                var lo = list[_dragIndex - 1].X + MinStepX;
                var hi = list[_dragIndex + 1].X - MinStepX;
                p.X = Math.Clamp(d.X, lo, Math.Max(lo, hi));
            }
            InvalidateVisual();
            CurveEdited?.Invoke();
            e.Handled = true;
            return;
        }
        if (EditableCustom)
            Cursor = HitPoint(pos) >= 0 ? Cursors.Hand : null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragIndex < 0)
            return;
        _dragIndex = -1;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        CurveEdited?.Invoke();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (!EditableCustom || Points is not List<CurvePoint> list)
            return;
        var hit = HitPoint(e.GetPosition(this));
        if (hit > 0 && hit < list.Count - 1 && list.Count > 2)
        {
            list.RemoveAt(hit); // 首尾端点不可删
            InvalidateVisual();
            CurveEdited?.Invoke();
            e.Handled = true;
        }
    }
}
