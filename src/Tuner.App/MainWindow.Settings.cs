using System.Text;
using System.Windows;
using System.Windows.Media;
using Tuner.Core.Config;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace Tuner;

// ---------- 设置 + 自动保存 ----------
// 任何修改（MarkDirty）后 800ms 防抖自动保存并即时生效；
// 设置项数字非法时不落盘，修正后随下一次修改或重试自动保存。

/// <summary>设置页与自动保存（防抖 800ms，保存后深拷贝生效到引擎）。
/// 另含：闪避暂停开关（运行态，不落盘）、开机自启（注册表）、诊断事件日志面板。</summary>
public partial class MainWindow
{
    /// <summary>诊断面板是否展开（普通字段：Diag.Changed 可能在后台线程触发，不能跨线程读 Visibility）。</summary>
    private bool _diagPanelOpen;

    private void LoadSettingsTab()
    {
        var s = App.Config.Settings;
        SetUseSmtc.IsChecked = s.UseMediaSessionStatus;
        SetFadeOut.Text = s.FadeOutDurationMs.ToString();
        SetFadeIn.Text = s.FadeInDurationMs.ToString();
        SetPoll.Text = s.PollIntervalMs.ToString();
        if (SetRuleConflict.Items.Count == 0)
            SetRuleConflict.Items.Add("取优先级最高");
        if (SetRuleConflict.Items.Count == 1)
            SetRuleConflict.Items.Add("取闪避最深（压得更低）");
        SetRuleConflict.SelectedIndex = s.RuleConflict == "deepest" ? 1 : 0;
        SetManualTakeover.IsChecked = s.ManualTakeover;
        SetSuspend.IsChecked = !App.Engine.Suspended; // 运行态开关：仅同步显示，不落盘
        try
        {
            SetAutoStart.IsChecked = AutoStart.IsEnabled();
        }
        catch
        {
            SetAutoStart.IsChecked = false;
        }
        DiagPathText.Text = "完整日志文件：" + App.LogPath + "（事件面板仅内存保留最近 600 条）";
        // 音量页的曲线编辑器同批初始化（XAML 一次解析全部页签，跨页控件可直接访问）
        LoadCurveEditor();
    }

    // ---------- 闪避暂停（运行态，与托盘菜单同步） ----------

    /// <summary>暂停状态同步：设置页开关 + 顶栏徽章（事件可能来自托盘菜单，App 统一调度到 UI 线程）。</summary>
    public void SyncSuspendUi(bool suspended)
    {
        _loadingUi = true;
        SetSuspend.IsChecked = !suspended;
        _loadingUi = false;
        PausedChip.Visibility = suspended ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>启用闪避开关（关闭＝暂停全部规则并立即还原音量）。</summary>
    private void OnSuspendSwitchChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi)
            return;
        App.SetSuspended(SetSuspend.IsChecked != true);
    }

    // ---------- 开机自启（注册表 HKCU Run，随 --minimized 静默启动） ----------

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi)
            return;
        try
        {
            var enable = SetAutoStart.IsChecked == true;
            AutoStart.SetEnabled(enable);
            App.Log(enable ? "已开启开机自启（HKCU Run，静默启动）" : "已关闭开机自启");
        }
        catch (Exception ex)
        {
            MessageBox.Show("设置开机自启失败：" + ex.Message, "Tuner");
            try
            {
                SetAutoStart.IsChecked = AutoStart.IsEnabled(); // 回滚为真实状态
            }
            catch { }
        }
    }

    // ---------- 诊断事件日志（隐蔽自查通道：默认收起，仅内存环形缓冲） ----------

    private void OnDiagToggleClicked(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        SetDiagPanelOpen(!_diagPanelOpen);

    private void SetDiagPanelOpen(bool open)
    {
        _diagPanelOpen = open;
        DiagPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        DiagToggle.Text = open ? "诊断 · 事件日志 ▾" : "诊断 · 事件日志 ▸";
        if (open)
            RefreshDiagBox();
    }

    /// <summary>诊断自查入口（托盘菜单「事件日志」）：切到设置页并展开面板。</summary>
    public void OpenDiagnostics()
    {
        Tabs.SelectedIndex = 4; // 设置页
        SetDiagPanelOpen(true);
        DiagPanel.BringIntoView();
    }

    private void RefreshDiagBox()
    {
        var sb = new StringBuilder();
        foreach (var entry in Core.Diag.Snapshot())
            sb.Append(entry.Time.ToString("HH:mm:ss"))
              .Append(" [").Append(entry.Category).Append("] ")
              .AppendLine(entry.Message);
        DiagBox.Text = sb.ToString();
        DiagBox.ScrollToEnd();
    }

    private void OnDiagCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(string.IsNullOrEmpty(DiagBox.Text) ? "（暂无事件）" : DiagBox.Text);
        }
        catch { /* 剪贴板被占用等：静默 */ }
    }

    private void OnDiagClear(object sender, RoutedEventArgs e)
    {
        Core.Diag.Clear();
        RefreshDiagBox();
    }

    // ---------- 数值设置 + 自动保存 ----------

    /// <summary>设置页字段变更：数值解析与钳制延后到自动保存时统一做（TryParseSettings）。</summary>
    private void OnSettingsFieldChanged(object sender, RoutedEventArgs e) => MarkDirty();

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
        s.RuleConflict = SetRuleConflict.SelectedIndex == 1 ? "deepest" : "priority";
        s.ManualTakeover = SetManualTakeover.IsChecked == true;
        return true;
    }

    private void MarkDirty()
    {
        if (_loadingUi)
            return;
        _dirty = true;
        SetSaveStatus("● 有未保存的修改…");
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void AutoSaveNow()
    {
        _autoSaveTimer.Stop();
        if (!_dirty)
            return;
        if (!TryParseSettings())
        {
            SetSaveStatus("设置里有无效数字，修正后自动保存");
            SaveStatus.Foreground = MakeBrush("#D97706");
            return; // 保持 _dirty，后续修改会再次触发
        }
        try
        {
            ConfigStore.Save(ConfigStore.DefaultPath, App.Config);
            App.Engine.ApplyConfig(ConfigStore.Clone(App.Config)); // 深拷贝后生效，避免引擎与 UI 共享可变集合
        }
        catch (Exception ex)
        {
            SetSaveStatus("自动保存失败: " + ex.Message);
            SaveStatus.Foreground = MakeBrush("#DC2626");
            return;
        }
        _dirty = false;
        SetSaveStatus($"✓ 已自动保存 {DateTime.Now:HH:mm:ss}");
        SaveStatus.Foreground = MakeBrush("#16A34A");
        App.Log($"配置已自动保存（{App.Config.Groups.Count} 组 / {App.Config.Rules.Count} 条规则）");
    }

    private void SetSaveStatus(string? text)
    {
        SaveStatus.Text = text ?? "修改会自动保存并立即生效";
        SaveStatus.Foreground = _dirty
            ? MakeBrush("#D97706")
            : new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    }
}
