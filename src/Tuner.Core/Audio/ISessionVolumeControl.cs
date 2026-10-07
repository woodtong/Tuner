namespace Tuner.Core.Audio;

/// <summary>对单个音频会话的音量控制句柄（引擎写音量的唯一入口）。</summary>
public interface ISessionVolumeControl
{
    string InstanceId { get; }

    /// <summary>读取当前会话音量（0..1）；会话已失效时返回 -1。</summary>
    float GetVolume();

    /// <summary>写入会话音量（0..1，自动钳制）；会话已失效时静默失败。</summary>
    void SetVolume(float volume);
}
