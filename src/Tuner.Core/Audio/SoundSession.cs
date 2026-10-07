namespace Tuner.Core.Audio;

/// <summary>音频会话状态（对应 Windows AudioSessionState）。</summary>
public enum SessionState
{
    Inactive,
    Active,
    Expired,
}

/// <summary>一个音频会话的快照视图，由 AudioSessionMonitor 持有并持续更新。</summary>
public sealed class SoundSession
{
    /// <summary>会话实例标识（跨枚举稳定的唯一键）。</summary>
    public required string InstanceId { get; init; }

    /// <summary>会话归属进程的 PID（System Sounds 会话为 0）。</summary>
    public required uint Pid { get; init; }

    public required string ProcessName { get; set; }

    public required bool IsSystemSounds { get; init; }

    public SessionState State { get; internal set; }

    /// <summary>会话音量（0..1，即音量混合器中该应用的音量）。</summary>
    public float Volume { get; internal set; }

    /// <summary>最近一次轮询的峰值电平（0..1）。</summary>
    public float Peak { get; internal set; }
}
