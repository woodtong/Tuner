using System.Drawing;
using System.Drawing.Drawing2D;

namespace Tuner;

/// <summary>运行时生成托盘图标（深蓝底 + 白色扬声器），无需外部资源文件。</summary>
internal static class TrayIconFactory
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(30, 60, 110));
            using var fg = new SolidBrush(Color.White);
            g.FillEllipse(bg, 0, 0, 31, 31);
            // 扬声器主体：小矩形 + 喇叭口
            g.FillRectangle(fg, 7, 13, 4, 6);
            var horn = new PointF[] { new(11, 13), new(17, 8), new(17, 24), new(11, 19) };
            g.FillPolygon(fg, horn);
            // 两道声波弧线
            using var pen = new Pen(fg, 2f);
            g.DrawArc(pen, 17, 11, 6, 10, -60, 120);
            g.DrawArc(pen, 19, 8, 10, 16, -60, 120);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
