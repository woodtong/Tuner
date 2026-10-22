using Microsoft.Win32;

namespace Tuner;

/// <summary>
/// 开机自启（HKCU Run 键，免管理员）。命令行带 --minimized，开机后静默驻留托盘、不弹窗口。
/// 注册表是唯一事实来源（不属于 TunerConfig）；检查开关时只认指向当前 exe 的条目，
/// 指向别的路径（比如旧目录的解压版）视为未启用，勾选后即重写为当前路径。
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Tuner";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string cmd && CommandPointsToThisExe(cmd);
        }
        catch
        {
            return false; // 注册表不可读（策略限制等）：当作未启用，界面仍可操作
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{ExePath()}\" --minimized");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string ExePath() =>
        Environment.ProcessPath ?? Environment.GetCommandLineArgs().FirstOrDefault() ?? "";

    private static bool CommandPointsToThisExe(string command)
    {
        var path = command.Trim().Trim('"');
        return path.Length > 0 && string.Equals(path, ExePath(), StringComparison.OrdinalIgnoreCase);
    }
}
