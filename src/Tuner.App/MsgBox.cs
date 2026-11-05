using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace Tuner;

/// <summary>
/// 应用内风格化提示框（替代 Windows 原生 MessageBox：深色圆角卡片 + 投影，与应用主题一致）。
/// Show＝单“确定”；Confirm＝“确定/取消”并返回是否确定。样式全部取自应用资源（随主题/强调色走）。
/// </summary>
public static class MsgBox
{
    public static void Show(Window? owner, string text) => _ = Run(owner, text, false);

    public static bool Confirm(Window? owner, string text) => Run(owner, text, true);

    private static bool Run(Window? owner, string text, bool confirm)
    {
        var res = Application.Current;
        Brush BrushOf(string key) => res.TryFindResource(key) as Brush ?? Brushes.Gray;
        var radius = res.TryFindResource("Rad.Card") as CornerRadius? ?? new CornerRadius(9);

        bool result = false;
        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            SizeToContent = SizeToContent.Height,
            Width = 440,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is { IsLoaded: true }
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
        };
        if (owner is { IsLoaded: true })
            win.Owner = owner;

        var okBtn = new Button { Content = Lang.Get("S.Common.Ok"), MinWidth = 96, Style = (Style)res.FindResource("BtnPrimary") };
        okBtn.Click += (_, _) => { result = true; win.Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (confirm)
        {
            var cancelBtn = new Button { Content = Lang.Get("S.Common.Cancel"), MinWidth = 96, Style = (Style)res.FindResource("Btn") };
            cancelBtn.Click += (_, _) => win.Close();
            buttons.Children.Add(cancelBtn);
        }
        buttons.Children.Add(okBtn);

        var card = new Border
        {
            Background = BrushOf("CardBg"),
            BorderBrush = BrushOf("FieldBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = radius,
            Margin = new Thickness(16), // 透明窗外沿给投影留位
            Padding = new Thickness(24, 20, 24, 20),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.45 },
        };
        card.Child = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = "Tuner",
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = BrushOf("TextPrimary"),
                    Margin = new Thickness(0, 0, 0, 10),
                },
                new TextBlock
                {
                    Text = text,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = BrushOf("TextSecondary"),
                    Margin = new Thickness(0, 0, 0, 20),
                },
                buttons,
            },
        };

        win.Content = card;
        // 拖动 + 键盘：Esc=取消，Enter=确定
        card.MouseLeftButtonDown += (_, _) => { try { win.DragMove(); } catch { } };
        win.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                win.Close();
            else if (e.Key == Key.Enter)
            {
                result = true;
                win.Close();
            }
        };
        okBtn.Focus();
        win.ShowDialog();
        return result;
    }
}
