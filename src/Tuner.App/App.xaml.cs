using System.IO;
using System.Windows;
using Tuner.Core.Audio;
using Tuner.Core.Config;
using Tuner.Core.Ducking;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Tuner;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private static TrayHost? _tray;
    private static MainWindow? _mainWindow;

    public static TunerConfig Config { get; private set; } = null!;
    public static AudioSessionMonitor Monitor { get; private set; } = null!;
    /// <summary>默认通信设备监控（通话走独立耳机的场景）；与主监控同物理设备时会话按 InstanceId 去重。</summary>
    public static AudioSessionMonitor CommsMonitor { get; private set; } = null!;
    public static DuckingEngine Engine { get; private set; } = null!;
    public static bool ForceExit { get; set; }

    /// <summary>双设备全会话快照（多媒体在前，通信补充去重）——界面列表/扫描统一走这里。</summary>
    public static IReadOnlyList<Core.Audio.SoundSession> AllSessions
    {
        get
        {
            var primary = Monitor.CurrentSnapshot;
            var extra = CommsMonitor.CurrentSnapshot;
            if (extra.Count == 0)
                return primary;
            var list = new List<Core.Audio.SoundSession>(primary.Count + extra.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in primary)
                if (seen.Add(s.InstanceId))
                    list.Add(s);
            foreach (var s in extra)
                if (seen.Add(s.InstanceId))
                    list.Add(s);
            return list;
        }
    }

    /// <summary>会话音量写入按所属设备路由（两个监控的待写队列互不相通，跨设备写会静默丢失）。</summary>
    public static void SetSessionVolume(string instanceId, float volume) =>
        (CommsOwns(instanceId) ? CommsMonitor : Monitor).SetSessionVolume(instanceId, volume);

    public static void SetSessionMute(string instanceId, bool mute) =>
        (CommsOwns(instanceId) ? CommsMonitor : Monitor).SetSessionMute(instanceId, mute);

    public static bool HasPendingVolumeWrite(string instanceId) =>
        (CommsOwns(instanceId) ? CommsMonitor : Monitor).HasPendingVolumeWrite(instanceId);

    private static bool CommsOwns(string instanceId) =>
        !Monitor.CurrentSnapshot.Any(s => s.InstanceId == instanceId) &&
        CommsMonitor.CurrentSnapshot.Any(s => s.InstanceId == instanceId);

    /// <summary>日志路径随配置目录走（便携模式＝程序目录，否则 %APPDATA%\Tuner）。</summary>
    public static string LogPath { get; } = Path.Combine(ConfigStore.ActiveDir, "app.log");

    private static readonly object LogGate = new();

    public static void Log(string message)
    {
        lock (LogGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }
        // 同步进内存诊断环（界面"事件日志"面板），分类按前缀归并
        var category = message.StartsWith("[引擎] ") ? "引擎"
            : message.StartsWith("[监控] ") ? "监控"
            : message.StartsWith("[监控-通信] ") ? "监控"
            : "运行";
        Core.Diag.Record(category, message);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        var shot = e.Args.Contains("--screenshot");
        var probe = e.Args.Contains("--probe");
        var dev = shot || probe || e.Args.Contains("--animtest");
        if (!dev) // 截图/探针/动效诊断等开发模式跳过单实例互斥，便于在运行中的实例旁做 UI 检查
        {
            _singleInstanceMutex = new Mutex(true, @"Local\Tuner_SingleInstance", out var createdNew);
            if (!createdNew)
            {
                MsgBox.Show(null, Lang.Get("S.App.AlreadyRunning"));
                Shutdown();
                return;
            }
        }

        base.OnStartup(e);
        Log("—— 启动 ——" + (e.Args.Length > 0 ? "（参数: " + string.Join(' ', e.Args) + "）" : ""));
        // UI 线程未处理异常至少留痕（不吞异常，崩溃照常发生，但 app.log 里有完整堆栈可查）
        DispatcherUnhandledException += (_, ex) => Log("未处理异常（UI 线程）: " + ex.Exception);

        Config = ConfigStore.Load(ConfigStore.ActivePath);
        Lang.Apply(Lang.Resolve(Config.Settings.Language)); // 界面语言：MainWindow 创建前装载（DynamicResource 首次取值即正确）
        ApplyDevAppearanceArgs(e.Args); // 开发取证参数：--appearance/--accent/...（仅内存，不落盘）
        Appearance.Apply(Config.Appearance); // 外观：MainWindow 创建前装载（深浅字典+令牌覆写一次到位）
        {
            // 开发参数 --lang <code>：强制界面语言（英文渲染截图验证用；不落盘）
            var li = Array.IndexOf(e.Args, "--lang");
            if (li >= 0 && li + 1 < e.Args.Length)
                Lang.Apply(e.Args[li + 1]);
        }
        Monitor = new AudioSessionMonitor(Math.Max(10, Config.Settings.PollIntervalMs));
        CommsMonitor = new AudioSessionMonitor(Math.Max(10, Config.Settings.PollIntervalMs),
            NAudio.CoreAudioApi.Role.Communications);
        Engine = new DuckingEngine(Monitor, Config, CommsMonitor, new Tuner.Core.Media.MediaSessionTracker());
        Monitor.Diagnostics += m => Log("[监控] " + m);
        CommsMonitor.Diagnostics += m => Log("[监控-通信] " + m);
        Engine.Diagnostics += m => Log("[引擎] " + m);
        // 暂停状态变化：同步托盘勾选/悬停文字与主界面开关/徽章（事件可能来自托盘或设置页）
        Engine.SuspendedChanged += () =>
        {
            _tray?.SyncSuspendState(Engine.Suspended);
            _mainWindow?.Dispatcher.BeginInvoke(() => _mainWindow.SyncSuspendUi(Engine.Suspended));
        };
        // 界面语言切换：托盘菜单/悬停文字随新语言重建
        Lang.LanguageChanged += () => _tray?.ApplyLanguage();

        try
        {
            Monitor.Start();
            CommsMonitor.Start();
            Engine.Start();
        }
        catch (Exception ex)
        {
            Log("启动失败: " + ex.Message);
            MsgBox.Show(null, Lang.Get("S.App.StartFail", ex.Message));
            Shutdown();
            return;
        }
        // 设备在轮询线程上异步绑定，首轮样本到达后再记录设备信息
        void OnFirstSample(IReadOnlyList<Core.Audio.SoundSession> sessions)
        {
            Monitor.SamplesUpdated -= OnFirstSample;
            Log($"监控已启动（设备: {Monitor.DeviceName}，会话 {sessions.Count} 个）");
        }
        Monitor.SamplesUpdated += OnFirstSample;

        _tray = new TrayHost();
        _mainWindow = new MainWindow();

        if (shot)
        {
            // 截图模式：引擎隔离为空规则，只读会话、绝不写音量（可与用户正在运行的实例共存）
            Engine.ApplyConfig(ConfigStore.CreateDefault());
        }

        if (probe)
        {
            // 探针模式：先隔离引擎，再由探针自身施加临时规则做对照实验
            Engine.ApplyConfig(ConfigStore.CreateDefault());
        }

        if (e.Args.Contains("--animtest"))
        {
            // 动效开关诊断模式：程序化 开→关→开，逐步探测隐式样式来自哪本主题字典
            var animTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            animTimer.Tick += (_, _) =>
            {
                animTimer.Stop();
                _ = AnimTestAsync();
            };
            animTimer.Start();
        }

        var smoke = e.Args.Contains("--smoke");
        var minimized = e.Args.Contains("--minimized") && !smoke;
        if (!minimized)
            _mainWindow.Show();
        else
            Log("静默启动（--minimized）：不弹窗口，驻留托盘");
        if (!minimized)
            _tray.ShowBalloon(Lang.Get("S.Tray.BalloonTitle"), Lang.Get("S.Tray.BalloonText"));

        if (smoke)
        {
            // 冒烟模式：6 秒后自动退出（走完整退出还原链路）
            Log("冒烟模式：6 秒后自动退出");
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                ForceExit = true;
                Log("冒烟模式：触发退出");
                Shutdown();
            };
            timer.Start();
        }

        if (probe)
        {
            // 探针模式：展示窗口，1.2s 后进入交互探针（进程内合成滚轮/拖动），完成即退出
            var win = _mainWindow!;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _ = win.RunFaderProbeAsync();
            };
            timer.Start();
        }

        if (e.Args.Contains("--anim-cycle"))
        {
            // 动效开关切换机取证：关→开→关逐段打点当前样式实例哈希——三段哈希必须各不相同，
            // 证明每次切换都物化了新字典（BAML 同 URI 实例缓存会让第二程静默失效的历史 bug）
            string StyleHash() => TryFindResource("Btn") is { } s ? s.GetHashCode().ToString("X8") : "null";
            Log($"[animcycle] 启动态 animations={Config.Appearance.Animations} Btn#{StyleHash()}");
            var step = 0;
            var cycle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            cycle.Tick += (_, _) =>
            {
                step++;
                if (step == 1)
                {
                    Config.Appearance.Animations = false;
                    Appearance.Apply(Config.Appearance);
                    Log($"[animcycle] step1 关动效 Btn#{StyleHash()}");
                }
                else if (step == 2)
                {
                    Config.Appearance.Animations = true;
                    Appearance.Apply(Config.Appearance);
                    Log($"[animcycle] step2 开动效 Btn#{StyleHash()}");
                }
                else
                {
                    cycle.Stop();
                    Log("[animcycle] 完成退出（三段哈希应各不相同）");
                    ForceExit = true;
                    Shutdown();
                }
            };
            cycle.Start();
        }

        if (e.Args.Contains("--screenshot"))
        {
            // 截图模式：展示窗口并自动遍历页签渲染 PNG（供 UI 检查），完成后退出。
            // --shot-size WxH 可指定窗口尺寸（如 800x580，即最小尺寸）验证布局适应性
            var idx = Array.IndexOf(e.Args, "--shot-dir");
            var dir = idx >= 0 && idx + 1 < e.Args.Length ? e.Args[idx + 1] : "ui-preview";
            Log($"截图模式：输出到 {Path.GetFullPath(dir)}");
            var win = _mainWindow!;
            var sIdx = Array.IndexOf(e.Args, "--shot-size");
            if (sIdx >= 0 && sIdx + 1 < e.Args.Length)
            {
                var parts = e.Args[sIdx + 1].Split('x');
                if (parts.Length == 2
                    && double.TryParse(parts[0], out var w) && double.TryParse(parts[1], out var h))
                {
                    win.Width = w;
                    win.Height = h;
                    Log($"截图模式：窗口尺寸 {w}x{h}");
                }
            }
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _ = win.CaptureAndExitAsync(dir);
            };
            timer.Start();
        }
    }

    /// <summary>开发取证参数（--appearance/--accent/...）是否生效。生效时禁止配置落盘：
    /// 截图运行会因程序化设值触发 MarkDirty 链，若照常保存会把 dev 外观写进用户真实配置。</summary>
    public static bool DevAppearanceActive { get; private set; }

    /// <summary>开发取证参数：--appearance dark|light --accent #HEX --fontscale small|large --density compact --radius sharp。
    /// 仅改内存配置（截图模式取证用），不落盘。</summary>
    private static void ApplyDevAppearanceArgs(string[] args)
    {
        static string? Val(string[] a, string k)
        {
            var i = Array.IndexOf(a, k);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }
        var a = Config.Appearance;
        var theme = Val(args, "--appearance");
        if (theme is "dark" or "light" or "system") { a.Theme = theme; DevAppearanceActive = true; }
        var accent = Val(args, "--accent");
        if (accent is { Length: 7 } && accent[0] == '#') { a.Accent = accent; DevAppearanceActive = true; }
        var fs = Val(args, "--fontscale");
        if (fs is "small" or "standard" or "large") { a.FontScale = fs; DevAppearanceActive = true; }
        var density = Val(args, "--density");
        if (density is "comfortable" or "compact") { a.Density = density; DevAppearanceActive = true; }
        var radius = Val(args, "--radius");
        if (radius is "sharp" or "soft" or "round") { a.Radius = radius; DevAppearanceActive = true; }
        if (Array.Exists(args, x => x.Equals("--noanim", StringComparison.OrdinalIgnoreCase)))
        {
            a.Animations = false;
            DevAppearanceActive = true;
        }
    }

    /// <summary>
    /// 动效开关诊断（--animtest）：程序化 开→关→开。每步后探测 TabControl 隐式样式与模板的实例身份
    /// （来自 Theme.xaml 还是 Theme.NoAnim.xaml），把"再开不回来"问题的失效环节钉死在日志里。
    /// </summary>
    private static async System.Threading.Tasks.Task AnimTestAsync()
    {
        Style ProbeStyle(string tag)
        {
            // 行为探测：取窗口内第一个 Button，扫其样式与模板两处触发器的动画时长（NoAnim 版全 0）
            var btn = FindDescendant<System.Windows.Controls.Button>(_mainWindow!);
            bool animated = false;
            int timelines = 0;
            void Scan(System.Windows.TriggerCollection? triggers)
            {
                if (triggers is null)
                    return;
                foreach (var trig in triggers)
                {
                    // 动画可能挂在 EnterActions/ExitActions（状态触发器）或 Actions（EventTrigger）三处
                    var t = (System.Windows.TriggerBase)trig;
                    var groups = new System.Windows.TriggerActionCollection[]
                    {
                        t.EnterActions,
                        t.ExitActions,
                        (trig as System.Windows.EventTrigger)?.Actions,
                    };
                    foreach (var group in groups)
                        if (group is not null)
                            foreach (var act in group)
                                if (act is System.Windows.Media.Animation.BeginStoryboard bbs)
                                    foreach (var tl in bbs.Storyboard.Children)
                                    {
                                        timelines++;
                                        var d = tl.Duration;
                                        if (d.HasTimeSpan && d.TimeSpan.TotalMilliseconds > 0)
                                            animated = true;
                                    }
                }
            }
            Scan(btn?.Style?.Triggers);
            Scan(btn?.Template?.Triggers);
            Log($"[animtest] {tag}: 样式+模板时间线{timelines}条 非零时长={(animated ? "有(动画版)" : "无(NoAnim版)")}");
            return (Style)_mainWindow.FindResource(typeof(System.Windows.Controls.TabControl));
        }

        var a = Config.Appearance;
        Log("[animtest] === 开（初始） ===");
        a.Animations = true;
        Appearance.Apply(a);
        await System.Threading.Tasks.Task.Delay(600);
        ProbeStyle("开");

        Log("[animtest] === 关 ===");
        a.Animations = false;
        Appearance.Apply(a);
        await System.Threading.Tasks.Task.Delay(600);
        ProbeStyle("关");

        Log("[animtest] === 再开 ===");
        a.Animations = true;
        Appearance.Apply(a);
        await System.Threading.Tasks.Task.Delay(600);
        ProbeStyle("再开");

        Log("[animtest] 完成，退出");
        ForceExit = true;
        Current.Shutdown();
    }

    private static T? FindDescendant<T>(System.Windows.DependencyObject? root) where T : System.Windows.DependencyObject
    {
        if (root is null)
            return null;
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T hit)
                return hit;
            var deeper = FindDescendant<T>(child);
            if (deeper is not null)
                return deeper;
        }
        return null;
    }

    public static void ShowMainWindow()
    {
        if (_mainWindow is null)
            return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    /// <summary>暂停/恢复全部闪避规则（托盘菜单、设置页开关共用入口；true=暂停并还原音量）。</summary>
    public static void SetSuspended(bool suspended) => Engine.SetSuspended(suspended);

    /// <summary>配置导入：整体替换当前配置对象（UI 各处动态读 App.Config，替换引用即全局生效）。</summary>
    public static void ReplaceConfig(TunerConfig imported) => Config = imported;

    /// <summary>打开诊断自查通道：主界面切到设置页并展开事件日志。</summary>
    public static void ShowDiagnostics() => _mainWindow?.OpenDiagnostics();

    protected override void OnExit(ExitEventArgs e)
    {
        Log("—— 退出：停止引擎并还原音量 ——");
        // 退出看门狗：任何一环（引擎停止/COM 调用）被音频服务挂起时，8 秒后硬退——
        // 绝不能让托盘常驻进程变成"点退出没反应"的僵尸（2026-10-24 实测：显示关闭触发
        // 设备电源切换时引擎线程持锁的 COM 音量调用可无限挂起）
        var watchdog = Task.Run(async () =>
        {
            await Task.Delay(8000);
            Log("退出流程超时（8s），强制结束进程");
            Environment.Exit(0);
        });
        // 启动早期中止（单实例互斥/设备失败）时 Engine/Monitor 可能尚未创建，需判空
        try
        {
            Engine?.Dispose();
            if (Engine is not null)
                Log($"退出还原完成：{Engine.LastRestoreCount} 个会话");
        }
        catch (Exception ex)
        {
            Log("引擎停止异常: " + ex); // 完整堆栈便于定位
        }
        try { Monitor?.Dispose(); } catch { }
        try { CommsMonitor?.Dispose(); } catch { }
        _tray?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
