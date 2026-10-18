using System.Windows;
using System.Windows.Media;
using Tuner.Core.Config;
using Color = System.Windows.Media.Color;

namespace Tuner;

// ---------- 设置 + 自动保存 ----------
// 任何修改（MarkDirty）后 800ms 防抖自动保存并即时生效；
// 设置项数字非法时不落盘，修正后随下一次修改或重试自动保存。

/// <summary>设置页与自动保存（防抖 800ms，保存后深拷贝生效到引擎）。</summary>
public partial class MainWindow
{
    private void LoadSettingsTab()
    {
        var s = App.Config.Settings;
        SetUseSmtc.IsChecked = s.UseMediaSessionStatus;
        SetFadeOut.Text = s.FadeOutDurationMs.ToString();
        SetFadeIn.Text = s.FadeInDurationMs.ToString();
        SetPoll.Text = s.PollIntervalMs.ToString();
        // 音量页的曲线编辑器同批初始化（XAML 一次解析全部页签，跨页控件可直接访问）
        LoadCurveEditor();
    }

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
