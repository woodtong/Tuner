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
        /// <summary>用户已手动接管：本轮闪避不干预该会话，规则重新触发后恢复。</summary>
        public required bool UserTookOver { get; init; }
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
        public bool UserTookOver;  // 手动接管：本轮闪避不干预，规则解除即复位
        public DateTime UnduckAt = DateTime.MinValue; // 上次结束闪避的时刻（锚点保护用）
        public float OriginalVolume = 1f;
        public float CurrentVolume = -1f; // -1 = 未知
        public float? TargetVolume;       // 非 null = 正在闪避

        // 当前生效的渐变节奏：来自正在命中的规则（含规则级覆盖），无规则时沿用上一条（恢复阶段），
        // 会话创建时以全局设置为初值
        public int ActiveFadeOut = 400;
        public int ActiveFadeIn = 1200;

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
        public string Label = ""; // 诊断事件用：「触发 → 目标」可读描述
        public string DeviceCond = ""; // 设备条件（已 trim；空=无条件），每拍与当前设备名比对
        public bool ConditionOk = true;
    }

    /// <summary>选择器"在播"评估项：由规则计划按键去重而来，换配置才重建（评估每拍进行，不再逐拍建表）。</summary>
    private sealed class SelectorPlan
    {
        public string Key = "";
        public bool IsApp;
        public string NormApp = "";
        public string GroupId = "";
        public bool StateMode;
    }

    /// <summary>引擎内部状态变化（出声切换、闪避切换、成员/分组变化），供 UI 刷新。</summary>
    public event Action? EngineStateChanged;

    public event Action<string>? Diagnostics;

    /// <summary>闪避暂停状态切换（SetSuspended 生效后触发，可能在调用线程）。</summary>
    public event Action? SuspendedChanged;

    /// <summary>闪避是否处于全局暂停（暂停期间不评估规则、不写任何音量）。</summary>
    public bool Suspended => _suspended;

    private const int FadeTickMs = 50;

    private readonly AudioSessionMonitor _monitor;      // 默认播放设备（多媒体角色，主设备）
    private readonly AudioSessionMonitor? _comms;       // 默认通信设备（可空；同一物理设备时靠 InstanceId 去重）
    private readonly object _gate = new();
    private readonly Dictionary<string, Tracked> _tracked = new();
    private readonly Dictionary<string, DateTime> _lastSounding = new(); // 分组最近一次真实出声时刻
    private readonly HashSet<string> _sounding = new(StringComparer.Ordinal); // 本拍生效选择器键（复用免分配）
    // 每拍复用的遍历缓冲（引擎 50ms 一拍，避免逐拍分配）
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly List<string> _gone = new();
    private readonly MediaSessionTracker _mediaTracker = new();
    private TunerConfig _config;
    private volatile bool _suspended;
    private string? _lastDeviceName; // 设备切换诊断事件（名称变化即重报）

    // 规则"生效期"记账：规则从不生效→生效记一轮，全部目标脱离才记解除——
    // 诊断事件按轮去重（每轮只各报一次），也是暂停/恢复不误报的依据
    private readonly Dictionary<string, long> _ruleEpisode = new(StringComparer.Ordinal); // 规则Id → 当前轮序号
    private readonly HashSet<string> _engagedNow = new(StringComparer.Ordinal); // 本拍命中的规则
    private readonly HashSet<string> _engagedNew = new(StringComparer.Ordinal); // 本拍新进入生效的规则

    // 按 _config 引用缓存的规则计划与进程→分组映射（换配置才重建）
    private TunerConfig? _planFor;
    private List<RulePlan>? _plan;
    private Dictionary<string, RulePlan>? _planById; // 规则Id → 计划（诊断事件查 Label 用）
    private List<SelectorPlan>? _selectors;
    private HashSet<string>? _selectorKeys;
    private Dictionary<string, string>? _groupByProc;
    private string? _defaultGroupId;
    private Dictionary<string, string>? _groupNames; // 分组 Id→名称（UI 快照用，换配置才重建）
    private CurveSpec _globalCurve = CurveSpec.Linear; // 全局音量曲线（换配置才重建）
    private bool _conflictDeepest; // 多规则裁决语义（RuleConflict=="deepest"，换配置才重建）

    private Thread? _thread;
    private CancellationTokenSource? _cts;

    public DuckingEngine(AudioSessionMonitor monitor, TunerConfig config, AudioSessionMonitor? commsMonitor = null)
    {
        _monitor = monitor;
        _comms = commsMonitor;
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
                EnsurePlan(_config); // 首拍未跑时也能给出正确分组名（幂等：引用相同即跳过）
                return _tracked.Values.Select(t => ToState(t, _groupNames!)).ToArray();
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

    /// <summary>
    /// 全局暂停/恢复闪避（用户一键停用全部规则）。
    /// 暂停时立即把所有被引擎接管的会话还原到用户原始音量；恢复后以真实音量为基准重新评估，
    /// 触发源仍在出声则重新进入闪避（原始音量锚点重新捕获）。
    /// </summary>
    public void SetSuspended(bool suspended)
    {
        lock (_gate)
        {
            if (_suspended == suspended)
                return;
            _suspended = suspended;
            var now = DateTime.UtcNow;
            if (suspended)
            {
                foreach (var t in _tracked.Values.Where(EngineControlling))
                {
                    t.Control?.SetVolume(t.OriginalVolume);
                    t.CurrentVolume = t.OriginalVolume;
                    t.Ducked = false;
                    t.TargetVolume = null;
                    t.UnduckAt = now;
                }
            }
            else
            {
                foreach (var t in _tracked.Values)
                    t.CurrentVolume = ReadVolume(t); // 丢弃暂停前的旧基准，以真实音量重新起算
            }
        }
        Diagnostics?.Invoke(suspended
            ? "闪避已暂停：全部规则停用，被压音量已还原"
            : "闪避已恢复：规则重新生效");
        SuspendedChanged?.Invoke();
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
    /// 用户在混音台手动调整某会话音量。引擎正接管该会话（闪避中，或正在渐变回原音量）时：
    /// 默认仅更新其"恢复锚点"（渐变结束时回到用户调整后的值），实际音量仍由规则接管；
    /// 开启手动接管（Settings.ManualTakeover）后，用户调节即视为接管——本轮闪避不再干预该会话，
    /// 音量以用户值为准（调用方随后直写生效），规则解除后的下一次触发恢复闪避。
    /// 引擎未接管的会话无需处理（引擎不写，不会覆盖用户的调整）。
    /// </summary>
    public void SetUserVolume(string instanceId, float volume)
    {
        lock (_gate)
        {
            if (!_tracked.TryGetValue(instanceId, out var t) || !EngineControlling(t))
                return;
            var v = Math.Clamp(volume, 0f, 1f);
            if (_config.Settings.ManualTakeover)
            {
                if (!t.UserTookOver)
                {
                    t.UserTookOver = true;
                    Diagnostics?.Invoke($"用户手动接管：{t.Session.ProcessName} 本轮闪避不再干预（规则重新触发后恢复）");
                }
                t.Ducked = false;
                t.TargetVolume = null;
                t.OriginalVolume = v;
                t.CurrentVolume = v;
                return;
            }
            t.OriginalVolume = v;
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
        var sessions = MergedSnapshot();

        lock (_gate)
        {
            var config = _config;
            var settings = config.Settings;
            var now = DateTime.UtcNow;

            // 0) 规则计划（换配置才重建）+ 规则评估：按选择器（分组或单应用）汇总"出声或在播"
            EnsurePlan(config);
            var plan = _plan!;
            _sounding.Clear();
            if (!_suspended)
                EvaluateSelectors(_selectors!, settings, now);
            var sounding = _sounding;

            // 设备切换诊断事件（名称变化即重报；首个名称不报——那是正常启动）
            var deviceName = _monitor.DeviceName;
            if (!string.Equals(_lastDeviceName, deviceName, StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(_lastDeviceName))
                    Diagnostics?.Invoke($"输出设备切换：{_lastDeviceName} → {deviceName}");
                _lastDeviceName = deviceName;
            }

            // 1) 成员差量 + 归一名/分组缓存维护（仅在进程名或配置变化时重算）
            var seen = _seen;
            seen.Clear();
            foreach (var s in sessions)
            {
                seen.Add(s.InstanceId);
                if (_tracked.TryGetValue(s.InstanceId, out var t))
                {
                    t.Session = s;
                    t.Control ??= _monitor.TryGetControl(s.InstanceId) ?? _comms?.TryGetControl(s.InstanceId);
                }
                else
                {
                    t = new Tracked
                    {
                        Session = s,
                        Control = _monitor.TryGetControl(s.InstanceId) ?? _comms?.TryGetControl(s.InstanceId),
                        ActiveFadeOut = Math.Max(50, settings.FadeOutDurationMs),
                        ActiveFadeIn = Math.Max(50, settings.FadeInDurationMs),
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
                        t.CurrentVolume = VolumeCurve.ToVolume(_globalCurve, initRule.TargetVolumePercent / 100f);
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
            _gone.Clear();
            foreach (var id in _tracked.Keys)
                if (!seen.Contains(id))
                    _gone.Add(id);
            foreach (var gone in _gone)
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

            // 暂停期间到此为止：成员/出声状态仍维护（界面照常显示），但不评估规则、不写任何音量
            if (_suspended)
                return stateChange;

            // 设备条件预评估（每拍一次）：规则的 Condition=="device" 时与当前主设备名做子串比对
            var deviceNameForCondition = _monitor.DeviceName;
            foreach (var p in plan)
                p.ConditionOk = p.DeviceCond.Length == 0 ||
                                deviceNameForCondition.Contains(p.DeviceCond, StringComparison.OrdinalIgnoreCase);

            // 3) 逐会话挑选生效规则并渐变（压低走"闪避开始"时长，恢复走"闪避结束"时长；
            //    时长优先取规则级覆盖，恢复阶段沿用刚解除规则的覆盖值——不同规则可有不同节奏）
            _engagedNow.Clear();
            _engagedNew.Clear();
            foreach (var t in _tracked.Values)
            {
                var rule = EvaluateWinner(t, plan, sounding);
                if (rule is not null && _engagedNow.Add(rule.Id) && !_ruleEpisode.ContainsKey(rule.Id))
                    _engagedNew.Add(rule.Id); // 本拍从"未生效"进入"生效"：记一轮新的生效期

                // 手动接管：音量完全由用户控制，本轮不评估不写（规则命中仍记账）；规则解除即复位，
                // 下一次触发重新接管
                if (t.UserTookOver)
                {
                    if (rule is not null)
                        continue;
                    t.UserTookOver = false;
                }

                float target;
                if (rule is not null)
                {
                    if (!t.Ducked)
                    {
                        // 刚结束闪避时渐变可能尚未走完，此时音量是中间值；保留原锚点防止"原始音量"被逐渐污染
                        // （保护窗口用上一轮的恢复时长——含规则级覆盖）
                        var sinceUnduck = (now - t.UnduckAt).TotalMilliseconds;
                        if (sinceUnduck >= t.ActiveFadeIn)
                            t.OriginalVolume = ReadVolume(t); // 记录用户原始音量
                        t.Ducked = true;
                        stateChange = true;
                    }
                    // 规则级时长覆盖（留空跟随全局）；恢复阶段沿用上一条生效规则的值
                    t.ActiveFadeOut = Math.Max(50, rule.FadeOutMs ?? settings.FadeOutDurationMs);
                    t.ActiveFadeIn = Math.Max(50, rule.FadeInMs ?? settings.FadeInDurationMs);
                    // 规则目标音量是曲线上的位置，按全局音量曲线换算为实际振幅
                    target = VolumeCurve.ToVolume(_globalCurve, rule.TargetVolumePercent / 100f);
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
                var fadeMs = t.Ducked ? t.ActiveFadeOut : t.ActiveFadeIn;
                var maxStep = FadeTickMs / (float)Math.Max(1, fadeMs);
                var next = MoveToward(t.CurrentVolume, target, maxStep);
                if (Math.Abs(next - t.CurrentVolume) > 0.0015)
                    t.Control?.SetVolume(next);
                t.CurrentVolume = next;
            }

            // 4) 规则生效期记账 + 诊断事件（每轮生效/解除各报一次，不逐拍刷屏）
            EmitRuleEpisodes();
        }

        return stateChange;
    }

    /// <summary>把"本拍命中规则集"与生效期台账对账：新进入→分配轮次并发事件，全部脱离→销账并发事件。</summary>
    private void EmitRuleEpisodes()
    {
        foreach (var id in _engagedNew)
            _ruleEpisode[id] = Environment.TickCount64;
        foreach (var id in _engagedNew)
        {
            if (_planById!.TryGetValue(id, out var p))
                Diagnostics?.Invoke($"规则生效：{p.Label} → 压至 {p.Rule.TargetVolumePercent:F0}%");
        }
        if (_ruleEpisode.Count > 0)
        {
            _gone.Clear();
            foreach (var k in _ruleEpisode.Keys)
                if (!_engagedNow.Contains(k))
                    _gone.Add(k);
            foreach (var k in _gone)
            {
                _ruleEpisode.Remove(k);
                if (_planById!.TryGetValue(k, out var p))
                    Diagnostics?.Invoke($"规则解除：{p.Label}");
            }
        }
    }

    /// <summary>双设备会话合并：多媒体快照在前，通信快照补充（同一物理设备被设为双默认时按 InstanceId 去重）。</summary>
    private IReadOnlyList<SoundSession> MergedSnapshot()
    {
        var primary = _monitor.CurrentSnapshot;
        if (_comms is null)
            return primary;
        var extra = _comms.CurrentSnapshot;
        if (extra.Count == 0)
            return primary;
        var merged = new List<SoundSession>(primary.Count + extra.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in primary)
            if (seen.Add(s.InstanceId))
                merged.Add(s);
        foreach (var s in extra)
            if (seen.Add(s.InstanceId))
                merged.Add(s);
        return merged;
    }

    /// <summary>
    /// 评估各选择器（分组或单应用）是否"在播"，结果写入 _sounding。键含判定方式。
    /// state（状态判定，默认）：SMTC 报告"播放中"→在播；已暂停/已停止→立即解除（无宽限）；
    ///   无 SMTC 信号的应用以会话 Active 兜底。不看峰值。
    /// sound（声音判定）：峰值迟滞（双阈值+低电平保持）+ 静音宽限。
    /// 选择器列表为换配置时按键去重的预解析结果，本方法每拍调用、零分配。
    /// </summary>
    private void EvaluateSelectors(List<SelectorPlan> selectors, EngineSettings settings, DateTime now)
    {
        foreach (var sel in selectors)
        {
            // 单遍扫描成员（不物化列表）：出声峰值命中 / SMTC 已知且在播 / 会话 Active
            // （SMTC 只在 state 模式下查询，与原实现一致）
            bool anyMember = false, anySpeaking = false, anyActive = false;
            bool anyKnown = false, anyPlaying = false;
            foreach (var t in _tracked.Values)
            {
                if (sel.IsApp ? t.NormName != sel.NormApp : t.GroupId != sel.GroupId)
                    continue;
                anyMember = true;
                if (t.Speaking)
                    anySpeaking = true;
                if (t.Session.State == Audio.SessionState.Active)
                    anyActive = true;
                if (sel.StateMode && settings.UseMediaSessionStatus && !anyPlaying)
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

            if (sel.StateMode)
            {
                // 应用自报了状态 → 以自报为准（暂停/停止立即解除）；未知 → 会话 Active 兜底
                if (anyKnown ? anyPlaying : anyActive)
                    _sounding.Add(sel.Key);
                continue; // 状态判定：无宽限、不看峰值
            }

            // sound 模式：峰值迟滞 + 静音宽限
            if (anySpeaking)
            {
                _lastSounding[sel.Key] = now;
                _sounding.Add(sel.Key);
                continue;
            }
            if (!_lastSounding.TryGetValue(sel.Key, out var lastSounding))
                continue;
            double silenceMs = (now - lastSounding).TotalMilliseconds;
            int graceMs = anyActive
                ? Math.Max(0, settings.StreamOpenSilenceGraceMs)
                : Math.Max(0, settings.SilenceGraceMs);
            if (silenceMs < graceMs)
                _sounding.Add(sel.Key);
        }

        // 清理不再被任何规则使用的选择器计时（复用缓冲，免逐拍分配）
        if (_lastSounding.Count > 0)
        {
            _gone.Clear();
            var keys = _selectorKeys!;
            foreach (var k in _lastSounding.Keys)
                if (!keys.Contains(k))
                    _gone.Add(k);
            foreach (var k in _gone)
                _lastSounding.Remove(k);
        }
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
    /// 裁决方式按 Settings.RuleConflict：优先级模式高者胜（同级时应用级优先于分组级）；
    /// 最深模式取目标音量更低者（并列时仍按优先级决出）。
    /// </summary>
    private DuckingRuleConfig? EvaluateWinner(
        Tracked t, List<RulePlan> plan, HashSet<string> sounding)
    {
        DuckingRuleConfig? winner = null;
        foreach (var p in plan)
        {
            if (!p.ConditionOk)
                continue; // 设备条件不满足：规则本轮不参与
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
        var selectorByKey = new Dictionary<string, SelectorPlan>(StringComparer.Ordinal);
        foreach (var r in config.Rules)
        {
            if (!r.Enabled || r.Trigger is null || r.Target is null)
                continue;
            var p = new RulePlan
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
                Label = "", // 分组名要等下方映射建好才能解析，循环结束后统一填
                DeviceCond = r.Condition == "device" ? (r.DeviceCondition ?? "").Trim() : "",
            };
            _plan.Add(p);
            AddSelector(selectorByKey, p.TriggerKey, r.Trigger, r.DetectionMode);
            AddSelector(selectorByKey, p.TargetKey, r.Target, r.DetectionMode);
        }
        _selectors = selectorByKey.Values.ToList();
        _selectorKeys = new HashSet<string>(selectorByKey.Keys, StringComparer.Ordinal);

        _groupByProc = new Dictionary<string, string>(StringComparer.Ordinal);
        _defaultGroupId = null;
        _groupNames = new Dictionary<string, string>();
        foreach (var g in config.Groups)
        {
            _groupNames[g.Id] = g.Name;
            _defaultGroupId ??= g.IsDefault ? g.Id : null;
            foreach (var p in g.ProcessNames)
            {
                var norm = ConfigStore.NormalizeProcessName(p);
                // 与原解析一致：按配置顺序首个命中的分组生效
                if (!_groupByProc.ContainsKey(norm))
                    _groupByProc[norm] = g.Id;
            }
        }

        // 规则诊断标签 + Id 索引（生效/解除事件用）
        _planById = new Dictionary<string, RulePlan>(StringComparer.Ordinal);
        foreach (var p in _plan)
        {
            p.Label = SelectorLabel(p.Rule.Trigger!, _groupNames) + " 出声 → " +
                      SelectorLabel(p.Rule.Target!, _groupNames);
            _planById[p.Rule.Id] = p;
        }

        // 配置问题预警（诊断事件）：启用规则的引用明显失配时提醒——这是"规则永不生效"类静默失效的主要来源。
        // 仅在重建计划（配置变更）时发，不逐拍刷屏。分组组成员是否在运行属运行时状态，不在此报。
        foreach (var p in _plan)
        {
            WarnBadRef(p.Label, p.Rule.Trigger!, "触发", _groupNames);
            WarnBadRef(p.Label, p.Rule.Target!, "目标", _groupNames);
        }

        _globalCurve = VolumeCurve.ResolveGlobal(config);
        _conflictDeepest = config.Settings.RuleConflict == "deepest";
        _planFor = config;
    }

    private void WarnBadRef(string ruleLabel, RuleRef sel, string role, Dictionary<string, string> groupNames)
    {
        if (sel.Type == "app")
        {
            if ((sel.ProcessName ?? "").Trim().Length == 0)
                Diagnostics?.Invoke($"配置问题：规则[{ruleLabel}] 的{role}应用名为空，该规则永不生效");
        }
        else if (!groupNames.ContainsKey(sel.GroupId ?? ""))
        {
            Diagnostics?.Invoke(
                $"配置问题：规则[{ruleLabel}] 的{role}分组「{sel.GroupId ?? ""}」不存在（分组可能已被删除），该规则永不生效");
        }
    }

    private static string SelectorLabel(RuleRef sel, Dictionary<string, string> groupNames) =>
        sel.Type == "app"
            ? $"应用「{sel.ProcessName}」"
            : $"分组「{groupNames.GetValueOrDefault(sel.GroupId ?? "", sel.GroupId ?? "")}」";

    /// <summary>登记一个选择器评估项；同一选择器被多条规则共用时只保留首个（评估一次，结果共享）。</summary>
    private static void AddSelector(
        Dictionary<string, SelectorPlan> byKey, string key, RuleRef selector, string detectionMode)
    {
        if (byKey.ContainsKey(key))
            return;
        byKey[key] = new SelectorPlan
        {
            Key = key,
            IsApp = selector.Type == "app",
            NormApp = ConfigStore.NormalizeProcessName(selector.ProcessName),
            GroupId = selector.GroupId,
            StateMode = detectionMode == "state",
        };
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

    /// <summary>优先级裁决：优先级高者胜；同级时应用级规则优先于分组级。</summary>
    private static bool BetterPriority(DuckingRuleConfig a, DuckingRuleConfig b) =>
        a.Priority > b.Priority ||
        (a.Priority == b.Priority && a.Target!.Type == "app" && b.Target!.Type != "app");

    /// <summary>两条命中规则谁生效：最深模式（RuleConflict=="deepest"）取闪避更深者（目标音量更低，并列按优先级），否则按优先级。</summary>
    private bool Better(DuckingRuleConfig a, DuckingRuleConfig b) =>
        _conflictDeepest
            ? a.TargetVolumePercent < b.TargetVolumePercent ||
              (a.TargetVolumePercent == b.TargetVolumePercent && BetterPriority(a, b))
            : BetterPriority(a, b);

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
            UserTookOver = t.UserTookOver,
            CurrentVolume = t.CurrentVolume,
            TargetVolume = t.TargetVolume,
            OriginalVolume = t.OriginalVolume,
        };
    }
}
