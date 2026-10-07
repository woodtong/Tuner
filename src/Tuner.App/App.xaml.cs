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
    public static DuckingEngine Engine { get; private set; } = null!;
    public static bool ForceExit { get; set; }

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
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        var shot = e.Args.Contains("--screenshot");
        if (!shot) // 截图/开发模式跳过单实例互斥，便于在运行中的实例旁做 UI 检查
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

        Config = ConfigStore.Load(ConfigStore.DefaultPath);
        Monitor = new AudioSessionMonitor(Math.Max(10, Config.Settings.PollIntervalMs));
        Engine = new DuckingEngine(Monitor, Config);
        Monitor.Diagnostics += m => Log("[监控] " + m);
        Engine.Diagnostics += m => Log("[引擎] " + m);

        try
        {
            Monitor.Start();
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

        var smoke = e.Args.Contains("--smoke");
        if (!e.Args.Contains("--minimized") || smoke)
            _mainWindow.Show();
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

        if (e.Args.Contains("--screenshot"))
        {
            // 截图模式：展示窗口并自动遍历页签渲染 PNG（供 UI 检查），完成后退出
            var idx = Array.IndexOf(e.Args, "--shot-dir");
            var dir = idx >= 0 && idx + 1 < e.Args.Length ? e.Args[idx + 1] : "ui-preview";
            Log($"截图模式：输出到 {Path.GetFullPath(dir)}");
            var win = _mainWindow!;
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

    protected override void OnExit(ExitEventArgs e)
    {
        Log("—— 退出：停止引擎并还原音量 ——");
        try
        {
            Engine.Dispose(); // 先停引擎（还原全部被修改的音量）
            Log($"退出还原完成：{Engine.LastRestoreCount} 个会话");
        }
        catch (Exception ex)
        {
            Log("引擎停止异常: " + ex); // 完整堆栈便于定位
        }
        try { Monitor.Dispose(); } catch { }
        _tray?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
