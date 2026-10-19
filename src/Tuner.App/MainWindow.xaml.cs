using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
            UpdateRuleMatchHints(); // 会话随时间变化，切回规则页时重算失配提示
    }
}
