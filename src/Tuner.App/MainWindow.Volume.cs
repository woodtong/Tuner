using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tuner.Core.Audio;
using Tuner.Core.Config;
using Tuner.Core.Ducking;
using Brush = System.Windows.Media.Brush;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseWheelEventArgs = System.Windows.Input.MouseWheelEventArgs;

namespace Tuner;

// ---------- 音量（输出混音台） ----------
//
// 交互层架构（单一数据源）：
//   VolumeDisplay(DP) 是推子显示的唯一来源，Slider 单向绑定它。
//   所有移动只经 ApplyFader(目标, 时长)（滚轮 357ms / 静音 500ms）；
//   拖动不经动画：DragDelta 直接写 VolumeDisplay（完全跟手）。
//   刷新线程在 动画中 / 拖动中 / 静音意图 期间绝不写显示值。
//   恢复值寄存器只有两个写入点：拖动松手（非 0）、滚轮逐格。

/// <summary>音量页：MASTER/应用通道、推子交互、设备切换与音量曲线编辑器。</summary>
public partial class MainWindow
{
    private sealed class VolumeChannel : DependencyObject, INotifyPropertyChanged
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

        public string Name { get => _name; set { if (value == _name) return; _name = value; Pc(); } }
        public string PidText { get => _pidText; set { if (value == _pidText) return; _pidText = value; Pc(); } }
        public string GroupName { get => _groupName; set { if (value == _groupName) return; _groupName = value; Pc(); } }
        public Brush GroupBrush { get => _groupBrush; set { if (ReferenceEquals(value, _groupBrush)) return; _groupBrush = value; Pc(); } }
        public double PeakPercent { get => _peakPercent; set { if (value == _peakPercent) return; _peakPercent = value; Pc(); } }

        private BitmapImage? _icon;
        public BitmapImage? Icon { get => _icon; set { if (ReferenceEquals(value, _icon)) return; _icon = value; Pc(); } }

        /// <summary>已解码图标对应的 PNG 源（引用不变则不重复解码）。</summary>
        public byte[]? IconPngSource;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Pc([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private readonly ObservableCollection<VolumeChannel> _volumeRows = new();
    private readonly Dictionary<string, Slider> _faderByInstance = new();
    private readonly Dictionary<string, VolumeChannel> _volumeById = new();
    private string _volumeSignature = "";
    private bool _masterSuppress;

    // ---------- 音量曲线 ----------
    //
    // 界面上的音量百分比（推子位置、恢复值、规则目标）统一是"曲线上的位置"，
    // 只有在真正写会话/设备振幅的那一刻才按全局曲线换算；读回来显示时做逆换算。
    // 切换曲线/编辑控制点只改换算方式，不动任何实际音量。

    private CurveSpec _uiCurve = CurveSpec.Linear;
    private TunerConfig? _uiCurveFor;
    private string _uiCurveId = "";
    private List<CurvePoint>? _uiCurvePoints;

    /// <summary>
    /// UI 侧全局曲线（MASTER 与全部应用）。缓存：换曲线/换控制点列表才重建，
    /// 每次换算（刷新 4Hz × 每会话多次 + 输入事件）不再分配 CurveSpec。
    /// </summary>
    private CurveSpec UiCurve
    {
        get
        {
            var cfg = App.Config;
            var s = cfg.Settings;
            if (!ReferenceEquals(_uiCurveFor, cfg) || _uiCurveId != s.VolumeCurve ||
                !ReferenceEquals(_uiCurvePoints, s.CustomCurve))
            {
                _uiCurve = VolumeCurve.ResolveGlobal(cfg);
                _uiCurveFor = cfg;
                _uiCurveId = s.VolumeCurve;
                _uiCurvePoints = s.CustomCurve;
            }
            return _uiCurve;
        }
    }

    /// <summary>振幅(0..1) → 推子位置(0..100)（按全局曲线逆映射）。</summary>
    private double PosOf(float amplitude) => VolumeCurve.ToPosition(UiCurve, amplitude) * 100.0;

    /// <summary>推子位置(0..100) → 振幅(0..1)（按全局曲线映射）。</summary>
    private float VolOf(double position) => VolumeCurve.ToVolume(UiCurve, position / 100.0);

    /// <summary>图表当前编辑的控制点副本（写回配置时才赋给配置对象）。</summary>
    private List<CurvePoint> _editPoints = new();

    private void RefreshVolumes(List<SoundSession> sessions,
        Dictionary<string, DuckingEngine.SessionState> states)
    {
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
            if (!ReferenceEquals(row.IconPngSource, s.IconPng))
            {
                row.IconPngSource = s.IconPng; // 与状态页同款：快照 PNG 引用稳定，换源才解码
                row.Icon = ProcessIconService.ToBitmap(s.IconPng);
            }
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

            var newDisplay = Math.Round(PosOf(s.Volume));
            if (App.Engine.IsVolumeControlled(row.InstanceId))
            {
                // 引擎接管（闪避中/渐变回程）：推子显示"恢复目标"（用户逻辑音量），
                // 不显示被规则压住的瞬时值——否则每次调整都会被拽回规则值，看起来就是一闪一闪
                newDisplay = Math.Round(PosOf(e?.OriginalVolume ?? s.Volume));
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
        var masterPos = Math.Round(PosOf(App.Monitor.MasterVolume));
        if (!MasterSlider.IsMouseCaptureWithin && Math.Abs(MasterSlider.Value - masterPos) > 0.01)
            MasterSlider.Value = masterPos;
        var masterText = $"{(int)masterPos}%";
        if (MasterVolText.Text != masterText)
            MasterVolText.Text = masterText;
        MasterMeterBig.Value = Math.Round(App.Monitor.DevicePeak * 100);
        var masterMuteText = App.Monitor.MasterMuted ? "取消静音" : "静音";
        if ((string)MasterMuteBtn.Content != masterMuteText)
            MasterMuteBtn.Content = masterMuteText;
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
        App.Monitor.SetMasterVolume(VolOf(Math.Clamp(MasterSlider.Value, 0, 100)));
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
        var anim = new DoubleAnimation(slider.Value, target, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
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
        App.Monitor.SetMasterVolume(VolOf(target));
    }

    private void OnChannelFaderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Slider s && s.DataContext is VolumeChannel ch)
        {
            _faderByInstance[ch.InstanceId] = s; // 动效与拖动的接管都要靠它找到滑块
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
        var sess = App.Monitor.CurrentSnapshot.FirstOrDefault(x => x.InstanceId == ch.InstanceId);
        if (sess is { Mute: true } && ch.FaderTarget > 0.5) // 位置抬离 0 即取消静音（阈值与曲线无关）
        {
            App.Monitor.SetSessionMute(ch.InstanceId, false);
            ch.MuteIntent = false;
        }
        WriteChannelVolume(ch.InstanceId, VolOf(ch.FaderTarget));
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
    private void MirrorFaderValue(VolumeChannel ch, Slider slider)
    {
        ch.FaderTarget = slider.Value;
        ch.VolumeDisplay = slider.Value;
        var sess = App.Monitor.CurrentSnapshot.FirstOrDefault(x => x.InstanceId == ch.InstanceId);
        if (sess is { Mute: true } && slider.Value > 0.5) // 位置抬离 0 即取消静音（阈值与曲线无关）
        {
            App.Monitor.SetSessionMute(ch.InstanceId, false);
            ch.MuteIntent = false;
        }
        WriteChannelVolume(ch.InstanceId, VolOf(slider.Value));
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
            var restorePos = Math.Clamp(ch.RestoreVolume, 1, 100); // 恢复值是曲线上的位置
            ch.FaderTarget = restorePos;
            App.Monitor.SetSessionMute(ch.InstanceId, false);
            WriteChannelVolume(ch.InstanceId, VolOf(restorePos));
            ApplyFader(ch, restorePos, 500);
        }
    }

    // ----- 曲线编辑器（音量页下方图表） -----

    /// <summary>按全局曲线配置加载曲线下拉、图表与提示（曲线切换/配置变化后调用）。</summary>
    private void LoadCurveEditor()
    {
        bool outer = _loadingUi;
        _loadingUi = true;

        var s = App.Config.Settings;
        VolumeCurveCombo.SelectedIndex = s.VolumeCurve switch { "square" => 1, "log" => 2, "custom" => 3, _ => 0 };
        _editPoints = s.CustomCurve.Select(p => new CurvePoint(p.X, p.Y)).ToList();
        CurveChartBox.Points = _editPoints;
        CurveChartBox.CurveId = s.VolumeCurve;
        CurveChartBox.IsEditable = s.VolumeCurve == "custom";
        UpdateVolumeCurveHint();
        _loadingUi = outer;
    }

    /// <summary>曲线下拉变更：写全局配置后重载编辑器（切到自定义时若还没有控制点，以切换前曲线为底稿播种）。</summary>
    private void OnVolumeCurveChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi)
            return;
        var id = VolumeCurveCombo.SelectedIndex switch
        {
            1 => "square",
            2 => "log",
            3 => "custom",
            _ => "linear",
        };
        var s = App.Config.Settings;
        if (id == "custom" && s.CustomCurve.Count < 2)
            s.CustomCurve = SeedPoints(s.VolumeCurve); // 首次切入：图上立即可拖，不至空白
        s.VolumeCurve = id;
        LoadCurveEditor();
        MarkDirty();
    }

    /// <summary>以指定预设曲线采样 5 个控制点（0/25/50/75/100%）。</summary>
    private static List<CurvePoint> SeedPoints(string fromCurve)
    {
        var pts = new List<CurvePoint>();
        for (int i = 0; i <= 4; i++)
        {
            var t = i / 4.0;
            pts.Add(new CurvePoint(t, VolumeCurve.ToVolume(fromCurve, t)));
        }
        return pts;
    }

    /// <summary>图表控制点被编辑（拖动/加点/删点）：把点集写回全局配置。</summary>
    private void OnCurveChartEdited()
    {
        if (_loadingUi)
            return;
        App.Config.Settings.CustomCurve = _editPoints;
        MarkDirty();
    }

    private void UpdateVolumeCurveHint() =>
        VolumeCurveHint.Text = VolumeCurveCombo.SelectedIndex switch
        {
            1 => "平方：实际音量 = 百分比²。小音量段拖动更细腻，整体近似听感",
            2 => "对数（等响度）：每 1% 恒定 0.4dB（全程 40dB），等格等响，小音量段不再一拖就炸",
            3 => "自定义：拖动图中控制点（双击空白加点 / 右键删点，端点仅可上下拖），响度随百分比折线映射",
            _ => "线性：实际音量 = 百分比。最直观，但小音量段每格听感变化明显",
        };
}
