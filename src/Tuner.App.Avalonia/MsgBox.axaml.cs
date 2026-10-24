using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Tuner.Cross;

/// <summary>简单提示/确认框（Avalonia 无内置 MessageBox 的替代）。</summary>
public partial class MsgBox : Window
{
    private bool _ok;

    public MsgBox()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>提示框（单按钮）。必须在 UI 线程调用。</summary>
    public static void Show(Window owner, string text)
    {
        var box = new MsgBox();
        box.Text.Text = text;
        box.CancelBtn.IsVisible = false;
        box.ShowDialog(owner);
    }

    /// <summary>确认框；返回 true=确定。</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string text)
    {
        var box = new MsgBox();
        box.Text.Text = text;
        box.CancelBtn.IsVisible = true;
        await box.ShowDialog(owner);
        return box._ok;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        _ok = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
