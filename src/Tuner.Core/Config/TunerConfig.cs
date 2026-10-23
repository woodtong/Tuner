namespace Tuner.Core.Config;

/// <summary>Tuner 全量配置（分组 + 规则 + 引擎设置），持久化为 JSON。</summary>
public sealed class TunerConfig
{
    public EngineSettings Settings { get; set; } = new();
    public List<AppGroupConfig> Groups { get; set; } = new();
    public List<DuckingRuleConfig> Rules { get; set; } = new();
}

public sealed class EngineSettings
{
    /// <summary>优先使用系统媒体会话（SMTC）的播放状态判定：应用自报"播放中"即保持闪避（歌间静音不误判），"已暂停/已停止"立即恢复。</summary>
    public bool UseMediaSessionStatus { get; set; } = true;

    /// <summary>进入"出声"的峰值阈值（0..1）。</summary>
    public float ActivePeakThreshold { get; set; } = 0.05f;

    /// <summary>退出"出声"的峰值阈值，低于进入阈值形成迟滞带。</summary>
    public float InactivePeakThreshold { get; set; } = 0.02f;

    /// <summary>峰值低于退出阈值持续该时长后才算停止出声，防止断续声音导致音量抽搐。</summary>
    public int InactiveHoldMs { get; set; } = 250;

    /// <summary>闪避开始（压低）：触发闪避时，音量从原始值压到闪避目标值的耗时。越短反应越急，压低了才不盖住触发源。</summary>
    public int FadeOutDurationMs { get; set; }

    /// <summary>闪避结束（恢复）：触发源停止出声/停止播放后，音量从闪避目标值恢复回原始值的耗时。越长越平顺。</summary>
    public int FadeInDurationMs { get; set; }

    /// <summary>旧字段（v0.1-nu 起拆分为开始/结束两个独立时长），仅用于旧配置迁移，保存时不再输出。</summary>
    public int? FadeDurationMs { get; set; }

    /// <summary>静音宽限（音频流已关闭）：触发组静音且音频流已关闭（真正停止/退出）时，保持规则生效的时长。</summary>
    public int SilenceGraceMs { get; set; } = 1500;

    /// <summary>静音宽限（音频流仍打开）：触发组静音但音频流未关闭（歌曲间隙、极弱段落）时，保持规则生效的时长。</summary>
    public int StreamOpenSilenceGraceMs { get; set; } = 8000;

    /// <summary>会话峰值轮询间隔（毫秒）。</summary>
    public int PollIntervalMs { get; set; } = 100;

    /// <summary>
    /// 同目标多条规则同时命中时的裁决方式："priority"=优先级最高者胜（默认，同级时应用级优先于分组级）；
    /// "deepest"=取闪避最深者（目标音量最低，如 20% 与 40% 同时命中压到 20%，并列时仍按优先级决出）。
    /// </summary>
    public string RuleConflict { get; set; } = "priority";

    /// <summary>
    /// 手动接管：闪避期间用户手动调节被压会话的音量即视为接管——该会话本轮闪避不再干预
    /// （音量完全由用户控制），规则解除后的下一次触发恢复闪避。关闭时仅更新恢复锚点（渐变结束回到用户值）。
    /// </summary>
    public bool ManualTakeover { get; set; }

    /// <summary>全局快捷键（Ctrl+Alt+P 暂停/恢复闪避、Ctrl+Alt+M 显示主界面）；组合键被占用时注册失败会在事件日志留痕。</summary>
    public bool HotkeysEnabled { get; set; } = true;

    /// <summary>
    /// 音量曲线（全局）："linear"=线性；"square"=平方；"log"=对数（等响度）；"custom"=自定义（CustomCurve 控制点）。
    /// 界面上的音量百分比（混音台推子位置、规则目标音量）统一视为曲线上的位置，按曲线映射为实际振幅。
    /// </summary>
    public string VolumeCurve { get; set; } = "linear";

    /// <summary>VolumeCurve=="custom" 时的自定义控制点（0..1 归一化，响度↔音量百分比映射）。</summary>
    public List<CurvePoint> CustomCurve { get; set; } = new();
}

public sealed class AppGroupConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>进程名列表（不区分大小写，可带可不带 .exe；"system sounds" 匹配系统提示音会话）。</summary>
    public List<string> ProcessNames { get; set; } = new();

    /// <summary>默认组：未命中任何分组的会话自动归入（全配置应恰好一个）。</summary>
    public bool IsDefault { get; set; }
}

/// <summary>规则引用的选择器：可以指向一个分组，也可以精确到单个应用（进程名）。</summary>
public sealed class RuleRef
{
    /// <summary>"group" 或 "app"。</summary>
    public string Type { get; set; } = "group";

    /// <summary>分组模式下的分组 Id。</summary>
    public string GroupId { get; set; } = "";

    /// <summary>应用模式下的进程名（不区分大小写，可带可不带 .exe）。</summary>
    public string ProcessName { get; set; } = "";
}

public sealed class DuckingRuleConfig
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>触发选择器：该分组/应用"出声或在播"时规则生效。</summary>
    public RuleRef? Trigger { get; set; }

    /// <summary>目标选择器：其成员音量渐变到目标值。</summary>
    public RuleRef? Target { get; set; }

    /// <summary>旧字段（v0.1-zeta 起由 Trigger/Target 取代），仅用于旧配置迁移。</summary>
    public string TriggerGroupId { get; set; } = "";

    /// <summary>旧字段，仅用于旧配置迁移。</summary>
    public string TargetGroupId { get; set; } = "";

    /// <summary>
    /// 判定方式："sound"=声音判定（峰值迟滞+宽限，有无声音）；"state"=状态判定
    /// （SMTC 播放状态优先、会话 Active 兜底，不看峰值、无宽限）。旧配置迁移默认 "sound"。
    /// </summary>
    public string DetectionMode { get; set; } = "sound";

    /// <summary>闪避期间的目标音量（百分比＝音量曲线上的位置，0..100，按曲线映射为实际振幅）。</summary>
    public float TargetVolumePercent { get; set; } = 20f;

    /// <summary>同一会话被多条规则命中时，优先级高者胜；同级时应用级规则优先于分组级。</summary>
    public int Priority { get; set; }

    /// <summary>规则级"闪避开始（压低）"时长覆盖（毫秒）；null = 跟随全局设置。</summary>
    public int? FadeOutMs { get; set; }

    /// <summary>规则级"闪避结束（恢复）"时长覆盖（毫秒）；null = 跟随全局设置。恢复阶段沿用刚解除规则的覆盖值。</summary>
    public int? FadeInMs { get; set; }

    /// <summary>生效条件："none"=无条件（默认）；"device"=仅当当前输出设备名包含 DeviceCondition 时生效。</summary>
    public string Condition { get; set; } = "none";

    /// <summary>Condition=="device" 时的设备名匹配文字（子串包含，不区分大小写，如"耳机"）。</summary>
    public string DeviceCondition { get; set; } = "";
}
