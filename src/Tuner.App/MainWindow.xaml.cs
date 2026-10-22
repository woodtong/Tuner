using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tuner.Core.Audio;
using Tuner.Core.Ducking;
using Color = System.Windows.Media.Color;
using Imaging = System.Windows.Interop.Imaging;
using TabControl = System.Windows.Controls.TabControl;

namespace Tuner;

/// <summary>
/// 主窗口。代码按职责拆分为多个 partial 文件：
/// 本文件＝窗口装配、公共调色板、刷新调度与共享工具；
/// 页签各自在 MainWindow.{Status,Volume,Groups,Rules,Settings}.cs；
/// 开发模式工具（--probe / --screenshot）在 MainWindow.DevHarness.cs。
/// </summary>
public partial class MainWindow : Window
{
    private bool _loadingUi;
    private bool _dirty;
    private readonly DispatcherTimer _autoSaveTimer;

    // 分组配色（按配置顺序取色，默认组固定灰蓝）
    private static readonly string[] Palette =
    {
        "#2563EB", "#0E9F6E", "#D97706", "#7C3AED", "#DB2777", "#0891B2", "#65A30D", "#DC2626",
    };
    private readonly Dictionary<string, SolidColorBrush> _groupColors = new();
    private static readonly SolidColorBrush DefaultGroupBrush = MakeBrush("#64748B");
    private static readonly SolidColorBrush AccentBrush = MakeBrush("#2563EB");
    private static readonly SolidColorBrush AmberBrush = MakeBrush("#F59E0B");
    private static readonly SolidColorBrush GreenBrush = MakeBrush("#22C55E");
    private static readonly SolidColorBrush IdleBrush = MakeBrush("#CBD5E1");

    private static SolidColorBrush MakeBrush(string hex)
    {
        var b = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public MainWindow()
    {
        InitializeComponent();
        ProcessIconService.Register();
        try
        {
            var icon = TrayIconFactory.Create();
            Icon = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        catch { /* 图标失败不影响功能 */ }

        DeviceText.Text = App.Monitor.DeviceName;
        OutputTitle.Text = "输出与设备：" + App.Monitor.DeviceName;
        StatusList.ItemsSource = _statusRows;
        VolumeList.ItemsSource = _volumeRows;
        RefreshGroupColors();
        RefreshGroupTab();
        RefreshRuleTab();
        _loadingUi = true;
        LoadSettingsTab();
        _loadingUi = false;
        SetSaveStatus(null);
        VersionText.Text = "v" + (typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            EnforceMuteIntents(); // 静音意图钉零与页签/可见性无关，恒定维持
            if (!IsVisible)
                return; // 托盘常驻：窗口隐藏时跳过全部界面刷新
            MasterMeter.Value = Math.Round(App.Monitor.DevicePeak * 100); // 顶栏电平表在页签外，任何页都刷新
            // 只刷新当前可见页签（隐藏页签的行级刷新是纯浪费；切回页签时 Tabs_SelectionChanged 立即补刷）
            switch (Tabs.SelectedIndex)
            {
                case 0:
                {
                    var (sessions, states) = BuildRefreshInputs();
                    RefreshStatus(sessions, states);
                    break;
                }
                case 1:
                {
                    var (sessions, states) = BuildRefreshInputs();
                    RefreshVolumes(sessions, states);
                    break;
                }
            }
        };
        timer.Start();
        App.Engine.EngineStateChanged += () =>
        {
            if (!IsVisible)
                return;
            Dispatcher.BeginInvoke(() =>
            {
                if (Tabs.SelectedIndex == 0) // 页签判断必须在 UI 线程做（Selector 跨线程读会抛异常）
                    RefreshStatus();
            });
        };

        // 诊断事件 → 面板实时追加（事件可能在任意线程产生；_diagPanelOpen 是普通字段，跨线程读安全）
        Core.Diag.Changed += () =>
        {
            if (!IsVisible || !_diagPanelOpen)
                return;
            Dispatcher.BeginInvoke(RefreshDiagBox);
        };

        // 导航滑动指示器（参考 cogent-gui useSlideHighlight）：单个胶囊跟随悬停平滑滑动，
        // 鼠标离开导航回落到选中项（悬停色→选中色随位过渡）。模板取件要等模板应用完（Loaded）
        Tabs.Loaded += (_, _) => InitNavIndicator();

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _autoSaveTimer.Tick += (_, _) => AutoSaveNow();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.ForceExit)
        {
            e.Cancel = true;
            Hide(); // 关闭窗口 = 最小化到托盘，退出请用托盘菜单
            return;
        }
        if (_dirty)
            AutoSaveNow(); // 真正退出前把防抖窗口内未落盘的修改补存
        base.OnClosing(e);
    }

    private int GroupIndex(string groupId)
    {
        var idx = App.Config.Groups.FindIndex(g => g.Id == groupId);
        return idx >= 0 ? idx : 999;
    }

    private SolidColorBrush GroupBrush(string groupId) =>
        _groupColors.GetValueOrDefault(groupId, DefaultGroupBrush);

    /// <summary>窗口不可见时也必须维持：意图静音的通道实际音量钉零（驱动静音回读旧值的对策）。</summary>
    private void EnforceMuteIntents()
    {
        List<string>? intents = null;
        foreach (var row in _volumeById.Values)
            if (row.MuteIntent)
                (intents ??= new List<string>()).Add(row.InstanceId);
        if (intents is null)
            return; // 无静音意图（常态）：零分配直接返回
        foreach (var id in intents)
        {
            var s = App.Monitor.CurrentSnapshot.FirstOrDefault(x => x.InstanceId == id);
            if (s is { Volume: > 0.005f })
                App.Monitor.SetSessionVolume(id, 0f);
        }
    }

    /// <summary>一次抓取引擎状态与按通道序排好的会话快照，供实时状态/音量两页共用。</summary>
    private (List<SoundSession> Sessions, Dictionary<string, DuckingEngine.SessionState> States)
        BuildRefreshInputs()
    {
        var states = App.Engine.CurrentStates.ToDictionary(s => s.InstanceId);
        var sessions = App.Monitor.CurrentSnapshot
            .OrderBy(s => GroupIndex(states.GetValueOrDefault(s.InstanceId)?.GroupId ?? ""))
            .ThenBy(s => s.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Pid)
            .ToList();
        return (sessions, states);
    }

    /// <summary>立即刷新当前可见的实时页（切页签后调用，避免等下一拍）。</summary>
    private void RefreshVisiblePage()
    {
        switch (Tabs.SelectedIndex)
        {
            case 0:
                RefreshStatus();
                break;
            case 1:
            {
                var (sessions, states) = BuildRefreshInputs();
                RefreshVolumes(sessions, states);
                break;
            }
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T hit)
                return hit;
            var deeper = FindDescendant<T>(child);
            if (deeper is not null)
                return deeper;
        }
        return null;
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || e.OriginalSource is not TabControl)
            return; // 只响应外层页签切换（页签内的下拉等选择事件也会冒泡到这里）
        if (IsVisible)
            RefreshVisiblePage();
        if (Tabs.SelectedItem is TabItem { Header: "分组" })
            RefreshGroupDetail();
        if (Tabs.SelectedItem is TabItem { Header: "规则" })
            RefreshRuleTab(); // 会话随时间变化：切回规则页时重算失配提醒（同时保持选择不变）
        // 导航胶囊滑到新选中项；鼠标仍在导航内时保持悬停色（点击刚选中的那条）
        SlideNavIndicator(Tabs.SelectedItem as TabItem, resting: _navRoot?.IsMouseOver != true);
    }

    // ---------- 导航滑动指示器（cogent-gui useSlideHighlight 的 WPF 移植） ----------
    //
    // 单个胶囊（TabControl 模板 NavIndicator）承载全部导航视觉反馈：
    // 悬停 → 滑到悬停条目（悬停色）；离开导航/切换选中 → 滑到选中条目（选中色）。
    // 位移动画 200ms ease-out（与 cogent 的 CSS transition 同参），高度/颜色同步过渡。

    private System.Windows.Controls.Border? _navIndicator;
    private System.Windows.Controls.Grid? _navRoot;

    private void InitNavIndicator()
    {
        _navIndicator = Tabs.Template.FindName("NavIndicator", Tabs) as System.Windows.Controls.Border;
        _navRoot = Tabs.Template.FindName("NavRoot", Tabs) as System.Windows.Controls.Grid;
        if (_navIndicator is null || _navRoot is null)
            return; // 模板缺件（理论上不可达）：指示器缺席，导航退化为纯文字高亮
        // 模板里声明的 Freezable（RenderTransform/Background 画刷）随模板密封被冻结，
        // 代码里 BeginAnimation 会抛"已密封或已冻结"（Storyboard 触发器路径才自动克隆）。
        // 换成本代码创建的可变实例后再驱动动画。
        _navIndicator.RenderTransform = new System.Windows.Media.TranslateTransform();
        _navIndicator.Background = new System.Windows.Media.SolidColorBrush(ColorsFromTheme.NavSelected);
        foreach (TabItem item in Tabs.Items)
            item.MouseEnter += (s, _) => SlideNavIndicator(s as TabItem, resting: false);
        _navRoot.MouseLeave += (_, _) => SlideNavIndicator(Tabs.SelectedItem as TabItem, resting: true);
        Tabs.SizeChanged += (_, _) => SnapNavIndicator(); // 窗口缩放/DPI 变化后原地校准（不带动画）
        try
        {
            SnapNavIndicator();
        }
        catch (Exception ex)
        {
            App.Log("导航指示器初始化异常: " + ex); // 定位用：不吞，继续走（指示器缺席仅损失动效）
        }
    }

    /// <summary>胶囊滑动到目标条目：resting=选中态（选中色），否则悬停态（悬停色）。目标为空则不动。</summary>
    private void SlideNavIndicator(TabItem? target, bool resting)
    {
        if (_navIndicator is null || _navRoot is null || target is null)
            return;
        if (target.TransformToVisual(_navRoot) is not { } toRoot)
            return; // 尚未布局完成
        var y = toRoot.Transform(new System.Windows.Point(0, 0)).Y;
        var duration = TimeSpan.FromMilliseconds(200);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        (_navIndicator.RenderTransform as System.Windows.Media.TranslateTransform)?.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(y, duration) { EasingFunction = ease });
        _navIndicator.BeginAnimation(System.Windows.Controls.Border.HeightProperty,
            new DoubleAnimation(target.ActualHeight, duration) { EasingFunction = ease });
        if (_navIndicator.Background is System.Windows.Media.SolidColorBrush brush)
            brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty,
                new System.Windows.Media.Animation.ColorAnimation(
                    resting ? ColorsFromTheme.NavSelected : ColorsFromTheme.NavHover,
                    TimeSpan.FromMilliseconds(180)));
    }

    /// <summary>无动画校准胶囊位置（启动落位、窗口尺寸变化后）。</summary>
    private void SnapNavIndicator()
    {
        if (_navIndicator is null || _navRoot is null || Tabs.SelectedItem is not TabItem target)
            return;
        if (target.ActualHeight <= 0 || target.TransformToVisual(_navRoot) is not { } toRoot)
            return; // 尚未完成布局，等下一次尺寸事件再校准
        var y = toRoot.Transform(new System.Windows.Point(0, 0)).Y;
        _navIndicator.BeginAnimation(System.Windows.Controls.Border.HeightProperty, null);
        _navIndicator.Height = target.ActualHeight;
        (_navIndicator.RenderTransform as System.Windows.Media.TranslateTransform)?.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty, null);
        (_navIndicator.RenderTransform as System.Windows.Media.TranslateTransform)!.Y = y;
        if (_navIndicator.Background is System.Windows.Media.SolidColorBrush brush)
        {
            brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, null);
            brush.Color = ColorsFromTheme.NavSelected;
        }
    }

    /// <summary>导航指示器两态颜色（与 Theme.xaml 的 AccentSoft/HoverSoft 保持一致的字面值）。</summary>
    private static class ColorsFromTheme
    {
        public static readonly System.Windows.Media.Color NavSelected =
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#332B1C");
        public static readonly System.Windows.Media.Color NavHover =
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#262E3B");
    }
}
