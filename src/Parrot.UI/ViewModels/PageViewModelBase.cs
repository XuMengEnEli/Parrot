using CommunityToolkit.Mvvm.ComponentModel;

namespace Parrot.UI.ViewModels;

/// <summary>主窗导航页的 VM 基类（主窗把各页常驻在可视树里，靠 IsSelected 切显示，不再重建视图）。</summary>
public abstract partial class PageViewModelBase : ObservableObject
{
    public abstract string Title { get; }
    public abstract string Glyph { get; }

    /// <summary>是否为主窗当前显示的那一页。</summary>
    [ObservableProperty]
    private bool _isSelected;
}
