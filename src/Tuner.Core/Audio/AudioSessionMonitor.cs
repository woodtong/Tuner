using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Tuner.Core.Audio;

/// <summary>
/// 监控默认播放设备上的全部音频会话：枚举、PID→进程映射、峰值/音量轮询。
/// 新会话通过 IAudioSessionEvents.OnSessionCreated 实时感知（无需手动刷新）；
/// 另以固定周期做全量对账，覆盖会话销毁、枚举滞后等事件不可靠的场景。
/// 默认播放设备切换时自动重新绑定（IMMNotificationClient）。
/// </summary>
public sealed class AudioSessionMonitor : IDisposable
{
    private readonly int _pollIntervalMs;
    private readonly int _fullReconcilePeriodTicks;
    private readonly object _gate = new();
    private readonly Dictionary<string, TrackedSession> _tracked = new();
    private readonly object _pendingGate = new();
    private readonly Dictionary<string, float> _pendingSessionVolumes = new();
    private readonly Dictionary<string, bool> _pendingSessionMutes = new();
    private float? _pendingMasterVolume;
    private bool? _pendingMasterMute;

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private AudioSessionManager? _manager;
    private Thread? _pollThread;
    private CancellationTokenSource? _cts;
    private volatile bool _enumeratePending = true;
    private volatile bool _rebindPending;
    private volatile IReadOnlyList<SoundSession> _lastSnapshot = Array.Empty<SoundSession>();
    private long _tick;
    private int _consecutiveErrors;
    private string? _lastDiagnostic;

    /// <summary>会话集合发生变化（新增/移除，含设备切换导致的整批更换）。</summary>
    public event Action<IReadOnlyList<SoundSession>>? SessionsChanged;

    /// <summary>每个轮询周期触发一次，携带全部会话的最新快照。</summary>
    public event Action<IReadOnlyList<SoundSession>>? SamplesUpdated;

    /// <summary>非致命诊断信息（轮询异常等，已去重）。</summary>
    public event Action<string>? Diagnostics;

    /// <summary>默认播放设备名。</summary>
    public string DeviceName => _device?.FriendlyName ?? string.Empty;

    /// <summary>默认播放设备 ID（供设备切换器高亮当前项）。</summary>
    public string DeviceId { get; private set; } = "";

    /// <summary>枚举全部可用的播放（渲染）设备：ID → 友好名。</summary>
    public IReadOnlyDictionary<string, string> EnumerateRenderDevices()
    {
        var result = new Dictionary<string, string>();
        lock (_gate)
        {
            if (_enumerator is null)
                return result;
            foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    result[d.ID] = d.FriendlyName;
                }
                catch { /* 个别设备属性读取失败，跳过 */ }
                finally
                {
                    d.Dispose();
                }
            }
        }
        return result;
    }

    /// <summary>用户主动切换默认播放设备（走 IMMNotificationClient 回调后由轮询线程重绑）。</summary>
    public static void SwitchDefaultDevice(string deviceId) => DefaultDeviceSwitcher.SetDefaultDevice(deviceId);

    /// <summary>设备整体峰值电平（0..1），用于诊断整机静音等问题。</summary>
    public float DevicePeak { get; private set; }

    /// <summary>主输出（默认设备）音量（0..1），由轮询线程每周期回读。</summary>
    public float MasterVolume { get; private set; }

    /// <summary>主输出是否静音。</summary>
    public bool MasterMuted { get; private set; }

    // —— 音量写入入口（UI 线程调用，实际 COM 写在轮询线程执行，避免跨套间） ——

    public void SetMasterVolume(float volume)
    {
        _pendingMasterVolume = Math.Clamp(volume, 0f, 1f);
    }

    public void SetMasterMute(bool mute)
    {
        _pendingMasterMute = mute;
    }

    public void SetSessionVolume(string instanceId, float volume)
    {
        lock (_pendingGate)
            _pendingSessionVolumes[instanceId] = Math.Clamp(volume, 0f, 1f);
    }

    public void SetSessionMute(string instanceId, bool mute)
    {
        lock (_pendingGate)
            _pendingSessionMutes[instanceId] = mute;
    }

    /// <summary>最近一个轮询周期的会话快照（供异步消费者以自己的节奏读取）。</summary>
    public IReadOnlyList<SoundSession> CurrentSnapshot => _lastSnapshot;

    /// <summary>全部会话的音量控制句柄（按 InstanceId 与快照一一对应）。</summary>
    public IReadOnlyList<ISessionVolumeControl> VolumeControls
    {
        get
        {
            lock (_gate)
                return _tracked.Values.Select(t => (ISessionVolumeControl)t.Control).ToArray();
        }
    }

    public AudioSessionMonitor(int pollIntervalMs = 100)
    {
        if (pollIntervalMs < 10)
            throw new ArgumentOutOfRangeException(nameof(pollIntervalMs), "轮询间隔不能小于 10ms");
        _pollIntervalMs = pollIntervalMs;
        _fullReconcilePeriodTicks = Math.Max(1, 2000 / pollIntervalMs);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_pollThread is not null)
                throw new InvalidOperationException("监控已启动");

            // COM 对象（枚举器/设备/会话管理器）全部延迟到轮询线程上创建：
            // WPF 等宿主的主线程是 STA，而轮询线程是 MTA，跨套间使用会导致 COM 调用失败。
            _cts = new CancellationTokenSource();
            _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "Tuner.AudioSessionMonitor" };
            _pollThread.Start();
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _pollThread;
            _cts?.Cancel();
        }
        thread?.Join(2500);

        // COM 对象的实际释放在轮询线程的 finally 块中完成（避免跨套间释放），这里只复位托管字段
        lock (_gate)
        {
            _tracked.Clear();
            _cts?.Dispose();
            _cts = null;
            _pollThread = null;
        }
    }

    private void OnSessionCreated(object? sender, IAudioSessionControl newSession)
    {
        // COM 回调线程上只做标记，重枚举放到轮询线程执行
        _enumeratePending = true;
    }

    private void EnsureEnumerator()
    {
        lock (_gate)
        {
            if (_enumerator is not null)
                return;
            var enumerator = new MMDeviceEnumerator();
            enumerator.RegisterEndpointNotificationCallback(new DeviceNotificationClient(this));
            _enumerator = enumerator;
        }
    }

    /// <summary>（重新）绑定默认播放设备。设备切换或设备消失后由轮询线程调用。</summary>
    private bool RebindDevice()
    {
        lock (_gate)
        {
            if (_enumerator is null)
                return false;
            if (_manager is not null)
                _manager.OnSessionCreated -= OnSessionCreated;
            _manager = null;
            _device?.Dispose();
            _device = null;

            var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var manager = device.AudioSessionManager;
            manager.OnSessionCreated += OnSessionCreated;
            _device = device;
            _manager = manager;
            DeviceId = device.ID;
            _tracked.Clear();
            _enumeratePending = true;
            DevicePeak = 0;
            return true;
        }
    }

    private void PollLoop()
    {
        var ct = _cts!.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    bool fullReconcile = _tick % _fullReconcilePeriodTicks == 0;
                    bool changed = false;

                    if (_enumerator is null)
                        EnsureEnumerator();
                    if (_rebindPending || _device is null)
                    {
                        changed = RebindDevice();
                        _rebindPending = false;
                    }
                    if (_device is not null && (_enumeratePending || fullReconcile))
                    {
                        changed |= Reconcile();
                        _enumeratePending = false;
                    }
                    if (_device is not null)
                    {
                        UpdateSamples(fullReconcile);
                        _consecutiveErrors = 0;
                    }

                    var snapshot = Snapshot();
                    _lastSnapshot = snapshot;
                    if (changed)
                        SessionsChanged?.Invoke(snapshot);
                    SamplesUpdated?.Invoke(snapshot);
                }
                catch (Exception ex)
                {
                    if (++_consecutiveErrors >= 10)
                        _rebindPending = true; // 连续失败多半是设备被拔出，尝试重绑
                    ReportDiagnostic($"轮询异常: {ex.Message}");
                }

                _tick++;
                ct.WaitHandle.WaitOne(_pollIntervalMs);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_manager is not null)
                    _manager.OnSessionCreated -= OnSessionCreated;
                _manager = null;
                _device?.Dispose();
                _device = null;
                _enumerator?.Dispose();
                _enumerator = null;
            }
        }
    }

    /// <summary>重新枚举会话并与跟踪表做差量。会话创建/销毁后 IAudioSessionEnumerator 可能返回旧数据，必须先 RefreshSessions。</summary>
    private bool Reconcile()
    {
        _manager!.RefreshSessions();
        var current = _manager.Sessions;
        bool changed = false;

        lock (_gate)
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < current.Count; i++)
            {
                var native = current[i];
                string id = native.GetSessionInstanceIdentifier;
                if (string.IsNullOrEmpty(id))
                    continue;
                seen.Add(id);
                if (_tracked.ContainsKey(id))
                    continue;

                uint pid = native.GetProcessID;
                bool isSystemSounds = native.IsSystemSoundsSession || pid == 0;
                var (name, alive) = ResolveProcess(pid);
                var snapshot = new SoundSession
                {
                    InstanceId = id,
                    Pid = pid,
                    ProcessName = isSystemSounds ? "System Sounds" : name,
                    IsSystemSounds = isSystemSounds,
                    State = MapState(native.State),
                    IconPng = TryGetProcessIcon(pid),
                };
                _tracked[id] = new TrackedSession
                {
                    Native = native,
                    Control = new SessionVolumeControl(id, native),
                    Snapshot = snapshot,
                    ProcessAlive = alive || isSystemSounds,
                };
                changed = true;
            }

            foreach (var staleId in _tracked.Keys.Where(id => !seen.Contains(id)).ToArray())
            {
                _tracked.Remove(staleId);
                changed = true;
            }
        }

        return changed;
    }

    private void UpdateSamples(bool fullReconcile)
    {
        float? pendingMasterVolume;
        bool? pendingMasterMute;
        Dictionary<string, float> pendingVolumes;
        Dictionary<string, bool> pendingMutes;
        lock (_pendingGate)
        {
            pendingMasterVolume = _pendingMasterVolume;
            _pendingMasterVolume = null;
            pendingMasterMute = _pendingMasterMute;
            _pendingMasterMute = null;
            pendingVolumes = new Dictionary<string, float>(_pendingSessionVolumes);
            _pendingSessionVolumes.Clear();
            pendingMutes = new Dictionary<string, bool>(_pendingSessionMutes);
            _pendingSessionMutes.Clear();
        }

        lock (_gate)
        {
            DevicePeak = Clamp01(_device!.AudioMeterInformation.MasterPeakValue);

            var endpoint = _device.AudioEndpointVolume;
            if (pendingMasterVolume is not null)
                endpoint.MasterVolumeLevelScalar = pendingMasterVolume.Value;
            if (pendingMasterMute is not null)
                endpoint.Mute = pendingMasterMute.Value;
            MasterVolume = Clamp01(endpoint.MasterVolumeLevelScalar);
            MasterMuted = endpoint.Mute;

            foreach (var t in _tracked.Values)
            {
                try
                {
                    if (pendingVolumes.TryGetValue(t.Snapshot.InstanceId, out var sv))
                        t.Native.SimpleAudioVolume.Volume = sv;
                    if (pendingMutes.TryGetValue(t.Snapshot.InstanceId, out var sm))
                        t.Native.SimpleAudioVolume.Mute = sm;

                    t.Snapshot.Peak = Clamp01(t.Native.AudioMeterInformation.MasterPeakValue);
                    t.Snapshot.Volume = Clamp01(t.Native.SimpleAudioVolume.Volume);
                    t.Snapshot.Mute = t.Native.SimpleAudioVolume.Mute;
                    t.Snapshot.State = MapState(t.Native.State);
                    if (t.Snapshot.IconPng is null && !t.IconAttempted && IconProvider is not null)
                    {
                        t.IconAttempted = true; // 提供器就绪后每会话只尝试一次（其内部有缓存）
                        t.Snapshot.IconPng = TryGetProcessIcon(t.Snapshot.Pid);
                    }
                }
                catch
                {
                    // 会话销毁瞬间的 COM 错误：本周期峰值归零，等下一次对账剔除
                    t.Snapshot.Peak = 0;
                }

                if (fullReconcile && !t.ProcessAlive)
                {
                    var (name, alive) = ResolveProcess(t.Snapshot.Pid);
                    t.ProcessAlive = alive;
                    if (alive && !t.Snapshot.IsSystemSounds)
                        t.Snapshot.ProcessName = name; // PID 被复用，或进程晚于会话创建
                }
            }
        }
    }

    private IReadOnlyList<SoundSession> Snapshot()
    {
        lock (_gate)
            return _tracked.Values.Select(t => t.Snapshot).ToArray();
    }

    private void ReportDiagnostic(string message)
    {
        // 同一条诊断只报一次，避免无设备时每 100ms 刷屏
        if (_lastDiagnostic == message)
            return;
        _lastDiagnostic = message;
        Diagnostics?.Invoke(message);
    }

    private static SessionState MapState(AudioSessionState state) => state switch
    {
        AudioSessionState.AudioSessionStateActive => SessionState.Active,
        AudioSessionState.AudioSessionStateInactive => SessionState.Inactive,
        _ => SessionState.Expired,
    };

    private static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);

    private static (string Name, bool Alive) ResolveProcess(uint pid)
    {
        if (pid == 0)
            return ("System Sounds", true);
        try
        {
            var process = Process.GetProcessById((int)pid);
            return (process.ProcessName, !process.HasExited);
        }
        catch
        {
            return ($"pid:{pid}(已退出)", false);
        }
    }

    /// <summary>取进程图标 PNG 的委托（由 UI 层注入，Core 不依赖图形库）；返回 null 表示无法获取。</summary>
    public static Func<uint, byte[]?>? IconProvider { get; set; }

    private static byte[]? TryGetProcessIcon(uint pid)
    {
        var provider = IconProvider;
        try
        {
            return provider?.Invoke(pid);
        }
        catch
        {
            return null;
        }
    }

    private sealed class TrackedSession
    {
        public required AudioSessionControl Native { get; init; }
        public required SessionVolumeControl Control { get; init; }
        public required SoundSession Snapshot { get; init; }
        public bool ProcessAlive { get; set; }
        public bool IconAttempted { get; set; }
    }

    private sealed class SessionVolumeControl : ISessionVolumeControl
    {
        private readonly AudioSessionControl _native;

        public SessionVolumeControl(string instanceId, AudioSessionControl native)
        {
            InstanceId = instanceId;
            _native = native;
        }

        public string InstanceId { get; }

        public float GetVolume()
        {
            try
            {
                return Math.Clamp(_native.SimpleAudioVolume.Volume, 0f, 1f);
            }
            catch
            {
                return -1f; // 会话已失效
            }
        }

        public void SetVolume(float volume)
        {
            try
            {
                _native.SimpleAudioVolume.Volume = Math.Clamp(volume, 0f, 1f);
            }
            catch
            {
                // 会话正在销毁，静默失败
            }
        }
    }

    /// <summary>系统设备通知：只关心默认渲染设备变化，其余事件忽略。</summary>
    private sealed class DeviceNotificationClient : IMMNotificationClient
    {
        private readonly AudioSessionMonitor _owner;

        public DeviceNotificationClient(AudioSessionMonitor owner) => _owner = owner;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }

        public void OnDeviceAdded(string deviceId) { }

        public void OnDeviceRemoved(string deviceId) { }

        public void OnDefaultDeviceChanged(DataFlow dataFlow, Role role, string deviceId)
        {
            if (dataFlow == DataFlow.Render)
                _owner._rebindPending = true;
        }

        public void OnPropertyValueChanged(string deviceId, PropertyKey propertyKey) { }
    }
}
