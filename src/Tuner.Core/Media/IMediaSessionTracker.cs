namespace Tuner.Core.Media;

/// <summary>
/// 系统媒体会话跟踪抽象："进程名 → 是否正在播放"查询（应用自报的播放事实）。
/// Windows 实现为 SMTC（Tuner.Core.Windows）；Linux 可接 MPRIS；无实现时传 null，
/// 引擎自动退化为会话 Active 兜底判定。
/// </summary>
public interface IMediaSessionTracker : IDisposable
{
    void Start();

    /// <summary>进程名是否正在播放；null = 无该应用的媒体会话信号（调用方自行兜底）。</summary>
    bool? IsPlaying(string processName);

    /// <summary>全部已知媒体会话快照（AUMID/标识 → 状态描述，诊断展示用）。</summary>
    IReadOnlyDictionary<string, string> Snapshot();
}
