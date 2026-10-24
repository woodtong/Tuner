using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Tuner.Cross;

/// <summary>
/// 界面文案多语言（Avalonia 版）：字典 Lang/Strings.*.axaml，XAML 经 {DynamicResource} 引用，
/// 代码经 Get；Apply 换合并字典后 DynamicResource 全局刷新。
/// </summary>
public static class Lang
{
    private static bool _loaded;
    public static event Action? LanguageChanged;
    public static string Current { get; private set; } = "zh-CN";

    public static string Resolve(string setting) =>
        string.IsNullOrEmpty(setting)
            ? (CultureInfo.CurrentUICulture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN")
            : (setting == "en-US" ? "en-US" : "zh-CN");

    public static void Apply(string code)
    {
        code = code == "en-US" ? "en-US" : "zh-CN";
        if (Current == code && _loaded)
            return;
        var app = Application.Current;
        if (app is null)
            return;
        var dict = (ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri($"avares://Tuner.Cross/Lang/Strings.{code}.axaml"));
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(dict);
        _loaded = true;
        Current = code;
        LanguageChanged?.Invoke();
    }

    /// <summary>按键取串；缺失返回键本身（开发期可见漏翻，不崩溃）。</summary>
    public static string Get(string key)
    {
        var app = Application.Current;
        if (app is not null && app.Resources.TryGetResource(key, app.ActualThemeVariant, out var v) && v is string s)
            return s;
        return key;
    }

    public static string Get(string key, params object[] args) =>
        args.Length == 0 ? Get(key) : string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
