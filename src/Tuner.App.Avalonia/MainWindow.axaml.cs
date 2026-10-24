using System.ComponentModel;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Tuner.Core.Audio;
using Tuner.Core.Config;

namespace Tuner.Cross;

public partial class MainWindow : Window
{
    private bool _loadingUi;
    private bool _dirty;
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly DispatcherTimer _refreshTimer;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = "v" + (typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?");
        DeviceText.Text = App.Monitor.DeviceName;

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _autoSaveTimer.Tick += (_, _) => AutoSaveNow();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += (_, _) => RefreshVisible();
        _refreshTimer.Start();

        RefreshGroups();
        LoadSettings();
        SetSaveStatus(null);

        Closing += (_, e) =>
        {
            if (!App.ForceExit)
            {
                e.Cancel = true;
                Hide(); // 关窗驻留托盘（与 WPF 版一致）
            }
        };
    }

    // ================= 状态页 =================

    public sealed class StatusRow : INotifyPropertyChanged
    {
        public string Process { get; set; } = "";
        public string GroupName { get; set; } = "";
        public IBrush SpeakingBrush { get; set; } = Brushes.Transparent;
        public string StateText { get; set; } = "";
        public string DuckText { get; set; } = "";
        public bool IsBadged { get; set; }
        public string VolumeText { get; set; } = "";
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly List<StatusRow> _statusRows = new();
    private static readonly IBrush SpeakingGreen = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush SpeakingIdle = new SolidColorBrush(Color.Parse("#CBD5E1"));

    private void RefreshStatus()
    {
        var states = App.Engine.CurrentStates.ToDictionary(s => s.InstanceId);
        var sessions = App.Monitor.CurrentSnapshot;
        int speaking = 0, ducking = 0;
        _statusRows.Clear();
        foreach (var s in sessions)
        {
            var e = states.GetValueOrDefault(s.InstanceId);
            bool speak = e?.Speaking == true;
            if (speak) speaking++;
            bool duck = e is { Ducked: true };
            if (duck) ducking++;
            _statusRows.Add(new StatusRow
            {
                Process = s.ProcessName,
                GroupName = e?.GroupName ?? "—",
                SpeakingBrush = speak ? SpeakingGreen : SpeakingIdle,
                StateText = e is { UserTookOver: true } ? "·" : (duck ? Lang.Get("S.Stat.Ducking") : ""),
                DuckText = duck ? Lang.Get("S.Status.DuckBadge", (int)Math.Round(ToPos(e?.TargetVolume ?? 0) * 100))
                          : Lang.Get("S.Status.TakeoverBadge"),
                IsBadged = duck || e is { UserTookOver: true },
                VolumeText = $"{(int)Math.Round(ToPos(s.Volume) * 100)}%",
            });
        }
        StatusList.ItemsSource = null;
        StatusList.ItemsSource = _statusRows;
        StatTotal.Text = sessions.Count.ToString();
        StatSpeaking.Text = speaking.ToString();
        StatDucking.Text = ducking.ToString();
        StatusEmpty.IsVisible = sessions.Count == 0;
    }

    // ================= 音量页（调音台） =================

    public sealed class VolumeRow : INotifyPropertyChanged
    {
        public string InstanceId { get; set; } = "";
        public string Name { get; set; } = "";
        public double VolumeDisplay { get; set; } = 100;
        public string VolumeText { get; set; } = "";
        public string MuteText { get; set; } = "";
        public bool SuppressWrite { get; set; } // 程序回写显示时置位，不反写会话音量
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Pc(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private readonly List<VolumeRow> _volumeRows = new();
    private bool _masterSuppress;

    private void RefreshVolume()
    {
        var states = App.Engine.CurrentStates.ToDictionary(s => s.InstanceId);
        var sessions = App.Monitor.CurrentSnapshot;

        // 差量对齐行（保持顺序与会话一致）
        for (int i = _volumeRows.Count - 1; i >= 0; i--)
            if (sessions.All(s => s.InstanceId != _volumeRows[i].InstanceId))
                _volumeRows.RemoveAt(i);
        foreach (var s in sessions)
        {
            var row = _volumeRows.FirstOrDefault(r => r.InstanceId == s.InstanceId);
            if (row is null)
            {
                row = new VolumeRow { InstanceId = s.InstanceId, Name = s.ProcessName };
                _volumeRows.Add(row);
            }
            row.Name = s.ProcessName;
            row.MuteText = s.Mute ? Lang.Get("S.Mixer.Unmute") : Lang.Get("S.Mixer.Mute");
            // 引擎接管时显示恢复锚点（用户逻辑音量），否则显示实际音量
            float actual = s.Volume;
            if (states.TryGetValue(s.InstanceId, out var st) && App.Engine.IsVolumeControlled(s.InstanceId))
                actual = st.OriginalVolume;
            double pos = Math.Round(ToPos(actual) * 100);
            if (Math.Abs(row.VolumeDisplay - pos) > 0.5 && !App.Monitor.HasPendingVolumeWrite(s.InstanceId))
            {
                row.SuppressWrite = true;
                row.VolumeDisplay = pos;
            }
            row.VolumeText = $"{(int)pos}%";
        }
        VolumeList.ItemsSource = null;
        VolumeList.ItemsSource = _volumeRows;

        _masterSuppress = true;
        var mpos = Math.Round(ToPos(App.Monitor.MasterVolume) * 100);
        if (Math.Abs(MasterSlider.Value - mpos) > 0.01)
            MasterSlider.Value = mpos;
        MasterVolText.Text = $"{(int)mpos}%";
        MasterMuteBtn.Content = App.Monitor.MasterMuted ? Lang.Get("S.Mixer.Unmute") : Lang.Get("S.Mixer.Mute");
        _masterSuppress = false;

        var dev = App.Monitor.DeviceName;
        if (DeviceText.Text != dev)
            DeviceText.Text = dev;
    }

    private void OnMasterVolumeChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_masterSuppress || _loadingUi)
            return;
        App.Monitor.SetMasterVolume((float)(e.NewValue / 100.0));
        MasterVolText.Text = $"{(int)Math.Round(e.NewValue)}%";
    }

    private void OnMasterMuteClick(object? sender, RoutedEventArgs e) =>
        App.Monitor.SetMasterMute(!App.Monitor.MasterMuted);

    private void OnChannelFaderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (sender is not Slider s || s.Tag is not string id)
            return;
        var row = _volumeRows.FirstOrDefault(r => r.InstanceId == id);
        if (row is null || row.SuppressWrite)
        {
            if (row is not null) row.SuppressWrite = false;
            return;
        }
        float vol = (float)(e.NewValue / 100.0);
        float amp = FromPos(vol);
        App.Engine.SetUserVolume(id, amp);
        if (!App.Engine.IsVolumeControlled(id))
            App.Monitor.SetSessionVolume(id, amp);
        row.VolumeText = $"{(int)Math.Round(e.NewValue)}%";
    }

    private void OnChannelMuteClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not string id)
            return;
        var s = App.Monitor.CurrentSnapshot.FirstOrDefault(x => x.InstanceId == id);
        App.Monitor.SetSessionMute(id, !(s?.Mute ?? false));
    }

    // ---------- 音量曲线 ----------

    private void LoadCurveCombo()
    {
        int idx = App.Config.Settings.VolumeCurve switch
        {
            "square" => 1, "log" => 2, _ => 0,
        };
        VolumeCurveCombo.SelectedIndex = idx;
    }

    private void OnVolumeCurveChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi)
            return;
        App.Config.Settings.VolumeCurve = VolumeCurveCombo.SelectedIndex switch
        {
            1 => "square", 2 => "log", _ => "linear",
        };
        UpdateCurveHint();
        MarkDirty();
    }

    private void UpdateCurveHint() => VolumeCurveHint.Text = VolumeCurveCombo.SelectedIndex switch
    {
        1 => Lang.Get("S.Curve.Hint.Square"),
        2 => Lang.Get("S.Curve.Hint.Log"),
        _ => Lang.Get("S.Curve.Hint.Linear"),
    };

    // 曲线上位置 ↔ 实际振幅（与 Core 的 VolumeCurve 对应的简化端点换算）
    private static float ToPos(float amp) => amp; // linear 直通；square/log 由设置页全局曲线在 Core 内部生效
    private static float FromPos(float pos) => pos;

    // ================= 分组页 =================

    public sealed class GroupVm
    {
        public AppGroupConfig Group { get; init; } = null!;
        public string CountText => Group.IsDefault
            ? Lang.Get("S.Group.Count", Group.ProcessNames.Count) + " · " + Lang.Get("S.Group.DefaultCount")
            : Lang.Get("S.Group.Count", Group.ProcessNames.Count);
    }

    private AppGroupConfig? SelectedGroup => (GroupList.SelectedItem as GroupVm)?.Group;

    private void RefreshGroups()
    {
        _loadingUi = true;
        var sel = SelectedGroup;
        GroupList.ItemsSource = App.Config.Groups.Select(g => new GroupVm { Group = g }).ToList();
        if (sel is not null)
            GroupList.SelectedItem = GroupList.Items.OfType<GroupVm>().FirstOrDefault(v => v.Group == sel);
        _loadingUi = false;
        RefreshGroupDetail();
    }

    private void RefreshGroupDetail()
    {
        var g = SelectedGroup;
        _loadingUi = true;
        GroupNameBox.Text = g?.Name ?? "";
        DefaultCheck.IsChecked = g?.IsDefault == true;
        GroupProcesses.ItemsSource = g?.ProcessNames.ToList() ?? new List<string>();
        GroupProcEmpty.IsVisible = g is null || g.ProcessNames.Count == 0;
        RefreshRunningProcs();
        _loadingUi = false;
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

    private void OnGroupSelected(object? sender, SelectionChangedEventArgs e) => RefreshGroupDetail();

    private void OnGroupAdd(object? sender, RoutedEventArgs e)
    {
        var g = new AppGroupConfig { Id = "g" + DateTime.Now.Ticks.ToString("x"), Name = Lang.Get("S.Group.Untitled") };
        App.Config.Groups.Add(g);
        RefreshGroups();
        MarkDirty();
    }

    private async void OnGroupDelete(object? sender, RoutedEventArgs e)
    {
        if (SelectedGroup is not { } g)
            return;
        if (g.IsDefault)
        {
            MsgBox.Show(this, Lang.Get("S.Group.DeleteDefault"));
            return;
        }
        if (!await MsgBox.ConfirmAsync(this, Lang.Get("S.Group.DeleteConfirm", g.Name)))
            return;
        App.Config.Rules.RemoveAll(r =>
            r.Trigger?.GroupId == g.Id || r.Target?.GroupId == g.Id ||
            r.TriggerGroupId == g.Id || r.TargetGroupId == g.Id);
        App.Config.Groups.Remove(g);
        RefreshGroups();
        RefreshRules();
        MarkDirty();
    }

    private void OnGroupNameChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingUi || SelectedGroup is not { } g)
            return;
        g.Name = GroupNameBox.Text?.Trim() ?? "";
        RefreshGroups();
        MarkDirty();
    }

    private void OnDefaultChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingUi || SelectedGroup is not { } g)
            return;
        if (DefaultCheck.IsChecked == true)
        {
            if (g.IsDefault)
                return;
            foreach (var other in App.Config.Groups)
                other.IsDefault = false;
            g.IsDefault = true;
        }
        else
        {
            if (!g.IsDefault)
                return;
            _loadingUi = true;
            DefaultCheck.IsChecked = true; // 默认组不可取消：弹回并说明
            _loadingUi = false;
            MsgBox.Show(this, Lang.Get("S.Group.DefaultOnlyOne"));
            return;
        }
        RefreshGroups();
        MarkDirty();
    }

    private void OnProcBoxKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter)
            OnProcAdd(sender, e);
    }

    private void OnProcAdd(object? sender, RoutedEventArgs e)
    {
        if (_loadingUi || SelectedGroup is not { } g)
            return;
        var name = NewProcBox.Text?.Trim() ?? "";
        if (name.Length == 0)
            return;
        if (!g.ProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            g.ProcessNames.Add(name);
        NewProcBox.Text = "";
        RefreshGroupDetail();
        MarkDirty();
    }

    private void OnProcRemoveItem(object? sender, RoutedEventArgs e)
    {
        if (_loadingUi || SelectedGroup is not { } g || (sender as Control)?.Tag is not string name)
            return;
        g.ProcessNames.RemoveAll(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        RefreshGroupDetail();
        MarkDirty();
    }

    private void OnRefreshRunning(object? sender, RoutedEventArgs e) => RefreshRunningProcs();

    private void OnProcAddRunning(object? sender, RoutedEventArgs e)
    {
        if (_loadingUi || SelectedGroup is not { } g || RunningProcs.SelectedItem is not string name)
            return;
        if (!g.ProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            g.ProcessNames.Add(name);
        RefreshGroupDetail();
        MarkDirty();
    }

    // ================= 规则页 =================

    public sealed class RuleVm
    {
        public DuckingRuleConfig Rule { get; init; } = null!;
        public string Sentence { get; set; } = "";
        public string WarnText { get; set; } = "";
        public bool HasWarn => WarnText.Length > 0;
    }

    private DuckingRuleConfig? SelectedRule => (RuleList.SelectedItem as RuleVm)?.Rule;

    private string ComputeRuleWarn(DuckingRuleConfig r)
    {
        if (!r.Enabled || r.Trigger is null || r.Target is null)
            return "";
        string Check(RuleRef sel, string role)
        {
            if (sel.Type == "app")
            {
                var name = (sel.ProcessName ?? "").Trim();
                if (name.Length == 0)
                    return Lang.Get("S.Rule.WarnAppEmpty", role);
                var key = ConfigStore.NormalizeProcessName(name);
                if (!App.Monitor.CurrentSnapshot.Any(s => ConfigStore.NormalizeProcessName(s.ProcessName) == key))
                    return Lang.Get("S.Rule.WarnNoSession", role, name);
            }
            else if (App.Config.Groups.All(g => g.Id != sel.GroupId))
            {
                return Lang.Get("S.Rule.WarnGroupGone", role);
            }
            return "";
        }
        return Check(r.Trigger, Lang.Get("S.Rule.RoleTrigger")) is { Length: > 0 } t
            ? t
            : Check(r.Target, Lang.Get("S.Rule.RoleTarget"));
    }

    private string RuleSentence(DuckingRuleConfig r)
    {
        string Label(RuleRef? s) =>
            s is null ? "?" : s.Type == "app"
                ? s.ProcessName + Lang.Get("S.Rule.AppSuffix")
                : (App.Config.Groups.FirstOrDefault(g => g.Id == s.GroupId)?.Name ?? s.GroupId + "?") + Lang.Get("S.Rule.GroupSuffix");
        return (r.Enabled ? "" : Lang.Get("S.Rule.DisabledPrefix")) +
               Lang.Get("S.Rule.Sentence", Label(r.Trigger), Label(r.Target), r.TargetVolumePercent.ToString("F0")) +
               (r.DetectionMode == "state" ? Lang.Get("S.Rule.StateTag") : Lang.Get("S.Rule.SoundTag")) +
               (r.Condition == "device" && r.DeviceCondition.Length > 0
                   ? Lang.Get("S.Rule.DeviceSuffix", r.DeviceCondition) : "");
    }

    private void RefreshRules()
    {
        _loadingUi = true;
        var sel = SelectedRule;
        RuleList.ItemsSource = App.Config.Rules
            .Select(r => new RuleVm { Rule = r, Sentence = RuleSentence(r), WarnText = ComputeRuleWarn(r) })
            .ToList();
        if (sel is not null)
            RuleList.SelectedItem = RuleList.Items.OfType<RuleVm>().FirstOrDefault(v => v.Rule == sel);
        _loadingUi = false;
        RefreshRuleDetail();
    }

    private void RefreshRuleDetail()
    {
        var r = SelectedRule;
        _loadingUi = true;
        SetSelector(RuleTriggerType, RuleTriggerValue, r?.Trigger);
        SetSelector(RuleTargetType, RuleTargetValue, r?.Target);
        RuleDetectionMode.SelectedIndex = r?.DetectionMode == "sound" ? 1 : 0;
        RuleVolume.Text = r is null ? "" : $"{r.TargetVolumePercent:F0}";
        RulePriority.Text = r?.Priority.ToString() ?? "";
        RuleFadeOut.Text = r?.FadeOutMs?.ToString() ?? "";
        RuleFadeIn.Text = r?.FadeInMs?.ToString() ?? "";
        bool hasCond = r?.Condition == "device";
        RuleCondType.SelectedIndex = hasCond ? 1 : 0;
        RuleCondDevice.IsVisible = hasCond;
        RuleCondDevice.ItemsSource = CondDevices();
        RuleCondDevice.SelectedItem = r?.DeviceCondition ?? "";
        RuleEnabled.IsChecked = r?.Enabled ?? false;
        _loadingUi = false;
    }

    private List<string> CondDevices()
    {
        var names = new List<string>();
        var current = App.Monitor.DeviceName;
        if (current.Length > 0)
            names.Add(current);
        try
        {
            foreach (var n in App.Monitor.EnumerateRenderDevices().Values.Where(n => n != current))
                names.Add(n);
        }
        catch { }
        return names;
    }

    private void SetSelector(ComboBox type, ComboBox value, RuleRef? sel)
    {
        bool isApp = sel?.Type == "app";
        type.SelectedIndex = isApp ? 1 : 0;
        if (isApp)
        {
            value.ItemsSource = App.Monitor.CurrentSnapshot.Select(s => s.ProcessName)
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var configured = sel?.ProcessName ?? "";
            var match = (value.ItemsSource as List<string>)?.FirstOrDefault(n =>
                string.Equals(n, configured, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                value.SelectedItem = match;
            value.SelectedItem = configured.Length > 0 && match is null ? null : value.SelectedItem;
        }
        else
        {
            value.ItemsSource = App.Config.Groups.Select(g => g.Name).ToList();
            var gid = sel?.GroupId ?? "";
            var g = App.Config.Groups.FirstOrDefault(x => x.Id == gid);
            value.SelectedItem = g?.Name ?? App.Config.Groups.FirstOrDefault()?.Name;
        }
    }

    private void OnRuleSelected(object? sender, SelectionChangedEventArgs e) => RefreshRuleDetail();

    private void OnRuleAdd(object? sender, RoutedEventArgs e)
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
        RefreshRules();
        MarkDirty();
    }

    private void OnRuleDelete(object? sender, RoutedEventArgs e)
    {
        if (SelectedRule is not { } r)
            return;
        App.Config.Rules.Remove(r);
        RefreshRules();
        MarkDirty();
    }

    private RuleRef ReadSelector(ComboBox type, ComboBox value)
    {
        if (type.SelectedIndex == 1)
            return new RuleRef { Type = "app", ProcessName = (value.SelectedItem as string ?? "").Trim() };
        var name = value.SelectedItem as string ?? "";
        var g = App.Config.Groups.FirstOrDefault(x => x.Name == name);
        return new RuleRef { Type = "group", GroupId = g?.Id ?? "" };
    }

    private void OnRuleSelectorTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || SelectedRule is null)
            return;
        _loadingUi = true;
        if (ReferenceEquals(sender, RuleTriggerType))
            SetSelector(RuleTriggerType, RuleTriggerValue, RefOfCurrentType(RuleTriggerType));
        else if (ReferenceEquals(sender, RuleTargetType))
            SetSelector(RuleTargetType, RuleTargetValue, RefOfCurrentType(RuleTargetType));
        _loadingUi = false;
        ApplyRuleEdits();
    }

    private static RuleRef RefOfCurrentType(ComboBox type) =>
        type.SelectedIndex == 1
            ? new RuleRef { Type = "app", ProcessName = "" }
            : new RuleRef { Type = "group", GroupId = "" };

    private void OnRuleSelectorChanged(object? sender, SelectionChangedEventArgs e) => ApplyRuleEdits();

    private void OnRuleFieldChanged(object? sender, RoutedEventArgs e) => ApplyRuleEdits();

    private void OnRuleCondTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi)
            return;
        RuleCondDevice.IsVisible = RuleCondType.SelectedIndex == 1;
        ApplyRuleEdits();
    }

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
        r.FadeOutMs = int.TryParse(RuleFadeOut.Text?.Trim(), out var fo) && fo > 0 ? Math.Clamp(fo, 50, 10_000) : null;
        r.FadeInMs = int.TryParse(RuleFadeIn.Text?.Trim(), out var fi) && fi > 0 ? Math.Clamp(fi, 50, 10_000) : null;
        if (RuleCondType.SelectedIndex == 1)
        {
            r.Condition = "device";
            r.DeviceCondition = (RuleCondDevice.SelectedItem as string ?? "").Trim();
            if (r.DeviceCondition.Length == 0)
                r.Condition = "none";
        }
        else
        {
            r.Condition = "none";
            r.DeviceCondition = "";
        }
        r.Enabled = RuleEnabled.IsChecked == true;
        _loadingUi = true;
        RuleList.ItemsSource = App.Config.Rules
            .Select(x => new RuleVm { Rule = x, Sentence = RuleSentence(x), WarnText = ComputeRuleWarn(x) })
            .ToList();
        RuleList.SelectedItem = RuleList.Items.OfType<RuleVm>().FirstOrDefault(v => v.Rule == r);
        _loadingUi = false;
        MarkDirty();
    }

    // ================= 设置页 =================

    private void LoadSettings()
    {
        var s = App.Config.Settings;
        _loadingUi = true;
        SetLanguage.Items.Clear();
        SetLanguage.Items.Add(Lang.Get("S.Set.LangAuto"));
        SetLanguage.Items.Add(Lang.Get("S.Set.LangZh"));
        SetLanguage.Items.Add(Lang.Get("S.Set.LangEn"));
        SetLanguage.SelectedIndex = s.Language switch { "zh-CN" => 1, "en-US" => 2, _ => 0 };
        SetRuleConflict.Items.Clear();
        SetRuleConflict.Items.Add(Lang.Get("S.Set.ConflictPriority"));
        SetRuleConflict.Items.Add(Lang.Get("S.Set.ConflictDeepest"));
        SetRuleConflict.SelectedIndex = s.RuleConflict == "deepest" ? 1 : 0;
        SetManualTakeover.IsChecked = s.ManualTakeover;
        SetSuspend.IsChecked = !App.Engine.Suspended;
        SetFadeOut.Text = s.FadeOutDurationMs.ToString();
        SetFadeIn.Text = s.FadeInDurationMs.ToString();
        ConfigPathText.Text = Lang.Get("S.Config.FileLabel", ConfigStore.ActivePath);
        PortableModeText.Text = ConfigStore.PortableBase is not null
            ? Lang.Get("S.Config.PortableOn")
            : Lang.Get("S.Config.PortableOff");
        DiagPathText.Text = Lang.Get("S.Diag.PathLabel", App.LogPath);
        LoadCurveCombo();
        UpdateCurveHint();
        _loadingUi = false;
    }

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi)
            return;
        App.Config.Settings.Language = SetLanguage.SelectedIndex switch
        {
            1 => "zh-CN", 2 => "en-US", _ => "",
        };
        // 延迟到 SelectionChanged 事件完成后再整体重建：在 ComboBox 自己的选择事件里
        // Clear/重建 Items 会重入（Avalonia 上可致崩溃），Post 到下一轮 UI 分发执行
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (App.ForceExit)
                return;
            Lang.Apply(Lang.Resolve(App.Config.Settings.Language));
            LoadSettings();
            RefreshGroups();
            RefreshRules();
            App.Log("界面语言已切换：" + Lang.Current);
            MarkDirty();
        });
    }

    /// <summary>语言切换自动化复现（--langtest 诊断模式）：逐步打点，崩溃时 app.log 停在死点。
    /// 第二阶段模拟真实交互：弹出框打开状态下改 SelectedIndex（触发真正的 OnLanguageChanged 路径）。</summary>
    public void RunLangTest()
    {
        App.Log("[langtest] step0 切到设置页");
        Tabs.SelectedIndex = 4;
        App.Log("[langtest] step1 Apply(en-US)");
        Lang.Apply("en-US");
        App.Log("[langtest] step2 Apply(zh-CN)");
        Lang.Apply("zh-CN");
        App.Log("[langtest] step3 LoadSettings");
        LoadSettings();
        App.Log("[langtest] step4 RefreshGroups");
        RefreshGroups();
        App.Log("[langtest] step5 RefreshRules");
        RefreshRules();
        App.Log("[langtest] 直接序列完成；阶段2：弹出框打开 + 真实选择事件路径");
        SetLanguage.IsDropDownOpen = true;
        SetLanguage.SelectedIndex = 2; // en-US → 走 OnLanguageChanged（含 Post 重建）
        App.Log("[langtest] 阶段2 选择已发出，等待延迟重建");
    }

    private void OnSuspendSwitchChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingUi)
            return;
        App.SetSuspended(SetSuspend.IsChecked != true);
        PausedChip.IsVisible = App.Engine.Suspended;
    }

    private void OnSettingsFieldChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Name: "SetFadeOut" } or TextBox { Name: "SetFadeIn" })
        { MarkDirty(); return; }
        MarkDirty();
    }

    private bool TryParseSettings()
    {
        if (!int.TryParse(SetFadeOut.Text, out var fadeOut) ||
            !int.TryParse(SetFadeIn.Text, out var fadeIn))
            return false;
        var s = App.Config.Settings;
        s.FadeOutDurationMs = Math.Clamp(fadeOut, 50, 10_000);
        s.FadeInDurationMs = Math.Clamp(fadeIn, 50, 10_000);
        s.RuleConflict = SetRuleConflict.SelectedIndex == 1 ? "deepest" : "priority";
        s.ManualTakeover = SetManualTakeover.IsChecked == true;
        return true;
    }

    private void MarkDirty()
    {
        if (_loadingUi)
            return;
        _dirty = true;
        SetSaveStatus(Lang.Get("S.Save.Pending"));
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private async void AutoSaveNow()
    {
        _autoSaveTimer.Stop();
        if (!_dirty)
            return;
        if (!TryParseSettings())
        {
            SetSaveStatus(Lang.Get("S.Save.Invalid"));
            return;
        }
        try
        {
            ConfigStore.Save(ConfigStore.ActivePath, App.Config);
            App.Engine.ApplyConfig(ConfigStore.Clone(App.Config));
        }
        catch (Exception ex)
        {
            SetSaveStatus(Lang.Get("S.Save.Fail", ex.Message));
            return;
        }
        _dirty = false;
        SetSaveStatus(Lang.Get("S.Save.Saved", DateTime.Now.ToString("HH:mm:ss")));
        App.Log($"配置已自动保存（{App.Config.Groups.Count} 组 / {App.Config.Rules.Count} 条规则）");
        await Task.CompletedTask;
    }

    private void SetSaveStatus(string? text) =>
        SaveStatus.Text = text ?? Lang.Get("S.Save.Default");

    // ---------- 配置导入/导出 ----------

    private async void OnConfigExport(object? sender, RoutedEventArgs e)
    {
        AutoSaveNow();
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = Lang.Get("S.Config.ExportTitle"),
            SuggestedFileName = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                Lang.Get("S.Config.ExportDefaultName"), DateTime.Now.ToString("yyyyMMdd-HHmm")),
            FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Tuner config") { Patterns = new[] { "*.json" } } },
        });
        if (file is null)
            return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            var json = System.Text.Json.JsonSerializer.Serialize(App.Config, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });
            await using var w = new System.IO.StreamWriter(stream);
            await w.WriteAsync(json);
            App.Log("配置已导出：" + file.Path);
        }
        catch (Exception ex)
        {
            MsgBox.Show(this, Lang.Get("S.Config.ExportFail", ex.Message));
        }
    }

    private async void OnConfigImport(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = Lang.Get("S.Config.ImportTitle"),
            AllowMultiple = false,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Tuner config") { Patterns = new[] { "*.json" } } },
        });
        if (files.Count == 0)
            return;
        var path = files[0].Path.LocalPath;
        var imported = ConfigStore.TryLoadForImport(path);
        if (imported is null)
        {
            MsgBox.Show(this, Lang.Get("S.Config.ImportBadFile", System.IO.Path.GetFileName(path)));
            return;
        }
        ReplaceConfig(imported);
        MsgBox.Show(this, Lang.Get("S.Config.ImportDone", imported.Groups.Count, imported.Rules.Count));
    }

    /// <summary>整体替换配置并全界面重载（与 WPF 版 App.ReplaceConfig + 导入流程合并）。</summary>
    private void ReplaceConfig(TunerConfig imported)
    {
        App.ReplaceConfig(imported);
        try
        {
            ConfigStore.Save(ConfigStore.ActivePath, App.Config);
            App.Engine.ApplyConfig(ConfigStore.Clone(App.Config));
        }
        catch (Exception ex)
        {
            App.Log("导入后保存失败：" + ex.Message);
        }
        _loadingUi = true;
        RefreshGroups();
        RefreshRules();
        _loadingUi = false;
        _dirty = false;
        SetSaveStatus(null);
        Lang.Apply(Lang.Resolve(App.Config.Settings.Language));
        LoadSettings();
        App.Log($"配置已导入：{imported.Groups.Count} 组 / {imported.Rules.Count} 条规则");
    }

    // ---------- 更新检查 ----------

    private bool _updateChecking;

    private async void OnCheckUpdate(object? sender, RoutedEventArgs e)
    {
        if (_updateChecking)
            return;
        _updateChecking = true;
        UpdateCheckButton.IsEnabled = false;
        UpdateStatusText.Text = Lang.Get("S.Update.Checking");
        try
        {
            var current = typeof(MainWindow).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion.Split('+')[0] ?? "?";
            var (tag, url, err) = await Task.Run(FetchLatestRelease);
            if (tag is null)
            {
                UpdateStatusText.Text = Lang.Get("S.Update.Failed", err ?? "");
                return;
            }
            var latest = ParseVersionTag(tag);
            var cur = ParseVersionTag(current);
            UpdateStatusText.Text = latest is not null && cur is not null && latest > cur
                ? Lang.Get("S.Update.New", tag, current, url ?? "github.com/woodtong/Tuner/releases")
                : Lang.Get("S.Update.Latest", current, tag);
        }
        finally
        {
            _updateChecking = false;
            UpdateCheckButton.IsEnabled = true;
        }
    }

    private static async Task<(string? Tag, string? Url, string? Err)> FetchLatestRelease()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Add("User-Agent", "Tuner-UpdateCheck");
            using var resp = await http.GetAsync("https://api.github.com/repos/woodtong/Tuner/releases/latest");
            if (!resp.IsSuccessStatusCode)
                return (null, null, $"HTTP {(int)resp.StatusCode}");
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            return string.IsNullOrEmpty(tag) ? (null, url, "no tag") : (tag, url, null);
        }
        catch (Exception ex)
        {
            return (null, null, ex.Message);
        }
    }

    private static Version? ParseVersionTag(string s)
    {
        var digits = new string(s.Trim().TrimStart('v', 'V').TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(digits, out var v) ? v : null;
    }

    // ---------- 诊断 ----------

    private void OnDiagExpanded(object? sender, RoutedEventArgs e) => RefreshDiag();

    private void OnDiagCollapsed(object? sender, RoutedEventArgs e) { }

    private void RefreshDiag()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var entry in Core.Diag.Snapshot())
            sb.Append(entry.Time.ToString("HH:mm:ss"))
              .Append(" [").Append(entry.Category).Append("] ")
              .AppendLine(entry.Message);
        DiagBox.Text = sb.ToString();
        DiagBox.CaretIndex = DiagBox.Text.Length;
    }

    private async void OnDiagCopy(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is not null)
                await Clipboard.SetTextAsync(string.IsNullOrEmpty(DiagBox.Text) ? "（暂无事件）" : DiagBox.Text);
        }
        catch { }
    }

    private void OnDiagClear(object? sender, RoutedEventArgs e)
    {
        Core.Diag.Clear();
        RefreshDiag();
    }

    // ================= 刷新调度 =================

    private void RefreshVisible()
    {
        if (!IsVisible)
            return;
        switch (Tabs.SelectedIndex)
        {
            case 0: RefreshStatus(); break;
            case 1: RefreshVolume(); break;
            case 2: RefreshGroupDetailLight(); break;
        }
        PausedChip.IsVisible = App.Engine.Suspended;
    }

    private void RefreshGroupDetailLight()
    {
        // 会话随时间变化：切回分组页时刷新运行中进程下拉（选中态与文本不动）
        if (Tabs.SelectedIndex == 2 && !_loadingUi)
            RefreshRunningProcs();
    }
}
