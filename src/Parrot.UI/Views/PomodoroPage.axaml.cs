using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Parrot.UI.ViewModels;

namespace Parrot.UI.Views;

public partial class PomodoroPage : UserControl
{
    private PomodoroPageViewModel? _vm;
    private bool _plotDrawn, _plotDirty;

    public PomodoroPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnVmChanged;
                _vm.StatsRefreshRequested -= DrawStats;
            }
            _vm = DataContext as PomodoroPageViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmChanged;
                _vm.StatsRefreshRequested += DrawStats;
            }
        };
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ScheduleDraw();
    }

    /// <summary>本页现在常驻主窗可视树（切页只切 IsVisible），Loaded 只在启动时来一次，
    /// 所以"切回来补画统计图"得挂在自身可见性变化上。</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible) ScheduleDraw();
    }

    private void ScheduleDraw()
        // 延后一帧（AvaPlot 需要已附加并完成布局才有有效尺寸）
        => DispatcherTimer.RunOnce(() => { if (!_plotDrawn || _plotDirty) DrawStats(); }, TimeSpan.FromMilliseconds(100));

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 番茄完成/重置都会改今日汇总 → 统计图自动跟进（不可见时置脏，切回再画）
        if (e.PropertyName is nameof(PomodoroPageViewModel.TodayMinutes)
                           or nameof(PomodoroPageViewModel.TodaySessions))
            DrawStats();
    }

    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_plotDirty && Tabs.SelectedIndex == 1)
            DrawStats();
    }

    private void DrawStats()
    {
        if (DataContext is not PomodoroPageViewModel vm) return;
        if (!IsEffectivelyVisible) // 启动时本页隐藏：画布尺寸为 0，画了也白画，等切回再画
        {
            _plotDirty = true;
            return;
        }
        if (!StatsPlot.IsVisible) // 统计 Tab 未选中时 AvaPlot 尺寸为 0，画了也白画
        {
            _plotDirty = true;
            return;
        }
        var data = vm.GetFocusDaily(14);

        var plot = StatsPlot.Plot;
        plot.Clear();

        double[] ys = [.. data.Select(d => (double)d.TotalMinutes)];
        var bar = plot.Add.Bars(ys);
        var barColor = ScottPlot.Color.FromHex("#4C6EF5");
        foreach (var b in bar.Bars) b.FillColor = barColor;

        // 每天两个刻度太挤：隔一天标一次 MM-dd
        var positions = Enumerable.Range(0, data.Count).Where(i => i % 2 == 0).Select(i => (double)i).ToArray();
        var labels = positions.Select(i => data[(int)i].Date.ToString("MM-dd")).ToArray();
        if (positions.Length > 0)
            plot.Axes.Bottom.SetTicks(positions, labels);

        plot.Title($"近 14 天专注（今日 {vm.TodaySessions} 个 · {vm.TodayMinutes} 分钟）");
        plot.Axes.Left.Label.Text = "分钟";
        StatsPlot.Refresh();
        _plotDrawn = true;
        _plotDirty = false;
    }
}
