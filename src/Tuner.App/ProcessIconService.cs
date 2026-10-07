using System.Collections.Concurrent;
using System.IO;
using System.Drawing;
using System.Windows.Media.Imaging;

namespace Tuner;

/// <summary>进程图标服务：提取进程可执行文件图标 → PNG 字节（供 Core 会话快照与 WPF 位图转换）。</summary>
internal static class ProcessIconService
{
    private static readonly ConcurrentDictionary<string, byte[]?> Cache = new();

    /// <summary>注册到 Core 的图标提供器（App 启动时调用一次）。</summary>
    public static void Register() => Core.Audio.AudioSessionMonitor.IconProvider = GetPngByPid;

    private static byte[]? GetPngByPid(uint pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            // MainModule 在跨位数/权限不足时抛异常，回退用进程名拼可执行名
            var exe = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(exe))
                exe = process.ProcessName + ".exe";
            return GetPngByExe(exe);
        }
        catch
        {
            return null;
        }
    }

    public static byte[]? GetPngByExe(string exePath) =>
        Cache.GetOrAdd(exePath.ToLowerInvariant(), _ =>
        {
            try
            {
                using var icon = Icon.ExtractAssociatedIcon(exePath);
                if (icon is null)
                    return null;
                using var bmp = icon.ToBitmap();
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                var bytes = ms.ToArray();
                return bytes.Length > 0 ? bytes : null;
            }
            catch (Exception ex)
            {
                App.Log($"图标提取失败 {exePath}: {ex.Message}");
                return null;
            }
        });

    /// <summary>PNG 字节 → WPF BitmapImage（UI 线程用）。</summary>
    public static BitmapImage? ToBitmap(byte[]? png)
    {
        if (png is null || png.Length == 0)
            return null;
        try
        {
            var img = new BitmapImage();
            using var ms = new MemoryStream(png);
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }
}
