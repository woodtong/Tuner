using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
        if (SetLanguage.Items.Count == 0)
        {
            SetLanguage.Items.Add(Lang.Get("S.Set.LangAuto"));
            SetLanguage.Items.Add(Lang.Get("S.Set.LangZh"));
            SetLanguage.Items.Add(Lang.Get("S.Set.LangEn"));
        }
        SetLanguage.SelectedIndex = s.Language switch { "zh-CN" => 1, "en-US" => 2, _ => 0 };
        if (SetRuleConflict.Items.Count == 0)
            SetRuleConflict.Items.Add(Lang.Get("S.Set.ConflictPriority"));
        if (SetRuleConflict.Items.Count == 1)
            SetRuleConflict.Items.Add(Lang.Get("S.Set.ConflictDeepest"));
        SetRuleConflict.SelectedIndex = s.RuleConflict == "deepest" ? 1 : 0;
        SetManualTakeover.IsChecked = s.ManualTakeover;
        SetHotkeys.IsChecked = s.HotkeysEnabled;
        SetSuspend.IsChecked = !App.Engine.Suspended; // 运行态开关：仅同步显示，不落盘
        try
        {
            SetAutoStart.IsChecked = AutoStart.IsEnabled();
        }
        catch
        {
            SetAutoStart.IsChecked = false;
        }
        DiagPathText.Text = Lang.Get("S.Diag.PathLabel", App.LogPath);
        ConfigPathText.Text = Lang.Get("S.Config.FileLabel", ConfigStore.ActivePath);
        PortableModeText.Text = ConfigStore.PortableBase is not null
            ? Lang.Get("S.Config.PortableOn")
            : Lang.Get("S.Config.PortableOff");
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

    // ---------- 界面语言（切换立即生效，随自动保存持久化） ----------

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi)
            return;
        App.Config.Settings.Language = SetLanguage.SelectedIndex switch
        {
            1 => "zh-CN",
            2 => "en-US",
            _ => "",
        };
        Lang.Apply(Lang.Resolve(App.Config.Settings.Language));
        App.Log("界面语言已切换：" + Lang.Current);
        MarkDirty();
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
            MessageBox.Show(Lang.Get("S.App.AutoStartFail", ex.Message), "Tuner");
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
        DiagToggle.Text = Lang.Get(open ? "S.Set.DiagToggleOpen" : "S.Set.DiagToggleClosed");
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

    // ---------- 配置导入/导出（整体文件级操作，导入后全界面重载） ----------

    private void OnConfigExport(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Lang.Get("S.Config.ExportTitle"),
            Filter = Lang.Get("S.Config.Filter"),
            FileName = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                Lang.Get("S.Config.ExportDefaultName"), DateTime.Now.ToString("yyyyMMdd-HHmm")),
        };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            AutoSaveNow(); // 先把未落盘的界面修改写完，导出的才是完整现状
            ConfigStore.Save(dlg.FileName, App.Config);
            App.Log($"配置已导出：{dlg.FileName}（{App.Config.Groups.Count} 组 / {App.Config.Rules.Count} 条规则）");
        }
        catch (Exception ex)
        {
            MessageBox.Show(Lang.Get("S.Config.ExportFail", ex.Message), "Tuner");
        }
    }

    private void OnConfigImport(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Lang.Get("S.Config.ImportTitle"),
            Filter = Lang.Get("S.Config.Filter"),
        };
        if (dlg.ShowDialog() != true)
            return;
        var imported = ConfigStore.TryLoadForImport(dlg.FileName);
        if (imported is null)
        {
            MessageBox.Show(Lang.Get("S.Config.ImportBadFile", System.IO.Path.GetFileName(dlg.FileName)), "Tuner");
            return;
        }

        // 整体替换：落盘（自动留 .bak 备份）→ 引擎生效 → 全部页签重载
        App.ReplaceConfig(imported);
        try
        {
            ConfigStore.Save(ConfigStore.ActivePath, App.Config);
            App.Engine.ApplyConfig(ConfigStore.Clone(App.Config));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Lang.Get("S.Config.ImportSaveFail", ex.Message), "Tuner");
        }
        _loadingUi = true;
        RefreshGroupColors();
        RefreshGroupTab();
        RefreshRuleTab();
        LoadSettingsTab();
        _loadingUi = false;
        _dirty = false;
        SetSaveStatus(null);
        _hotkeys?.SetEnabled(App.Config.Settings.HotkeysEnabled); // 导入配置的快捷键开关即时生效
        Lang.Apply(Lang.Resolve(App.Config.Settings.Language)); // 导入配置的语言即时生效
        App.Log($"配置已导入：{dlg.FileName}（{App.Config.Groups.Count} 组 / {App.Config.Rules.Count} 条规则）");
        MessageBox.Show(Lang.Get("S.Config.ImportDone", App.Config.Groups.Count, App.Config.Rules.Count), "Tuner");
    }

    // ---------- 更新检查（GitHub latest 比对，仅提示不下载；链接以文字展示，不拉起浏览器） ----------

    private bool _updateChecking;

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
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
            App.Log($"更新检查：最新发布 {tag}，当前 {current}");
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
            http.DefaultRequestHeaders.Add("User-Agent", "Tuner-UpdateCheck"); // GitHub API 拒绝无 UA 请求
            using var resp = await http.GetAsync("https://api.github.com/repos/woodtong/Tuner/releases/latest");
            if (!resp.IsSuccessStatusCode)
                return (null, null, $"HTTP {(int)resp.StatusCode}");
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            return string.IsNullOrEmpty(tag) ? (null, url, "响应缺少版本号") : (tag, url, null);
        }
        catch (Exception ex)
        {
            return (null, null, ex.Message); // 网络/代理异常：如实呈现，不影响任何功能
        }
    }

    /// <summary>"v0.1.2-eta" → 0.1.2（只比数字位，希腊字母后缀忽略）；解析失败返回 null。</summary>
    private static Version? ParseVersionTag(string s)
    {
        var digits = new string(s.Trim().TrimStart('v', 'V').TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(digits, out var v) ? v : null;
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
        s.HotkeysEnabled = SetHotkeys.IsChecked == true;
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

    private void AutoSaveNow()
    {
        _autoSaveTimer.Stop();
        if (!_dirty)
            return;
        if (!TryParseSettings())
        {
            SetSaveStatus(Lang.Get("S.Save.Invalid"));
            SaveStatus.Foreground = MakeBrush("#D97706");
            return; // 保持 _dirty，后续修改会再次触发
        }
        try
        {
            ConfigStore.Save(ConfigStore.ActivePath, App.Config);
            App.Engine.ApplyConfig(ConfigStore.Clone(App.Config)); // 深拷贝后生效，避免引擎与 UI 共享可变集合
        }
        catch (Exception ex)
        {
            SetSaveStatus(Lang.Get("S.Save.Fail", ex.Message));
            SaveStatus.Foreground = MakeBrush("#DC2626");
            return;
        }
        _dirty = false;
        SetSaveStatus(Lang.Get("S.Save.Saved", DateTime.Now.ToString("HH:mm:ss")));
        SaveStatus.Foreground = MakeBrush("#16A34A");
        _hotkeys?.SetEnabled(App.Config.Settings.HotkeysEnabled); // 快捷键开关随保存即时生效
        App.Log($"配置已自动保存（{App.Config.Groups.Count} 组 / {App.Config.Rules.Count} 条规则）");
    }

    private void SetSaveStatus(string? text)
    {
        SaveStatus.Text = text ?? Lang.Get("S.Save.Default");
        SaveStatus.Foreground = _dirty
            ? MakeBrush("#D97706")
            : new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    }
}
