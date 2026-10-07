using System.Windows.Forms;

namespace Tuner;

/// <summary>系统托盘图标与右键菜单（基于 WinForms NotifyIcon）。</summary>
internal sealed class TrayHost : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayHost()
    {
        _icon = new NotifyIcon
        {
            Icon = TrayIconFactory.Create(),
            Text = "Tuner — 分组音频闪避",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 Tuner", null, (_, _) => App.ShowMainWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出（还原音量）", null, (_, _) =>
        {
            App.ForceExit = true;
            App.Current.Shutdown();
        });
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => App.ShowMainWindow();
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(2500, title, text, ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
