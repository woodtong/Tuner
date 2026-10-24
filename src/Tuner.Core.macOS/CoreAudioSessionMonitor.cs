using System.Runtime.InteropServices;
using Tuner.Core.Audio;

namespace Tuner.Core.macOS;

/// <summary>
/// CoreAudio 公开 API 能覆盖的部分（设备级）：默认输出设备枚举、主音量/静音读写。
/// ⚠ per-app 会话枚举/音量/峰值在 macOS 公开 API 中不存在——本实现的 CurrentSnapshot
/// 恒为空、会话写入静默失败；完整 per-app 闪避需要虚拟音频驱动（docs/macOS-plan.md）。
/// ⚠ 未经 macOS 真机验证（Windows 上仅编译）：CoreAudio 常量/调用约定需在 Mac 上复核。
/// </summary>
public sealed class CoreAudioSessionMonitor : IAudioSessionMonitor
{
    private const string Framework = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";

    // ---------- CoreAudio C API（公开部分） ----------

    private const uint KAudioObjectSystemObject = 1;
    private const uint KAudioObjectPropertyName = 0x6F6E /* 'nm  ' 实际 0x6E6D */ & 0;
    private const uint KAudioHardwarePropertyDefaultOutputDevice = 0x6F757470; // 'outp'
    private const uint KAudioHardwarePropertyDevices = 0x64657621;             // 'dev#'
    private const uint KAudioDevicePropertyStreamConfiguration = 0x73747223;   // 'str#'
    private const uint KAudioDevicePropertyScopeOutput = 0x6F7574;             // 'out '
    private const uint KAudioDevicePropertyVolumeScalar = 0x76F_6C73;          // 'vlsc'
    private const uint KAudioDevicePropertyMute = 0x6D7574;                    // 'mut '
    private const uint KAudioDevicePropertyDeviceNameCFString = 0x646E616D;    // 'dnam'

    private const uint KAudioHardwareNoError = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioObjectPropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
    }

    [DllImport(Framework)]
    private static extern uint AudioObjectGetPropertyDataSize(
        uint objectId, ref AudioObjectPropertyAddress address, uint qualifierDataSize,
        IntPtr qualifierData, out uint dataSize);

    [DllImport(Framework)]
    private static extern uint AudioObjectGetPropertyData(
        uint objectId, ref AudioObjectPropertyAddress address, uint qualifierDataSize,
        IntPtr qualifierData, ref uint dataSize, IntPtr outData);

    [DllImport(Framework)]
    private static extern uint AudioObjectSetPropertyData(
        uint objectId, ref AudioObjectPropertyAddress address, uint qualifierDataSize,
        IntPtr qualifierData, uint dataSize, IntPtr inData);

    [DllImport(Framework)]
    private static extern uint AudioObjectHasProperty(uint objectId, ref AudioObjectPropertyAddress address);

    // ---------- IAudioSessionMonitor（设备级可用部分） ----------

    public event Action<IReadOnlyList<SoundSession>>? SamplesUpdated;
    public event Action<string>? Diagnostics;

    public string DeviceName { get; private set; } = "";
    public string DeviceId { get; private set; } = "";
    public float DevicePeak => 0f; // 公开 API 无设备峰值（需 tap/IO_proc，驱动路线内提供）
    public float MasterVolume { get; private set; } = 1f;
    public bool MasterMuted { get; private set; }
    public IReadOnlyList<SoundSession> CurrentSnapshot { get; private set; } = Array.Empty<SoundSession>();
    public IReadOnlyList<ISessionVolumeControl> VolumeControls { get; private set; } = Array.Empty<ISessionVolumeControl>();

    public void Start()
    {
        // 低频回读主音量（无订阅 API 封装，保持与轮询模型一致的最小实现）
        _thread = new Thread(() =>
        {
            while (_running)
            {
                try
                {
                    RefreshDevice();
                    SamplesUpdated?.Invoke(CurrentSnapshot);
                }
                catch (Exception ex)
                {
                    Diagnostics?.Invoke("CoreAudio 轮询异常: " + ex.Message);
                }
                Thread.Sleep(500);
            }
        })
        { IsBackground = true, Name = "Tuner.CoreAudioMonitor" };
        _running = true;
        _thread.Start();
    }

    private Thread? _thread;
    private volatile bool _running;
    private uint _deviceId;

    private void RefreshDevice()
    {
        var addr = new AudioObjectPropertyAddress
        {
            Selector = KAudioHardwarePropertyDefaultOutputDevice,
            Scope = KAudioDevicePropertyScopeOutput,
            Element = 0,
        };
        uint size = 4;
        uint id = 0;
        unsafe
        {
            if (AudioObjectGetPropertyData(KAudioObjectSystemObject, ref addr, 0, IntPtr.Zero, ref size, (IntPtr)(&id)) != KAudioHardwareNoError)
                return;
        }
        _deviceId = id;
        DeviceId = id.ToString();
        DeviceName = ReadDeviceName(id);
        MasterVolume = HasStereoVolume(id) ? ReadChannelVolume(id, 1) : 1f;
        MasterMuted = ReadMute(id);
    }

    private static uint ChannelCount(uint deviceId)
    {
        var addr = new AudioObjectPropertyAddress
        {
            Selector = KAudioDevicePropertyStreamConfiguration,
            Scope = KAudioDevicePropertyScopeOutput,
            Element = 0,
        };
        if (AudioObjectGetPropertyDataSize(deviceId, ref addr, 0, IntPtr.Zero, out var size) != KAudioHardwareNoError)
            return 0;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(deviceId, ref addr, 0, IntPtr.Zero, ref size, buf) != KAudioHardwareNoError)
                return 0;
            // AudioBufferList: uint count; AudioBuffer{ uint channels; uint dataByteSize; ptr data }
            return (uint)Marshal.ReadInt16(buf, 4); // AudioBuffer.channels（首个缓冲，UInt32 但声道数 ≤ 32 实际写入短整形惯例——需在 Mac 上复核
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static bool HasStereoVolume(uint deviceId)
    {
        var addr = new AudioObjectPropertyAddress
        {
            Selector = KAudioDevicePropertyVolumeScalar,
            Scope = KAudioDevicePropertyScopeOutput,
            Element = 1,
        };
        return AudioObjectHasProperty(deviceId, ref addr) != 0;
    }

    private static float ReadChannelVolume(uint deviceId, uint channel)
    {
        var addr = new AudioObjectPropertyAddress
        {
            Selector = KAudioDevicePropertyVolumeScalar,
            Scope = KAudioDevicePropertyScopeOutput,
            Element = channel,
        };
        uint size = 4;
        float v = 1f;
        unsafe
        {
            if (AudioObjectGetPropertyData(deviceId, ref addr, 0, IntPtr.Zero, ref size, (IntPtr)(&v)) != KAudioHardwareNoError)
                return 1f;
        }
        return v;
    }

    private static bool ReadMute(uint deviceId)
    {
        var addr = new AudioObjectPropertyAddress
        {
            Selector = KAudioDevicePropertyMute,
            Scope = KAudioDevicePropertyScopeOutput,
            Element = 0,
        };
        uint size = 4;
        int mute = 0;
        unsafe
        {
            if (AudioObjectGetPropertyData(deviceId, ref addr, 0, IntPtr.Zero, ref size, (IntPtr)(&mute)) != KAudioHardwareNoError)
                return false;
        }
        return mute != 0;
    }

    private static void WriteChannelVolume(uint deviceId, uint channel, float v)
    {
        var addr = new AudioObjectPropertyAddress
        {
            Selector = KAudioDevicePropertyVolumeScalar,
            Scope = KAudioDevicePropertyScopeOutput,
            Element = channel,
        };
        unsafe
        {
            AudioObjectSetPropertyData(deviceId, ref addr, 0, IntPtr.Zero, 4, (IntPtr)(&v));
        }
    }

    private static void WriteMute(uint deviceId, bool mute)
    {
        var addr = new AudioObjectPropertyAddress
        {
            Selector = KAudioDevicePropertyMute,
            Scope = KAudioDevicePropertyScopeOutput,
            Element = 0,
        };
        int m = mute ? 1 : 0;
        unsafe
        {
            AudioObjectSetPropertyData(deviceId, ref addr, 0, IntPtr.Zero, 4, (IntPtr)(&m));
        }
    }

    private static string ReadDeviceName(uint deviceId)
    {
        var addr = new AudioObjectPropertyAddress
        {
            Selector = KAudioDevicePropertyDeviceNameCFString,
            Scope = KAudioDevicePropertyScopeOutput,
            Element = 0,
        };
        uint size = 8;
        var cfstr = IntPtr.Zero;
        unsafe
        {
            if (AudioObjectGetPropertyData(deviceId, ref addr, 0, IntPtr.Zero, ref size, (IntPtr)(&cfstr)) != KAudioHardwareNoError)
                return "";
        }
        // CFString → C 字符串（CoreFoundation，公开 API）
        var cstr = CFStringGetCStringPtr(cfstr, 0x08000100 /* UTF8 */);
        return cstr == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(cstr) ?? "";
    }

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringGetCStringPtr(IntPtr cf, uint encoding);

    public void SetMasterVolume(float volume)
    {
        var v = Math.Clamp(volume, 0f, 1f);
        WriteChannelVolume(_deviceId, 1, v);
        WriteChannelVolume(_deviceId, 2, v);
        MasterVolume = v;
    }

    public void SetMasterMute(bool mute)
    {
        WriteMute(_deviceId, mute);
        MasterMuted = mute;
    }

    // per-app 会话部分：公开 API 不存在，保持空实现（驱动路线接入点见 docs/macOS-plan.md）
    public void SetSessionVolume(string instanceId, float volume) { }
    public void SetSessionMute(string instanceId, bool mute) { }
    public bool HasPendingVolumeWrite(string instanceId) => false;
    public ISessionVolumeControl? TryGetControl(string instanceId) => null;

    public IReadOnlyDictionary<string, string> EnumerateRenderDevices()
    {
        var result = new Dictionary<string, string>();
        var addr = new AudioObjectPropertyAddress
        {
            Selector = KAudioHardwarePropertyDevices,
            Scope = KAudioDevicePropertyScopeOutput,
            Element = 0,
        };
        if (AudioObjectGetPropertyDataSize(KAudioObjectSystemObject, ref addr, 0, IntPtr.Zero, out var size) != KAudioHardwareNoError || size < 4)
            return result;
        int count = (int)(size / 4);
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(KAudioObjectSystemObject, ref addr, 0, IntPtr.Zero, ref size, buf) != KAudioHardwareNoError)
                return result;
            for (int i = 0; i < count; i++)
            {
                uint id = (uint)Marshal.ReadInt32(buf, i * 4);
                result[id.ToString()] = ReadDeviceName(id);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return result;
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(1500);
    }
}
