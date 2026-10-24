// Linux 平台可行性探针（todo 15）：验证 PulseAudio 会话监控与音量控制的端到端链路。
// 用法:
//   LinuxProbe list                列出 sink_input 会话（进程/PID/corked/音量）与 sink 设备
//   LinuxProbe setvol <实例Id> <pct>  写会话音量并回读验证
//   LinuxProbe setmaster <pct>     写默认 sink 主音量并回读
// 环境：PULSE_SERVER（WSLg 默认 unix:/mnt/wslg/PulseServer）；先在 WSL 里放一段声音生成会话。

using Tuner.Core.Linux;

var mode = (args.FirstOrDefault() ?? "list").ToLowerInvariant();

var monitor = new PulseAudioSessionMonitor(100);
monitor.Diagnostics += m => Console.WriteLine("  [diag] " + m);
monitor.Start();

// 等连接与首轮列表（连接失败会以诊断消息报告）
Thread.Sleep(1500);

if (mode is "list" or "-l")
{
    Console.WriteLine($"默认设备: {monitor.DeviceName}（{_sinksDesc(monitor)}）");
    Console.WriteLine();
    var sessions = monitor.CurrentSnapshot;
    Console.WriteLine($"会话 {sessions.Count} 个：");
    foreach (var s in sessions)
        Console.WriteLine($"- {s.InstanceId}  进程={s.ProcessName}(pid {s.Pid})  状态={s.State}  音量={s.Volume * 100:F0}%  静音={s.Mute}");
    if (sessions.Count == 0)
        Console.WriteLine("（无 —— 在本机播放任意音频生成 sink_input，如 paplay xxx.wav）");
    return 0;
}

if (mode == "setvol" && args.Length >= 3)
{
    var id = args[1];
    var pct = Math.Clamp(float.Parse(args[2]) / 100f, 0f, 1f);
    Console.WriteLine($"写入 {id} → {pct * 100:F0}%");
    monitor.SetSessionVolume(id, pct);
    for (int i = 0; i < 20; i++)
    {
        Thread.Sleep(200);
        var s = monitor.CurrentSnapshot.FirstOrDefault(x => x.InstanceId == id);
        if (s is not null && Math.Abs(s.Volume - pct) < 0.02f)
        {
            Console.WriteLine($"✓ 回读生效：{s.ProcessName} 音量 {s.Volume * 100:F0}%");
            return 0;
        }
    }
    var last = monitor.CurrentSnapshot.FirstOrDefault(x => x.InstanceId == id);
    Console.WriteLine($"✗ 回读未生效：{(last is null ? "会话不存在" : $"仍为 {last.Volume * 100:F0}%")}");
    return 1;
}

if (mode == "setmaster" && args.Length >= 2)
{
    var pct = Math.Clamp(float.Parse(args[1]) / 100f, 0f, 1f);
    var before = monitor.MasterVolume;
    monitor.SetMasterVolume(pct);
    Thread.Sleep(1200);
    Console.WriteLine($"主音量 {before * 100:F0}% → 写 {pct * 100:F0}% → 回读 {monitor.MasterVolume * 100:F0}%");
    monitor.SetMasterVolume(before); // 恢复
    Thread.Sleep(600);
    Console.WriteLine("已恢复原主音量");
    return 0;
}

Console.Error.WriteLine("用法: LinuxProbe [list | setvol <实例Id> <pct> | setmaster <pct>]");
return 1;

static string _sinksDesc(PulseAudioSessionMonitor m)
{
    var sb = new System.Text.StringBuilder();
    foreach (var (id, name) in m.EnumerateRenderDevices())
        sb.Append($"\n  - {id} = {name}");
    return sb.ToString();
}
