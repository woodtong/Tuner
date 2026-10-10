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

        // 归一化进程名/分组缓存：进程名不变就逐拍复用（引擎 50ms 一拍，重算纯属浪费）
        public string NormSource = "";   // 生成 NormName 时的原始进程名
        public string NormName = "";
        public TunerConfig? GroupResolvedFor; // 生成 GroupId 时的配置（换配置即失效）
    }

    /// <summary>规则的预解析匹配数据：键与选择器归一化结果只在换配置时算一次。</summary>
    private sealed class RulePlan
    {
        public DuckingRuleConfig Rule = null!;
        public string TriggerKey = "";
        public string TargetKey = "";
        public bool TriggerIsApp, TargetIsApp;
        public string TriggerNormApp = "", TargetNormApp = "";
        public string TriggerGroupId = "", TargetGroupId = "";
    }

    /// <summary>引擎内部状态变化（出声切换、闪避切换、成员/分组变化），供 UI 刷新。</summary>
    public event Action? EngineStateChanged;

    public event Action<string>? Diagnostics;

    private const int FadeTickMs = 50;

    private readonly AudioSessionMonitor _monitor;
    private readonly object _gate = new();
    private readonly Dictionary<string, Tracked> _tracked = new();
    private readonly Dictionary<string, DateTime> _lastSounding = new(); // 分组最近一次真实出声时刻
    private readonly HashSet<string> _sounding = new(StringComparer.Ordinal); // 本拍生效选择器键（复用免分配）
    private readonly MediaSessionTracker _mediaTracker = new();
    private TunerConfig _config;

    // 按 _config 引用缓存的规则计划与进程→分组映射（换配置才重建）
    private TunerConfig? _planFor;
    private List<RulePlan>? _plan;
    private Dictionary<string, string>? _groupByProc;
    private string? _defaultGroupId;

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
            {
                var groupNames = new Dictionary<string, string>();
                foreach (var g in _config.Groups)
                    groupNames[g.Id] = g.Name;
                return _tracked.Values.Select(t => ToState(t, groupNames)).ToArray();
            }
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
        var sessions = _monitor.CurrentSnapshot;

        lock (_gate)
        {
            var config = _config;
            var settings = config.Settings;
            var now = DateTime.UtcNow;

            // 0) 规则计划（换配置才重建）+ 规则评估：按选择器（分组或单应用）汇总"出声或在播"
            EnsurePlan(config);
            var plan = _plan!;
            _sounding.Clear();
            EvaluateSelectors(plan, settings, now);
            var sounding = _sounding;

            // 1) 成员差量 + 归一名/分组缓存维护（仅在进程名或配置变化时重算）
            var seen = new HashSet<string>();
            foreach (var s in sessions)
            {
                seen.Add(s.InstanceId);
                if (_tracked.TryGetValue(s.InstanceId, out var t))
                {
                    t.Session = s;
                    t.Control ??= _monitor.TryGetControl(s.InstanceId);
                }
                else
                {
                    t = new Tracked
                    {
                        Session = s,
                        Control = _monitor.TryGetControl(s.InstanceId),
                    };
                    RefreshNormAndGroup(t, config);
                    var v = ReadVolume(t);
                    t.CurrentVolume = v;
                    t.OriginalVolume = v;

                    // 短促声音对策：新会话已被某条生效规则的目标命中 → 创建即初始化音量
                    var initRule = EvaluateWinner(t, plan, sounding);
                    if (initRule is not null)
                    {
                        t.OriginalVolume = v;
                        t.Ducked = true;
                        t.CurrentVolume = Math.Clamp(initRule.TargetVolumePercent / 100f, 0f, 1f);
                        t.Control?.SetVolume(t.CurrentVolume);
                    }
                    _tracked[s.InstanceId] = t;
                    stateChange = true;
                    continue;
                }

                if (!ReferenceEquals(t.GroupResolvedFor, config) ||
                    !string.Equals(t.NormSource, s.ProcessName, StringComparison.Ordinal))
                {
                    var oldGroup = t.GroupId;
                    RefreshNormAndGroup(t, config);
                    if (oldGroup != t.GroupId)
                        stateChange = true;
                }
            }
            foreach (var gone in _tracked.Keys.Where(id => !seen.Contains(id)).ToArray())
            {
                _tracked.Remove(gone); // 会话消失，音量随会话终结，无需还原
                stateChange = true;
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

            // 3) 逐会话挑选生效规则并渐变（压低走"闪避开始"时长，恢复走"闪避结束"时长）
            foreach (var t in _tracked.Values)
            {
                var rule = EvaluateWinner(t, plan, sounding);
                float target;
                if (rule is not null)
                {
                    if (!t.Ducked)
                    {
                        // 刚结束闪避时渐变可能尚未走完，此时音量是中间值；保留原锚点防止"原始音量"被逐渐污染
                        var sinceUnduck = (now - t.UnduckAt).TotalMilliseconds;
                        if (sinceUnduck >= settings.FadeInDurationMs)
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
                // 闪避阶段（规则生效）走"开始"时长，恢复阶段走"结束"时长——按阶段而非数值方向，
                // 因为目标音量可能高于当前音量（用户原音量比闪避目标还低）
                var fadeMs = t.Ducked ? settings.FadeOutDurationMs : settings.FadeInDurationMs;
                var maxStep = FadeTickMs / (float)Math.Max(1, fadeMs);
                var next = MoveToward(t.CurrentVolume, target, maxStep);
                if (Math.Abs(next - t.CurrentVolume) > 0.0015)
                    t.Control?.SetVolume(next);
                t.CurrentVolume = next;
            }
        }

        return stateChange;
    }

    /// <summary>
    /// 评估各选择器（分组或单应用）是否"在播"，结果写入 _sounding。键含判定方式。
    /// state（状态判定，默认）：SMTC 报告"播放中"→在播；已暂停/已停止→立即解除（无宽限）；
    ///   无 SMTC 信号的应用以会话 Active 兜底。不看峰值。
    /// sound（声音判定）：峰值迟滞（双阈值+低电平保持）+ 静音宽限。
    /// </summary>
    private void EvaluateSelectors(List<RulePlan> plan, EngineSettings settings, DateTime now)
    {
        // 同一选择器可能被多条规则共用：按键去重，只评估一次
        var byKey = new Dictionary<string, (RulePlan P, bool Trig)>(StringComparer.Ordinal);
        foreach (var p in plan)
        {
            if (byKey.ContainsKey(p.TriggerKey))
                byKey[p.TriggerKey] = (p, true);
            else
                byKey.Add(p.TriggerKey, (p, true));
            if (byKey.ContainsKey(p.TargetKey))
                byKey[p.TargetKey] = (p, false);
            else
                byKey.Add(p.TargetKey, (p, false));
        }

        foreach (var (key, (p, trig)) in byKey)
        {
            bool isApp = trig ? p.TriggerIsApp : p.TargetIsApp;
            string normApp = trig ? p.TriggerNormApp : p.TargetNormApp;
            string groupId = trig ? p.TriggerGroupId : p.TargetGroupId;

            // 单遍扫描成员（不物化列表）：出声峰值命中 / SMTC 已知且在播 / 会话 Active
            // （SMTC 只在 state 模式下查询，与原实现一致）
            bool stateMode = p.Rule.DetectionMode == "state";
            bool anyMember = false, anySpeaking = false, anyActive = false;
            bool anyKnown = false, anyPlaying = false;
            foreach (var t in _tracked.Values)
            {
                if (isApp ? t.NormName != normApp : t.GroupId != groupId)
                    continue;
                anyMember = true;
                if (t.Speaking)
                    anySpeaking = true;
                if (t.Session.State == Audio.SessionState.Active)
                    anyActive = true;
                if (stateMode && settings.UseMediaSessionStatus && !anyPlaying)
                {
                    var playing = _mediaTracker.IsPlaying(t.Session.ProcessName);
                    if (playing is not null)
                    {
                        anyKnown = true;
                        if (playing == true)
                            anyPlaying = true;
                    }
                }
            }

            if (!anyMember)
                continue; // 无成员：无从在播（键计时由下方清理）

            if (stateMode)
            {
                // 应用自报了状态 → 以自报为准（暂停/停止立即解除）；未知 → 会话 Active 兜底
                bool sound = anyKnown ? anyPlaying : anyActive;
                if (sound)
                    _sounding.Add(key);
                continue; // 状态判定：无宽限、不看峰值
            }

            // sound 模式：峰值迟滞 + 静音宽限
            if (anySpeaking)
            {
                _lastSounding[key] = now;
                _sounding.Add(key);
                continue;
            }
            if (!_lastSounding.TryGetValue(key, out var lastSounding))
                continue;
            double silenceMs = (now - lastSounding).TotalMilliseconds;
            int graceMs = anyActive
                ? Math.Max(0, settings.StreamOpenSilenceGraceMs)
                : Math.Max(0, settings.SilenceGraceMs);
            if (silenceMs < graceMs)
                _sounding.Add(key);
        }

        // 清理不再被任何规则使用的选择器计时
        foreach (var gone in _lastSounding.Keys.Where(k => !byKey.ContainsKey(k)).ToArray())
            _lastSounding.Remove(gone);
    }

    /// <summary>会话是否命中计划中的选择器：应用模式按归一化进程名，分组模式按解析后的分组。</summary>
    private static bool PlanMatches(RulePlan p, bool trigger, Tracked t)
    {
        if (trigger)
            return p.TriggerIsApp ? t.NormName == p.TriggerNormApp : t.GroupId == p.TriggerGroupId;
        return p.TargetIsApp ? t.NormName == p.TargetNormApp : t.GroupId == p.TargetGroupId;
    }

    /// <summary>
    /// 为单个会话挑选生效规则：触发选择器"在播"、目标选择器命中且不是触发者自身。
    /// 优先级高者胜；同级时应用级规则优先于分组级；再同级取先配置者。
    /// </summary>
    private DuckingRuleConfig? EvaluateWinner(
        Tracked t, List<RulePlan> plan, HashSet<string> sounding)
    {
        DuckingRuleConfig? winner = null;
        foreach (var p in plan)
        {
            if (!sounding.Contains(p.TriggerKey))
                continue;
            if (PlanMatches(p, true, t))
                continue; // 不闪避触发者自身
            if (!PlanMatches(p, false, t))
                continue;
            if (winner is null || Better(p.Rule, winner))
                winner = p.Rule;
        }
        return winner;
    }

    /// <summary>换配置时重建规则计划与进程→分组映射（键/归一化名等不变量只在此处计算）。</summary>
    private void EnsurePlan(TunerConfig config)
    {
        if (ReferenceEquals(_planFor, config))
            return;

        _plan = new List<RulePlan>();
        foreach (var r in config.Rules)
        {
            if (!r.Enabled || r.Trigger is null || r.Target is null)
                continue;
            _plan.Add(new RulePlan
            {
                Rule = r,
                TriggerKey = SoundingKey(r, r.Trigger),
                TargetKey = SoundingKey(r, r.Target),
                TriggerIsApp = r.Trigger.Type == "app",
                TargetIsApp = r.Target.Type == "app",
                TriggerNormApp = ConfigStore.NormalizeProcessName(r.Trigger.ProcessName ?? ""),
                TargetNormApp = ConfigStore.NormalizeProcessName(r.Target.ProcessName ?? ""),
                TriggerGroupId = r.Trigger.GroupId ?? "",
                TargetGroupId = r.Target.GroupId ?? "",
            });
        }

        _groupByProc = new Dictionary<string, string>(StringComparer.Ordinal);
        _defaultGroupId = null;
        foreach (var g in config.Groups)
        {
            _defaultGroupId ??= g.IsDefault ? g.Id : null;
            foreach (var p in g.ProcessNames)
            {
                var norm = ConfigStore.NormalizeProcessName(p);
                // 与原解析一致：按配置顺序首个命中的分组生效
                if (!_groupByProc.ContainsKey(norm))
                    _groupByProc[norm] = g.Id;
            }
        }

        _planFor = config;
    }

    /// <summary>刷新会话的归一化进程名与分组缓存（仅在进程名或配置变化时调用）。</summary>
    private void RefreshNormAndGroup(Tracked t, TunerConfig config)
    {
        t.NormSource = t.Session.ProcessName;
        t.NormName = ConfigStore.NormalizeProcessName(t.Session.ProcessName);
        t.GroupId = ResolveGroup(config, t.NormName);
        t.GroupResolvedFor = config;
    }

    private static string SoundingKey(DuckingRuleConfig rule, RuleRef selector) =>
        (rule.DetectionMode == "state" ? "state:" : "sound:") + ConfigStore.RefKey(selector);

    private static bool Better(DuckingRuleConfig a, DuckingRuleConfig b) =>
        a.Priority > b.Priority ||
        (a.Priority == b.Priority && a.Target!.Type == "app" && b.Target!.Type != "app");

    /// <summary>按归一化进程名解析分组：查 EnsurePlan 建好的映射，未匹配落默认组。</summary>
    private string ResolveGroup(TunerConfig config, string normName)
    {
        if (_groupByProc is not null && _groupByProc.TryGetValue(normName, out var gid))
            return gid;
        return _defaultGroupId ?? "default";
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

    private static SessionState ToState(Tracked t, Dictionary<string, string> groupNames)
    {
        return new SessionState
        {
            InstanceId = t.Session.InstanceId,
            Pid = t.Session.Pid,
            ProcessName = t.Session.ProcessName,
            GroupId = t.GroupId,
            GroupName = groupNames.GetValueOrDefault(t.GroupId, t.GroupId),
            Speaking = t.Speaking,
            Ducked = t.Ducked,
            CurrentVolume = t.CurrentVolume,
            TargetVolume = t.TargetVolume,
            OriginalVolume = t.OriginalVolume,
        };
    }
}
