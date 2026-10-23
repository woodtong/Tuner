using System.Globalization;
using System.Windows;

namespace Tuner;

/// <summary>
/// 界面文案多语言（简体中文默认 / English）。
/// 字典在 Lang/Strings.*.xaml（编译为 Page），XAML 侧经 {DynamicResource S.xxx} 引用，
/// 代码侧经 Lang.Get("S.xxx")；Apply 换字典后 DynamicResource 自动刷新，动态文字由 LanguageChanged 订阅方重建。
/// </summary>
public static class Lang
{
    private static ResourceDictionary? _current;
    public static event Action? LanguageChanged;
    public static string Current { get; private set; } = "zh-CN";

    /// <summary>配置值（""=跟随系统 / "zh-CN" / "en-US"）→ 实际语言代码。</summary>
    public static string Resolve(string setting) =>
        string.IsNullOrEmpty(setting)
            ? (CultureInfo.CurrentUICulture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN")
            : (setting == "en-US" ? "en-US" : "zh-CN");

    public static void Apply(string code)
    {
        code = code == "en-US" ? "en-US" : "zh-CN";
        if (Current == code && _current is not null)
            return;
        var dict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Lang/Strings.{code}.xaml", UriKind.Absolute),
        };
        var res = CurrentResources;
        if (res is null)
            return; // Application.Resources 未就绪（极早期）：忽略，后续再 Apply
        if (_current is not null)
            res.MergedDictionaries.Remove(_current);
        res.MergedDictionaries.Insert(0, dict); // 语言字典置顶（覆盖样式包等其它合并字典）
        _current = dict;
        Current = code;
        LanguageChanged?.Invoke();
    }

    private static ResourceDictionary? CurrentResources =>
        (System.Windows.Application.Current?.Resources) as ResourceDictionary;

    /// <summary>按键取串；字典缺失时原样返回键（开发期可见漏翻，不崩溃）。</summary>
    public static string Get(string key)
    {
        if (_current is not null && _current[key] is string s)
            return s;
        return key;
    }

    /// <summary>带参数的格式化取串。</summary>
    public static string Get(string key, params object[] args) =>
        args.Length == 0 ? Get(key) : string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
