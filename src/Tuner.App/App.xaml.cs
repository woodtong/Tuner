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

    public static string LogPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tuner", "app.log");

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
        if (!shot && !probe) // 截图/探针/开发模式跳过单实例互斥，便于在运行中的实例旁做 UI 检查
        {
            _singleInstanceMutex = new Mutex(true, @"Local\Tuner_SingleInstance", out var createdNew);
            if (!createdNew)
            {
                MessageBox.Show("Tuner 已在运行（请检查系统托盘）。", "Tuner");
                Shutdown();
                return;
            }
        }

        base.OnStartup(e);
        Log("—— 启动 ——" + (e.Args.Length > 0 ? "（参数: " + string.Join(' ', e.Args) + "）" : ""));
        // UI 线程未处理异常至少留痕（不吞异常，崩溃照常发生，但 app.log 里有完整堆栈可查）
        DispatcherUnhandledException += (_, ex) => Log("未处理异常（UI 线程）: " + ex.Exception);

        Config = ConfigStore.Load(ConfigStore.DefaultPath);
        Monitor = new AudioSessionMonitor(Math.Max(10, Config.Settings.PollIntervalMs));
        CommsMonitor = new AudioSessionMonitor(Math.Max(10, Config.Settings.PollIntervalMs),
            NAudio.CoreAudioApi.Role.Communications);
        Engine = new DuckingEngine(Monitor, Config, CommsMonitor);
        Monitor.Diagnostics += m => Log("[监控] " + m);
        CommsMonitor.Diagnostics += m => Log("[监控-通信] " + m);
        Engine.Diagnostics += m => Log("[引擎] " + m);
        // 暂停状态变化：同步托盘勾选/悬停文字与主界面开关/徽章（事件可能来自托盘或设置页）
        Engine.SuspendedChanged += () =>
        {
            _tray?.SyncSuspendState(Engine.Suspended);
            _mainWindow?.Dispatcher.BeginInvoke(() => _mainWindow.SyncSuspendUi(Engine.Suspended));
        };

        try
        {
            Monitor.Start();
            CommsMonitor.Start();
            Engine.Start();
        }
        catch (Exception ex)
        {
            Log("启动失败: " + ex.Message);
            MessageBox.Show($"无法访问默认播放设备：{ex.Message}", "Tuner 启动失败");
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

        var smoke = e.Args.Contains("--smoke");
        var minimized = e.Args.Contains("--minimized") && !smoke;
        if (!minimized)
            _mainWindow.Show();
        else
            Log("静默启动（--minimized）：不弹窗口，驻留托盘");
        if (!minimized)
            _tray.ShowBalloon("Tuner 已启动", "正在监控音频会话并执行分组闪避。关闭窗口即最小化到托盘。");

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

    /// <summary>打开诊断自查通道：主界面切到设置页并展开事件日志。</summary>
    public static void ShowDiagnostics() => _mainWindow?.OpenDiagnostics();

    protected override void OnExit(ExitEventArgs e)
    {
        Log("—— 退出：停止引擎并还原音量 ——");
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
