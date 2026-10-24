using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
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
    Console.WriteLine("  --selftest  内置闪避验收（免外部播放器）：触发压低/间隙保持/停止恢复/暂停闪避/退出还原，逐项断言，退出码=失败数");
    Console.WriteLine("  --interval  峰值轮询间隔（覆盖配置），默认取配置值（100ms）");
    Console.WriteLine("  --duration  运行指定秒数后自动退出，默认运行到 Ctrl+C");
    Console.WriteLine("  --setvol    调试用：把指定进程的所有会话音量设为给定百分比后退出");
    Console.WriteLine("  配置默认从 %APPDATA%\\Tuner\\config.json 加载；程序目录有 portable.marker 时改从该目录加载");
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
var selftestResults = new List<(string Name, bool Pass, string Detail)>();
float selftestAnchor = -1f;
TunerConfig config;
if (selftest)
{
    // 自测使用内嵌配置：触发=本进程播放，目标=系统提示音会话（双会话，免外部播放器）
    configPath = null;
    config = new TunerConfig();
    config.Groups.Add(new AppGroupConfig { Id = "trig", Name = "触发", ProcessNames = { selfName } });
    config.Groups.Add(new AppGroupConfig { Id = "sys", Name = "系统提示音", ProcessNames = { "system sounds" } });
    config.Groups.Add(new AppGroupConfig { Id = "default", Name = "其他", IsDefault = true });
    config.Rules.Add(new DuckingRuleConfig { Id = "r1", TriggerGroupId = "trig", TargetGroupId = "sys", TargetVolumePercent = 30, Priority = 10, DetectionMode = "state" });
    // 裸构造的 Settings 渐变为 0/0（产品默认 400/1200 由 ConfigStore.CreateDefault 注入），这里显式对齐
    config.Settings.FadeOutDurationMs = 400;
    config.Settings.FadeInDurationMs = 1200;
}
else
{
    // 仅从默认位置（%APPDATA%\Tuner\config.json）加载配置；
    // 手动复现测试时把 config/test-ducking.json 复制过去即可
    var defaultConfigPath = File.Exists(ConfigStore.ActivePath) ? ConfigStore.ActivePath : null;
    config = defaultConfigPath is not null ? ConfigStore.Load(defaultConfigPath) : ConfigStore.CreateDefault();
    configPath = defaultConfigPath;
}
if (intervalMs < 0)
    intervalMs = Math.Max(10, config.Settings.PollIntervalMs);

Console.WriteLine($"Tuner 控制台原型 v{version} — 音频会话识别 + 分组闪避引擎{(selftest ? "（自测模式）" : "")}");
Console.WriteLine(
    $"配置: {(selftest ? "内嵌测试配置" : configPath ?? "（空配置，无闪避规则）")} | 轮询 {intervalMs}ms | 迟滞 {config.Settings.ActivePeakThreshold:P0}/{config.Settings.InactivePeakThreshold:P0} | 渐变 出{config.Settings.FadeOutDurationMs}/入{config.Settings.FadeInDurationMs}ms | Ctrl+C 退出");
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
var engine = new DuckingEngine(monitor, config, null, new Tuner.Core.Media.MediaSessionTracker()); // 释放顺序：先停引擎（还原音量）再停监控
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

float SysVol() => monitor.CurrentSnapshot.FirstOrDefault(s => s.IsSystemSounds)?.Volume ?? -1f;

void SelfCheck(string name, bool pass, string detail)
{
    selftestResults.Add((name, pass, detail));
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}  [{detail}]");
}

void RunSelftest()
{
    var wav = new[] { "Windows Balloon.wav", "Windows Ding.wav", "tada.wav", "notify.wav" }
        .Select(f => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", f))
        .FirstOrDefault(File.Exists);
    if (wav is null)
    {
        SelfCheck("找到触发源 wav", false, "Windows Media 目录无可用 wav");
        return;
    }
    Console.WriteLine("[自测] 触发=本进程 WasapiOut 循环播放，目标=系统提示音会话（双会话，免外部播放器）");
    Console.WriteLine("[自测] 时间线：压低 → 间隙保持 → 停止恢复 → 再压低 → 暂停闪避即时还原 → 恢复再压低 → 退出还原，全程约 24s");
    Console.WriteLine();

    // 预热目标会话：PlaySound（系统别名）。注意：本进程调用 PlaySound 时声音计入本进程会话，
    // 会让"触发"组短暂出声 → 预热即引发一轮闪避，须等其完全解除（宽限+恢复渐变）后再读锚点。
    for (int i = 0; i < 3 && SysVol() < 0f; i++)
    {
        WinMm.PlaySound("SystemExclamation", IntPtr.Zero, 0x10001); // SND_ALIAS|SND_ASYNC
        Thread.Sleep(600);
    }
    for (int i = 0; i < 16 && SysVol() < 0f; i++)
        Thread.Sleep(250);
    var sys = monitor.CurrentSnapshot.FirstOrDefault(s => s.IsSystemSounds);
    if (sys is null)
    {
        SelfCheck("系统提示音会话可用", false, "PlaySound 预热后未出现会话（系统提示音被禁用？）");
        return;
    }
    string sysId = sys.InstanceId;
    bool Engaged() => engine.CurrentStates.Any(s => s.InstanceId == sysId && s.Ducked);
    for (int i = 0; i < 30 && Engaged(); i++)
        Thread.Sleep(300);
    Thread.Sleep(1500); // 等恢复渐变走完，锚点稳定
    selftestAnchor = SysVol();
    if (selftestAnchor < 0f)
    {
        SelfCheck("系统提示音会话可用", false, "预热后目标会话消失");
        return;
    }
    Console.WriteLine($"[自测] 目标会话就绪，锚点音量 {selftestAnchor:P0}");

    void TriggerOn()
    {
        selfTestOutput = new WasapiOut(AudioClientShareMode.Shared, 100);
        selfTestOutput.Init(new LoopStream(new WaveFileReader(wav)));
        selfTestOutput.Play();
    }

    TriggerOn();
    Thread.Sleep(2000);
    SelfCheck("触发出声后目标压至 30%", SysVol() is >= 0.24f and <= 0.45f, $"vol={SysVol():P0}");

    selfTestOutput!.Pause();
    Thread.Sleep(5000);
    SelfCheck("歌曲间隙保持（流打开暂停不恢复）", SysVol() is >= 0.24f and <= 0.45f, $"vol={SysVol():P0}");
    selfTestOutput.Play();
    Thread.Sleep(2000);

    selfTestOutput.Stop();
    selfTestOutput.Dispose();
    selfTestOutput = null;
    Thread.Sleep(4500); // 关流短宽限 1.5s + 恢复渐变
    SelfCheck("触发停止后恢复锚点", Math.Abs(SysVol() - selftestAnchor) <= 0.06f, $"vol={SysVol():P0}，锚点 {selftestAnchor:P0}");

    TriggerOn();
    Thread.Sleep(2000);
    SelfCheck("再触发重新压低", SysVol() is >= 0.24f and <= 0.45f, $"vol={SysVol():P0}");

    engine.SetSuspended(true);
    Thread.Sleep(700);
    SelfCheck("暂停闪避立即还原锚点", Math.Abs(SysVol() - selftestAnchor) <= 0.07f, $"vol={SysVol():P0}");

    engine.SetSuspended(false);
    Thread.Sleep(2500);
    SelfCheck("恢复闪避引擎后重新压低", SysVol() is >= 0.24f and <= 0.45f, $"vol={SysVol():P0}");
    // 结束时保持触发出声：退出路径的 Dispose 在"压低中"执行，检验退出还原链路
}

if (selftest)
{
    RunSelftest();
}
else
{
    cts.Token.WaitHandle.WaitOne();
}
Console.WriteLine("正在退出并还原音量…");
engine.Dispose();
if (selftest)
{
    // 退出还原验证：上一段结束时仍处于压低中，Dispose 应把目标会话还原到锚点
    Thread.Sleep(500); // 等监控回读一次音量
    float v = SysVol();
    if (selftestAnchor >= 0f && v >= 0f)
        SelfCheck("退出时还原锚点", Math.Abs(v - selftestAnchor) <= 0.07f, $"vol={v:P0}，锚点 {selftestAnchor:P0}");
    try { selfTestOutput?.Dispose(); } catch { } // 会话随流关闭，放在还原验证之后
    int failed = selftestResults.Count(r => !r.Pass);
    Console.WriteLine();
    Console.WriteLine($"===== 自测结果 {selftestResults.Count - failed}/{selftestResults.Count} 通过 =====");
    try { monitor.Dispose(); } catch { }
    Console.WriteLine("已退出。");
    return failed > 0 ? 1 : 0;
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

/// <summary>系统提示音播放（P/Invoke，进程内 API 调用，非外部命令）：预热"系统提示音"音频会话用。</summary>
internal static class WinMm
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    internal static extern bool PlaySound(string sound, IntPtr hmod, int flags);
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
