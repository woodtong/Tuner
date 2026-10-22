using System.Windows;
using System.Windows.Controls;
using Tuner.Core.Config;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace Tuner;

/// <summary>规则页：规则列表与触发/目标选择器编辑。</summary>
public partial class MainWindow
{
    private sealed class RuleVm
    {
        public required DuckingRuleConfig Rule { get; init; }
        public string Sentence =>
            (Rule.Enabled ? "" : "（已停用）") + Label(Rule.Trigger) + " 出声 → " + Label(Rule.Target) +
            $" 渐变到 {Rule.TargetVolumePercent:F0}%" + (Rule.DetectionMode == "state" ? "［状态判定］" : "［声音判定］");

        /// <summary>失配提醒：启用规则的触发/目标当前匹配不上任何会话（或引用已删除的分组）时随行出现。</summary>
        public string WarnText { get; init; } = "";

        public Visibility WarnVisibility => WarnText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

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

    /// <summary>
    /// 规则失配检测（诊断主动提示的一部分）：应用名（含别名等价）不在当前任何会话中＝未运行或名字不符，
    /// 分组 Id 不在配置中＝分组已被删除。停用的规则不打扰。
    /// </summary>
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
                    return $"{role}应用名为空，规则永不生效";
                var key = ConfigStore.NormalizeProcessName(name);
                if (!App.Monitor.CurrentSnapshot.Any(s => ConfigStore.NormalizeProcessName(s.ProcessName) == key))
                    return $"⚠ {role}应用「{name}」当前没有音频会话（未运行，或名字与实际进程不符）";
            }
            else if (App.Config.Groups.All(g => g.Id != sel.GroupId))
            {
                return $"⚠ {role}分组不存在（可能已被删除），规则永不生效";
            }
            return "";
        }
        return Check(r.Trigger, "触发") is { Length: > 0 } triggerWarn ? triggerWarn : Check(r.Target, "目标");
    }

    private void RefreshRuleTab()
    {
        _loadingUi = true;
        var selected = SelectedRule;
        RuleList.ItemsSource = App.Config.Rules
            .Select(r => new RuleVm { Rule = r, WarnText = ComputeRuleWarn(r) })
            .ToList();
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
        UpdateRuleMatchHints();
    }

    /// <summary>应用模式下拉失配提示：输入的名字（含别名等价）在当前会话中都不存在时提醒，
    /// 避免配了一个永远匹配不上的名字而毫无反馈（应用未运行也会提示，措辞已涵盖）。</summary>
    private void UpdateRuleMatchHints()
    {
        SetSelectorHint(RuleTriggerHint, RuleTriggerType, RuleTriggerValue);
        SetSelectorHint(RuleTargetHint, RuleTargetType, RuleTargetValue);
    }

    private static void SetSelectorHint(System.Windows.Controls.TextBlock hint, ComboBox type, ComboBox value)
    {
        var raw = value.Text ?? "";
        if (type.SelectedIndex != 1 || raw.Trim().Length == 0)
        {
            hint.Visibility = Visibility.Collapsed;
            return;
        }
        var name = raw.Trim();
        var key = ConfigStore.NormalizeProcessName(name);
        if (App.Monitor.CurrentSnapshot.Any(s => ConfigStore.NormalizeProcessName(s.ProcessName) == key))
        {
            hint.Visibility = Visibility.Collapsed;
            return;
        }
        hint.Text = $"⚠ 当前没有名为「{name}」的音频会话（应用未运行，或名字与实际进程不符）";
        hint.Foreground = MakeBrush("#D97706");
        hint.Visibility = Visibility.Visible;
    }

    /// <summary>按选择器类型填充"取值"下拉：分组模式列出分组，应用模式列出当前真实存在的应用（纯扫描）。
    /// 已配置但当前不在列表里的名字如实保留在输入框中（应用未运行），不再强制跳到列表首项——
    /// 那会让"已关闭的应用"的规则在随便改个别的字段时被静默改写成别的应用。</summary>
    private void SetSelector(ComboBox type, ComboBox value, RuleRef? sel)
    {
        bool isApp = sel?.Type == "app";
        type.SelectedIndex = isApp ? 1 : 0;
        if (isApp)
        {
            var names = AppNames();
            var configured = sel?.ProcessName ?? "";
            value.ItemsSource = names;
            value.SelectedItem = names.FirstOrDefault(n =>
                string.Equals(n, configured, StringComparison.OrdinalIgnoreCase));
            value.Text = configured;
        }
        else
        {
            var groups = App.Config.Groups.Select(g => new GroupVm { Group = g }).ToList();
            value.ItemsSource = groups;
            value.SelectedItem = groups.FirstOrDefault(v => v.Group.Id == sel?.GroupId) ?? groups.FirstOrDefault();
        }
    }

    /// <summary>切换选择器类型时重填取值下拉（保留刚选的类型，否则会被强制改回分组）。</summary>
    private void OnRuleSelectorTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi)
            return;
        _loadingUi = true;
        if (sender == RuleTriggerType)
            SetSelector(RuleTriggerType, RuleTriggerValue, RefOfCurrentType(RuleTriggerType));
        else if (sender == RuleTargetType)
            SetSelector(RuleTargetType, RuleTargetValue, RefOfCurrentType(RuleTargetType));
        _loadingUi = false;
        ApplyRuleEdits();
    }

    /// <summary>按类型下拉当前选中项构造空值引用：应用=空进程名，分组=空 GroupId，取值下拉各自落到首项。</summary>
    private static RuleRef RefOfCurrentType(ComboBox type) =>
        type.SelectedIndex == 1
            ? new RuleRef { Type = "app", ProcessName = "" }
            : new RuleRef { Type = "group", GroupId = "" };

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

    /// <summary>应用模式下拉的数据源：只列当前真实存在的音频会话（扫描结果）。
    /// 列表里出现的每一个条目都可信、都能命中；配置里手输过的名字绝不混入——
    /// 无效名字的可选性正是"看起来识别过、实际永不匹配"的静默失效来源。
    /// 尚未出过声的应用不在列表中，可用输入框手输，失配时下方有提示。</summary>
    private List<string> AppNames() =>
        App.Monitor.CurrentSnapshot.Select(s => s.ProcessName)
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
        UpdateRuleMatchHints();
        MarkDirty();
    }
}
