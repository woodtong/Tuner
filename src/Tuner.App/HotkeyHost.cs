using System.Runtime.InteropServices;

namespace Tuner;

/// <summary>
/// 全局快捷键（RegisterHotKey）：Ctrl+Alt+P 暂停/恢复闪避、Ctrl+Alt+M 显示主界面。
/// EnsureHandle 强制创建窗口 HWND 而不显示窗口，托盘驻留（--minimized 未 Show）时同样可用。
/// </summary>
internal sealed class HotkeyHost : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2;
    private const uint VK_P = 0x50, VK_M = 0x4D;
    private const int IdToggleSuspend = 1, IdShowMain = 2;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private readonly System.Windows.Interop.HwndSource _source;
    private bool _registered;

    /// <summary>Ctrl+Alt+P 请求：切换闪避全局暂停/恢复。</summary>
    public event Action? ToggleSuspendRequested;

    /// <summary>Ctrl+Alt+M 请求：显示主界面。</summary>
    public event Action? ShowMainRequested;

    public HotkeyHost(System.Windows.Window window)
    {
        _hwnd = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        _source = System.Windows.Interop.HwndSource.FromHwnd(_hwnd)!;
        _source.AddHook(WndProc);
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled == _registered)
            return;
        if (enabled)
        {
            bool a = RegisterHotKey(_hwnd, IdToggleSuspend, MOD_CONTROL | MOD_ALT, VK_P);
            bool b = RegisterHotKey(_hwnd, IdShowMain, MOD_CONTROL | MOD_ALT, VK_M);
            if (a && b)
            {
                _registered = true;
                App.Log("全局快捷键已注册：Ctrl+Alt+P 暂停/恢复闪避 · Ctrl+Alt+M 显示主界面");
            }
            else
            {
                if (a) UnregisterHotKey(_hwnd, IdToggleSuspend);
                if (b) UnregisterHotKey(_hwnd, IdShowMain);
                App.Log("全局快捷键注册失败：组合键可能被其他程序占用（设置页可关闭）");
            }
        }
        else
        {
            UnregisterHotKey(_hwnd, IdToggleSuspend);
            UnregisterHotKey(_hwnd, IdShowMain);
            _registered = false;
            App.Log("全局快捷键已关闭");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case IdToggleSuspend:
                    ToggleSuspendRequested?.Invoke();
                    handled = true;
                    break;
                case IdShowMain:
                    ShowMainRequested?.Invoke();
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        SetEnabled(false);
    }
}
