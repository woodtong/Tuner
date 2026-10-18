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
}
