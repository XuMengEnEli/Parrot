using CommunityToolkit.Mvvm.ComponentModel;

namespace Parrot.UI.ViewModels;

/// <summary>主窗导航页的 VM 基类（ContentControl 经 App.axaml 的 DataTemplates 映射到 View）。</summary>
public abstract partial class PageViewModelBase : ObservableObject
{
    public abstract string Title { get; }
    public abstract string Glyph { get; }
}
