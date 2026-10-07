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
    /// <summary>进入"出声"的峰值阈值（0..1）。</summary>
    public float ActivePeakThreshold { get; set; } = 0.05f;

    /// <summary>退出"出声"的峰值阈值，低于进入阈值形成迟滞带。</summary>
    public float InactivePeakThreshold { get; set; } = 0.02f;

    /// <summary>峰值低于退出阈值持续该时长后才算停止出声，防止断续声音导致音量抽搐。</summary>
    public int InactiveHoldMs { get; set; } = 250;

    /// <summary>完整量程渐变时长（如 100%→0% 约 1.2 秒）。</summary>
    public int FadeDurationMs { get; set; } = 1200;

    /// <summary>静音宽限（音频流已关闭）：触发组静音且音频流已关闭（真正停止/退出）时，保持规则生效的时长。</summary>
    public int SilenceGraceMs { get; set; } = 1500;

    /// <summary>静音宽限（音频流仍打开）：触发组静音但音频流未关闭（歌曲间隙、极弱段落）时，保持规则生效的时长。</summary>
    public int StreamOpenSilenceGraceMs { get; set; } = 8000;

    /// <summary>会话峰值轮询间隔（毫秒）。</summary>
    public int PollIntervalMs { get; set; } = 100;
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

public sealed class DuckingRuleConfig
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>触发组：该组"出声"时规则生效。</summary>
    public string TriggerGroupId { get; set; } = "";

    /// <summary>目标组：其成员音量渐变到目标值。</summary>
    public string TargetGroupId { get; set; } = "";

    /// <summary>闪避期间的目标音量（百分比，0..100）。</summary>
    public float TargetVolumePercent { get; set; } = 20f;

    /// <summary>同一目标组有多条规则同时生效时，优先级高者胜。</summary>
    public int Priority { get; set; }
}
