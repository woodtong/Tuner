using System.Windows.Forms;

namespace Tuner;

/// <summary>系统托盘图标与右键菜单（基于 WinForms NotifyIcon）。</summary>
internal sealed class TrayHost : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _suspendItem;

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

        // 暂停/恢复闪避：勾选即暂停（立即还原被压音量），状态与主界面/设置页同步
        _suspendItem = new ToolStripMenuItem("暂停闪避（恢复音量）") { CheckOnClick = true };
        _suspendItem.Click += (_, _) => App.SetSuspended(_suspendItem.Checked);
        menu.Items.Add(_suspendItem);

        // 诊断自查入口（隐蔽通道：事件日志只在这里与设置页底部露出）
        menu.Items.Add("事件日志", null, (_, _) => App.ShowDiagnostics());

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出（还原音量）", null, (_, _) =>
        {
            App.ForceExit = true;
            App.Current.Shutdown();
        });
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => App.ShowMainWindow();
    }

    /// <summary>把引擎暂停状态同步到托盘（菜单勾选 + 悬停文字）。</summary>
    public void SyncSuspendState(bool suspended)
    {
        _suspendItem.Checked = suspended;
        _icon.Text = suspended ? "Tuner — 闪避已暂停" : "Tuner — 分组音频闪避";
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(2500, title, text, ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
