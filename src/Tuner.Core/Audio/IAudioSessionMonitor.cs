namespace Tuner.Core.Audio;

/// <summary>
/// 平台音频会话监控抽象：默认输出设备上的会话枚举、峰值/音量状态与音量写入。
/// Windows 实现见 Tuner.Core.Windows（WASAPI/NAudio）；其他平台按此实现接入
/// （引擎与全部业务逻辑只依赖本接口）。
/// </summary>
public interface IAudioSessionMonitor : IDisposable
{
    /// <summary>周期采样完成（轮询线程触发；处理器不得阻塞）。</summary>
    event Action<IReadOnlyList<SoundSession>>? SamplesUpdated;

    /// <summary>监控层诊断消息（设备重绑、枚举异常等）。</summary>
    event Action<string>? Diagnostics;

    /// <summary>当前默认设备友好名（重绑时更新；未绑定为空串）。</summary>
    string DeviceName { get; }

    /// <summary>当前默认设备 Id。</summary>
    string DeviceId { get; }

    /// <summary>设备总峰值（0..1）。</summary>
    float DevicePeak { get; }

    /// <summary>设备主音量（0..1）。</summary>
    float MasterVolume { get; }

    /// <summary>设备主静音状态。</summary>
    bool MasterMuted { get; }

    /// <summary>最近一次采样快照（调用方不得修改或长期持有）。</summary>
    IReadOnlyList<SoundSession> CurrentSnapshot { get; }

    /// <summary>全部会话的音量控制句柄（含待写队列中的）。</summary>
    IReadOnlyList<ISessionVolumeControl> VolumeControls { get; }

    /// <summary>启动轮询线程（幂等）。</summary>
    void Start();

    /// <summary>写设备主音量（0..1）。</summary>
    void SetMasterVolume(float volume);

    /// <summary>写设备主静音。</summary>
    void SetMasterMute(bool mute);

    /// <summary>按会话 Id 写音量（0..1）；写不进当前设备时会话时静默失败。</summary>
    void SetSessionVolume(string instanceId, float volume);

    /// <summary>按会话 Id 写静音。</summary>
    void SetSessionMute(string instanceId, bool mute);

    /// <summary>该会话是否有尚未写入的音量更新（UI 侧防回写抖动用）。</summary>
    bool HasPendingVolumeWrite(string instanceId);

    /// <summary>取会话音量控制句柄；不存在返回 null。</summary>
    ISessionVolumeControl? TryGetControl(string instanceId);

    /// <summary>枚举全部渲染输出设备（Id → 友好名）。</summary>
    IReadOnlyDictionary<string, string> EnumerateRenderDevices();
}
