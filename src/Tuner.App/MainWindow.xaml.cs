using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tuner.Core.Config;
using Tuner.Core.Ducking;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using Imaging = System.Windows.Interop.Imaging;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
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
        try
        {
            var icon = TrayIconFactory.Create();
            Icon = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        catch { /* 图标失败不影响功能 */ }

        DeviceText.Text = App.Monitor.DeviceName;
        StatusList.ItemsSource = _statusRows;
        RefreshGroupColors();
        RefreshGroupTab();
        RefreshRuleTab();
        LoadSettingsTab();
        SetSaveStatus(null);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => RefreshStatus();
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
            foreach (var s in sessions)
            {
                var row = new StatusRow { InstanceId = s.InstanceId };
                _statusRows.Add(row);
                _statusById[s.InstanceId] = row;
            }
        }

        int speaking = 0, ducking = 0;
        foreach (var s in sessions)
        {
            var row = _statusById[s.InstanceId];
            var e = states.GetValueOrDefault(s.InstanceId);
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
            row.OriginalText = e is null ? "" : $"原始 {(int)Math.Round(e.OriginalVolume * 100)}%";
        }

        StatTotal.Text = sessions.Count.ToString();
        StatSpeaking.Text = speaking.ToString();
        StatDucking.Text = ducking.ToString();
        StatusEmpty.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        RefreshGroupCombo();
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
        RefreshGroupCombo();
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

    private void RefreshGroupCombo()
    {
        var groups = App.Config.Groups.Select(g => new GroupVm { Group = g }).ToList();
        RuleTrigger.ItemsSource = groups;
        RuleTarget.ItemsSource = groups;
    }

    // ---------- 规则 ----------

    private sealed class RuleVm
    {
        public required DuckingRuleConfig Rule { get; init; }
        public string Sentence =>
            GroupName(Rule.TriggerGroupId) + " 出声 → " + GroupName(Rule.TargetGroupId) +
            $" 渐变到 {Rule.TargetVolumePercent:F0}%" + (Rule.Enabled ? "" : "（已停用）");

        private string GroupName(string id) =>
            App.Config.Groups.FirstOrDefault(g => g.Id == id)?.Name ?? id + "?";
    }

    private DuckingRuleConfig? SelectedRule => (RuleList.SelectedItem as RuleVm)?.Rule;

    private void RefreshRuleTab()
    {
        _loadingUi = true;
        var selected = SelectedRule;
        RuleList.ItemsSource = App.Config.Rules.Select(r => new RuleVm { Rule = r }).ToList();
        RuleList.SelectedItem = RuleList.ItemsSource.Cast<RuleVm>().FirstOrDefault(v => v.Rule == selected)
                                ?? RuleList.ItemsSource.Cast<RuleVm>().FirstOrDefault();
        RefreshGroupCombo();
        _loadingUi = false;
        RefreshRuleDetail();
    }

    private void RefreshRuleDetail()
    {
        bool outer = _loadingUi;
        _loadingUi = true;
        var r = SelectedRule;
        RuleTrigger.SelectedItem = (RuleTrigger.ItemsSource as IEnumerable<GroupVm>)?.FirstOrDefault(v => v.Group.Id == r?.TriggerGroupId);
        RuleTarget.SelectedItem = (RuleTarget.ItemsSource as IEnumerable<GroupVm>)?.FirstOrDefault(v => v.Group.Id == r?.TargetGroupId);
        RuleVolume.Text = r is null ? "" : $"{r.TargetVolumePercent:F0}";
        RulePriority.Text = r?.Priority.ToString() ?? "";
        RuleEnabled.IsChecked = r?.Enabled ?? false;
        _loadingUi = outer; // 恢复外层状态（可能被嵌套调用）
    }

    private void OnRuleSelected(object sender, SelectionChangedEventArgs e) => RefreshRuleDetail();

    private void OnRuleAdd(object sender, RoutedEventArgs e)
    {
        var groups = App.Config.Groups;
        var trigger = groups.FirstOrDefault(g => !g.IsDefault) ?? groups.FirstOrDefault();
        var target = groups.FirstOrDefault(g => g.IsDefault) ?? groups.LastOrDefault();
        var rule = new DuckingRuleConfig
        {
            Id = "r" + DateTime.Now.Ticks.ToString("x"),
            TriggerGroupId = trigger?.Id ?? "",
            TargetGroupId = target?.Id ?? "",
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

    private void OnRuleFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi || SelectedRule is not { } r)
            return;
        if (RuleTrigger.SelectedItem is GroupVm t)
            r.TriggerGroupId = t.Group.Id;
        if (RuleTarget.SelectedItem is GroupVm g)
            r.TargetGroupId = g.Group.Id;
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
        SetActive.Text = $"{s.ActivePeakThreshold * 100:F0}";
        SetInactive.Text = $"{s.InactivePeakThreshold * 100:F0}";
        SetHold.Text = s.InactiveHoldMs.ToString();
        SetGraceOpen.Text = s.StreamOpenSilenceGraceMs.ToString();
        SetGraceClosed.Text = s.SilenceGraceMs.ToString();
        SetFade.Text = s.FadeDurationMs.ToString();
        SetPoll.Text = s.PollIntervalMs.ToString();
    }

    private bool TryParseSettings()
    {
        float active = 0, inactive = 0;
        int hold = 0, fade = 0, poll = 0, graceOpen = 0, graceClosed = 0;
        var ok =
            float.TryParse(SetActive.Text, out active) &&
            float.TryParse(SetInactive.Text, out inactive) &&
            int.TryParse(SetHold.Text, out hold) &&
            int.TryParse(SetGraceOpen.Text, out graceOpen) &&
            int.TryParse(SetGraceClosed.Text, out graceClosed) &&
            int.TryParse(SetFade.Text, out fade) &&
            int.TryParse(SetPoll.Text, out poll);
        if (!ok)
            return false;
        var s = App.Config.Settings;
        s.ActivePeakThreshold = Math.Clamp(active, 0.1f, 100f) / 100f;
        s.InactivePeakThreshold = Math.Clamp(inactive, 0f, s.ActivePeakThreshold * 100f) / 100f;
        s.InactiveHoldMs = Math.Clamp(hold, 0, 10_000);
        s.StreamOpenSilenceGraceMs = Math.Clamp(graceOpen, 0, 60_000);
        s.SilenceGraceMs = Math.Clamp(graceClosed, 0, 60_000);
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
            var names = new[] { "1-实时状态", "2-分组", "3-规则", "4-设置" };
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
