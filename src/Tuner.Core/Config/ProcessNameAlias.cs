namespace Tuner.Core.Config;

/// <summary>
/// 同一应用的常见异名（中英文、品牌名↔进程名）折叠为统一规范键。
/// 部分应用的进程名与用户习惯输入的名字不一致（典型：B 站客户端进程是中文"哔哩哔哩.exe"，
/// 用户往往输 bilibili），匹配层统一折叠后两种写法任选其一都能命中。
/// 仅用于匹配：界面显示与配置保存均保留用户输入的原始名。
/// </summary>
public static class ProcessNameAlias
{
    // 每组第一个为规范键（取英文/常见进程名形式，便于与 SMTC AUMID 的包含比较）
    private static readonly string[][] AliasGroups =
    {
        new[] { "bilibili", "哔哩哔哩" },
        new[] { "cloudmusic", "网易云音乐" },
        new[] { "qqmusic", "qq音乐" },
        new[] { "msedge", "microsoftedge" },
    };

    private static readonly Dictionary<string, string> Map = Build();

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in AliasGroups)
            foreach (var name in group)
                map[name] = group[0];
        return map;
    }

    /// <summary>别名折叠为规范名；未知名字原样返回。假定已小写、无 .exe 后缀（兼容未处理的情况）。</summary>
    public static string Canonical(string name) =>
        Map.TryGetValue(name, out var canonical) ? canonical : name;
}
