using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tuner.Core.Audio;
using Tuner.Core.Config;
using Tuner.Core.Ducking;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using Imaging = System.Windows.Interop.Imaging;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseWheelEventArgs = System.Windows.Input.MouseWheelEventArgs;
using MessageBox = System.Windows.MessageBox;
using TabControl = System.Windows.Controls.TabControl;

namespace Tuner;

public partial class MainWindow : Window
{
    private bool _loadingUi;
    private bool _dirty;

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
        RefreshGroupColors();
        RefreshGroupTab();
        RefreshRuleTab();
        LoadSettingsTab();
        SetSaveStatus(null);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => { RefreshStatus(); RefreshVolumes(); };
        timer.Start();
        App.Engine.EngineStateChanged += () => Dispatcher.BeginInvoke(() => RefreshStatus());
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.ForceExit)
        {
            e.Cancel = true;
            Hide(); // 关闭窗口 = 最小化到托盘，退出请用托盘菜单
            return;
        }
        base.OnClosing(e);
    }

    // ---------- 实时状态（按 InstanceId 差量更新，避免整表重建） ----------

    private sealed class StatusRow : INotifyPropertyChanged
    {
        public string InstanceId = "";
        private string _process = "";
        private string _pidText = "";
        private string _groupName = "";
        private Brush _groupBrush = IdleBrushStatic;
        private Brush _speakingBrush = IdleBrushStatic;
        private Visibility _duckVisibility = Visibility.Collapsed;
        private string _duckText = "";
        private double _volumePercent;
        private string _volumeText = "";
        private Brush _volBrush = IdleBrushStatic;
        private string _originalText = "";

        private static readonly Brush IdleBrushStatic = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));

        private string _channel = "";
        private double _peakPercent;

        public string Channel { get => _channel; set { _channel = value; Pc(); } }
        public double PeakPercent { get => _peakPercent; set { _peakPercent = value; Pc(); } }

        private double _stripOpacity = 0.25;
        /// <summary>通道色条透明度：出声时点亮。</summary>
        public double StripOpacity { get => _stripOpacity; set { _stripOpacity = value; Pc(); } }

        private BitmapImage? _icon;
        /// <summary>应用图标。</summary>
        public BitmapImage? Icon { get => _icon; set { _icon = value; Pc(); } }

        public string Process { get => _process; set { _process = value; Pc(); } }
        public string PidText { get => _pidText; set { _pidText = $"PID {value}"; Pc(); } }
        public string GroupName { get => _groupName; set { _groupName = value; Pc(); } }
        public Brush GroupBrush { get => _groupBrush; set { _groupBrush = value; Pc(); } }
        public Brush SpeakingBrush { get => _speakingBrush; set { _speakingBrush = value; Pc(); } }
        public Visibility DuckVisibility { get => _duckVisibility; set { _duckVisibility = value; Pc(); } }
        public string DuckText { get => _duckText; set { _duckText = value; Pc(); } }
        public double VolumePercent { get => _volumePercent; set { _volumePercent = value; Pc(); } }
        public string VolumeText { get => _volumeText; set { _volumeText = value; Pc(); } }
        public Brush VolBrush { get => _volBrush; set { _volBrush = value; Pc(); } }
        public string OriginalText { get => _originalText; set { _originalText = value; Pc(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Pc() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private readonly ObservableCollection<StatusRow> _statusRows = new();
    private readonly Dictionary<string, StatusRow> _statusById = new();
    private string _statusSignature = "";

    private int GroupIndex(string groupId)
    {
        var idx = App.Config.Groups.FindIndex(g => g.Id == groupId);
        return idx >= 0 ? idx : 999;
    }

    private SolidColorBrush GroupBrush(string groupId) =>
        _groupColors.GetValueOrDefault(groupId, DefaultGroupBrush);

    private void RefreshStatus()
    {
        var states = App.Engine.CurrentStates.ToDictionary(s => s.InstanceId);
        var sessions = App.Monitor.CurrentSnapshot
            .OrderBy(s => GroupIndex(states.GetValueOrDefault(s.InstanceId)?.GroupId ?? ""))
            .ThenBy(s => s.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Pid)
            .ToList();

        var sig = string.Join("|", sessions.Select(s => s.InstanceId));
        if (sig != _statusSignature)
        {
            _statusSignature = sig;
            _statusRows.Clear();
            _statusById.Clear();
            for (int i = 0; i < sessions.Count; i++)
            {
                var row = new StatusRow { InstanceId = sessions[i].InstanceId, Channel = ((i + 1).ToString("00")) };
                _statusRows.Add(row);
                _statusById[sessions[i].InstanceId] = row;
            }
        }

        int speaking = 0, ducking = 0;
        foreach (var s in sessions)
        {
            var row = _statusById[s.InstanceId];
            var e = states.GetValueOrDefault(s.InstanceId);
            row.PeakPercent = Math.Round(s.Peak * 100);
            row.StripOpacity = e?.Speaking == true ? 1.0 : 0.25;
            row.Process = s.ProcessName;
            row.PidText = s.Pid.ToString();
            row.GroupName = e?.GroupName ?? "—";
            row.GroupBrush = GroupBrush(e?.GroupId ?? "");
            row.SpeakingBrush = e?.Speaking == true ? GreenBrush : IdleBrush;
            if (e?.Speaking == true) speaking++;
            row.VolumePercent = Math.Round(s.Volume * 100);
            row.VolumeText = $"{(int)Math.Round(s.Volume * 100)}%";
            row.VolBrush = e is { Ducked: true } ? AmberBrush : AccentBrush;
            row.DuckVisibility = e is { Ducked: true } ? Visibility.Visible : Visibility.Collapsed;
            row.DuckText = $"闪避中 → {(int)Math.Round((e?.TargetVolume ?? 0) * 100)}%";
            if (e is { Ducked: true }) ducking++;
            row.Icon = ProcessIconService.ToBitmap(s.IconPng);
        }

        StatTotal.Text = sessions.Count.ToString();
        StatSpeaking.Text = speaking.ToString();
        StatDucking.Text = ducking.ToString();
        MasterMeter.Value = Math.Round(App.Monitor.DevicePeak * 100);
        StatusEmpty.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- 音量（输出混音台） ----------

    private sealed class VolumeChannel : INotifyPropertyChanged
    {
        public string InstanceId = "";
        public bool Suppress; // 程序化刷新时抑制 ValueChanged 回写

        private string _name = "";
        private string _pidText = "";
        private string _groupName = "";
        private Brush _groupBrush = IdleBrush;
        private double _peakPercent;
        private double _volumePercent = 100;
        private string _muteText = "静音";
        private BitmapImage? _icon;
        private bool _zeroState;
        /// <summary>有效音量为 0（静音标志或音量≈0，不论何种原因触发）。</summary>
        public bool ZeroState { get => _zeroState; set { _zeroState = value; Pc(); } }
        /// <summary>动画期间冻结实际值回写（防推子被刷新拉扯）。</summary>
        public DateTime HoldUntil = DateTime.MinValue;
        /// <summary>最近一次非零音量（取消静音时恢复）。仅用户主动动作写入：拖动松手、滚轮逐格。</summary>
        public double RestoreVolume { get; set; } = 50;
        /// <summary>用户意图的静音状态（快速点击时按意图严格交替，不依赖滞后的实际状态）。</summary>
        public bool MuteIntent;
        /// <summary>滚轮 delta 累计（满 120 记 1 步）。</summary>
        public double WheelAccum;
        /// <summary>逻辑目标值：滚轮在其上累加（与动画进度无关，快速滚动不丢格）。</summary>
        public double FaderTarget = 100;
        public bool FaderTargetInit;

        public string Name { get => _name; set { _name = value; Pc(); } }
        public string PidText { get => _pidText; set { _pidText = value; Pc(); } }
        public string GroupName { get => _groupName; set { _groupName = value; Pc(); } }
        public Brush GroupBrush { get => _groupBrush; set { _groupBrush = value; Pc(); } }
        public double PeakPercent { get => _peakPercent; set { _peakPercent = value; Pc(); } }
        public double VolumePercent { get => _volumePercent; set { _volumePercent = value; Pc(); } }
        public string MuteText { get => _muteText; set { _muteText = value; Pc(); } }
        public BitmapImage? Icon { get => _icon; set { _icon = value; Pc(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Pc() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private readonly ObservableCollection<VolumeChannel> _volumeRows = new();
    private readonly Dictionary<string, VolumeChannel> _volumeById = new();
    private string _volumeSignature = "";
    private bool _masterSuppress;

    private void RefreshVolumes()
    {
        var states = App.Engine.CurrentStates.ToDictionary(s => s.InstanceId);
        var sessions = App.Monitor.CurrentSnapshot
            .OrderBy(s => GroupIndex(states.GetValueOrDefault(s.InstanceId)?.GroupId ?? ""))
            .ThenBy(s => s.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Pid)
            .ToList();

        var sig = string.Join("|", sessions.Select(s => s.InstanceId));
        if (sig != _volumeSignature)
        {
            _volumeSignature = sig;
            _volumeRows.Clear();
            _volumeById.Clear();
            foreach (var s in sessions)
            {
                var row = new VolumeChannel { InstanceId = s.InstanceId };
                _volumeRows.Add(row);
                _volumeById[s.InstanceId] = row;
            }
            VolumeList.ItemsSource = _volumeRows;
        }

        foreach (var s in sessions)
        {
            var row = _volumeById[s.InstanceId];
            var e = states.GetValueOrDefault(s.InstanceId);
            row.Suppress = true;
            row.Name = s.ProcessName;
            row.PidText = s.Pid.ToString();
            row.Icon = ProcessIconService.ToBitmap(s.IconPng);
            row.GroupName = e?.GroupName ?? "—";
            row.GroupBrush = GroupBrush(e?.GroupId ?? "");
            row.PeakPercent = Math.Round(s.Peak * 100);
            // 意图静音：音量钉零 + 显示完全冻结（驱动在静音时回读的可能是静音前旧值，绝不能上屏）
            if (row.MuteIntent)
            {
                if (s.Volume > 0.005f)
                    App.Monitor.SetSessionVolume(row.InstanceId, 0f);
                row.Suppress = false;
                continue;
            }
            // 注意：不被动跟踪音量——动画滑落/闪避渐变的中间值会污染恢复值；
            // 恢复值只在用户主动动作（拖动松手、滚轮）时捕获
            if (DateTime.UtcNow >= row.HoldUntil) // 动画期间不回写推子，避免拉扯卡顿
                row.VolumePercent = Math.Round(s.Volume * 100);
            if (!row.FaderTargetInit)
            {
                row.FaderTarget = row.VolumePercent; // 首次以实际音量为逻辑基准
                row.FaderTargetInit = true;
            }
            // 显示规则唯一判断：音量为 0 → 取消静音
            row.MuteText = row.VolumePercent <= 0 ? "取消静音" : "静音";
            row.Suppress = false;
        }

        _masterSuppress = true;
        if (!MasterSlider.IsMouseCaptureWithin)
            MasterSlider.Value = Math.Round(App.Monitor.MasterVolume * 100);
        MasterVolText.Text = $"{(int)Math.Round(App.Monitor.MasterVolume * 100)}%";
        MasterMeterBig.Value = Math.Round(App.Monitor.DevicePeak * 100);
        MasterMuteBtn.Content = App.Monitor.MasterMuted ? "取消静音" : "静音";
        _masterSuppress = false;
    }

    private void OnMasterVolumeChanged(object sender, RoutedEventArgs e)
    {
        if (_masterSuppress)
            return;
        App.Monitor.SetMasterVolume((float)Math.Clamp(MasterSlider.Value, 0, 100) / 100f);
    }

    private void OnMasterMuteClick(object sender, RoutedEventArgs e)
    {
        App.Monitor.SetMasterMute(!App.Monitor.MasterMuted);
    }

    // ---------- 输出设备切换 ----------

    private void OnDeviceButtonClicked(object sender, RoutedEventArgs e)
    {
        var devices = App.Monitor.EnumerateRenderDevices();
        var menu = (ContextMenu)FindResource("DeviceMenu");
        menu.Items.Clear();
        foreach (var (id, name) in devices.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
        {
            var item = new MenuItem
            {
                Header = id == App.Monitor.DeviceId ? "● " + name : name,
                Tag = id,
            };
            item.Click += OnDeviceMenuItemClicked;
            menu.Items.Add(item);
        }
        menu.PlacementTarget = DeviceButton;
        menu.IsOpen = true;
    }

    private void OnDeviceMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not string id)
            return;
        try
        {
            AudioSessionMonitor.SwitchDefaultDevice(id); // 切换后经 IMMNotificationClient 自动重绑会话
            App.Log("默认输出设备已切换: " + id);
        }
        catch (Exception ex)
        {
            MessageBox.Show("切换输出设备失败：" + ex.Message, "Tuner");
        }
    }

    private void OnChannelVolumeChanged(object sender, RoutedEventArgs e)
    {
        if ((sender as Slider)?.DataContext is not VolumeChannel ch || ch.Suppress)
            return;
        var v = (float)Math.Clamp(((Slider)sender).Value, 0, 100) / 100f;
        var sess = App.Monitor.CurrentSnapshot.FirstOrDefault(x => x.InstanceId == ch.InstanceId);
        if (sess is { Mute: true } && v > 0.005f)
        {
            App.Monitor.SetSessionMute(ch.InstanceId, false); // 拖起音量即解除静音
            ch.MuteIntent = false;
        }
        App.Monitor.SetSessionVolume(ch.InstanceId, v);
        App.Engine.SetUserVolume(ch.InstanceId, v); // 闪避中手动调整 → 更新恢复锚点
        // 恢复值不在此处记录：拖动松手（DragCompleted）才记录，避免渐变中间值污染
    }

    private readonly Dictionary<string, Slider> _faderByInstance = new();

    private void OnChannelFaderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Slider s && s.DataContext is VolumeChannel ch)
        {
            _faderByInstance[ch.InstanceId] = s;
            var thumb = FindDescendant<System.Windows.Controls.Primitives.Thumb>(s);
            if (thumb is not null)
            {
                thumb.DragStarted += (_, _) => OnChannelFaderDragStarted(ch, s);
                thumb.DragDelta += (_, e) => OnChannelFaderDragDelta(ch, s, e);
                thumb.DragCompleted += (_, _) => OnChannelFaderDragCompleted(ch, s);
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

    /// <summary>悬停于滑轨区时显示凹陷槽。</summary>
    private void OnChannelFaderMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Slider s)
            SetGrooveVisible(s, true);
    }

    private void OnChannelFaderMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Slider s)
            SetGrooveVisible(s, false);
    }

    private static void SetGrooveVisible(Slider s, bool on)
    {
        if (s.Parent is System.Windows.Controls.Panel panel)
            foreach (var child in panel.Children)
                if (child is System.Windows.Controls.Border b && b.Name == "FaderGroove")
                    b.Opacity = on ? 1 : 0;
    }

    /// <summary>滚轮 1% 精度微调；每次调节都记录恢复值（用户主动微调）。</summary>
    private void OnChannelFaderMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Slider s || s.DataContext is not VolumeChannel ch)
            return;
        if (ch.Suppress)
        {
            e.Handled = true;
            return; // 滑块到位前处于保护期
        }
        // 累计滚轮 delta：每 120（一格）= 2%；在逻辑目标值上累加，动画进度不影响计数
        ch.WheelAccum += e.Delta;
        e.Handled = true;
        int steps = 0;
        while (ch.WheelAccum >= 120) { ch.WheelAccum -= 120; steps++; }
        while (ch.WheelAccum <= -120) { ch.WheelAccum += 120; steps--; }
        if (steps == 0)
            return;
        ch.FaderTarget = Math.Clamp(ch.FaderTarget + steps * 2, 0, 100);
        var target = ch.FaderTarget;
        // 实际值立即落（一次写入），视觉动效 357ms（静音 500ms 的 140% 速度）
        var v = (float)(target / 100.0);
        var sessLive = App.Monitor.CurrentSnapshot.FirstOrDefault(x => x.InstanceId == ch.InstanceId);
        if (sessLive is { Mute: true } && v > 0.005f)
        {
            App.Monitor.SetSessionMute(ch.InstanceId, false);
            ch.MuteIntent = false;
        }
        App.Monitor.SetSessionVolume(ch.InstanceId, v);
        App.Engine.SetUserVolume(ch.InstanceId, v);
        ch.RestoreVolume = target; // 每一次滚轮微调都记录
        AnimateFader(ch, target, (int)(500 / 1.4));
    }

    /// <summary>MASTER 滚轮 1% 微调。</summary>
    private void OnMasterFaderMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Slider s)
            return;
        var target = Math.Clamp(s.Value + (e.Delta > 0 ? 1 : -1), 0, 100);
        e.Handled = true;
        if (Math.Abs(target - s.Value) < 0.001)
            return;
        App.Monitor.SetMasterVolume((float)target / 100f);
    }

    /// <summary>拖动开始：推子立即吸附到光标位置（零延迟起步），冻结刷新回写；保护期内拖动不接管。</summary>
    private void OnChannelFaderDragStarted(VolumeChannel ch, Slider slider)
    {
        if (ch.Suppress)
            return; // 滑块到位前处于保护期
        slider.BeginAnimation(Slider.ValueProperty, null); // 终止进行中的动画
        var snapped = ValueAtMouse(slider);               // 立即吸附，消除起步迟滞
        slider.Value = snapped;
        ch.FaderTarget = snapped;
        ch.HoldUntil = DateTime.UtcNow + TimeSpan.FromSeconds(10); // 拖动期间刷新不回写
    }

    private static double ValueAtMouse(Slider slider)
    {
        var track = FindDescendant<System.Windows.Controls.Primitives.Track>(slider);
        if (track is null || track.ActualHeight < 1)
            return slider.Value;
        var pos = System.Windows.Input.Mouse.GetPosition(track);
        return Math.Clamp((1.0 - pos.Y / track.ActualHeight) * 100.0, 0, 100);
    }

    /// <summary>拖动：推子直接跟随光标（零动画零延迟），会话音量由 ValueChanged 统一写入。</summary>
    private void OnChannelFaderDragDelta(VolumeChannel ch, Slider slider, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (ch.Suppress)
            return;
        var target = ValueAtMouse(slider);
        ch.FaderTarget = target;
        slider.Value = target;
    }

    /// <summary>松手才记录恢复值；松手位置为 0 则不记录。</summary>
    private void OnChannelFaderDragCompleted(VolumeChannel ch, Slider slider)
    {
        slider.BeginAnimation(Slider.ValueProperty, null);
        slider.Value = ch.FaderTarget; // 定格逻辑目标
        ch.HoldUntil = DateTime.UtcNow; // 解除拖动冻结，刷新恢复
        if (ch.FaderTarget > 0.005)
            ch.RestoreVolume = ch.FaderTarget;
    }

    /// <summary>推子动画到目标值（结束后解除动画时钟并回写绑定值）。</summary>
    private void AnimateFader(VolumeChannel ch, double target, int durationMs = 500)
    {
        if (!_faderByInstance.TryGetValue(ch.InstanceId, out var slider))
        {
            ch.VolumePercent = target;
            return;
        }
        // 保护期＝动画期：到位之前滚轮/拖动/再次点击静音一律不响应
        ch.Suppress = true;
        ch.HoldUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(durationMs + 400); // 动画期+余量，刷新不回写推子
        // 从当前动画位置出发（中途重定向不跳变）
        var anim = new System.Windows.Media.Animation.DoubleAnimation(slider.Value, target, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        anim.Completed += (_, _) =>
        {
            slider.BeginAnimation(Slider.ValueProperty, null);
            slider.Value = target;
            ch.Suppress = false; // 到位，解除保护
            ch.HoldUntil = DateTime.UtcNow;
        };
        slider.BeginAnimation(Slider.ValueProperty, anim);
    }

    private void OnChannelMuteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VolumeChannel ch)
            return;
        if (ch.Suppress)
        {
            if (DateTime.UtcNow < ch.HoldUntil)
                return; // 滑块到位前处于保护期
            ch.Suppress = false; // 安全阀：超时未解锁（完成回调丢失）则强制解锁
        }
        // 严格按用户意图交替，不读滞后的实际状态；恢复值寄存器此处绝不写入
        ch.MuteIntent = !ch.MuteIntent;
        if (ch.MuteIntent)
        {
            ch.FaderTarget = 0;
            App.Monitor.SetSessionMute(ch.InstanceId, true);
            App.Monitor.SetSessionVolume(ch.InstanceId, 0f);
            App.Engine.SetUserVolume(ch.InstanceId, 0f);
            AnimateFader(ch, 0);
        }
        else
        {
            var restore = (float)Math.Clamp(ch.RestoreVolume, 1, 100) / 100f;
            ch.FaderTarget = Math.Round(restore * 100);
            App.Monitor.SetSessionMute(ch.InstanceId, false);
            App.Monitor.SetSessionVolume(ch.InstanceId, restore);
            App.Engine.SetUserVolume(ch.InstanceId, restore);
            AnimateFader(ch, Math.Round(restore * 100));
        }
    }

    /// <summary>调音台区滚轮 → 横向滚动（多应用时显示全部通道）。</summary>
    private void OnChannelsMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ChannelsScroller.ScrollableWidth <= 0)
            return;
        ChannelsScroller.ScrollToHorizontalOffset(ChannelsScroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    // ---------- 分组 ----------

    private sealed class GroupVm
    {
        public required AppGroupConfig Group { get; init; }
        public Visibility DefaultVisibility => Group.IsDefault ? Visibility.Visible : Visibility.Collapsed;
        public string CountText => Group.IsDefault
            ? $"{Group.ProcessNames.Count} 个进程 · 承接未匹配应用"
            : $"{Group.ProcessNames.Count} 个进程";

        public override string ToString() => Group.Name;
    }

    private AppGroupConfig? SelectedGroup => (GroupList.SelectedItem as GroupVm)?.Group;

    private void RefreshGroupColors()
    {
        _groupColors.Clear();
        for (int i = 0; i < App.Config.Groups.Count; i++)
        {
            var g = App.Config.Groups[i];
            _groupColors[g.Id] = g.IsDefault ? DefaultGroupBrush : MakeBrush(Palette[i % Palette.Length]);
        }
    }

    private void RefreshGroupTab()
    {
        _loadingUi = true;
        RefreshGroupColors();
        var selected = SelectedGroup;
        GroupList.ItemsSource = App.Config.Groups.Select(g => new GroupVm { Group = g }).ToList();
        GroupList.SelectedItem = GroupList.ItemsSource.Cast<GroupVm>().FirstOrDefault(v => v.Group == selected)
                                 ?? GroupList.ItemsSource.Cast<GroupVm>().FirstOrDefault();
        _loadingUi = false;
        RefreshGroupDetail();
    }

    private void RefreshGroupDetail()
    {
        bool outer = _loadingUi;
        _loadingUi = true;
        var g = SelectedGroup;
        GroupNameBox.Text = g?.Name ?? "";
        DefaultCheck.IsChecked = g?.IsDefault == true;
        GroupProcesses.ItemsSource = g?.ProcessNames.ToList() ?? new List<string>();
        GroupProcEmpty.Visibility = (g is null || g.ProcessNames.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
        RefreshRunningProcs();
        _loadingUi = outer; // 恢复外层状态（可能被嵌套调用）
    }

    private void RefreshRunningProcs()
    {
        var inSelected = SelectedGroup?.ProcessNames.ToHashSet(StringComparer.OrdinalIgnoreCase)
                         ?? new HashSet<string>();
        var running = App.Monitor.CurrentSnapshot
            .Select(s => s.ProcessName)
            .Where(n => n.Length > 0 && n != "System Sounds" && !inSelected.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        RunningProcs.ItemsSource = running;
        if (running.Count > 0)
            RunningProcs.SelectedIndex = 0;
    }

    private void OnGroupSelected(object sender, SelectionChangedEventArgs e) => RefreshGroupDetail();

    private void OnGroupAdd(object sender, RoutedEventArgs e)
    {
        var g = new AppGroupConfig { Id = "g" + DateTime.Now.Ticks.ToString("x"), Name = "新分组" };
        App.Config.Groups.Add(g);
        RefreshGroupTab();
        RefreshRuleTab();
        GroupList.SelectedItem = GroupList.ItemsSource.Cast<GroupVm>().First(v => v.Group == g);
        MarkDirty();
    }

    private void OnGroupDelete(object sender, RoutedEventArgs e)
    {
        var g = SelectedGroup;
        if (g is null)
            return;
        if (g.IsDefault)
        {
            MessageBox.Show("默认组不能删除，可将其他分组设为默认组。", "Tuner");
            return;
        }
        if (MessageBox.Show($"确定删除分组「{g.Name}」？关联的规则也会一并删除。", "Tuner",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        App.Config.Rules.RemoveAll(r => r.TriggerGroupId == g.Id || r.TargetGroupId == g.Id);
        App.Config.Groups.Remove(g);
        RefreshGroupTab();
        RefreshRuleTab();
        MarkDirty();
    }

    private void OnGroupNameChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi || SelectedGroup is not { } g)
            return;
        g.Name = GroupNameBox.Text.Trim();
        _loadingUi = true;
        GroupList.Items.Refresh();
        _loadingUi = false;
        MarkDirty();
    }

    private void OnProcBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnProcAdd(sender, e);
    }

    private void OnProcAdd(object sender, RoutedEventArgs e)
    {
        if (_loadingUi || SelectedGroup is not { } g)
            return;
        var name = NewProcBox.Text.Trim();
        if (name.Length == 0)
            return;
        if (!g.ProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            g.ProcessNames.Add(name);
        NewProcBox.Text = "";
        RefreshGroupTab();
        MarkDirty();
    }

    private void OnProcRemoveItem(object sender, RoutedEventArgs e)
    {
        if (_loadingUi || SelectedGroup is not { } g || (sender as FrameworkElement)?.Tag is not string name)
            return;
        g.ProcessNames.RemoveAll(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        RefreshGroupTab();
        MarkDirty();
    }

    private void OnRefreshRunning(object sender, RoutedEventArgs e) => RefreshRunningProcs();

    private void OnProcAddRunning(object sender, RoutedEventArgs e)
    {
        if (_loadingUi || SelectedGroup is not { } g || RunningProcs.SelectedItem is not string name)
            return;
        if (!g.ProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            g.ProcessNames.Add(name);
        RefreshGroupTab();
        MarkDirty();
    }

    private void OnDefaultChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi || SelectedGroup is not { } g)
            return;
        if (DefaultCheck.IsChecked == true)
        {
            foreach (var other in App.Config.Groups)
                other.IsDefault = false;
            g.IsDefault = true;
        }
        else if (g.IsDefault)
        {
            DefaultCheck.IsChecked = true; // 至少保留一个默认组
        }
        _loadingUi = true;
        GroupList.Items.Refresh();
        _loadingUi = false;
        MarkDirty();
    }

    // ---------- 规则 ----------

    private sealed class RuleVm
    {
        public required DuckingRuleConfig Rule { get; init; }
        public string Sentence =>
            (Rule.Enabled ? "" : "（已停用）") + Label(Rule.Trigger) + " 出声 → " + Label(Rule.Target) +
            $" 渐变到 {Rule.TargetVolumePercent:F0}%" + (Rule.DetectionMode == "state" ? "［状态判定］" : "［声音判定］");

        private static string Label(RuleRef? r)
        {
            if (r is null)
                return "?";
            return r.Type == "app"
                ? r.ProcessName + "（应用）"
                : (App.Config.Groups.FirstOrDefault(g => g.Id == r.GroupId)?.Name ?? r.GroupId + "?") + "（分组）";
        }
    }

    private DuckingRuleConfig? SelectedRule => (RuleList.SelectedItem as RuleVm)?.Rule;

    private void RefreshRuleTab()
    {
        _loadingUi = true;
        var selected = SelectedRule;
        RuleList.ItemsSource = App.Config.Rules.Select(r => new RuleVm { Rule = r }).ToList();
        RuleList.SelectedItem = RuleList.ItemsSource.Cast<RuleVm>().FirstOrDefault(v => v.Rule == selected)
                                ?? RuleList.ItemsSource.Cast<RuleVm>().FirstOrDefault();
        _loadingUi = false;
        RefreshRuleDetail();
    }

    private void RefreshRuleDetail()
    {
        bool outer = _loadingUi;
        _loadingUi = true;
        var r = SelectedRule;
        SetSelector(RuleTriggerType, RuleTriggerValue, r?.Trigger);
        SetSelector(RuleTargetType, RuleTargetValue, r?.Target);
        RuleDetectionMode.SelectedIndex = r?.DetectionMode == "sound" ? 1 : 0;
        RuleVolume.Text = r is null ? "" : $"{r.TargetVolumePercent:F0}";
        RulePriority.Text = r?.Priority.ToString() ?? "";
        RuleEnabled.IsChecked = r?.Enabled ?? false;
        _loadingUi = outer; // 恢复外层状态（可能被嵌套调用）
    }

    /// <summary>按选择器类型填充"取值"下拉：分组模式列出分组，应用模式列出已知进程名（可编辑输入）。</summary>
    private void SetSelector(ComboBox type, ComboBox value, RuleRef? sel)
    {
        bool isApp = sel?.Type == "app";
        type.SelectedIndex = isApp ? 1 : 0;
        if (isApp)
        {
            var names = AppNames();
            value.ItemsSource = names;
            value.Text = sel?.ProcessName ?? "";
            if (names.Count > 0 && !names.Contains(sel?.ProcessName ?? "", StringComparer.OrdinalIgnoreCase))
                value.SelectedIndex = 0;
        }
        else
        {
            var groups = App.Config.Groups.Select(g => new GroupVm { Group = g }).ToList();
            value.ItemsSource = groups;
            value.SelectedItem = groups.FirstOrDefault(v => v.Group.Id == sel?.GroupId) ?? groups.FirstOrDefault();
        }
    }

    /// <summary>切换选择器类型时重填取值下拉。</summary>
    private void OnRuleSelectorTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi)
            return;
        _loadingUi = true;
        if (sender == RuleTriggerType)
            SetSelector(RuleTriggerType, RuleTriggerValue, null);
        else if (sender == RuleTargetType)
            SetSelector(RuleTargetType, RuleTargetValue, null);
        _loadingUi = false;
        ApplyRuleEdits();
    }

    private void OnRuleSelectorChanged(object sender, SelectionChangedEventArgs e) => ApplyRuleEdits();

    /// <summary>从界面读取一个选择器：应用模式取输入文本，分组模式取选中分组。</summary>
    private RuleRef ReadSelector(ComboBox type, ComboBox value)
    {
        if (type.SelectedIndex == 1)
            return new RuleRef { Type = "app", ProcessName = (value.Text ?? "").Trim() };
        return new RuleRef
        {
            Type = "group",
            GroupId = (value.SelectedItem as GroupVm)?.Group.Id ?? "",
        };
    }

    /// <summary>已知应用名：实时会话 + 各分组配置 + 既有规则引用，供应用模式下拉与手输提示。</summary>
    private List<string> AppNames() =>
        App.Monitor.CurrentSnapshot.Select(s => s.ProcessName)
            .Concat(App.Config.Groups.SelectMany(g => g.ProcessNames))
            .Concat(App.Config.Rules.SelectMany(r => new[] { r.Trigger?.ProcessName ?? "", r.Target?.ProcessName ?? "" }))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void OnRuleSelected(object sender, SelectionChangedEventArgs e) => RefreshRuleDetail();

    private void OnRuleAdd(object sender, RoutedEventArgs e)
    {
        var groups = App.Config.Groups;
        var trigger = groups.FirstOrDefault(g => !g.IsDefault) ?? groups.FirstOrDefault();
        var target = groups.FirstOrDefault(g => g.IsDefault) ?? groups.LastOrDefault();
        var rule = new DuckingRuleConfig
        {
            Id = "r" + DateTime.Now.Ticks.ToString("x"),
            Trigger = new RuleRef { Type = "group", GroupId = trigger?.Id ?? "" },
            Target = new RuleRef { Type = "group", GroupId = target?.Id ?? "" },
            DetectionMode = "state",
            TargetVolumePercent = 20,
            Priority = 5,
        };
        App.Config.Rules.Add(rule);
        RefreshRuleTab();
        RuleList.SelectedItem = RuleList.ItemsSource.Cast<RuleVm>().First(v => v.Rule == rule);
        MarkDirty();
    }

    private void OnRuleDelete(object sender, RoutedEventArgs e)
    {
        if (SelectedRule is not { } r)
            return;
        App.Config.Rules.Remove(r);
        RefreshRuleTab();
        MarkDirty();
    }

    private void OnRuleToggle(object sender, RoutedEventArgs e)
    {
        if (_loadingUi || (sender as CheckBox)?.DataContext is not RuleVm vm)
            return;
        var target = ((CheckBox)sender).IsChecked == true;
        if (vm.Rule.Enabled == target)
            return; // 列表项延迟实例化时 Checked 事件会重放一次，值未变则不算修改
        vm.Rule.Enabled = target;
        _loadingUi = true;
        RuleList.Items.Refresh();
        _loadingUi = false;
        MarkDirty();
    }

    /// <summary>XAML 事件入口：音量/优先级/启用变更（选择器事件另有入口）。</summary>
    private void OnRuleFieldChanged(object sender, RoutedEventArgs e) => ApplyRuleEdits();

    private void ApplyRuleEdits()
    {
        if (_loadingUi || SelectedRule is not { } r)
            return;
        r.Trigger = ReadSelector(RuleTriggerType, RuleTriggerValue);
        r.Target = ReadSelector(RuleTargetType, RuleTargetValue);
        r.DetectionMode = RuleDetectionMode.SelectedIndex == 1 ? "sound" : "state";
        if (float.TryParse(RuleVolume.Text, out var vol))
            r.TargetVolumePercent = Math.Clamp(vol, 0f, 100f);
        if (int.TryParse(RulePriority.Text, out var prio))
            r.Priority = prio;
        r.Enabled = RuleEnabled.IsChecked == true;
        _loadingUi = true;
        RuleList.Items.Refresh();
        _loadingUi = false;
        MarkDirty();
    }

    // ---------- 设置 ----------

    private void LoadSettingsTab()
    {
        var s = App.Config.Settings;
        SetUseSmtc.IsChecked = s.UseMediaSessionStatus;
        SetFade.Text = s.FadeDurationMs.ToString();
        SetPoll.Text = s.PollIntervalMs.ToString();
    }

    private bool TryParseSettings()
    {
        // 峰值阈值/低电平保持/静音宽限为"未接入 SMTC 应用"的兜底参数，保留在配置文件中，界面不再暴露
        if (!int.TryParse(SetFade.Text, out var fade))
            return false;
        if (!int.TryParse(SetPoll.Text, out var poll))
            return false;
        var s = App.Config.Settings;
        s.UseMediaSessionStatus = SetUseSmtc.IsChecked == true;
        s.FadeDurationMs = Math.Clamp(fade, 50, 10_000);
        s.PollIntervalMs = Math.Clamp(poll, 10, 5_000);
        return true;
    }

    // ---------- 保存 ----------

    private void MarkDirty()
    {
        if (_loadingUi)
            return;
        _dirty = true;
        SetSaveStatus("● 有未保存的修改");
    }

    private void SetSaveStatus(string? text)
    {
        SaveStatus.Text = text ?? "就绪 · 修改后点击「应用并保存」立即生效";
        SaveStatus.Foreground = _dirty
            ? MakeBrush("#D97706")
            : new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    }

    private void OnApplySave(object sender, RoutedEventArgs e)
    {
        if (!TryParseSettings())
        {
            SetSaveStatus("设置里有无效数字，未保存");
            SaveStatus.Foreground = MakeBrush("#DC2626");
            return;
        }
        ConfigStore.Save(ConfigStore.DefaultPath, App.Config);
        App.Engine.ApplyConfig(ConfigStore.Clone(App.Config)); // 深拷贝后生效，避免引擎与 UI 共享可变集合
        App.Log($"配置已应用并保存（{App.Config.Groups.Count} 组 / {App.Config.Rules.Count} 条规则）");
        _dirty = false;
        SetSaveStatus($"✓ 已应用并保存 {DateTime.Now:HH:mm:ss}");
        SaveStatus.Foreground = MakeBrush("#16A34A");
        RefreshGroupTab();
        RefreshRuleTab();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi)
            return;
        if (e.OriginalSource is TabControl && Tabs.SelectedItem is TabItem { Header: "分组" })
            RefreshGroupDetail();
    }

    // ---------- UI 截图（--screenshot 模式用，自动遍历页签渲染 PNG） ----------

    public async System.Threading.Tasks.Task CaptureAndExitAsync(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var names = new[] { "1-实时状态", "2-音量", "3-分组", "4-规则", "5-设置" };
            for (int i = 0; i < Tabs.Items.Count && i < names.Length; i++)
            {
                Tabs.SelectedIndex = i;
                await System.Threading.Tasks.Task.Delay(350);
                UpdateLayout();
                const double scale = 1.5;
                var rtb = new RenderTargetBitmap(
                    (int)(ActualWidth * scale), (int)(ActualHeight * scale), 96 * scale, 96 * scale,
                    System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(this);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(Path.Combine(dir, $"tab{names[i]}.png"));
                enc.Save(fs);
            }
            App.Log("截图完成");
        }
        catch (Exception ex)
        {
            App.Log("截图失败: " + ex.Message);
        }
        finally
        {
            App.ForceExit = true;
            Application.Current.Shutdown();
        }
    }
}
