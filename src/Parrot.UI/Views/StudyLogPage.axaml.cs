using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Parrot.UI.ViewModels;

namespace Parrot.UI.Views;

public partial class StudyLogPage : UserControl
{
    public StudyLogPage()
    {
        InitializeComponent();
    }

    /// <summary>「＋ 添加」：展开手动记词输入行，并把焦点放进输入框（点开即打字，不用再点一次）。</summary>
    private void OnAddWordClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StudyLogPageViewModel vm) return;
        vm.OpenManualInputCommand.Execute(null);
        ManualBox.Focus();
    }

    /// <summary>手动记词输入框：回车记入、Esc 收起。</summary>
    private void OnManualBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not StudyLogPageViewModel vm) return;
        if (e.Key == Key.Enter)
        {
            vm.ConfirmManualInputCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelManualInputCommand.Execute(null);
            e.Handled = true;
        }
    }
}
