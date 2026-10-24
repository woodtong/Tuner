using System.Runtime.InteropServices;
using Tuner.Core.Audio;

namespace Tuner.Core.Linux;

/// <summary>
/// PulseAudio 会话监控（IAudioSessionMonitor 的 Linux 实现；PipeWire 经 pipewire-pulse 同样可用）。
/// sink_input ≈ Windows 音频会话：每个应用播放流一个，带独立音量与 corked（暂停）状态。
/// 结构：pa_threaded_mainloop 专用线程收事件/回请求，订阅 SINK_INPUT 变化后整表重拉；
/// 快照与音量写走待写队列（与 Windows 实现同构，UI/引擎侧无感切换）。
/// 已知边界：PulseAudio 无 sink_input 级峰值，Peak 恒 0——sound 判定不可用，
/// 规则建议走 state 判定（corked 即"暂停/停止"，应用暂停流时置位）。
/// </summary>
public sealed class PulseAudioSessionMonitor : IAudioSessionMonitor
{
    private const string Lib = "libpulse.so.0";
    private const uint PaVolumeNorm = 0x10000;
    // 订阅常量（pulse/subscribe.h）：facility 低 4 位 SINK_INPUT=0x2；掩码 SINK_INPUT=1<<2
    private const int PaSubscriptionMaskSinkInput = 0x0004;
    private const int PaSubscriptionEventSinkInput = 0x0002;
    private const int PaContextReady = 4; // PA_CONTEXT_READY

    // ---------- native 声明 ----------

    [DllImport(Lib)] private static extern IntPtr pa_threaded_mainloop_new();
    [DllImport(Lib)] private static extern int pa_threaded_mainloop_start(IntPtr m);
    [DllImport(Lib)] private static extern void pa_threaded_mainloop_stop(IntPtr m);
    [DllImport(Lib)] private static extern void pa_threaded_mainloop_free(IntPtr m);
    [DllImport(Lib)] private static extern void pa_threaded_mainloop_lock(IntPtr m);
    [DllImport(Lib)] private static extern void pa_threaded_mainloop_unlock(IntPtr m);
    [DllImport(Lib)] private static extern void pa_threaded_mainloop_signal(IntPtr m, int waitForAccept);
    [DllImport(Lib)] private static extern void pa_threaded_mainloop_wait(IntPtr m);
    [DllImport(Lib)] private static extern IntPtr pa_threaded_mainloop_get_api(IntPtr m);

    [DllImport(Lib)] private static extern IntPtr pa_context_new(IntPtr api, string name);
    [DllImport(Lib)] private static extern void pa_context_set_state_callback(IntPtr c, ContextStateCb cb, IntPtr ud);
    [DllImport(Lib)] private static extern int pa_context_connect(IntPtr c, string? server, int flags, IntPtr spawn);
    [DllImport(Lib)] private static extern int pa_context_get_state(IntPtr c);
    [DllImport(Lib)] private static extern void pa_context_disconnect(IntPtr c);
    [DllImport(Lib)] private static extern void pa_context_unref(IntPtr c);
    [DllImport(Lib)] private static extern void pa_context_set_subscribe_callback(IntPtr c, SubscribeCb cb, IntPtr ud);
    [DllImport(Lib)] private static extern IntPtr pa_context_subscribe(IntPtr c, int mask, IntPtr successCb, IntPtr ud);
    [DllImport(Lib)] private static extern IntPtr pa_context_get_sink_input_info_list(IntPtr c, SinkInputListCb cb, IntPtr ud);
    [DllImport(Lib)] private static extern IntPtr pa_context_get_sink_info_list(IntPtr c, SinkListCb cb, IntPtr ud);
    [DllImport(Lib)] private static extern IntPtr pa_context_get_server_info(IntPtr c, ServerInfoCb cb, IntPtr ud);
    [DllImport(Lib)] private static extern IntPtr pa_context_set_sink_input_volume(IntPtr c, uint index, IntPtr cvolume, SuccessCb? successCb, IntPtr ud);
    [DllImport(Lib)] private static extern IntPtr pa_context_set_sink_input_mute(IntPtr c, uint index, int mute, SuccessCb? successCb, IntPtr ud);
    [DllImport(Lib)] private static extern IntPtr pa_context_set_sink_volume_by_name(IntPtr c, string name, IntPtr cvolume, SuccessCb? successCb, IntPtr ud);
    [DllImport(Lib)] private static extern IntPtr pa_context_set_sink_mute_by_name(IntPtr c, string name, int mute, SuccessCb? successCb, IntPtr ud);
    [DllImport(Lib)] private static extern void pa_operation_unref(IntPtr o);
    [DllImport(Lib)] private static extern int pa_operation_get_state(IntPtr o);
    [DllImport(Lib)] private static extern IntPtr pa_proplist_gets(IntPtr p, string key);
    [DllImport(Lib)] private static extern void pa_xfree(IntPtr p);

    private delegate void ContextStateCb(IntPtr c, IntPtr ud);
    private delegate void SubscribeCb(IntPtr c, uint eventType, uint idx, IntPtr ud);
    private delegate void SinkInputListCb(IntPtr c, IntPtr info, int eol, IntPtr ud);
    private delegate void SinkListCb(IntPtr c, IntPtr info, int eol, IntPtr ud);
    private delegate void ServerInfoCb(IntPtr c, IntPtr info, IntPtr ud);
    private delegate void SuccessCb(IntPtr c, int success, IntPtr ud);

    // ---------- native 结构：不用 marshal 嵌套数组（ByValArray 编组在回调方向上不稳定，实测段错误），
    //             一律按 x86_64/arm64 的显式 offset 手读 ----------

    /// <summary>pa_cvolume：byte channels; uint32 values[32]（offset 4 起）。</summary>
    private static (byte Channels, double Avg) ReadCVolume(nint p)
    {
        if (p == IntPtr.Zero)
            return (2, 1.0);
        byte ch = Marshal.ReadByte(p);
        if (ch == 0)
            return (2, 1.0);
        double sum = 0;
        for (int i = 0; i < ch && i < 32; i++)
            sum += Marshal.ReadInt32(p + 4 + 4 * i); // 音量值不会超 int32 上限（PA_VOLUME_NORM=65536）
        return (ch, sum / ch / PaVolumeNorm);
    }

    /// <summary>pa_sink_input_info 字段 offset（x86_64；libpulse 16.x 字段序：index/name/owner_module/client/sink/
    /// sample_spec/channel_map/cvolume/buffer_usec/sink_usec/resample_method/driver/mute/proplist/corked/has_volume）。</summary>
    private static class SinkInput
    {
        public const int Index = 0, Name = 8;
        public const int Volume = 0xAC, Mute = 0x150, Proplist = 0x158, Corked = 0x160, HasVolume = 0x164;
    }

    /// <summary>pa_sink_info 字段 offset（x86_64；字段序：name/index/description/sample_spec/channel_map/
    /// owner_module/cvolume/mute/...）。注意 index 夹在 name 与 description 之间。</summary>
    private static class Sink
    {
        public const int Name = 0, Description = 0x10, Volume = 0xAC, Mute = 0x130;
    }

    /// <summary>pa_server_info 字段 offset（64 位平台 ABI；default_sink_name 在 sample_spec(12B) 之后按 8 对齐）。</summary>
    private static class Server
    {
        public const int DefaultSinkName = 48;
    }

    // ---------- 状态 ----------

    private readonly object _gate = new();          // 托管数据（快照/待写）锁，与 pulse 锁独立
    private readonly Dictionary<uint, Tracked> _tracked = new();
    private readonly Dictionary<string, float> _pendingVolumes = new();
    private readonly Dictionary<string, bool> _pendingMutes = new();
    private readonly List<SoundSession> _snapshot = new();
    private readonly Dictionary<string, string> _sinks = new();     // sink name → description
    private string _defaultSink = "";
    private float _masterVolume = 1f;
    private bool _masterMuted;
    private volatile bool _listDirty = true;
    private volatile bool _sinksDirty = true;
    private volatile bool _running;
    private Thread? _thread;

    // native 句柄与回调（静态字段钉住 delegate 防 GC）
    private IntPtr _mainloop;
    private IntPtr _context;
    private ContextStateCb? _stateCb;
    private SubscribeCb? _subscribeCb;
    private SinkInputListCb? _sinkInputCb;
    private SinkListCb? _sinkCb;
    private ServerInfoCb? _serverCb;
    private SuccessCb? _successCb;

    private sealed class Tracked
    {
        public PulseControl? Control;
        public SoundSession Snapshot = null!;
    }

    public event Action<IReadOnlyList<SoundSession>>? SamplesUpdated;
    public event Action<string>? Diagnostics;

    public string DeviceName { get { lock (_gate) return _sinks.GetValueOrDefault(_defaultSink, _defaultSink); } }
    public string DeviceId { get { lock (_gate) return _defaultSink; } }
    public float DevicePeak => 0f;   // PulseAudio 无 sink_input 级峰值
    public float MasterVolume { get { lock (_gate) return _masterVolume; } }
    public bool MasterMuted { get { lock (_gate) return _masterMuted; } }

    public IReadOnlyList<SoundSession> CurrentSnapshot
    {
        get { lock (_gate) return _snapshot.ToArray(); }
    }

    public IReadOnlyList<ISessionVolumeControl> VolumeControls
    {
        get { lock (_gate) return _tracked.Values.Select(t => (ISessionVolumeControl)t.Control!).ToArray(); }
    }

    /// <summary>连接默认 PulseAudio 服务器（PULSE_SERVER 环境变量优先，WSLg 即 unix:/mnt/wslg/PulseServer）。</summary>
    public PulseAudioSessionMonitor(int pollIntervalMs = 100)
    {
        _pollIntervalMs = Math.Max(50, pollIntervalMs);
    }

    private readonly int _pollIntervalMs;

    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null)
                return;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Tuner.PulseAudioMonitor" };
            _thread.Start();
        }
    }

    private void Loop()
    {
        if (!Connect())
        {
            Diagnostics?.Invoke("PulseAudio 连接失败：无法连接服务器（检查 PULSE_SERVER / pipewire-pulse 是否在运行）");
            return;
        }
        Diagnostics?.Invoke("PulseAudio 已连接");

        // 轮询：订阅事件置脏 → 重拉列表；待写队列每拍消费（写完由订阅事件触发重拉自然回读）
        while (_running)
        {
            Thread.Sleep(_pollIntervalMs);
            try
            {
                if (_listDirty)
                {
                    _listDirty = false;
                    QuerySinkInputs();
                }
                FlushPending();
                if (_sinksDirty)
                {
                    _sinksDirty = false;
                    QuerySinks();
                }
                lock (_gate)
                    SamplesUpdated?.Invoke(_snapshot.ToArray());
            }
            catch (Exception ex)
            {
                Diagnostics?.Invoke("轮询异常: " + ex.Message);
            }
        }
    }

    // ---------- 连接与事件 ----------

    private bool Connect()
    {
        _mainloop = pa_threaded_mainloop_new();
        if (_mainloop == IntPtr.Zero)
            return false;
        var api = pa_threaded_mainloop_get_api(_mainloop);
        _context = pa_context_new(api, "Tuner");
        if (_context == IntPtr.Zero)
            return false;

        _stateCb = OnContextState;
        pa_context_set_state_callback(_context, _stateCb, IntPtr.Zero);
        _subscribeCb = OnSubscribeEvent;
        pa_context_set_subscribe_callback(_context, _subscribeCb, IntPtr.Zero);
        _sinkInputCb = OnSinkInputInfo;
        _sinkCb = OnSinkInfo;
        _serverCb = OnServerInfo;
        _successCb = OnWriteSuccess;

        if (pa_threaded_mainloop_start(_mainloop) != 0)
            return false;

        pa_threaded_mainloop_lock(_mainloop);
        try
        {
            if (pa_context_connect(_context, null, 0, IntPtr.Zero) < 0)
                return false;
            // 等待 READY（状态回调里 signal）
            while (pa_context_get_state(_context) != PaContextReady)
                pa_threaded_mainloop_wait(_mainloop);
            var op = pa_context_subscribe(_context, PaSubscriptionMaskSinkInput, IntPtr.Zero, IntPtr.Zero);
            if (op != IntPtr.Zero) pa_operation_unref(op);
            return true;
        }
        finally
        {
            pa_threaded_mainloop_unlock(_mainloop);
        }
    }

    private void OnContextState(IntPtr c, IntPtr ud)
    {
        // threaded mainloop 线程：READY/失败都 signal 唤醒连接等待方
        pa_threaded_mainloop_signal(_mainloop, 0);
    }

    private void OnSubscribeEvent(IntPtr c, uint eventType, uint idx, IntPtr ud)
    {
        // 事件类型低 4 位是 facility（SINK_INPUT=1），高 4 位是操作（new/change/remove）
        if ((eventType & 0xF) == PaSubscriptionEventSinkInput)
            _listDirty = true; // 整表重拉（差量维护不值得为原型复杂化）
    }

    private void OnWriteSuccess(IntPtr c, int success, IntPtr ud)
    {
        // 写操作完成：signal 唤醒 WaitForOperation（失败也不重试，订阅事件自会对账）
        pa_threaded_mainloop_signal(_mainloop, 0);
    }

    // ---------- 查询（threaded mainloop 标准模式：lock → 发 operation → wait eol → unlock） ----------

    private void QuerySinkInputs()
    {
        pa_threaded_mainloop_lock(_mainloop);
        try
        {
            var op = pa_context_get_sink_input_info_list(_context, _sinkInputCb!, IntPtr.Zero);
            WaitForOperation(op);
        }
        finally
        {
            pa_threaded_mainloop_unlock(_mainloop);
        }
    }

    private readonly List<SoundSession> _pendingSnapshot = new();

    private void OnSinkInputInfo(IntPtr c, IntPtr infoPtr, int eol, IntPtr ud)
    {
        if (eol != 0)
        {
            // 结束：换快照（回调线程 → 托管锁短暂持有）
            lock (_gate)
            {
                _snapshot.Clear();
                _snapshot.AddRange(_pendingSnapshot);
            }
            _pendingSnapshot.Clear();
            pa_threaded_mainloop_signal(_mainloop, 0);
            return;
        }
        var index = (uint)Marshal.ReadInt32(infoPtr + SinkInput.Index);
        var namePtr = Marshal.ReadIntPtr(infoPtr + SinkInput.Name);
        var (_, volAvg) = ReadCVolume(infoPtr + SinkInput.Volume);
        var mute = Marshal.ReadInt32(infoPtr + SinkInput.Mute) != 0;
        var corked = Marshal.ReadInt32(infoPtr + SinkInput.Corked) != 0;
        var proplist = Marshal.ReadIntPtr(infoPtr + SinkInput.Proplist);
        uint pid = 0;
        var binary = PropString(proplist, "application.process.binary");
        if (uint.TryParse(PropString(proplist, "application.process.id"), out var p))
            pid = p;
        var name = (string.IsNullOrEmpty(binary) ? PtrToStringUtf8(namePtr) : binary) ?? "unknown";
        var vol = (float)Math.Clamp(volAvg, 0f, 1f);
        var t = new Tracked
        {
            Control = new PulseControl(this, index),
            Snapshot = new SoundSession
            {
                InstanceId = "pi-" + index,
                Pid = pid,
                ProcessName = name,
                IsSystemSounds = false,
                State = corked ? SessionState.Inactive : SessionState.Active,
                Volume = vol,
                Peak = 0f,
                Mute = mute,
            },
        };
        lock (_gate)
            _tracked[index] = t;
        _pendingSnapshot.Add(t.Snapshot);
    }

    private void QuerySinks()
    {
        pa_threaded_mainloop_lock(_mainloop);
        try
        {
            var op = pa_context_get_server_info(_context, _serverCb!, IntPtr.Zero);
            WaitForOperation(op);
            var op2 = pa_context_get_sink_info_list(_context, _sinkCb!, IntPtr.Zero);
            WaitForOperation(op2);
        }
        finally
        {
            pa_threaded_mainloop_unlock(_mainloop);
        }
    }

    private void OnServerInfo(IntPtr c, IntPtr infoPtr, IntPtr ud)
    {
        var defPtr = Marshal.ReadIntPtr(infoPtr + Server.DefaultSinkName);
        var def = PtrToStringUtf8(defPtr);
        lock (_gate)
            _defaultSink = def ?? "";
        pa_threaded_mainloop_signal(_mainloop, 0);
    }

    private void OnSinkInfo(IntPtr c, IntPtr infoPtr, int eol, IntPtr ud)
    {
        if (eol != 0)
        {
            pa_threaded_mainloop_signal(_mainloop, 0);
            return;
        }
        var namePtr = Marshal.ReadIntPtr(infoPtr + Sink.Name);
        var descPtr = Marshal.ReadIntPtr(infoPtr + Sink.Description);
        var (_, volAvg) = ReadCVolume(infoPtr + Sink.Volume);
        var sinkMute = Marshal.ReadInt32(infoPtr + Sink.Mute) != 0;
        var name = PtrToStringUtf8(namePtr) ?? "";
        var desc = PtrToStringUtf8(descPtr) ?? name;
        lock (_gate)
        {
            _sinks[name] = desc;
            if (name == _defaultSink)
            {
                _masterVolume = (float)Math.Clamp(volAvg, 0f, 1f);
                _masterMuted = sinkMute;
            }
        }
    }

    private void WaitForOperation(IntPtr op)
    {
        // 调用方已 lock；回调 eol 时 signal
        while (op != IntPtr.Zero && pa_operation_get_state(op) == 1) // PA_OPERATION_RUNNING
            pa_threaded_mainloop_wait(_mainloop);
        if (op != IntPtr.Zero)
            pa_operation_unref(op);
    }

    // ---------- 音量写入（待写队列，轮询线程在 pulse 锁内执行） ----------

    public void SetMasterVolume(float volume)
    {
        lock (_gate)
        {
            _pendingMasterVolume = Math.Clamp(volume, 0f, 1f);
            _sinksDirty = true; // 触发一次轮询以执行待写并回读
        }
    }

    private float? _pendingMasterVolume;
    private bool? _pendingMasterMute;

    public void SetMasterMute(bool mute)
    {
        lock (_gate)
        {
            _pendingMasterMute = mute;
            _sinksDirty = true;
        }
    }

    public void SetSessionVolume(string instanceId, float volume)
    {
        lock (_gate)
            _pendingVolumes[instanceId] = Math.Clamp(volume, 0f, 1f);
    }

    public void SetSessionMute(string instanceId, bool mute)
    {
        lock (_gate)
            _pendingMutes[instanceId] = mute;
    }

    private void FlushPending()
    {
        List<(uint Index, float Vol)>? vols = null;
        List<(uint Index, bool Mute)>? mutes = null;
        float? masterVol; bool? masterMute; string sink;
        lock (_gate)
        {
            foreach (var (idx, t) in _tracked)
            {
                if (_pendingVolumes.TryGetValue(t.Snapshot.InstanceId, out var v))
                    (vols ??= new()).Add((idx, v));
                if (_pendingMutes.TryGetValue(t.Snapshot.InstanceId, out var m))
                    (mutes ??= new()).Add((idx, m));
            }
            _pendingVolumes.Clear();
            _pendingMutes.Clear();
            masterVol = _pendingMasterVolume;
            _pendingMasterVolume = null;
            masterMute = _pendingMasterMute;
            _pendingMasterMute = null;
            sink = _defaultSink;
        }

        pa_threaded_mainloop_lock(_mainloop);
        try
        {
            if (vols is not null)
                foreach (var (idx, v) in vols)
                {
                    var cv = MakeCVolumePtr(v);
                    try
                    {
                        // 等待完成再 unref：立即 unref 会取消还在执行中的写操作
                        var op = pa_context_set_sink_input_volume(_context, idx, cv, _successCb, IntPtr.Zero);
                        WaitForOperation(op);
                    }
                    finally { Marshal.FreeHGlobal(cv); }
                }
            if (mutes is not null)
                foreach (var (idx, m) in mutes)
                {
                    var op = pa_context_set_sink_input_mute(_context, idx, m ? 1 : 0, _successCb, IntPtr.Zero);
                    WaitForOperation(op);
                }
            if (masterVol is not null && sink.Length > 0)
            {
                var cv = MakeCVolumePtr(masterVol.Value);
                try
                {
                    var op = pa_context_set_sink_volume_by_name(_context, sink, cv, _successCb, IntPtr.Zero);
                    WaitForOperation(op);
                }
                finally { Marshal.FreeHGlobal(cv); }
            }
            if (masterMute is not null && sink.Length > 0)
            {
                var op = pa_context_set_sink_mute_by_name(_context, sink, masterMute.Value ? 1 : 0, _successCb, IntPtr.Zero);
                WaitForOperation(op);
            }
        }
        finally
        {
            pa_threaded_mainloop_unlock(_mainloop);
        }
        // 写入会触发订阅事件 → 下轮自动重拉回读
    }

    public bool HasPendingVolumeWrite(string instanceId)
    {
        lock (_gate)
            return _pendingVolumes.ContainsKey(instanceId);
    }

    public ISessionVolumeControl? TryGetControl(string instanceId)
    {
        lock (_gate)
            return _tracked.Values.FirstOrDefault(t => t.Snapshot.InstanceId == instanceId)?.Control;
    }

    public IReadOnlyDictionary<string, string> EnumerateRenderDevices()
    {
        lock (_gate)
            return new Dictionary<string, string>(_sinks);
    }

    /// <summary>构造 pa_cvolume 非托管内存（byte channels=2 + uint32[2]；PulseAudio 按 channels 读值数）。调用方负责 Free。</summary>
    private static IntPtr MakeCVolumePtr(float v)
    {
        var buf = new byte[4 + 32 * 4];
        buf[0] = 2;
        var val = (uint)(Math.Clamp(v, 0f, 1f) * PaVolumeNorm);
        BitConverter.TryWriteBytes(buf.AsSpan(4, 4), val);
        BitConverter.TryWriteBytes(buf.AsSpan(8, 4), val);
        var p = Marshal.AllocHGlobal(buf.Length);
        Marshal.Copy(buf, 0, p, buf.Length);
        return p;
    }

    private static string? PtrToStringUtf8(nint p) =>
        p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

    private static string? PropString(nint proplist, string key) =>
        PtrToStringUtf8(pa_proplist_gets(proplist, key));

    // ---------- 会话音量句柄（写走待写队列，读走快照） ----------

    private sealed class PulseControl : ISessionVolumeControl
    {
        private readonly PulseAudioSessionMonitor _owner;
        private readonly uint _index;

        public PulseControl(PulseAudioSessionMonitor owner, uint index)
        {
            _owner = owner;
            _index = index;
        }

        public string InstanceId => "pi-" + _index;

        public float GetVolume()
        {
            lock (_owner._gate)
                return _owner._tracked.TryGetValue(_index, out var t) ? t.Snapshot.Volume : -1f;
        }

        public void SetVolume(float volume) => _owner.SetSessionVolume(InstanceId, volume);
    }

    public void Dispose()
    {
        lock (_gate)
            _running = false;
        _thread?.Join(1500);

        if (_mainloop != IntPtr.Zero)
        {
            pa_threaded_mainloop_lock(_mainloop);
            if (_context != IntPtr.Zero)
            {
                pa_context_disconnect(_context);
                pa_context_unref(_context);
                _context = IntPtr.Zero;
            }
            pa_threaded_mainloop_unlock(_mainloop);
            pa_threaded_mainloop_stop(_mainloop);
            pa_threaded_mainloop_free(_mainloop);
            _mainloop = IntPtr.Zero;
        }
    }
}
