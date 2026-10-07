using System.Diagnostics;
using System.Reflection;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Tuner.Core.Audio;
using Tuner.Core.Config;
using Tuner.Core.Ducking;

bool logMode = args.Contains("--log") || args.Contains("-l");
bool selftest = args.Contains("--selftest");
int intervalMs = -1;
int durationSec = 0;
string? configPath = null;
string? setvolProcess = null;
float setvolPercent = 0;
for (int i = 0; i + 1 < args.Length; i++)
{
    if (args[i] is "--interval" or "-i")
        int.TryParse(args[i + 1], out intervalMs);
    if (args[i] is "--duration" or "-d")
        int.TryParse(args[i + 1], out durationSec);
    if (args[i] is "--setvol" && i + 2 < args.Length)
    {
        setvolProcess = args[i + 1];
        float.TryParse(args[i + 2], out setvolPercent);
    }
}
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("用法: Tuner.Console [--log|--selftest] [--interval <毫秒>] [--duration <秒>] [--setvol <进程名> <百分比>]");
    Console.WriteLine("  --log       逐行滚动输出（适合重定向/记录），默认为原地刷新的实时表格");
    Console.WriteLine("  --selftest  内置闪避验收测试：自动拉起测试音源，验证降/恢复/创建初始化/退出还原");
    Console.WriteLine("  --interval  峰值轮询间隔（覆盖配置），默认取配置值（100ms）");
    Console.WriteLine("  --duration  运行指定秒数后自动退出，默认运行到 Ctrl+C");
    Console.WriteLine("  --setvol    调试用：把指定进程的所有会话音量设为给定百分比后退出");
    Console.WriteLine("  配置从 %APPDATA%\\Tuner\\config.json 加载（不存在则用空配置，不闪避）");
    return 0;
}

try { Console.OutputEncoding = Encoding.UTF8; }
catch { /* 输出被重定向等场景下可能失败，忽略 */ }

if (!logMode && Console.IsOutputRedirected)
{
    logMode = true;
    Console.Error.WriteLine("(输出已重定向，自动切换为日志模式)");
}

var version = Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "dev";

IWavePlayer? selfTestOutput = null;
string selfName = Process.GetCurrentProcess().ProcessName;
TunerConfig config;
if (selftest)
{
    // 自测使用内嵌配置（与 config/test-ducking.json 等价）
    configPath = null;
    config = new TunerConfig();
    config.Groups.Add(new AppGroupConfig { Id = "music", Name = "音乐", ProcessNames = { "qqmusic" } });
    config.Groups.Add(new AppGroupConfig { Id = "aux", Name = "测试目标", ProcessNames = { selfName } });
    config.Groups.Add(new AppGroupConfig { Id = "default", Name = "其他", IsDefault = true });
    config.Rules.Add(new DuckingRuleConfig { Id = "r1", TriggerGroupId = "aux", TargetGroupId = "music", TargetVolumePercent = 20, Priority = 10 });
    config.Rules.Add(new DuckingRuleConfig { Id = "r2", TriggerGroupId = "music", TargetGroupId = "aux", TargetVolumePercent = 50, Priority = 5 });
    config.Settings.InactiveHoldMs = 500; // 盖过测试 wav 循环间隙（~350ms），排除已知间隙导致的恢复循环
    durationSec = 24;
}
else
{
    // 仅从默认位置（%APPDATA%\Tuner\config.json）加载配置；
    // 手动复现测试时把 config/test-ducking.json 复制过去即可
    var defaultConfigPath = File.Exists(ConfigStore.DefaultPath) ? ConfigStore.DefaultPath : null;
    config = defaultConfigPath is not null ? ConfigStore.Load(defaultConfigPath) : ConfigStore.CreateDefault();
    configPath = defaultConfigPath;
}
if (intervalMs < 0)
    intervalMs = Math.Max(10, config.Settings.PollIntervalMs);

Console.WriteLine($"Tuner 控制台原型 v{version} — 音频会话识别 + 分组闪避引擎{(selftest ? "（自测模式）" : "")}");
Console.WriteLine(
    $"配置: {(selftest ? "内嵌测试配置" : configPath ?? "（空配置，无闪避规则）")} | 轮询 {intervalMs}ms | 迟滞 {config.Settings.ActivePeakThreshold:P0}/{config.Settings.InactivePeakThreshold:P0} | 渐变 {config.Settings.FadeDurationMs}ms | Ctrl+C 退出");
Console.WriteLine();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
if (durationSec > 0)
    cts.CancelAfter(TimeSpan.FromSeconds(durationSec));

var sync = new object();
var monitor = new AudioSessionMonitor(intervalMs);
var engine = new DuckingEngine(monitor, config); // 释放顺序：先停引擎（还原音量）再停监控
monitor.Diagnostics += msg => { lock (sync) Console.Error.WriteLine($"[监控诊断] {msg}"); };
engine.Diagnostics += msg => { lock (sync) Console.Error.WriteLine($"[引擎诊断] {msg}"); };

try
{
    monitor.Start();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"启动失败：无法访问默认播放设备（{ex.Message}）");
    return 1;
}

if (args.Contains("--smtc-status"))
{
    // 诊断：列出系统媒体会话（SMTC）及其播放状态（MediaSessionTracker 的实际视角）
    using var tracker = new Tuner.Core.Media.MediaSessionTracker();
    tracker.Start();
    Thread.Sleep(1200); // 等首轮轮询
    var snap = tracker.Snapshot();
    Console.WriteLine($"系统媒体会话 {snap.Count} 个：");
    foreach (var (aumid, status) in snap)
        Console.WriteLine($"- {aumid} → {status}");
    if (snap.Count == 0)
        Console.WriteLine("（无 —— 播放器需集成 SMTC 才会出现）");
    return 0;
}

if (setvolProcess is not null)
{
    Thread.Sleep(800); // 等首轮会话枚举完成
    var controls = monitor.VolumeControls.ToDictionary(c => c.InstanceId);
    var target = ConfigStore.NormalizeProcessName(setvolProcess);
    var matched = monitor.CurrentSnapshot.Where(s => ConfigStore.NormalizeProcessName(s.ProcessName) == target).ToList();
    foreach (var s in matched)
    {
        controls[s.InstanceId].SetVolume(Math.Clamp(setvolPercent / 100f, 0f, 1f));
        Console.WriteLine($"{s.ProcessName}(pid={s.Pid}) → {setvolPercent}%");
    }
    if (matched.Count == 0)
        Console.Error.WriteLine($"未找到进程 {setvolProcess} 的音频会话。");
    monitor.Dispose();
    return matched.Count > 0 ? 0 : 1;
}

engine.Start();

// 设备在轮询线程上异步绑定，等首轮完成再显示设备名
for (int i = 0; i < 20 && monitor.DeviceName.Length == 0; i++)
    Thread.Sleep(100);
Console.WriteLine($"默认播放设备: {monitor.DeviceName}");
Console.WriteLine();

if (selftest)
{
    // 时间线：+3s 起响 4s → 静默 4s → 再响 4s → 保持静默至 +20s 退出。
    // 验证点：创建即初始化 50%、音乐降 20%、触发停止后恢复 61%、二次降、退出还原 100%。
    if (!monitor.CurrentSnapshot.Any(s => s.ProcessName == "qqmusic" && s.Peak > 0.02f))
        Console.WriteLine("⚠ 自测前提：QQMusic 应正在播放，否则部分验证点无法触发。");
    var wav = new[] { "Windows Balloon.wav", "Windows Ding.wav", "tada.wav", "notify.wav" }
        .Select(f => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", f))
        .FirstOrDefault(File.Exists);
    if (wav is null)
    {
        Console.Error.WriteLine("自测失败：找不到系统提示音 wav。");
        engine.Dispose();
        monitor.Dispose();
        return 1;
    }
    Console.WriteLine($"[自测] +3s 起响（{Path.GetFileName(wav)}，进程内播放）：响4s → 暂停6s(流保持打开，模拟歌间间隙) → 再响4s → 停止，全程约 24s");
    Console.WriteLine("[自测] 预期：暂停期间闪避保持不恢复；停止后约 1.5s 恢复；退出时还原音量");
    Console.WriteLine();
    // 时间线在进程内驱动：+3s 起响 4s → 静默 4s → 再响 4s → 静默至 +20s 退出。
    // 测试会话归属本进程（与外部音源等价地验证"新会话动态出现"），且不产生任何外部命令执行
    _ = Task.Run(async () =>
    {
        try
        {
            // 时间线：+3s 起响 4s → +7s 暂停 6s（音频流保持打开，模拟歌曲间隙）
            //        → +13s 恢复播放 4s → +17s 停止（流关闭）→ +24s 退出。
            // 预期：暂停期间闪避保持（无"恢复"事件）；停止后约 1.5s（关流宽限）恢复。
            await Task.Delay(3000);
            var loop = new LoopStream(new WaveFileReader(wav));
            selfTestOutput = new WasapiOut(AudioClientShareMode.Shared, 100);
            selfTestOutput.Init(loop);
            selfTestOutput.Play();
            await Task.Delay(4000);
            selfTestOutput.Pause();
            await Task.Delay(6000);
            loop.Position = 0;
            selfTestOutput.Play();
            await Task.Delay(4000);
            selfTestOutput.Stop();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[自测] 播放失败: {ex.Message}");
        }
    });
}

if (logMode)
{
    // 闪避/出声状态切换的过程日志
    var prev = new Dictionary<string, (bool Speaking, bool Ducked)>();
    engine.EngineStateChanged += () =>
    {
        var states = engine.CurrentStates;
        var lines = new List<string>();
        foreach (var s in states)
        {
            var (pSpeaking, pDucked) = prev.GetValueOrDefault(s.InstanceId, (false, false));
            if (s.Speaking && !pSpeaking)
                lines.Add($"{s.ProcessName}({s.GroupName}) 开始出声");
            if (!s.Speaking && pSpeaking)
                lines.Add($"{s.ProcessName}({s.GroupName}) 停止出声");
            if (s.Ducked && !pDucked)
                lines.Add($"★ {s.ProcessName}({s.GroupName}) 闪避 → {(int)Math.Round((s.TargetVolume ?? 0) * 100)}%");
            if (!s.Ducked && pDucked)
                lines.Add($"☆ {s.ProcessName}({s.GroupName}) 恢复 → 原音量 {(int)Math.Round(s.OriginalVolume * 100)}%");
            prev[s.InstanceId] = (s.Speaking, s.Ducked);
        }
        foreach (var gone in prev.Keys.Where(id => states.All(s => s.InstanceId != id)).ToList())
            prev.Remove(gone);

        if (lines.Count > 0)
            lock (sync)
                foreach (var line in lines)
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} ▶ {line}");
    };

    // 周期状态输出（含音量实际读数，验证写入生效）
    int tickNo = 0;
    monitor.SamplesUpdated += sessions =>
    {
        if (++tickNo % 2 != 0)
            return;
        var engineStates = engine.CurrentStates.ToDictionary(s => s.InstanceId);
        lock (sync)
        {
            var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
            foreach (var s in ConsoleView.Sort(sessions))
            {
                var e = engineStates.GetValueOrDefault(s.InstanceId);
                string duck = e is { Ducked: true } ? $" ▶闪避→{(int)Math.Round((e.TargetVolume ?? 0) * 100)}%" : "";
                Console.WriteLine(
                    $"{stamp} | {ConsoleView.Fit(s.ProcessName, 16),-16} pid={s.Pid,-6} [{ConsoleView.PadCell(e?.GroupName ?? "—", 8)}] {(e?.Speaking == true ? "出声" : "  ")} peak={s.Peak * 100,5:F1}% vol={(int)Math.Round(s.Volume * 100),3}%{duck}");
            }
            if (sessions.Count == 0)
                Console.WriteLine($"{stamp} | （暂无音频会话）");
        }
    };
}
else
{
    var table = new LiveSessionTable();
    monitor.SamplesUpdated += _ => table.Render(monitor.DeviceName, monitor.DevicePeak, monitor.CurrentSnapshot, engine.CurrentStates);
}

cts.Token.WaitHandle.WaitOne();
Console.WriteLine("正在退出并还原音量…");
engine.Dispose();
if (selftest)
{
    // 退出还原验证：自测音源会话若仍存在，其音量应已被引擎还原为 100%
    Thread.Sleep(400); // 等监控回读一次音量
    var ps = monitor.CurrentSnapshot.FirstOrDefault(s => s.ProcessName == selfName);
    Console.WriteLine(ps is null
        ? "[退出还原验证] 自测音源会话已不存在（还原对象随之终结）"
        : $"[退出还原验证] {selfName} 会话音量 = {(int)Math.Round(ps.Volume * 100)}%（预期 100%）");
    try { selfTestOutput?.Dispose(); } catch { } // 会话随流关闭，放在还原验证之后
}
try { monitor.Dispose(); } catch { }
Console.WriteLine("已退出。");
return 0;

internal static class ConsoleView
{
    public static IEnumerable<SoundSession> Sort(IReadOnlyList<SoundSession> sessions) =>
        sessions
            .OrderByDescending(s => s.State == SessionState.Active)
            .ThenBy(s => s.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Pid);

    public static string StateZh(SessionState state) => state switch
    {
        SessionState.Active => "出声中",
        SessionState.Inactive => "空闲",
        _ => "已过期",
    };

    public static string Fit(string text, int maxWidth)
    {
        int w = 0;
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            int cw = CharWidth(ch);
            if (w + cw > maxWidth - 1)
                break;
            sb.Append(ch);
            w += cw;
        }
        if (w < DisplayWidth(text))
            sb.Append('…');
        return sb.ToString();
    }

    /// <summary>按显示宽度补齐单元格（中日韩字符按 2 列计）。</summary>
    public static string PadCell(string text, int width)
    {
        var fitted = Fit(text, width);
        return fitted + new string(' ', Math.Max(0, width - DisplayWidth(fitted)));
    }

    public static string Row(SoundSession s, DuckingEngine.SessionState? e)
    {
        const int slots = 10;
        int filled = (int)Math.Round(Math.Clamp(s.Peak, 0f, 1f) * slots);
        string bar = new string('█', filled) + new string('·', slots - filled);
        string group = ConsoleView.PadCell(e?.GroupName ?? "—", 8);
        string speaking = e?.Speaking == true ? "出声" : "  ";
        string duck = e is { Ducked: true } ? $"闪避→{(int)Math.Round((e.TargetVolume ?? 0) * 100)}%" : "";
        return $"{Fit(s.ProcessName, 14),-14}  {s.Pid,-6} {group} {speaking}  {bar} {s.Peak * 100,5:F1}%  {(int)Math.Round(s.Volume * 100),3}%  {duck}";
    }

    public static int DisplayWidth(string text)
    {
        int w = 0;
        foreach (var ch in text)
            w += CharWidth(ch);
        return w;
    }

    // 近似：CJK 及全角字符按 2 列计，其余按 1 列
    private static int CharWidth(char ch) => ch >= 0x2E80 ? 2 : 1;
}

/// <summary>循环播放包装：读到结尾自动回到起点（自测音源用）。</summary>
internal sealed class LoopStream : WaveStream
{
    private readonly WaveStream _source;

    public LoopStream(WaveStream source) => _source = source;

    public override WaveFormat WaveFormat => _source.WaveFormat;

    public override long Length => _source.Length;

    public override long Position
    {
        get => _source.Position;
        set => _source.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = _source.Read(buffer, offset + total, count - total);
            if (read == 0)
            {
                if (_source.Position == 0)
                    break; // 空源防死循环
                _source.Position = 0;
                continue;
            }
            total += read;
        }
        return total;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _source.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>实时表格视图：在固定区域原地重绘，窗口异常时自动回退到追加打印。</summary>
internal sealed class LiveSessionTable
{
    private readonly object _sync = new();
    private int _top = -1;
    private int _rows;

    public void Render(
        string deviceName, float devicePeak, IReadOnlyList<SoundSession> sessions,
        IReadOnlyList<DuckingEngine.SessionState> engineStates)
    {
        lock (_sync)
        {
            var byId = engineStates.ToDictionary(s => s.InstanceId);
            var ordered = ConsoleView.Sort(sessions).ToList();
            var lines = new List<string>
            {
                $"设备: {deviceName}    会话数: {ordered.Count}    {DateTime.Now:HH:mm:ss}",
                new string('─', 84),
                "进程             PID     分组        出声  峰值电平   峰值%  音量%  闪避",
            };
            lines.AddRange(ordered.Select(s => ConsoleView.Row(s, byId.GetValueOrDefault(s.InstanceId))));
            if (ordered.Count == 0)
                lines.Add("（暂无音频会话 — 让任意应用出声后自动出现）");
            lines.Add($"设备总峰值 {devicePeak * 100,5:F1}%    Ctrl+C 退出");

            try
            {
                int width = Math.Max(90, Console.WindowWidth - 1);
                var display = lines.Select(l => PadErase(l, width)).ToList();

                if (_top < 0)
                {
                    _top = Console.CursorTop;
                    foreach (var l in display)
                        Console.WriteLine(l);
                    _rows = display.Count;
                }
                else
                {
                    Console.SetCursorPosition(0, _top);
                    foreach (var l in display)
                        Console.WriteLine(l);
                    for (int i = display.Count; i < _rows; i++)
                        Console.WriteLine(new string(' ', width));
                    if (display.Count < _rows)
                        Console.SetCursorPosition(0, _top + display.Count);
                    _rows = display.Count;
                }
            }
            catch
            {
                // 光标定位失败（窗口过小或内容被滚走）：重置锚点，下个周期重新打印
                _top = -1;
            }
        }
    }

    private static string PadErase(string line, int width)
    {
        int w = 0;
        var sb = new StringBuilder();
        foreach (var ch in line)
        {
            int cw = ch >= 0x2E80 ? 2 : 1;
            if (w + cw > width)
                break;
            sb.Append(ch);
            w += cw;
        }
        while (w < width)
        {
            sb.Append(' ');
            w++;
        }
        return sb.ToString();
    }
}
