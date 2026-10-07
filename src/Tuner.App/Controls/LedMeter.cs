using System.Windows;
using System.Windows.Controls;
using Orientation = System.Windows.Controls.Orientation;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Tuner.Controls;

/// <summary>调音台风格的 LED 分段电平表：低位绿、高位琥珀、峰值红，未点亮段为暗色。</summary>
public sealed class LedMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LedMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(int), typeof(LedMeter),
        new FrameworkPropertyMetadata(14, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Segments
    {
        get => (int)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(LedMeter),
        new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.AffectsRender));

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    private static readonly Brush Unlit = Freeze(new SolidColorBrush(Color.FromRgb(0x24, 0x2A, 0x34)));
    private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x2F, 0xD0, 0x8C)));
    private static readonly Brush Amber = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xB9, 0x42)));
    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)));

    private static SolidColorBrush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;
        int segs = Math.Max(4, Segments);
        int lit = (int)Math.Round(Math.Clamp(Value, 0, 100) / 100.0 * segs);
        double gap = 1.6;
        for (int i = 0; i < segs; i++)
        {
            Rect rect;
            if (Orientation == Orientation.Vertical)
            {
                // 纵向：从底部向上点亮（电平表习惯）
                double segH = Math.Max(1, (h - gap * (segs - 1)) / segs);
                double y = h - (i + 1) * segH - i * gap;
                rect = new Rect(0, y, w, segH);
            }
            else
            {
                double segW = Math.Max(1, (w - gap * (segs - 1)) / segs);
                rect = new Rect(i * (segW + gap), 0, segW, h);
            }
            bool on = i < lit;
            double frac = (i + 1) / (double)segs;
            var brush = !on ? Unlit : frac >= 0.9 ? Red : frac >= 0.7 ? Amber : Green;
            dc.DrawRoundedRectangle(brush, null, rect, 1, 1);
        }
    }
}
