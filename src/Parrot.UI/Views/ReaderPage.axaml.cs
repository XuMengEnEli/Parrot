using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Parrot.UI.ViewModels;

namespace Parrot.UI.Views;

public partial class ReaderPage : UserControl
{
    public ReaderPage()
    {
        InitializeComponent();
        ImportButton.Click += OnImportClick;
        ImportButton2.Click += OnImportClick;
        ZoomBox.SelectionChanged += OnZoomChanged;
    }

    private ReaderPageViewModel? Vm => DataContext as ReaderPageViewModel;

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var picker = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (picker is null) return;

        var files = await picker.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择讲义 PDF",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
        });

        foreach (var f in files)
        {
            var path = f.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
                await vm.ImportAsync(path);
        }
    }

    private void OnZoomChanged(object? sender, SelectionChangedEventArgs e)
    {
        // 与 XAML 里的档位一一对应
        int[] dpis = [96, 150, 200, 288];
        if (Vm is { } vm && ZoomBox.SelectedIndex >= 0 && ZoomBox.SelectedIndex < dpis.Length)
            vm.RenderDpi = dpis[ZoomBox.SelectedIndex];
    }
}
