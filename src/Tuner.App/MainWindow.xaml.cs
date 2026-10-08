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
        VolumeList.ItemsSource = _volumeRows;
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
    //
    // 交互层架构（单一数据源）：
    //   VolumeDisplay(DP) 是推子显示的唯一来源，Slider 单向绑定它。
    //   所有移动只经 ApplyFader(目标, 时长)（滚轮 357ms / 静音 500ms）；
    //   拖动不经动画：DragDelta 直接写 VolumeDisplay（完全跟手）。
    //   刷新线程在 动画中 / 拖动中 / 静音意图 期间绝不写显示值。
    //   恢复值寄存器只有两个写入点：拖动松手（非 0）、滚轮逐格。

    private sealed class VolumeChannel : DependencyObject, System.ComponentModel.INotifyPropertyChanged
    {
        public string InstanceId = "";
        public bool IsAnimating;
        public bool IsDragging;
        public DateTime BusyUntil = DateTime.MinValue;
        public double WheelAccum;
        public double FaderTarget = 100;
        public bool FaderTargetInit;
        public double RestoreVolume = 50;
        public bool MuteIntent;

        /// <summary>动效序号：递增后，被替换/被接管的那次动画的 Completed 事件会被丢弃。</summary>
        public int AnimSeq;

        /// <summary>拖动镜像已排队（一帧一次）。</summary>
        public bool MirrorPending;

        public static readonly DependencyProperty VolumeDisplayProperty = DependencyProperty.Register(
            nameof(VolumeDisplay), typeof(double), typeof(VolumeChannel),
            new FrameworkPropertyMetadata(100.0, OnVolumeDisplayChanged));

        public static readonly DependencyProperty MuteTextProperty = DependencyProperty.Register(
            nameof(MuteText), typeof(string), typeof(VolumeChannel),
            new FrameworkPropertyMetadata("静音"));

        public double VolumeDisplay
        {
            get => (double)GetValue(VolumeDisplayProperty);
            set => SetValue(VolumeDisplayProperty, value);
        }

        public string MuteText
        {
            get => (string)GetValue(MuteTextProperty);
            set => SetValue(MuteTextProperty, value);
        }

        private static void OnVolumeDisplayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ch = (VolumeChannel)d;
            // 显示规则唯一判断：显示音量为 0 → 取消静音
            ch.MuteText = (double)e.NewValue <= 0 ? "取消静音" : "静音";
        }

        private string _name = "";
        private string _pidText = "";
        private string _groupName = "";
        private Brush _groupBrush = IdleBrush;
        private double _peakPercent;

        public string Name { get => _name; set { _name = value; Pc(); } }
        public string PidText { get => _pidText; set { _pidText = value; Pc(); } }
        public string GroupName { get => _groupName; set { _groupName = value; Pc(); } }
        public Brush GroupBrush { get => _groupBrush; set { _groupBrush = value; Pc(); } }
        public double PeakPercent { get => _peakPercent; set { _peakPercent = value; Pc(); } }

        private BitmapImage? _icon;
        public BitmapImage? Icon { get => _icon; set { _icon = value; Pc(); } }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void Pc([System.Runtime.CompilerServices.CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
    }

    private readonly ObservableCollection<VolumeChannel> _volumeRows = new();
    private readonly Dictionary<string, Slider> _faderByInstance = new();
    private readonly Dictionary<string, VolumeChannel> _volumeById = new();
    private string _volumeSignature = "";
    private bool _masterSuppress;

    /// <summary>--probe 模式下打开：记录"刷新改写显示"这类关键回写（正常运行为关闭）。</summary>
    internal static bool FaderTrace;

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
            ReconcileVolumeRows(sessions);
        }

        foreach (var s in sessions)
        {
            var row = _volumeById[s.InstanceId];
            var e = states.GetValueOrDefault(s.InstanceId);
            row.Name = s.ProcessName;
            row.PidText = s.Pid.ToString();
            var img = ProcessIconService.GetImageByPid(s.Pid);
            if (!ReferenceEquals(row.Icon, img))
                row.Icon = img; // 引用稳定，避免每拍重渲染图标（拖动时表现为闪烁）
            row.GroupName = e?.GroupName ?? "—";
            row.GroupBrush = GroupBrush(e?.GroupId ?? "");
            var peak = Math.Round(s.Peak * 100);
            if (row.PeakPercent != peak)
                row.PeakPercent = peak; // 无变化不触发 LED 重绘

            if (row.MuteIntent)
            {
                // 意图静音：实际音量钉零 + 显示完全冻结
                // （驱动在静音时回读的可能是静音前旧值，绝不能上屏）
                if (s.Volume > 0.005f)
                    App.Monitor.SetSessionVolume(row.InstanceId, 0f);
                continue;
            }
            if (row.IsAnimating && DateTime.UtcNow > row.BusyUntil)
                StopFaderAnimation(row); // 超时安全阀：动画完成事件丢失时不永久冻结显示
            if (row.IsAnimating || row.IsDragging)
                continue; // 动效/拖动期间显示由交互驱动，刷新不插手
            if (App.Monitor.HasPendingVolumeWrite(row.InstanceId))
                continue; // 音量写入还在队列里：快照必然滞后，不能用旧值把显示拽回去

            var newDisplay = Math.Round(s.Volume * 100);
            if (App.Engine.IsVolumeControlled(row.InstanceId))
            {
                // 引擎接管（闪避中/渐变回程）：推子显示"恢复目标"（用户逻辑音量），
                // 不显示被规则压住的瞬时值——否则每次调整都会被拽回规则值，看起来就是一闪一闪
                newDisplay = Math.Round((e?.OriginalVolume ?? s.Volume) * 100);
            }
            if (FaderTrace && Math.Abs(row.VolumeDisplay - newDisplay) > 0.5)
                App.Log($"[probe] 刷新改写显示 {row.Name} {row.VolumeDisplay:F0} -> {newDisplay:F0}（实际 {s.Volume * 100:F1} 目标 {row.FaderTarget:F0}）");
            row.VolumeDisplay = newDisplay;
            if (!row.FaderTargetInit)
            {
                row.FaderTarget = row.VolumeDisplay; // 首次以实际音量为逻辑基准
                row.FaderTargetInit = true;
            }
        }

        _masterSuppress = true;
        if (!MasterSlider.IsMouseCaptureWithin)
            MasterSlider.Value = Math.Round(App.Monitor.MasterVolume * 100);
        MasterVolText.Text = $"{(int)Math.Round(App.Monitor.MasterVolume * 100)}%";
        MasterMeterBig.Value = Math.Round(App.Monitor.DevicePeak * 100);
        MasterMuteBtn.Content = App.Monitor.MasterMuted ? "取消静音" : "静音";
        _masterSuppress = false;
    }

    /// <summary>
    /// 会话集合变化时按目标顺序差量对齐通道行：只插入/移动/移除，不整表重建。
    /// （整表重建会让所有推子瞬间重建、并打断正在进行的拖动 = 肉眼可见的闪一下）
    /// </summary>
    private void ReconcileVolumeRows(List<SoundSession> sessions)
    {
        var alive = sessions.Select(s => s.InstanceId).ToHashSet();
        for (int i = _volumeRows.Count - 1; i >= 0; i--)
        {
            var id = _volumeRows[i].InstanceId;
            if (alive.Contains(id))
                continue;
            _volumeRows.RemoveAt(i);
            _volumeById.Remove(id);
            _faderByInstance.Remove(id);
            _trackByInstance.Remove(id);
        }
        for (int i = 0; i < sessions.Count; i++)
        {
            var id = sessions[i].InstanceId;
            if (_volumeById.TryGetValue(id, out var row))
            {
                int cur = _volumeRows.IndexOf(row);
                if (cur != i)
                    _volumeRows.Move(cur, i);
            }
            else
            {
                var created = new VolumeChannel { InstanceId = id };
                _volumeRows.Insert(Math.Min(i, _volumeRows.Count), created);
                _volumeById[id] = created;
            }
        }
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

    // ---------- 推子交互 ----------

    /// <summary>唯一动效入口：从当前显示位置平滑滑向目标。期间该通道显示由动画驱动。</summary>
    private void ApplyFader(VolumeChannel ch, double target, int durationMs)
    {
        ch.FaderTarget = target;
        if (!_faderByInstance.TryGetValue(ch.InstanceId, out var slider))
        {
            ch.VolumeDisplay = target; // 无滑块可动效时至少保证显示正确
            return;
        }
        ch.IsAnimating = true;
        int seq = ++ch.AnimSeq;
        ch.BusyUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(durationMs + 400); // 超时安全阀
        var anim = new System.Windows.Media.Animation.DoubleAnimation(slider.Value, target, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        anim.Completed += (_, _) =>
        {
            // 被新动效替换、或被拖动/静音接管后，撤下的动画仍会触发 Completed：
            // 此时它揣着的是旧目标，写回去就是一次肉眼可见的回弹，必须丢弃
            if (seq != ch.AnimSeq)
                return;
            ch.IsAnimating = false;
            slider.BeginAnimation(Slider.ValueProperty, null); // 撤销 HoldEnd，把值的决定权交回绑定
            ch.VolumeDisplay = target; // 到位定格
        };
        slider.BeginAnimation(Slider.ValueProperty, anim);
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

    /// <summary>调音台区滚轮 → 横向滚动（多应用时显示全部通道）。</summary>
    private void OnChannelsMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ChannelsScroller.ScrollableWidth <= 0)
            return;
        ChannelsScroller.ScrollToHorizontalOffset(ChannelsScroller.HorizontalOffset - e.Delta);
        e.Handled = true;
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

    private readonly Dictionary<string, System.Windows.Controls.Primitives.Track> _trackByInstance = new();

    private void OnChannelFaderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Slider s && s.DataContext is VolumeChannel ch)
        {
            _faderByInstance[ch.InstanceId] = s; // 动效与拖动的接管都要靠它找到滑块
            _trackByInstance[ch.InstanceId] = FindDescendant<System.Windows.Controls.Primitives.Track>(s);
            var thumb = FindDescendant<System.Windows.Controls.Primitives.Thumb>(s);
            if (thumb is not null)
            {
                thumb.DragStarted += (_, _) => OnChannelFaderDragStarted(ch, s);
                thumb.DragDelta += (_, e) => OnChannelFaderDragDelta(ch, s, e);
                thumb.DragCompleted += (_, _) => OnChannelFaderDragCompleted(ch, s);
            }
        }
    }

    private void OnChannelFaderMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Slider s && !_faderBusy(s))
            SetGrooveVisible(s, true);
    }

    private void OnChannelFaderMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Slider s && !_faderBusy(s))
            SetGrooveVisible(s, false);
    }

    private bool _faderBusy(Slider s) =>
        s.DataContext is VolumeChannel ch && (ch.IsDragging || ch.IsAnimating);

    private static void SetGrooveVisible(Slider s, bool on)
    {
        if (s.Parent is System.Windows.Controls.Panel panel)
            foreach (var child in panel.Children)
                if (child is System.Windows.Controls.Border b && b.Name == "FaderGroove")
                    b.Opacity = on ? 1 : 0;
    }

    /// <summary>
    /// 会话音量写入的唯一入口：引擎正接管（闪避中/渐变回程）时只更新其恢复锚点，
    /// 绝不直写实际音量——否则与引擎的每拍渐变互相覆盖，听感就是音量抽动。
    /// </summary>
    private static void WriteChannelVolume(string instanceId, float volume)
    {
        if (!App.Engine.IsVolumeControlled(instanceId))
            App.Monitor.SetSessionVolume(instanceId, volume);
        App.Engine.SetUserVolume(instanceId, volume);
    }

    /// <summary>滚轮：每格（delta 120）= 2%，逻辑目标值累加（快速滚动不丢格），每次调节都记录恢复值。</summary>
    private void OnChannelFaderMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Slider s || s.DataContext is not VolumeChannel ch)
            return;
        e.Handled = true;
        if (ch.IsDragging)
            return;
        ch.WheelAccum += e.Delta;
        int steps = 0;
        while (ch.WheelAccum >= 120) { ch.WheelAccum -= 120; steps++; }
        while (ch.WheelAccum <= -120) { ch.WheelAccum += 120; steps--; }
        if (steps == 0)
            return;
        ch.FaderTarget = Math.Clamp(ch.FaderTarget + steps * 2, 0, 100);
        var v = (float)(ch.FaderTarget / 100.0);
        var sess = App.Monitor.CurrentSnapshot.FirstOrDefault(x => x.InstanceId == ch.InstanceId);
        if (sess is { Mute: true } && v > 0.005f)
        {
            App.Monitor.SetSessionMute(ch.InstanceId, false);
            ch.MuteIntent = false;
        }
        WriteChannelVolume(ch.InstanceId, v);
        ch.RestoreVolume = ch.FaderTarget; // 每一次滚轮调节都记录
        ApplyFader(ch, ch.FaderTarget, 357); // 140% 速度动效
    }

    /// <summary>
    /// 用户操作接管在跑的动效：标记结束并作废该次动画的 Completed（避免它把旧目标写回显示）。
    /// </summary>
    private static void StopFaderAnimation(VolumeChannel ch)
    {
        ch.IsAnimating = false;
        ch.AnimSeq++;
    }

    /// <summary>
    /// 按下即接管：终止在跑的动效（并作废它的 Completed），此后滑块值由 Track 原生拖拽驱动。
    /// 这里绝不能再按光标绝对定位回写滑块值——此前正是"绝对写 + 原生增量写"两个写者互抢，
    /// 才出现拖动中的回弹/抖动；用户按下的位置即抓取点，光标与推子从此 1:1。
    /// </summary>
    private void OnChannelFaderDragStarted(VolumeChannel ch, Slider slider)
    {
        StopFaderAnimation(ch);
        ch.VolumeDisplay = slider.Value; // 以当前显示位置为基准，撤动画瞬间不回跳
        slider.BeginAnimation(Slider.ValueProperty, null); // 终止动画
        ch.IsDragging = true;
        SetGrooveVisible(slider, true); // 拖动期间凹槽常亮，不随光标进出闪烁
    }

    /// <summary>
    /// 拖动：只镜像原生拖拽产生的滑块值（每帧一次，取该帧最终值），同步显示与会话音量。
    /// </summary>
    private void OnChannelFaderDragDelta(VolumeChannel ch, Slider slider, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (!ch.IsDragging || ch.MirrorPending)
            return;
        ch.MirrorPending = true; // 一帧内只同步一次；本处理器比原生拖拽先跑，需等它写完值
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            ch.MirrorPending = false;
            if (ch.IsDragging)
                MirrorFaderValue(ch, slider);
        });
    }

    private void OnChannelFaderDragCompleted(VolumeChannel ch, Slider slider)
    {
        if (!ch.IsDragging)
            return;
        MirrorFaderValue(ch, slider); // 定格到滑块真实值
        ch.IsDragging = false;
        if (slider.Value > 0.5)
            ch.RestoreVolume = slider.Value; // 松手才记录；为 0 不记录
    }

    /// <summary>把滑块当前值同步到显示与会话音量（拖动期间滑块由 Track 原生拖拽驱动）。</summary>
    private static void MirrorFaderValue(VolumeChannel ch, Slider slider)
    {
        ch.FaderTarget = slider.Value;
        ch.VolumeDisplay = slider.Value;
        var v = (float)(slider.Value / 100.0);
        var sess = App.Monitor.CurrentSnapshot.FirstOrDefault(x => x.InstanceId == ch.InstanceId);
        if (sess is { Mute: true } && v > 0.005f)
        {
            App.Monitor.SetSessionMute(ch.InstanceId, false);
            ch.MuteIntent = false;
        }
        WriteChannelVolume(ch.InstanceId, v);
    }

    /// <summary>静音切换：意图严格交替（连点即交替）；用户操作立即接管在跑的动效。</summary>
    private void OnChannelMuteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VolumeChannel ch)
            return;
        if (ch.IsDragging)
            return;
        StopFaderAnimation(ch); // 用户操作优先：立即接管动效
        ch.MuteIntent = !ch.MuteIntent;
        if (ch.MuteIntent)
        {
            ch.FaderTarget = 0;
            App.Monitor.SetSessionMute(ch.InstanceId, true);
            WriteChannelVolume(ch.InstanceId, 0f);
            ApplyFader(ch, 0, 500);
        }
        else
        {
            var restore = (float)Math.Clamp(ch.RestoreVolume, 1, 100) / 100f;
            ch.FaderTarget = Math.Round(restore * 100);
            App.Monitor.SetSessionMute(ch.InstanceId, false);
            WriteChannelVolume(ch.InstanceId, restore);
            ApplyFader(ch, Math.Round(restore * 100), 500);
        }
    }

    // ---------- 推子交互探针（--probe 开发模式：进程内触发滚轮/拖动事件，观察"显示值 vs 刷新回写"的博弈） ----------

    /// <summary>探针：切到音量页 → 合成滚轮 → 合成拖动 → 施加临时闪避规则后再合成滚轮；全程落日志后退出。</summary>
    public async System.Threading.Tasks.Task RunFaderProbeAsync()
    {
        void L(string m) => App.Log("[probe] " + m);
        FaderTrace = true;
        var originals = new Dictionary<string, float>();
        List<Slider> sliders = new();
        try
        {
            Tabs.SelectedIndex = 1;
            await System.Threading.Tasks.Task.Delay(2500);
            UpdateLayout();

            CollectSliders(VolumeList, sliders);
            L($"音量页滑块数={sliders.Count}（MASTER 在 VolumeList 之外）");
            foreach (var s in sliders)
                if (s.DataContext is VolumeChannel c && !originals.ContainsKey(c.InstanceId))
                {
                    var sess = App.Monitor.CurrentSnapshot.FirstOrDefault(x => x.InstanceId == c.InstanceId);
                    originals[c.InstanceId] = sess?.Volume ?? 0.5f;
                }
            DumpFaders(sliders, "初始");
            if (sliders.Count == 0)
            {
                L("无应用通道滑块");
                return;
            }
            var target = sliders[0];
            L("目标通道=" + Describe(target));

            L("阶段A：合成滚轮（无闪避）。首格后每 40ms 采样 12 次，观察显示值是否被刷新拽回");
            RaiseWheel(target, 120);
            for (int i = 1; i <= 12; i++)
            {
                await System.Threading.Tasks.Task.Delay(40);
                DumpFaders(sliders, $"A1.{i}");
            }
            for (int i = 2; i <= 4; i++)
            {
                RaiseWheel(target, 120);
                await System.Threading.Tasks.Task.Delay(300);
                DumpFaders(sliders, $"A{i}");
            }
            await System.Threading.Tasks.Task.Delay(1600);
            DumpFaders(sliders, "A结束");

            L("阶段B：合成拖动（DragStarted + 12×DragDelta + DragCompleted）");
            RaiseDragStart(target);
            for (int i = 0; i < 12; i++)
            {
                RaiseDragDelta(target, -3);
                await System.Threading.Tasks.Task.Delay(40);
            }
            RaiseDragDone(target);
            await System.Threading.Tasks.Task.Delay(1800);
            DumpFaders(sliders, "B结束");

            L("阶段C：施加临时闪避规则");
            var ducked = ApplyProbeDuckConfigFor(target);
            L("闪避配置=" + (ducked ?? "(无可用触发组)"));
            sliders = new List<Slider>();
            CollectSliders(VolumeList, sliders);
            await System.Threading.Tasks.Task.Delay(3000);
            DumpFaders(sliders, "C闪避生效");
            target = sliders.Count > 0 ? sliders[0] : target;
            L("阶段C：合成滚轮 x4（被闪避期间，向下）");
            for (int i = 1; i <= 4; i++)
            {
                RaiseWheel(target, -120);
                await System.Threading.Tasks.Task.Delay(300);
                DumpFaders(sliders, $"C{i}");
            }
            await System.Threading.Tasks.Task.Delay(1400);
            DumpFaders(sliders, "C结束");

            L("阶段D：被闪避期间合成拖动");
            RaiseDragStart(target);
            for (int i = 0; i < 10; i++)
            {
                RaiseDragDelta(target, -3);
                await System.Threading.Tasks.Task.Delay(40);
            }
            RaiseDragDone(target);
            await System.Threading.Tasks.Task.Delay(1800);
            DumpFaders(sliders, "D结束");

            // 恢复配置后测真实拖动（会话音量回到用户值，便于观察）
            App.Engine.ApplyConfig(ConfigStore.Clone(App.Config));
            await System.Threading.Tasks.Task.Delay(600);
            sliders = new List<Slider>();
            CollectSliders(VolumeList, sliders);
            target = sliders.Count > 0 ? sliders[0] : target;

            L("阶段F：滚轮后 120ms 内立刻合成拖动（保护窗口内）");
            RaiseWheel(target, 120);
            await System.Threading.Tasks.Task.Delay(120);
            RaiseDragStart(target);
            for (int i = 1; i <= 10; i++)
            {
                RaiseDragDelta(target, -4);
                await System.Threading.Tasks.Task.Delay(40);
                DumpFaders(sliders, $"F{i}");
            }
            RaiseDragDone(target);
            await System.Threading.Tasks.Task.Delay(1400);
            DumpFaders(sliders, "F结束");

            L("阶段G：滚轮后等 1200ms 再合成拖动（保护窗口外）");
            RaiseWheel(target, 120);
            await System.Threading.Tasks.Task.Delay(1200);
            RaiseDragStart(target);
            for (int i = 1; i <= 10; i++)
            {
                RaiseDragDelta(target, -4);
                await System.Threading.Tasks.Task.Delay(40);
                DumpFaders(sliders, $"G{i}");
            }
            RaiseDragDone(target);
            await System.Threading.Tasks.Task.Delay(1400);
            DumpFaders(sliders, "G结束");
        }
        catch (Exception ex)
        {
            L("异常: " + ex);
        }
        finally
        {
            try
            {
                App.Engine.ApplyConfig(ConfigStore.Clone(App.Config)); // 还原真实配置
                foreach (var kv in originals)
                {
                    WriteChannelVolume(kv.Key, kv.Value);
                }
                L("已还原配置与音量");
            }
            catch (Exception ex) { L("还原失败: " + ex.Message); }
            await System.Threading.Tasks.Task.Delay(600);
            App.ForceExit = true;
            Application.Current.Shutdown();
        }
    }

    private static void CollectSliders(DependencyObject root, List<Slider> found)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Slider s)
                found.Add(s);
            CollectSliders(child, found);
        }
    }

    private static string Describe(Slider s)
    {
        var ch = s.DataContext as VolumeChannel;
        return ch is null ? "?" : $"{ch.Name}（{ch.InstanceId}）";
    }

    private void DumpFaders(List<Slider> sliders, string tag)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in sliders)
        {
            if (s.DataContext is not VolumeChannel ch)
                continue;
            var sess = App.Monitor.CurrentSnapshot.FirstOrDefault(x => x.InstanceId == ch.InstanceId);
            bool bind = System.Windows.Data.BindingOperations.GetBindingExpression(s, Slider.ValueProperty) is not null;
            sb.Append($" | {ch.Name}: 显示={ch.VolumeDisplay:F0} 滑块={s.Value:F0} 实际={(sess is null ? -1 : sess.Volume * 100):F0} 目标={ch.FaderTarget:F0} drag={ch.IsDragging} anim={ch.IsAnimating} bind={bind}");
        }
        App.Log($"[probe] [{tag}]" + sb);
    }

    private static void RaiseWheel(Slider s, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
        };
        s.RaiseEvent(args);
    }

    private static void RaiseDragStart(Slider s)
    {
        var thumb = FindDescendant<System.Windows.Controls.Primitives.Thumb>(s);
        thumb?.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0)
        {
            RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
        });
    }

    private static void RaiseDragDelta(Slider s, double dy)
    {
        var thumb = FindDescendant<System.Windows.Controls.Primitives.Thumb>(s);
        thumb?.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0, dy)
        {
            RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent,
        });
    }

    private static void RaiseDragDone(Slider s)
    {
        var thumb = FindDescendant<System.Windows.Controls.Primitives.Thumb>(s);
        thumb?.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, 0, false)
        {
            RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
        });
    }

    /// <summary>临时规则：任一有会话的组（探针把声音判定阈值压到 0，"必然出声"）→ 目标通道所在组 0%。</summary>
    private string? ApplyProbeDuckConfigFor(Slider slider)
    {
        if (slider.DataContext is not VolumeChannel ch)
            return null;
        var cfg = ConfigStore.Clone(App.Config);
        cfg.Settings.UseMediaSessionStatus = false;
        cfg.Settings.FadeOutDurationMs = 300;
        cfg.Settings.FadeInDurationMs = 300;
        cfg.Settings.ActivePeakThreshold = 0f;   // 探针专用：让触发组恒判为出声
        cfg.Settings.InactivePeakThreshold = 0f;
        cfg.Settings.InactiveHoldMs = 100;
        cfg.Settings.SilenceGraceMs = 0;
        cfg.Settings.StreamOpenSilenceGraceMs = 0;

        // 注意：探针模式下引擎持有的是隔离配置，这里按用户配置自行解析分组
        var targetGroup = ResolveProbeGroup(cfg, ch.Name);
        string? triggerGroup = null;
        foreach (var s in App.Monitor.CurrentSnapshot)
        {
            var gid = ResolveProbeGroup(cfg, s.ProcessName);
            if (gid != targetGroup)
            {
                triggerGroup = gid;
                break;
            }
        }
        cfg.Rules.Clear();
        if (triggerGroup is null)
        {
            App.Engine.ApplyConfig(cfg); // 无可用触发组：清空规则即可
            return null;
        }
        cfg.Rules.Add(new DuckingRuleConfig
        {
            Id = "probe-duck",
            Enabled = true,
            Trigger = new RuleRef { Type = "group", GroupId = triggerGroup },
            Target = new RuleRef { Type = "group", GroupId = targetGroup },
            DetectionMode = "sound",
            TargetVolumePercent = 0,
            Priority = 20,
        });
        App.Engine.ApplyConfig(cfg);
        return $"触发组={triggerGroup} 目标组={targetGroup}";
    }

    /// <summary>与引擎一致的进程名→分组解析（未匹配落入默认组）。</summary>
    private static string ResolveProbeGroup(TunerConfig cfg, string processName)
    {
        var name = ConfigStore.NormalizeProcessName(processName);
        AppGroupConfig? fallback = null;
        foreach (var g in cfg.Groups)
        {
            if (g.IsDefault)
                fallback ??= g;
            foreach (var p in g.ProcessNames)
                if (ConfigStore.NormalizeProcessName(p) == name)
                    return g.Id;
        }
        return fallback?.Id ?? "default";
    }

    // ---------- 分组 ----------
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
        SetFadeOut.Text = s.FadeOutDurationMs.ToString();
        SetFadeIn.Text = s.FadeInDurationMs.ToString();
        SetPoll.Text = s.PollIntervalMs.ToString();
    }

    private bool TryParseSettings()
    {
        // 峰值阈值/低电平保持/静音宽限为"未接入 SMTC 应用"的兜底参数，保留在配置文件中，界面不再暴露
        if (!int.TryParse(SetFadeOut.Text, out var fadeOut))
            return false;
        if (!int.TryParse(SetFadeIn.Text, out var fadeIn))
            return false;
        if (!int.TryParse(SetPoll.Text, out var poll))
            return false;
        var s = App.Config.Settings;
        s.UseMediaSessionStatus = SetUseSmtc.IsChecked == true;
        s.FadeOutDurationMs = Math.Clamp(fadeOut, 50, 10_000);
        s.FadeInDurationMs = Math.Clamp(fadeIn, 50, 10_000);
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
