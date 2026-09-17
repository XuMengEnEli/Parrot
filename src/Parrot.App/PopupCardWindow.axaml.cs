using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Parrot.UI.ViewModels;

namespace Parrot.App;

/// <summary>
/// 右下角"伪广告"弹窗（需求 2.3）：不抢焦点（ShowActivated=False），可拖拽，× 关闭。
/// 定位在工作区右下角（避开任务栏），App 层控制定时弹出与老板键收起。
/// </summary>
public partial class PopupCardWindow : Window
{
    public PopupCardWindow()
    {
        InitializeComponent();
    }

    public new PopupCardViewModel? DataContext
    {
        get => (PopupCardViewModel?)base.DataContext;
        set => base.DataContext = value;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        PositionBottomRight();
    }

    public void ShowAtCorner()
    {
        PositionBottomRight();
        IsVisible = true;
    }

    private void PositionBottomRight()
    {
        // 主屏工作区右下角（v1 单显示器场景；多显示器再按光标所在屏选）
        var screen = Screens.All.FirstOrDefault();
        if (screen is null) return;
        var wa = screen.WorkingArea;
        Position = new PixelPoint(
            Math.Max(wa.X, wa.Right - (int)Width - 12),
            Math.Max(wa.Y, wa.Bottom - (int)Height - 12));
    }

    private void OnDragAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Hide();
}
