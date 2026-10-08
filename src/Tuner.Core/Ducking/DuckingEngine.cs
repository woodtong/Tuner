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
        ConfigStore.Normalize(config); // 旧字段迁移（TriggerGroupId → Trigger 选择器）
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
            // 退出还原：凡被引擎写过的会话（闪避中，或渐变回程被中断）都恢复到用户原始音量
            int restored = 0;
            foreach (var t in _tracked.Values.Where(EngineControlling))
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

    /// <summary>
    /// 用户在混音台手动调整某会话音量：若引擎正接管该会话（闪避中，或正在渐变回原音量），
    /// 仅更新其"恢复锚点"（渐变结束时回到用户调整后的值），实际音量仍由规则接管；
    /// 引擎未接管的会话无需处理（引擎不写，不会覆盖用户的调整）。
    /// </summary>
    public void SetUserVolume(string instanceId, float volume)
    {
        lock (_gate)
        {
            if (_tracked.TryGetValue(instanceId, out var t) && EngineControlling(t))
                t.OriginalVolume = Math.Clamp(volume, 0f, 1f);
        }
    }

    /// <summary>引擎是否正在写该会话的音量（闪避中，或朝恢复锚点渐变中）。</summary>
    public bool IsVolumeControlled(string instanceId)
    {
        lock (_gate)
            return _tracked.TryGetValue(instanceId, out var t) && EngineControlling(t);
    }

    private static bool EngineControlling(Tracked t) =>
        t.Ducked || Math.Abs(t.CurrentVolume - t.OriginalVolume) > 0.005f;

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

            // 0) 规则评估：按选择器（分组或单应用）汇总"出声或在播"
            var activeRules = config.Rules
                .Where(r => r.Enabled && r.Trigger is not null && r.Target is not null)
                .ToList();
            var sounding = EvaluateSelectors(activeRules, settings, now);

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

                    // 短促声音对策：新会话已被某条生效规则的目标命中 → 创建即初始化音量
                    var initRule = EvaluateWinner(t, activeRules, sounding);
                    if (initRule is not null)
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

            // 3) 逐会话挑选生效规则并渐变
            float maxStep = FadeTickMs / (float)Math.Max(1, settings.FadeDurationMs);
            foreach (var t in _tracked.Values)
            {
                var rule = EvaluateWinner(t, activeRules, sounding);
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
    /// 评估各选择器（分组或单应用）是否"在播"，返回生效选择器键集合。键含判定方式。
    /// state（状态判定，默认）：SMTC 报告"播放中"→在播；已暂停/已停止→立即解除（无宽限）；
    ///   无 SMTC 信号的应用以会话 Active 兜底。不看峰值。
    /// sound（声音判定）：峰值迟滞（双阈值+低电平保持）+ 静音宽限。
    /// </summary>
    private HashSet<string> EvaluateSelectors(List<DuckingRuleConfig> rules, EngineSettings settings, DateTime now)
    {
        var sounding = new HashSet<string>(StringComparer.Ordinal);
        var byKey = new Dictionary<string, (RuleRef Sel, string Mode)>(StringComparer.Ordinal);
        foreach (var r in rules)
        {
            byKey[SoundingKey(r, r.Trigger!)] = (r.Trigger!, r.DetectionMode);
            byKey[SoundingKey(r, r.Target!)] = (r.Target!, r.DetectionMode);
        }

        foreach (var (key, (selector, mode)) in byKey)
        {
            List<Tracked> matches;
            if (selector.Type == "app")
            {
                var norm = ConfigStore.NormalizeProcessName(selector.ProcessName);
                matches = _tracked.Values
                    .Where(t => ConfigStore.NormalizeProcessName(t.Session.ProcessName) == norm)
                    .ToList();
            }
            else
            {
                matches = _tracked.Values.Where(t => t.GroupId == selector.GroupId).ToList();
            }

            if (matches.Count == 0)
                continue; // 无成员：无从在播（键计时由下方清理）

            if (mode == "state")
            {
                bool sound;
                if (settings.UseMediaSessionStatus)
                {
                    bool anyKnown = false, anyPlaying = false;
                    foreach (var t in matches)
                    {
                        var playing = _mediaTracker.IsPlaying(t.Session.ProcessName);
                        if (playing is null)
                            continue;
                        anyKnown = true;
                        if (playing == true)
                        {
                            anyPlaying = true;
                            break;
                        }
                    }
                    // 应用自报了状态 → 以自报为准（暂停/停止立即解除）；未知 → 会话 Active 兜底
                    sound = anyKnown ? anyPlaying : matches.Any(t => t.Session.State == Audio.SessionState.Active);
                }
                else
                {
                    sound = matches.Any(t => t.Session.State == Audio.SessionState.Active);
                }
                if (sound)
                    sounding.Add(key);
                continue; // 状态判定：无宽限、不看峰值
            }

            // sound 模式：峰值迟滞 + 静音宽限
            if (matches.Any(t => t.Speaking))
            {
                _lastSounding[key] = now;
                sounding.Add(key);
                continue;
            }
            if (!_lastSounding.TryGetValue(key, out var lastSounding))
                continue;
            double silenceMs = (now - lastSounding).TotalMilliseconds;
            int graceMs = matches.Any(t => t.Session.State == Audio.SessionState.Active)
                ? Math.Max(0, settings.StreamOpenSilenceGraceMs)
                : Math.Max(0, settings.SilenceGraceMs);
            if (silenceMs < graceMs)
                sounding.Add(key);
        }

        // 清理不再被任何规则使用的选择器计时
        foreach (var gone in _lastSounding.Keys.Where(k => !byKey.ContainsKey(k)).ToArray())
            _lastSounding.Remove(gone);

        return sounding;
    }

    private static string SoundingKey(DuckingRuleConfig rule, RuleRef selector) =>
        (rule.DetectionMode == "state" ? "state:" : "sound:") + ConfigStore.RefKey(selector);

    /// <summary>会话是否匹配选择器：分组模式按解析后的分组，应用模式按归一化进程名。</summary>
    private static bool RefMatchesSession(RuleRef selector, SoundSession session, string sessionGroupId) =>
        selector.Type == "app"
            ? ConfigStore.NormalizeProcessName(selector.ProcessName) == ConfigStore.NormalizeProcessName(session.ProcessName)
            : selector.GroupId == sessionGroupId;

    /// <summary>
    /// 为单个会话挑选生效规则：触发选择器"在播"、目标选择器命中且不是触发者自身。
    /// 优先级高者胜；同级时应用级规则优先于分组级；再同级取先配置者。
    /// </summary>
    private DuckingRuleConfig? EvaluateWinner(
        Tracked t, List<DuckingRuleConfig> rules, HashSet<string> sounding)
    {
        DuckingRuleConfig? winner = null;
        foreach (var r in rules)
        {
            if (!sounding.Contains(SoundingKey(r, r.Trigger!)))
                continue;
            if (RefMatchesSession(r.Trigger!, t.Session, t.GroupId))
                continue; // 不闪避触发者自身
            if (!RefMatchesSession(r.Target!, t.Session, t.GroupId))
                continue;
            if (winner is null || Better(r, winner))
                winner = r;
        }
        return winner;
    }

    private static bool Better(DuckingRuleConfig a, DuckingRuleConfig b) =>
        a.Priority > b.Priority ||
        (a.Priority == b.Priority && a.Target!.Type == "app" && b.Target!.Type != "app");

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
