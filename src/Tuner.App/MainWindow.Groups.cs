using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Tuner.Core.Config;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace Tuner;

/// <summary>分组页：分组列表、进程匹配与默认组管理。</summary>
public partial class MainWindow
{
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
        var running = App.AllSessions
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
        // 新旧两种选择器字段都要查（旧 TriggerGroupId/TargetGroupId 仅为迁移遗留，但配置里可能仍在）
        App.Config.Rules.RemoveAll(r =>
            r.Trigger?.GroupId == g.Id || r.Target?.GroupId == g.Id ||
            r.TriggerGroupId == g.Id || r.TargetGroupId == g.Id);
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

    /// <summary>
    /// 「设为默认组」切换（2026-10-22 用户定稿逻辑）：
    /// 默认组有且仅有一个——勾选某分组＝把默认组换成它（原默认组自动让位）；
    /// 默认组自身**不允许取消勾选**，尝试取消时弹回并提示如何更换。
    /// （此前两版静默弹回仍会让用户困惑"为什么勾不掉"，显式提示把规则说透。）
    /// </summary>
    private void OnDefaultChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi || SelectedGroup is not { } g)
            return;
        if (DefaultCheck.IsChecked == true)
        {
            if (g.IsDefault)
                return; // 已是默认组，无事发生
            foreach (var other in App.Config.Groups)
                other.IsDefault = false;
            g.IsDefault = true;
        }
        else
        {
            if (!g.IsDefault)
                return; // 纯显示抖动（非默认组的复选框本就不该有取消事件）
            // 默认组不可取消：先弹回勾选（_loadingUi 抑制重入），再说明规则
            _loadingUi = true;
            DefaultCheck.IsChecked = true;
            _loadingUi = false;
            MessageBox.Show("默认组有且仅有一个，不能取消。\r\n若需要更改默认组，请直接勾选对应分组。", "Tuner");
            return;
        }
        _loadingUi = true;
        GroupList.Items.Refresh();
        _loadingUi = false;
        MarkDirty();
    }
}
