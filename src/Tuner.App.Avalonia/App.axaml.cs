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
        // 崩溃捕获先行：后台线程未处理异常/未观察任务异常在 .NET 默认直接终止进程（SIGABRT，不留托管日志），
        // 挂钩后至少在 app.log 留下完整堆栈（2026-10-24 Linux 切语言 SIGABRT 事件的直接对策）
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("致命（AppDomain 未处理异常, 终止=" + e.IsTerminating + "）: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Log("未观察任务异常: " + e.Exception);

        _singleInstance = new Mutex(true, "Tuner-SingleInstance", out var createdNew);
        // 截图/取证模式允许与运行中的实例共存（引擎已隔离为空规则，只读会话）
        var bypass = ((ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Args ?? Array.Empty<string>())
            .Any(a => a is "--screenshot" or "--langtest");
        if (!createdNew && !bypass)
        {
            Environment.Exit(0); // 已有实例：直接退出（托盘常驻工具，多开会双份闪避）
        }

        Config = ConfigStore.Load(ConfigStore.ActivePath);
        Lang.Apply(Lang.Resolve(Config.Settings.Language));
        {
            // 开发取证参数：--lang <code> / --appearance / --accent / --fontscale / --density / --radius（仅内存，不落盘）
            var args = (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Args
                       ?? Array.Empty<string>();
            var li = Array.IndexOf(args, "--lang");
            if (li >= 0 && li + 1 < args.Length)
                Lang.Apply(args[li + 1]);
            ApplyDevAppearanceArgs(args);
        }
        Appearance.Apply(Config.Appearance); // 外观：MainWindow 创建前装载（主题变体 + 令牌覆写一次到位）
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

        var cmdArgs = (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Args
                      ?? Array.Empty<string>();
        if (cmdArgs.Contains("--screenshot"))
        {
            // 截图模式：引擎隔离为空规则（只读会话、绝不写音量），自动遍历页签渲染 PNG 后退出
            try { Engine.ApplyConfig(ConfigStore.CreateDefault()); } catch { }
            var idx = Array.IndexOf(cmdArgs, "--shot-dir");
            var dir = idx >= 0 && idx + 1 < cmdArgs.Length ? cmdArgs[idx + 1] : "ui-preview-avalonia";
            var sIdx = Array.IndexOf(cmdArgs, "--shot-size");
            if (sIdx >= 0 && sIdx + 1 < cmdArgs.Length)
            {
                var parts = cmdArgs[sIdx + 1].Split('x');
                if (parts.Length == 2
                    && double.TryParse(parts[0], out var w) && double.TryParse(parts[1], out var h))
                {
                    _main.Width = w;
                    _main.Height = h;
                }
            }
            Log("截图模式：输出到 " + System.IO.Path.GetFullPath(dir));
            var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _ = _main.CaptureAndExitAsync(dir);
            };
            timer.Start();
        }

        if ((Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.Args is { } appArgs && appArgs.Contains("--langtest"))
        {
            // 语言切换自动化复现（诊断模式）：2.5s 后执行切换序列逐步打点，存活 4s 后干净退出
            Log("[langtest] 模式启动");
            var t1 = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            t1.Tick += (_, _) =>
            {
                t1.Stop();
                try
                {
                    _main.RunLangTest();
                }
                catch (Exception ex)
                {
                    Log("[langtest] 外层异常: " + ex);
                }
                var t2 = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                t2.Tick += (_, _) =>
                {
                    t2.Stop();
                    Log("[langtest] 存活确认，退出");
                    ForceExit = true;
                    (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                };
                t2.Start();
            };
            t1.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>开发取证参数（--appearance/--accent/...）是否生效。生效时禁止配置落盘（同 WPF 端防污染挡板）。</summary>
    public static bool DevAppearanceActive { get; private set; }

    /// <summary>开发取证参数（外观）：与 WPF 端同参，截图矩阵取证用，不落盘。</summary>
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
            TrayAvailable = true;
        }
        catch (Exception ex)
        {
            Log("托盘不可用（不影响功能）: " + ex.Message);
        }
    }

    /// <summary>托盘是否可用。不可用（如 WSLg 无系统托盘）时关窗＝退出，不做无托盘的后台驻留。</summary>
    public static bool TrayAvailable { get; private set; }

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
