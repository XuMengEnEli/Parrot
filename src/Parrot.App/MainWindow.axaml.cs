using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Parrot.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>老板键应用内兜底（全局版由 SharpHook 在 App 层挂：mac 全局键需辅助功能权限，应用内永远可用）。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.H && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            App.CollapseAll();
            e.Handled = true;
        }
    }

    /// <summary>关窗 → 托盘（设置可关；托盘"退出"会置 App.IsExiting 放行真正关闭）。</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (App.ShouldMinimizeOnClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
