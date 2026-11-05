using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Tuner;

// ---------- 外观页：主题/强调色/字号/密度/圆角/动效（主流显示风格全量可调） ----------
// 全部改动即时 Appearance.Apply（DynamicResource 热切换，不重建窗口），随既有 800ms 防抖自动保存。

public partial class MainWindow
{
    private void LoadAppearanceTab()
    {
        var a = App.Config.Appearance;
        // 语言切换即时刷新：条目文本每次重灌，选中态按配置回填（索引赋同值不触发事件）
        ThemeCombo.Items.Clear();
        ThemeCombo.Items.Add(Lang.Get("S.Ap.ThemeDark"));
        ThemeCombo.Items.Add(Lang.Get("S.Ap.ThemeLight"));
        ThemeCombo.Items.Add(Lang.Get("S.Ap.ThemeSystem"));
        FontScaleCombo.Items.Clear();
        FontScaleCombo.Items.Add(Lang.Get("S.Ap.ScaleSmall"));
        FontScaleCombo.Items.Add(Lang.Get("S.Ap.ScaleStandard"));
        FontScaleCombo.Items.Add(Lang.Get("S.Ap.ScaleLarge"));
        DensityCombo.Items.Clear();
        DensityCombo.Items.Add(Lang.Get("S.Ap.DensityComfortable"));
        DensityCombo.Items.Add(Lang.Get("S.Ap.DensityCompact"));
        RadiusCombo.Items.Clear();
        RadiusCombo.Items.Add(Lang.Get("S.Ap.RadiusSharp"));
        RadiusCombo.Items.Add(Lang.Get("S.Ap.RadiusSoft"));
        RadiusCombo.Items.Add(Lang.Get("S.Ap.RadiusRound"));

        ThemeCombo.SelectedIndex = a.Theme switch { "light" => 1, "system" => 2, _ => 0 };
        FontScaleCombo.SelectedIndex = a.FontScale switch { "small" => 0, "large" => 2, _ => 1 };
        DensityCombo.SelectedIndex = a.Density == "compact" ? 1 : 0;
        RadiusCombo.SelectedIndex = a.Radius switch { "sharp" => 0, "round" => 2, _ => 1 };
        AnimToggle.IsChecked = a.Animations;
        BuildAccentSwatches();
    }

    /// <summary>强调色预设色板（第一个＝默认琥珀，选中态描环）。</summary>
    private void BuildAccentSwatches()
    {
        AccentPanel.Children.Clear();
        var current = App.Config.Appearance.Accent;
        if (string.IsNullOrEmpty(current))
            current = Appearance.AccentPresets[0];
        foreach (var hex in Appearance.AccentPresets)
        {
            var fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            fill.Freeze();
            var selected = hex == current;
            var swatch = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 8, 0),
                Tag = hex,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = hex,
                Content = new Ellipse { Width = 16, Height = 16, Fill = fill },
                Style = (Style)FindResource("Btn"),
                BorderThickness = new Thickness(selected ? 2 : 1),
                BorderBrush = selected
                    ? (TryFindResource("AccentFg") as Brush ?? fill)
                    : (TryFindResource("FieldBorder") as Brush ?? Brushes.Gray),
            };
            swatch.Click += OnAccentSwatchClick;
            AccentPanel.Children.Add(swatch);
        }
    }

    private void OnAccentSwatchClick(object sender, RoutedEventArgs e)
    {
        if (_loadingUi || sender is not Button { Tag: string hex })
            return;
        // 默认琥珀以空串持久化（ConfigStore 归一后与色板第一项等价）
        App.Config.Appearance.Accent = hex == Appearance.AccentPresets[0] ? "" : hex;
        Appearance.Apply(App.Config.Appearance);
        BuildAccentSwatches();
        App.Log("强调色已切换：" + hex);
        MarkDirty();
    }

    private void OnAppearancePrefChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi)
            return;
        var a = App.Config.Appearance;
        a.Theme = ThemeCombo.SelectedIndex switch { 1 => "light", 2 => "system", _ => "dark" };
        a.FontScale = FontScaleCombo.SelectedIndex switch { 0 => "small", 2 => "large", _ => "standard" };
        a.Density = DensityCombo.SelectedIndex == 1 ? "compact" : "comfortable";
        a.Radius = RadiusCombo.SelectedIndex switch { 0 => "sharp", 2 => "round", _ => "soft" };
        Appearance.Apply(a);
        App.Log($"外观已切换：主题 {a.Theme} / 字号 {a.FontScale} / 密度 {a.Density} / 圆角 {a.Radius}");
        MarkDirty();
    }

    private void OnAnimToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi)
            return;
        App.Config.Appearance.Animations = AnimToggle.IsChecked == true;
        Appearance.Apply(App.Config.Appearance);
        App.Log("界面动效已" + (App.Config.Appearance.Animations ? "开启" : "关闭"));
        MarkDirty();
    }
}
