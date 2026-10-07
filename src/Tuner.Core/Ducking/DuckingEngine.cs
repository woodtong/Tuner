using Tuner.Core.Audio;
using Tuner.Core.Config;
using Tuner.Core.Media;

namespace Tuner.Core.Ducking;

/// <summary>
/// 分组闪避引擎：
/// 1. 以迟滞阈值（进入/退出 + 低电平保持时长）判定每个会话是否"出声"，组内任一会话出声即整组出声；
/// 2. 汇总生效规则（同一目标组取优先级最高者），得到每个会话的目标音量；
/// 3. 以固定节拍把会话音量向目标线性渐变，闪避开始时记录用户原始音量，闪避结束或引擎退出时还原；
/// 4. 新会话创建时若其组已是生效规则的目标，立即初始化音量（应对短促提示音，不等峰值检测）。
/// </summary>
public sealed class DuckingEngine : IDisposable
{
    /// <summary>供 UI 展示的单会话状态（只读快照）。</summary>
    public sealed class SessionState
    {
        public required string InstanceId { get; init; }
        public required uint Pid { get; init; }
        public required string ProcessName { get; init; }
        public required string GroupId { get; init; }
        public required string GroupName { get; init; }
        public required bool Speaking { get; init; }
        public required bool Ducked { get; init; }
        public required float CurrentVolume { get; init; }
        /// <summary>当前目标音量；null 表示保持原音量。</summary>
        public required float? TargetVolume { get; init; }
        public required float OriginalVolume { get; init; }
    }

    private sealed class Tracked
    {
        public SoundSession Session = null!;
        public ISessionVolumeControl? Control;
        public string GroupId = "";
        public bool Speaking;
        public DateTime? BelowSince;
        public bool Ducked;
        public DateTime UnduckAt = DateTime.MinValue; // 上次结束闪避的时刻（锚点保护用）
        public float OriginalVolume = 1f;
        public float CurrentVolume = -1f; // -1 = 未知
        public float? TargetVolume;       // 非 null = 正在闪避
    }

    /// <summary>引擎内部状态变化（出声切换、闪避切换、成员/分组变化），供 UI 刷新。</summary>
    public event Action? EngineStateChanged;

    public event Action<string>? Diagnostics;

    private const int FadeTickMs = 50;

    private readonly AudioSessionMonitor _monitor;
    private readonly object _gate = new();
    private readonly Dictionary<string, Tracked> _tracked = new();
    private readonly Dictionary<string, DateTime> _lastSounding = new(); // 分组最近一次真实出声时刻
    private readonly MediaSessionTracker _mediaTracker = new();
    private TunerConfig _config;
    private Thread? _thread;
    private CancellationTokenSource? _cts;

    public DuckingEngine(AudioSessionMonitor monitor, TunerConfig config)
    {
        _monitor = monitor;
        _config = config;
    }

    /// <summary>全部会话的当前引擎状态。</summary>
    public IReadOnlyList<SessionState> CurrentStates
    {
        get
        {
            lock (_gate)
                return _tracked.Values.Select(ToState).ToArray();
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null)
                throw new InvalidOperationException("引擎已启动");
            _cts = new CancellationTokenSource();
            _mediaTracker.Start();
            _thread = new Thread(Loop) { IsBackground = true, Name = "Tuner.DuckingEngine" };
            _thread.Start();
        }
    }

    /// <summary>替换配置并立即生效。传入后不要再修改该实例，改动请重新调用本方法。</summary>
    public void ApplyConfig(TunerConfig config)
    {
        lock (_gate)
        {
            _config = config;
            ConfigStore.Normalize(config);
        }
        EngineStateChanged?.Invoke();
    }

    /// <summary>最近一次 Dispose 时还原音量的会话数（诊断用）。</summary>
    public int LastRestoreCount { get; private set; }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _cts?.Cancel();
        }
        thread?.Join(2000);

        lock (_gate)
        {
            // 退出还原：所有被修改过的音量恢复原值
            int restored = 0;
            foreach (var t in _tracked.Values.Where(t => t.Ducked))
            {
                t.Ducked = false;
                t.Control?.SetVolume(t.OriginalVolume);
                restored++;
            }
            LastRestoreCount = restored;
            _tracked.Clear();
            _lastSounding.Clear();
            _mediaTracker.Dispose();
            _cts?.Dispose();
            _cts = null;
            _thread = null;
        }
    }

    private void Loop()
    {
        var ct = _cts!.Token;
        while (!ct.IsCancellationRequested)
        {
            bool changed = false;
            try
            {
                changed = Tick();
            }
            catch (Exception ex)
            {
                Diagnostics?.Invoke($"引擎异常: {ex}"); // 完整堆栈便于定位
            }
            if (changed)
                EngineStateChanged?.Invoke();
            ct.WaitHandle.WaitOne(FadeTickMs);
        }
    }

    private bool Tick()
    {
        bool stateChange = false;
        var controls = _monitor.VolumeControls.ToDictionary(c => c.InstanceId);
        var sessions = _monitor.CurrentSnapshot;

        lock (_gate)
        {
            var config = _config;
            var settings = config.Settings;
            var now = DateTime.UtcNow;

            // 1) 成员差量 + 分组解析（配置可能已更换，所有成员每拍重解析）
            var seen = new HashSet<string>();
            foreach (var s in sessions)
            {
                seen.Add(s.InstanceId);
                if (_tracked.TryGetValue(s.InstanceId, out var t))
                {
                    t.Session = s;
                    t.Control ??= controls.GetValueOrDefault(s.InstanceId);
                }
                else
                {
                    t = new Tracked
                    {
                        Session = s,
                        Control = controls.GetValueOrDefault(s.InstanceId),
                        GroupId = ResolveGroup(config, s),
                    };
                    var v = ReadVolume(t);
                    t.CurrentVolume = v;
                    t.OriginalVolume = v;

                    // 短促声音对策：新会话所属组已是生效规则目标 → 创建即初始化音量
                    var initWinners = ComputeWinners(config, GroupSoundingMap(settings, now));
                    if (initWinners.TryGetValue(t.GroupId, out var initRule) && initRule.TriggerGroupId != t.GroupId)
                    {
                        t.OriginalVolume = v;
                        t.Ducked = true;
                        t.CurrentVolume = Math.Clamp(initRule.TargetVolumePercent / 100f, 0f, 1f);
                        t.Control?.SetVolume(t.CurrentVolume);
                    }
                    _tracked[s.InstanceId] = t;
                    stateChange = true;
                }
            }
            foreach (var gone in _tracked.Keys.Where(id => !seen.Contains(id)).ToArray())
            {
                _tracked.Remove(gone); // 会话消失，音量随会话终结，无需还原
                stateChange = true;
            }
            foreach (var t in _tracked.Values)
            {
                var gid = ResolveGroup(config, t.Session);
                if (gid != t.GroupId)
                {
                    t.GroupId = gid;
                    stateChange = true;
                }
            }

            // 清理已消失分组的静音计时
            var liveGroups = _tracked.Values.Select(t => t.GroupId).ToHashSet();
            foreach (var gone in _lastSounding.Keys.Where(id => !liveGroups.Contains(id)).ToArray())
                _lastSounding.Remove(gone);

            // 2) 出声判定：双阈值迟滞 + 低电平保持时长
            foreach (var t in _tracked.Values)
            {
                float peak = t.Session.Peak;
                if (peak >= settings.ActivePeakThreshold)
                {
                    t.BelowSince = null;
                    if (!t.Speaking)
                    {
                        t.Speaking = true;
                        stateChange = true;
                    }
                }
                else if (peak <= settings.InactivePeakThreshold)
                {
                    t.BelowSince ??= now;
                    if (t.Speaking && (now - t.BelowSince.Value).TotalMilliseconds >= settings.InactiveHoldMs)
                    {
                        t.Speaking = false;
                        stateChange = true;
                    }
                }
                else
                {
                    t.BelowSince = null; // 迟滞带内保持原状态
                }
            }

            // 3) 规则汇总 + 渐变
            var winners = ComputeWinners(config, GroupSoundingMap(settings, now));
            float maxStep = FadeTickMs / (float)Math.Max(1, settings.FadeDurationMs);
            foreach (var t in _tracked.Values)
            {
                var rule = winners.TryGetValue(t.GroupId, out var r) && r.TriggerGroupId != t.GroupId ? r : null;
                float target;
                if (rule is not null)
                {
                    if (!t.Ducked)
                    {
                        // 刚结束闪避时渐变可能尚未走完，此时音量是中间值；保留原锚点防止"原始音量"被逐渐污染
                        var sinceUnduck = (now - t.UnduckAt).TotalMilliseconds;
                        if (sinceUnduck >= settings.FadeDurationMs)
                            t.OriginalVolume = ReadVolume(t); // 记录用户原始音量
                        t.Ducked = true;
                        stateChange = true;
                    }
                    target = Math.Clamp(rule.TargetVolumePercent / 100f, 0f, 1f);
                    t.TargetVolume = target;
                }
                else
                {
                    if (t.Ducked)
                    {
                        t.Ducked = false;
                        t.UnduckAt = now;
                        stateChange = true;
                    }
                    target = t.OriginalVolume;
                    t.TargetVolume = null;
                }

                if (t.CurrentVolume < 0)
                    t.CurrentVolume = ReadVolume(t);
                var next = MoveToward(t.CurrentVolume, target, maxStep);
                if (Math.Abs(next - t.CurrentVolume) > 0.0015)
                    t.Control?.SetVolume(next);
                t.CurrentVolume = next;
            }
        }

        return stateChange;
    }

    /// <summary>
    /// 组级"出声/在播"判定（用于规则触发），三层信号：
    /// 1. 峰值出声（即时、任何应用可用）；
    /// 2. SMTC 播放状态（应用自报"还在播放"，覆盖歌间静音/极弱段落；报告"已暂停/已停止"则立即解除，不进宽限）；
    /// 3. 静音宽限（仅对没有 SMTC 信号的应用兜底，避免短暂静音误判为停止）。
    /// </summary>
    private Dictionary<string, bool> GroupSoundingMap(EngineSettings settings, DateTime now)
    {
        var map = new Dictionary<string, bool>();
        foreach (var g in _tracked.Values.GroupBy(t => t.GroupId))
        {
            if (g.Any(t => t.Speaking))
            {
                _lastSounding[g.Key] = now;
                map[g.Key] = true;
                continue;
            }

            if (settings.UseMediaSessionStatus)
            {
                bool smtcPlaying = false;
                bool smtcKnown = false;
                foreach (var t in g)
                {
                    var playing = _mediaTracker.IsPlaying(t.Session.ProcessName);
                    if (playing is null)
                        continue;
                    smtcKnown = true;
                    if (playing == true)
                    {
                        smtcPlaying = true;
                        break;
                    }
                }
                if (smtcKnown)
                {
                    // 应用自己声明了播放状态：播放中→保持；已暂停/已停止→立即解除
                    if (smtcPlaying)
                    {
                        _lastSounding[g.Key] = now;
                        map[g.Key] = true;
                    }
                    continue;
                }
            }

            // 宽限兜底：无 SMTC 信号的应用，按音频流是否仍打开取宽限时长
            if (!_lastSounding.TryGetValue(g.Key, out var lastSounding))
                continue;
            double silenceMs = (now - lastSounding).TotalMilliseconds;
            int graceMs = g.Any(t => t.Session.State == Audio.SessionState.Active)
                ? Math.Max(0, settings.StreamOpenSilenceGraceMs)
                : Math.Max(0, settings.SilenceGraceMs);
            if (silenceMs < graceMs)
                map[g.Key] = true;
        }
        return map;
    }

    /// <summary>汇总生效规则：触发组出声 → 规则生效；同一目标组多条生效时优先级高者胜（同优先级取先配置者）。</summary>
    private static Dictionary<string, DuckingRuleConfig> ComputeWinners(
        TunerConfig config, Dictionary<string, bool> groupSpeaking)
    {
        var winners = new Dictionary<string, DuckingRuleConfig>();
        foreach (var rule in config.Rules)
        {
            if (!rule.Enabled)
                continue;
            if (!groupSpeaking.TryGetValue(rule.TriggerGroupId, out var speaking) || !speaking)
                continue;
            if (winners.TryGetValue(rule.TargetGroupId, out var current) && current.Priority >= rule.Priority)
                continue;
            winners[rule.TargetGroupId] = rule;
        }
        return winners;
    }

    private string ResolveGroup(TunerConfig config, SoundSession session)
    {
        var name = ConfigStore.NormalizeProcessName(session.ProcessName);
        AppGroupConfig? fallback = null;
        foreach (var g in config.Groups)
        {
            if (g.IsDefault)
                fallback ??= g;
            foreach (var p in g.ProcessNames)
                if (ConfigStore.NormalizeProcessName(p) == name)
                    return g.Id;
        }
        return fallback?.Id ?? "default";
    }

    private static float ReadVolume(Tracked t)
    {
        var v = t.Control?.GetVolume() ?? -1f;
        if (v >= 0)
            return v;
        v = t.Session.Volume;
        return v is >= 0 and <= 1 ? v : 1f;
    }

    private static float MoveToward(float current, float target, float maxStep)
    {
        var d = target - current;
        if (Math.Abs(d) <= maxStep)
            return target;
        return current + Math.Sign(d) * maxStep;
    }

    private SessionState ToState(Tracked t)
    {
        lock (_gate)
        {
            var groupName = _config.Groups.FirstOrDefault(g => g.Id == t.GroupId)?.Name ?? t.GroupId;
            return new SessionState
            {
                InstanceId = t.Session.InstanceId,
                Pid = t.Session.Pid,
                ProcessName = t.Session.ProcessName,
                GroupId = t.GroupId,
                GroupName = groupName,
                Speaking = t.Speaking,
                Ducked = t.Ducked,
                CurrentVolume = t.CurrentVolume,
                TargetVolume = t.TargetVolume,
                OriginalVolume = t.OriginalVolume,
            };
        }
    }
}
