using Avalonia.Controls;
using Avalonia.Interactivity;
using Parrot.UI.ViewModels;

namespace Parrot.UI.Views;

/// <summary>
/// 页容器：虚拟化下容器复用，Loaded/Unloaded 即"进入/离开视口"，驱动 PageCardViewModel 的按需渲染与取消。
/// </summary>
public partial class PageHost : UserControl
{
    public PageHost()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { /* 复用时旧卡已 OnInvisible（Unloaded 先触发） */ };
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        (DataContext as PageCardViewModel)?.OnVisible();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        (DataContext as PageCardViewModel)?.OnInvisible();
    }
}
