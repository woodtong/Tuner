using Tuner.Core.Audio;
using Windows.Media.Control;

// SMTC（系统媒体传输控制）会话控制可行性验证工具 —— 实验分支 feat/media-pause-probe
// 用法:
//   PauseProbe list             列出当前系统媒体会话（含状态、标题、AUMID）
//   PauseProbe test <关键字>    对匹配会话执行 播放→观察→暂停→观察 的完整验证，并恢复初始状态
//
// 验证目标：能否通过 SMTC 让播放器"真启停"（暂停 = 停在当前位置，而非无声后台播放），
// 并用 Tuner.Core 的音频峰值监控独立确认声音确实停了、又能恢复。

var mode = (args.FirstOrDefault() ?? "list").ToLowerInvariant();
var keyword = args.Skip(1).FirstOrDefault() ?? "";

var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

GlobalSystemMediaTransportControlsSession? FindSession()
{
    var sessions = manager.GetSessions();
    if (keyword.Length == 0)
        return sessions.FirstOrDefault();
    return sessions.FirstOrDefault(s =>
        s.SourceAppUserModelId?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false);
}

static string Zh(GlobalSystemMediaTransportControlsSessionPlaybackStatus st) => st switch
{
    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => "已关闭",
    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => "已就绪",
    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => "切换中",
    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => "已停止",
    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "播放中",
    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "已暂停",
    _ => st.ToString(),
};

if (mode is "list" or "-l")
{
    var sessions = manager.GetSessions();
    Console.WriteLine($"系统媒体会话 {sessions.Count} 个：");
    foreach (var s in sessions)
    {
        string title = "";
        try { title = (await s.TryGetMediaPropertiesAsync()).Title ?? ""; } catch { /* 个别会话不提供 */ }
        Console.WriteLine($"- AUMID={s.SourceAppUserModelId} 状态={Zh(s.GetPlaybackInfo().PlaybackStatus),-4} 标题={title}");
    }
    if (sessions.Count == 0)
        Console.WriteLine("（无 —— 当前没有应用注册系统媒体会话；播放器需集成 SMTC 才会出现）");
    return 0;
}

if (mode == "test")
{
    if (keyword.Length == 0)
    {
        Console.Error.WriteLine("用法: PauseProbe test <关键字>（如 qqmusic）");
        return 1;
    }
    var session = FindSession();
    if (session is null)
    {
        Console.Error.WriteLine($"未找到匹配「{keyword}」的媒体会话，先用 list 查看全部会话。");
        return 1;
    }

    var before = session.GetPlaybackInfo().PlaybackStatus;
    string mediaTitle = "";
    try { mediaTitle = (await session.TryGetMediaPropertiesAsync()).Title ?? ""; } catch { }
    Console.WriteLine($"目标: AUMID={session.SourceAppUserModelId} | 初始状态={Zh(before)} | 标题={mediaTitle}");
    Console.WriteLine();

    // 音频峰值 + WASAPI 会话状态观察器（与 SMTC 状态对照，验证"播放/暂停时注册状态如何变化"）
    var gate = new object();
    var phaseMax = new Dictionary<string, (float Peak, string State)>();
    using var monitor = new AudioSessionMonitor(100);
    monitor.SamplesUpdated += sessions =>
    {
        lock (gate)
            foreach (var s in sessions)
            {
                var cur = phaseMax.GetValueOrDefault(s.ProcessName);
                phaseMax[s.ProcessName] = (Math.Max(cur.Peak, s.Peak), s.State.ToString());
            }
    };
    monitor.Start();

    async Task Observe(string label, int seconds)
    {
        for (int i = 0; i < seconds * 2; i++)
        {
            await Task.Delay(500);
            string line;
            lock (gate)
            {
                // 每个观察窗独立统计（窗口内最大峰值 + WASAPI 会话状态），避免旧值"粘"住误导
                line = string.Join("  ", phaseMax
                    .Where(kv => kv.Value.Peak > 0.01f || kv.Value.State == "Active")
                    .OrderByDescending(kv => kv.Value.Peak)
                    .Take(4)
                    .Select(kv => $"{kv.Key}={kv.Value.Peak * 100:F0}%({kv.Value.State})"));
                phaseMax.Clear();
            }
            Console.WriteLine($"  [{label} +{(i + 1) * 0.5:F1}s] {(line.Length > 0 ? line : "（无音频会话）")}");
        }
    }

    // 1) 播放基线（原本暂停则短暂恢复，用于对比）
    var weStarted = false;
    if (before != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
    {
        var ok = await session.TryPlayAsync();
        Console.WriteLine($"TryPlay 返回 {ok}，观察 4s：");
        weStarted = true;
        await Observe("播放", 4);
    }
    else
    {
        Console.WriteLine("目标正在播放，先观察 3s 基线：");
        await Observe("播放", 3);
    }

    // 2) 暂停 —— 实验核心：声音是否真的停住
    var okPause = await session.TryPauseAsync();
    Console.WriteLine($"TryPause 返回 {okPause}，观察 4s：");
    await Observe("暂停", 4);
    var afterPause = session.GetPlaybackInfo().PlaybackStatus;
    Console.WriteLine($"暂停后状态: {Zh(afterPause)}");

    // 3) 恢复初始状态（结束态 = 开始态）
    if (before == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
    {
        var ok = await session.TryPlayAsync();
        Console.WriteLine($"已恢复播放（TryPlay={ok}）");
        await Observe("恢复", 2);
    }

    Console.WriteLine();
    Console.WriteLine("判定：暂停观察阶段目标进程峰值归零且状态=已暂停、播放观察阶段有峰值 → SMTC 真启停可行。");
    return 0;
}

Console.Error.WriteLine($"未知模式: {mode}（支持 list / test）");
return 1;
