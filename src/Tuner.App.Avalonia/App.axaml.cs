using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Tuner.Core.Audio;
using Tuner.Core.Config;
using Tuner.Core.Ducking;

namespace Tuner.Cross;

public class App : Application
{
    private static Mutex? _singleInstance;
    private static TrayIcon? _tray;
    private static MainWindow? _main;

    public static TunerConfig Config { get; private set; } = null!;
    public static IAudioSessionMonitor Monitor { get; private set; } = null!;
    public static IAudioSessionMonitor? CommsMonitor { get; private set; }
    public static DuckingEngine Engine { get; private set; } = null!;
    public static bool ForceExit { get; set; }

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
            catch { /* 日志失败不影响主流程 */ }
        }
        var category = message.StartsWith("[引擎] ") ? "引擎"
            : message.StartsWith("[监控] ") ? "监控"
            : "运行";
        Core.Diag.Record(category, message);
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _singleInstance = new Mutex(true, "Tuner-SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Environment.Exit(0); // 已有实例：直接退出（托盘常驻工具，多开会双份闪避）
        }

        Config = ConfigStore.Load(ConfigStore.ActivePath);
        Lang.Apply(Lang.Resolve(Config.Settings.Language));
        int poll = Math.Max(10, Config.Settings.PollIntervalMs);
#if WINDOWS
        Monitor = new AudioSessionMonitor(poll);
        CommsMonitor = new AudioSessionMonitor(poll, NAudio.CoreAudioApi.Role.Communications);
        Engine = new DuckingEngine(Monitor, Config, CommsMonitor, new Tuner.Core.Media.MediaSessionTracker());
#else
        Monitor = new Tuner.Core.Linux.PulseAudioSessionMonitor(poll);
        Engine = new DuckingEngine(Monitor, Config, null, null);
#endif
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
            Environment.Exit(1);
        }

        _main = new MainWindow();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = _main;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; // 关窗驻留托盘，退出走托盘菜单
            desktop.Exit += (_, _) => ShutdownCleanup();
            if ((desktop.Args ?? Array.Empty<string>()).Contains("--smoke"))
            {
                // 冒烟模式：6 秒后自动退出（走完整退出还原链路）
                Log("冒烟模式：6 秒后自动退出");
                var timer = new Avalonia.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(6),
                };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    ForceExit = true;
                    desktop.Shutdown();
                };
                timer.Start();
            }
        }
        AttachTray();
        _main.Show();
        base.OnFrameworkInitializationCompleted();
    }

    // ---------- 托盘（Avalonia TrayIcon；Linux 环境不支持时静默降级为仅任务栏） ----------

    private void AttachTray()
    {
        try
        {
            var menu = new NativeMenu();
            var openItem = new NativeMenuItem { Header = Lang.Get("S.Tray.Open") };
            openItem.Click += (_, _) => ShowMain();
            var suspendItem = new NativeMenuItem
            {
                Header = Lang.Get("S.Tray.Suspend"),
                ToggleType = NativeMenuItemToggleType.CheckBox,
            };
            suspendItem.Click += (_, _) => SetSuspended(suspendItem.IsChecked == true);
            var exitItem = new NativeMenuItem { Header = Lang.Get("S.Tray.Exit") };
            exitItem.Click += (_, _) => ExitApp();
            menu.Items.Add(openItem);
            menu.Items.Add(suspendItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exitItem);
            _trayMenu = menu;
            _traySuspend = suspendItem;
            _tray = new TrayIcon { Menu = menu, ToolTipText = Lang.Get("S.Tray.Title"), IsVisible = true };
        }
        catch (Exception ex)
        {
            Log("托盘不可用（不影响功能）: " + ex.Message);
        }
    }

    private static NativeMenu? _trayMenu;
    private static NativeMenuItem? _traySuspend;

    public static void SetSuspended(bool suspended)
    {
        if (_traySuspend is not null)
            _traySuspend.IsChecked = suspended;
        Engine.SetSuspended(suspended);
    }

    public static void ShowMain()
    {
        _main?.Show();
        _main?.Activate();
    }

    /// <summary>配置导入：整体替换当前配置对象（界面动态读 App.Config，替换引用即生效）。</summary>
    public static void ReplaceConfig(TunerConfig imported) => Config = imported;

    public static void ExitApp()
    {
        ForceExit = true;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private static void ShutdownCleanup()
    {
        Log("—— 退出：停止引擎并还原音量 ——");
        // 退出看门狗：音频服务挂起时 8 秒硬退（与 WPF 版同源的防御，见 2026-10-24 退出卡死事件）
        var watchdog = Task.Run(async () =>
        {
            await Task.Delay(8000);
            Log("退出流程超时（8s），强制结束进程");
            Environment.Exit(0);
        });
        try
        {
            Engine?.Dispose();
            Log($"退出还原完成：{Engine.LastRestoreCount} 个会话");
        }
        catch (Exception ex)
        {
            Log("引擎停止异常: " + ex);
        }
        try { Monitor?.Dispose(); } catch { }
        _tray?.Dispose();
        _singleInstance?.Dispose();
    }
}
