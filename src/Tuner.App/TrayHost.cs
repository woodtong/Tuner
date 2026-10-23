using System.Windows.Forms;

namespace Tuner;

/// <summary>系统托盘图标与右键菜单（基于 WinForms NotifyIcon）。文字随界面语言即时重建。</summary>
internal sealed class TrayHost : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _suspendItem;

    public TrayHost()
    {
        _icon = new NotifyIcon
        {
            Icon = TrayIconFactory.Create(),
            Text = Lang.Get("S.Tray.Title"),
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(Lang.Get("S.Tray.Open"), null, (_, _) => App.ShowMainWindow());

        // 暂停/恢复闪避：勾选即暂停（立即还原被压音量），状态与主界面/设置页同步
        _suspendItem = new ToolStripMenuItem(Lang.Get("S.Tray.Suspend")) { CheckOnClick = true };
        _suspendItem.Click += (_, _) => App.SetSuspended(_suspendItem.Checked);
        menu.Items.Add(_suspendItem);

        // 诊断自查入口（隐蔽通道：事件日志只在这里与设置页底部露出）
        menu.Items.Add(Lang.Get("S.Tray.Diag"), null, (_, _) => App.ShowDiagnostics());

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Lang.Get("S.Tray.Exit"), null, (_, _) =>
        {
            App.ForceExit = true;
            App.Current.Shutdown();
        });
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => App.ShowMainWindow();
    }

    /// <summary>界面语言切换：菜单项与悬停文字按新语言重建（菜单结构不变）。</summary>
    public void ApplyLanguage()
    {
        if (_icon.ContextMenuStrip?.Items is not { } items || items.Count < 5)
            return;
        if (items[0] is ToolStripMenuItem open)
            open.Text = Lang.Get("S.Tray.Open");
        _suspendItem.Text = Lang.Get("S.Tray.Suspend");
        if (items[2] is ToolStripMenuItem diag)
            diag.Text = Lang.Get("S.Tray.Diag");
        if (items[4] is ToolStripMenuItem exit)
            exit.Text = Lang.Get("S.Tray.Exit");
        _icon.Text = _suspendItem.Checked ? Lang.Get("S.Tray.TitlePaused") : Lang.Get("S.Tray.Title");
    }

    /// <summary>把引擎暂停状态同步到托盘（菜单勾选 + 悬停文字）。</summary>
    public void SyncSuspendState(bool suspended)
    {
        _suspendItem.Checked = suspended;
        _icon.Text = suspended ? Lang.Get("S.Tray.TitlePaused") : Lang.Get("S.Tray.Title");
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(2500, title, text, ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
