using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tuner.Core.Config;

/// <summary>TunerConfig 的 JSON 读写。</summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tuner");

    public static string DefaultPath => Path.Combine(DefaultDir, "config.json");

    public static TunerConfig Load(string path)
    {
        // 主文件缺失/损坏 → 回退上一次保存留下的 .bak → 仍没有则全新默认
        if (!TryLoad(path, out var config) && !TryLoad(path + ".bak", out config))
            return CreateDefault();
        Normalize(config);
        return config;
    }

    private static bool TryLoad(string path, out TunerConfig config)
    {
        config = null!;
        try
        {
            if (!File.Exists(path))
                return false;
            config = JsonSerializer.Deserialize<TunerConfig>(File.ReadAllText(path), Options) ?? null!;
            return config is not null;
        }
        catch
        {
            return false; // JSON 损坏等读取异常：交给调用方回退备份
        }
    }

    /// <summary>深拷贝配置（用于把 UI 编辑的配置交给引擎，避免共享可变集合）。</summary>
    public static TunerConfig Clone(TunerConfig config) =>
        JsonSerializer.Deserialize<TunerConfig>(JsonSerializer.Serialize(config, Options), Options) ?? CreateDefault();

    public static TunerConfig CreateDefault()
    {
        var config = new TunerConfig();
        Normalize(config);
        return config;
    }

    public static void Save(string path, TunerConfig config)
    {
        Normalize(config);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // 原子落盘：先写临时文件再整体替换，写一半崩溃/断电不会留下截断的配置；
        // 替换同时留 .bak 备份，主文件意外损坏时 Load 端可回退
        var json = JsonSerializer.Serialize(config, Options);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(path))
            File.Replace(tmp, path, path + ".bak");
        else
            File.Move(tmp, path);
    }

    /// <summary>修复缺失字段：确保恰好一个默认组、去掉无效字符。</summary>
    public static void Normalize(TunerConfig config)
    {
        config.Groups ??= new List<AppGroupConfig>();
        config.Rules ??= new List<DuckingRuleConfig>();
        config.Settings ??= new EngineSettings();

        // 渐变时长迁移：旧配置只有单一 fadeDurationMs → 两个方向都取该值；
        // 新配置缺失/非法时落默认（开始 400 急压，结束 1200 平顺）
        var s = config.Settings;
        var legacyFade = Math.Max(0, s.FadeDurationMs ?? 0);
        if (s.FadeOutDurationMs <= 0)
            s.FadeOutDurationMs = legacyFade > 0 ? legacyFade : 400;
        if (s.FadeInDurationMs <= 0)
            s.FadeInDurationMs = legacyFade > 0 ? legacyFade : 1200;
        s.FadeOutDurationMs = Math.Clamp(s.FadeOutDurationMs, 50, 10_000);
        s.FadeInDurationMs = Math.Clamp(s.FadeInDurationMs, 50, 10_000);
        s.FadeDurationMs = null; // 已迁移，保存时不再写旧字段

        // 音量曲线：非法值回退线性；自定义点规整（custom 但点数不足同样回退线性）
        s.VolumeCurve = VolumeCurve.Normalize(s.VolumeCurve);
        s.CustomCurve = VolumeCurve.NormalizePoints(s.CustomCurve);
        if (s.VolumeCurve == "custom" && s.CustomCurve.Count < 2)
            s.VolumeCurve = "linear";

        foreach (var g in config.Groups)
        {
            g.Id ??= "";
            g.Name ??= "";
            g.ProcessNames ??= new List<string>();
        }
        if (config.Groups.All(g => !g.IsDefault))
            config.Groups.Add(new AppGroupConfig { Id = "default", Name = "其他", IsDefault = true });

        foreach (var r in config.Rules)
        {
            r.Id ??= "";
            // 旧配置迁移：TriggerGroupId/TargetGroupId → Trigger/Target 选择器
            if (r.Trigger is null && !string.IsNullOrEmpty(r.TriggerGroupId))
                r.Trigger = new RuleRef { Type = "group", GroupId = r.TriggerGroupId };
            if (r.Target is null && !string.IsNullOrEmpty(r.TargetGroupId))
                r.Target = new RuleRef { Type = "group", GroupId = r.TargetGroupId };
            r.Trigger ??= new RuleRef();
            r.Target ??= new RuleRef();
            if (r.Trigger.Type != "app")
                r.Trigger.Type = "group";
            if (r.Target.Type != "app")
                r.Target.Type = "group";
            if (r.DetectionMode != "sound")
                r.DetectionMode = "state"; // 默认状态判定
            // 规则级时长覆盖：非法值归 null（跟随全局）；生效条件归一到 none/device
            r.FadeOutMs = r.FadeOutMs is > 0 ? Math.Clamp(r.FadeOutMs.Value, 50, 10_000) : null;
            r.FadeInMs = r.FadeInMs is > 0 ? Math.Clamp(r.FadeInMs.Value, 50, 10_000) : null;
            r.Condition = r.Condition == "device" ? "device" : "none";
            r.DeviceCondition = (r.DeviceCondition ?? "").Trim();
            if (r.DeviceCondition.Length == 0)
                r.Condition = "none"; // 设备条件为空视同无条件
        }
    }

    /// <summary>
    /// 进程名归一化：小写、去 .exe 后缀、去空白，并经别名表折叠（哔哩哔哩↔bilibili 等）。
    /// 仅用于匹配比较；显示与保存一律用原始名。
    /// </summary>
    public static string NormalizeProcessName(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        if (n.EndsWith(".exe"))
            n = n[..^4];
        return ProcessNameAlias.Canonical(n);
    }

    /// <summary>选择器稳定键（宽限计时等按选择器维度记账）。</summary>
    public static string RefKey(RuleRef r) =>
        r.Type == "app" ? "app:" + NormalizeProcessName(r.ProcessName) : "group:" + r.GroupId;
}
