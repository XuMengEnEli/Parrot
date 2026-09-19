using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Parrot.UI.ViewModels;

namespace Parrot.App;

/// <summary>
/// 番茄钟到点庆祝窗：不抢焦点（ShowActivated=False，打字时弹出来也不会打断），
/// 居中偏上、12 秒自动收，点卡片任意处或 ✕ 立即收下。窗口常驻复用，只换 DataContext。
/// </summary>
public partial class CelebrationWindow : Window
{
    private readonly DispatcherTimer _autoHide;

    // 入场缩放：x:Name 只能命名控件，变换对象取不到字段，所以在代码里造
    private readonly ScaleTransform _cardScale = new(0.88, 0.88);

    public CelebrationWindow()
    {
        InitializeComponent();
        _cardScale.Transitions =
        [
            new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = TimeSpan.FromSeconds(0.24) },
            new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = TimeSpan.FromSeconds(0.24) },
        ];
        Card.RenderTransform = _cardScale;

        _autoHide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _autoHide.Tick += (_, _) => { _autoHide.Stop(); HideCard(); };
    }

    /// <summary>弹出一次庆祝：文案由 VM 算好，这里只负责 positioning + 入场动效。</summary>
    public void ShowCard(PomodoroCelebration celebration)
    {
        Show(); // 首次必须真正建出平台窗口，只置 IsVisible 是弹不出来的

        DataContext = celebration;
        Opacity = 0;
        _cardScale.ScaleX = _cardScale.ScaleY = 0.88;
        PositionCentered();
        IsVisible = true;

        // 隐藏态得先走完一帧，否则同一批属性变更会被合并，过渡根本不触发（看上去就是"啪"地出现）
        Dispatcher.UIThread.Post(BeginEntrance, DispatcherPriority.Render);
        _autoHide.Start();
    }

    public void HideCard()
    {
        _autoHide.Stop();
        IsVisible = false;
    }

    private void BeginEntrance()
    {
        Opacity = 1;
        _cardScale.ScaleX = _cardScale.ScaleY = 1;
    }

    private void PositionCentered()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        var wa = screen.WorkingArea;
        // 略偏上：正中央会被 Dock/任务栏的视觉重心压住，抬一点才像"浮在脸上"
        Position = new PixelPoint(
            (int)(wa.X + (wa.Width - Width) / 2),
            (int)(wa.Y + (wa.Height - Height) * 0.44));
    }

    private void OnCardPressed(object? sender, PointerPressedEventArgs e) => HideCard();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => HideCard();
}
