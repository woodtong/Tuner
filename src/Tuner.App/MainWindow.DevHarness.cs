using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Tuner.Core.Config;
using Application = System.Windows.Application;
using Mouse = System.Windows.Input.Mouse;
using MouseWheelEventArgs = System.Windows.Input.MouseWheelEventArgs;

namespace Tuner;

// ---------- 开发模式工具（--probe / --screenshot） ----------
// 与产品功能无关的进程内自检/出图工具，独立成文件便于裁剪维护。

/// <summary>开发模式：推子交互探针与 UI 截图出图。</summary>
public partial class MainWindow
{
    /// <summary>--probe 模式下打开：记录"刷新改写显示"这类关键回写（正常运行为关闭）。</summary>
    internal static bool FaderTrace;

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
                    var sess = App.AllSessions.FirstOrDefault(x => x.InstanceId == c.InstanceId);
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
            var sess = App.AllSessions.FirstOrDefault(x => x.InstanceId == ch.InstanceId);
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
        foreach (var s in App.AllSessions)
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
            SettingsLayoutSelfCheck(Path.Combine(dir, "settings-check.txt"));
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

    /// <summary>
    /// 设置页布局自检（--screenshot 模式专用，视觉通道不可用时的程序化验收）：
    /// 验证新增设置行可见、顺序正确、无截断与横向溢出，结果写入文本文件。
    /// </summary>
    private void SettingsLayoutSelfCheck(string resultPath)
    {
        try
        {
            Tabs.SelectedIndex = 4;
            UpdateLayout();
            var lines = new List<string>();
            void Check(string name, bool ok, string detail) =>
                lines.Add($"{(ok ? "PASS" : "FAIL")} {name}: {detail}");

            var suspend = PointOf(SetSuspend, this);
            var conflict = PointOf(SetRuleConflict, this);
            var takeover = PointOf(SetManualTakeover, this);
            var hint = PointOf(SetRuleConflictHint, this);

            Check("三行可见", SetSuspend.IsVisible && SetRuleConflict.IsVisible && SetManualTakeover.IsVisible,
                $"启用闪避{Vis(SetSuspend)} 多规则{Vis(SetRuleConflict)} 接管{Vis(SetManualTakeover)}");
            Check("顺序 启用闪避→多规则→接管",
                suspend.Y < conflict.Y && conflict.Y < takeover.Y,
                $"Y {suspend.Y:F0} < {conflict.Y:F0} < {takeover.Y:F0}");
            Check("下拉选中项", SetRuleConflict.SelectedIndex >= 0,
                $"选中={SetRuleConflict.SelectedItem}（SelectedIndex={SetRuleConflict.SelectedIndex}）");
            Check("提示行可见且换行正常", SetRuleConflictHint.IsVisible && SetRuleConflictHint.ActualHeight >= 14,
                $"高度 {SetRuleConflictHint.ActualHeight:F0}px");
            Check("控件在窗内（无溢出）",
                conflict.X >= 0 && takeover.X >= 0 &&
                conflict.X + SetRuleConflict.ActualWidth <= ActualWidth &&
                takeover.X + SetManualTakeover.ActualWidth <= ActualWidth,
                $"conflict=({conflict.X:F0},{conflict.Y:F0}) {SetRuleConflict.ActualWidth:F0}宽 takeover=({takeover.X:F0},{takeover.Y:F0}) 窗宽{ActualWidth:F0}");

            Check("配置分组可见", ConfigPathText.IsVisible && ConfigPathText.ActualHeight > 10,
                $"路径文案高度 {ConfigPathText.ActualHeight:F0}px：{ConfigPathText.Text}");
            Check("便携模式文案可见", PortableModeText.IsVisible && PortableModeText.ActualHeight > 10,
                $"文案高度 {PortableModeText.ActualHeight:F0}px");

            File.WriteAllLines(resultPath, lines);
            App.Log("[selfcheck] 设置页布局自检完成 → " + resultPath);
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultPath, "自检异常: " + ex);
        }
    }

    private static System.Windows.Point PointOf(UIElement v, UIElement relativeTo) =>
        v.TranslatePoint(new System.Windows.Point(0, 0), relativeTo);

    private static string Vis(UIElement e) => e.IsVisible ? "√" : "×";
}
