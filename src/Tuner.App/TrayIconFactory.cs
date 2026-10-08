using System.Drawing;
using System.Drawing.Drawing2D;

namespace Tuner;

/// <summary>运行时生成托盘/窗口图标：深色圆角底板 + 三路调音台推子（MASTER 琥珀居中最高）。
/// 与 src/Tuner.App/app.ico 同一设计（ico 由 tools/make_app_icon.py 生成）；
/// 此处按小尺寸变体用 GDI+ 直画，保证缩到 16px 时旋钮仍清晰。</summary>
internal static class TrayIconFactory
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 底板：圆角矩形 + 对角渐变，描一圈极淡的高光边
            using var tile = RoundedPath(1.2f, 1.2f, 30.8f, 30.8f, 7f);
            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, 32, 32),
                       Color.FromArgb(0x0F, 0x14, 0x1D), Color.FromArgb(0x2C, 0x37, 0x4A),
                       LinearGradientMode.ForwardDiagonal))
                g.FillPath(bg, tile);
            using (var edge = new Pen(Color.FromArgb(20, 255, 255, 255)))
                g.DrawPath(edge, tile);

            // 三道凹槽：亮边 + 暗芯
            using (var rim = new Pen(Color.FromArgb(0x39, 0x41, 0x4F), 3f))
            using (var core = new Pen(Color.FromArgb(0x10, 0x15, 0x1F), 1.5f))
            {
                rim.StartCap = rim.EndCap = LineCap.Round;
                core.StartCap = core.EndCap = LineCap.Round;
                foreach (var x in new float[] { 9.6f, 16f, 22.4f })
                {
                    g.DrawLine(rim, x, 9.5f, x, 22.5f);
                    g.DrawLine(core, x, 9.5f, x, 22.5f);
                }
            }

            // 推子旋钮：左绿居中偏上、中琥珀（MASTER）最高、右灰更低
            DrawKnob(g, 9.6f, 14.6f, Color.FromArgb(0x34, 0xD3, 0x99));
            DrawKnob(g, 16f, 10.7f, Color.FromArgb(0xF5, 0xB9, 0x42));
            DrawKnob(g, 22.4f, 19f, Color.FromArgb(0xCB, 0xD3, 0xE0));
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static void DrawKnob(Graphics g, float cx, float cy, Color color)
    {
        using var b = new SolidBrush(color);
        g.FillRectangle(b, cx - 4.5f, cy - 2f, 9f, 4f);
    }

    private static GraphicsPath RoundedPath(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        var d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
