using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parrot.Core.Notifications;
using Parrot.Core.Pomodoro;
using Parrot.Data;

namespace Parrot.UI.ViewModels;

/// <summary>
/// 番茄钟页（需求 2.1）：25 分钟专注 + 5/15 休息，阶段完成落 pomodoro_log，统计柱状图近 14 天。
/// 时钟源用挂钟差值（DateTime.UtcNow）推进，短节流自动补齐；但**超过 2 分钟的跳变视为系统休眠**，
/// 自动暂停并保留进度（旧版"休眠回来直接跑完"会伪造番茄、虚增统计——已修）。
/// 本实例同时供"讲义阅读页右上角迷你卡"共享（同一计时器，两处显示同源）。
/// </summary>
public sealed partial class PomodoroPageViewModel : PageViewModelBase, IDisposable
{
    private readonly PomodoroRepository _repo;
    private readonly PomodoroStateMachine _machine = new();
    private readonly DispatcherTimer _timer;
    private DateTime _lastWallUtc = DateTime.UtcNow;
    private DateTime _phaseStartedUtc;

    public PomodoroPageViewModel(PomodoroRepository repo)
    {
        _repo = repo;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += OnTick;
        RefreshToday();
    }

    public override string Title => "番茄钟";
    public override string Glyph => "🍅";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseText))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(PhaseBrush))]
    private PomodoroPhase _phase = PomodoroPhase.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartButtonText))]
    [NotifyPropertyChangedFor(nameof(ToggleGlyph))]
    private bool _isRunning;

    [ObservableProperty]
    private string _remainingText = "25:00";

    [ObservableProperty]
    private double _progress; // 0–1，当前阶段进度

    [ObservableProperty]
    private int _todaySessions;

    [ObservableProperty]
    private int _todayMinutes;

    [ObservableProperty]
    private string? _doneHint; // "专注完成！休息 5 分钟" 一闪提示

    public string PhaseText => Phase switch
    {
        PomodoroPhase.Focus => "专注中",
        PomodoroPhase.ShortBreak => "短休息",
        PomodoroPhase.LongBreak => "长休息",
        _ => "待开始",
    };

    public bool IsIdle => Phase == PomodoroPhase.Idle;
    public int CompletedFocusTotal => _machine.CompletedFocusCount;
    public string StartButtonText => IsRunning ? "⏸ 暂停" : Phase == PomodoroPhase.Idle ? "▶ 开始专注" : "▶ 继续";

    // ---------- 讲义页右上角迷你卡用 ----------
    public string ToggleGlyph => IsRunning ? "⏸" : "▶";

    public IBrush PhaseBrush => Phase switch
    {
        PomodoroPhase.Focus => FocusBrush,
        PomodoroPhase.ShortBreak or PomodoroPhase.LongBreak => BreakBrush,
        _ => IdleBrush,
    };

    private static readonly IBrush FocusBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly IBrush BreakBrush = new SolidColorBrush(Color.FromRgb(0x30, 0xA4, 0x6C));
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.FromRgb(0x8C, 0x91, 0x99));

    [RelayCommand]
    private void RefreshStats() => StatsRefreshRequested?.Invoke();

    /// <summary>View code-behind 订阅：把 ScottPlot 画图留在 View 层，VM 只供数据。</summary>
    public event Action? StatsRefreshRequested;

    [RelayCommand]
    private void ToggleStart()
    {
        if (IsRunning)
        {
            _machine.Pause();
            IsRunning = false;
            _timer.Stop();
            // 暂停即记一条未完成日志？不——只在整个会话被放弃/重置时记，避免碎片。
        }
        else
        {
            if (Phase == PomodoroPhase.Idle)
                _phaseStartedUtc = DateTime.UtcNow;
            _machine.Start();
            _lastWallUtc = DateTime.UtcNow;
            IsRunning = true;
            _timer.Start();
        }
        UpdateDisplay();
    }

    [RelayCommand]
    private void Reset()
    {
        if (_machine.Phase is PomodoroPhase.Focus && _machine.Elapsed > TimeSpan.FromSeconds(60))
        {
            // 放弃进行中的专注：按实际秒数记一条未完成（统计计入分钟、不计番茄数）
            _repo.Log("Focus", _phaseStartedUtc, (int)_machine.Elapsed.TotalSeconds, completed: false);
        }
        _timer.Stop();
        _machine.Reset();
        Phase = PomodoroPhase.Idle;
        IsRunning = false;
        UpdateDisplay();
        RefreshToday();
    }

    /// <summary>统计页数据：近 14 天每日专注分钟（View code-behind 画 ScottPlot）。</summary>
    public List<FocusDaySummary> GetFocusDaily(int days = 14) => _repo.DailyFocus(days);

    public void RefreshToday()
    {
        var (sessions, minutes) = _repo.TodayFocus();
        TodaySessions = sessions;
        TodayMinutes = minutes;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var delta = now - _lastWallUtc;
        _lastWallUtc = now;
        if (delta <= TimeSpan.Zero)
            return;

        if (delta > TimeSpan.FromMinutes(2))
        {
            // 进程被挂起（系统休眠）才会出现这种跳变：按挂钟"补齐"等于睡出一个番茄，统计会虚增。
            // 自动暂停并保留阶段与进度，用户回来点 ▶ 继续真实计时。
            _machine.Pause();
            IsRunning = false;
            _timer.Stop();
            DoneHint = "检测到系统休眠，已自动暂停（进度保留，点 ▶ 继续）";
            UpdateDisplay();
            return;
        }

        var finished = _machine.Tick(delta);
        if (finished is not null)
            OnPhaseCompleted(finished.Value);
        UpdateDisplay();
    }

    private void OnPhaseCompleted(PomodoroPhase finished)
    {
        var seconds = (int)(finished == PomodoroPhase.Focus
            ? _machine.FocusDuration.TotalSeconds
            : finished == PomodoroPhase.LongBreak
                ? _machine.LongBreakDuration.TotalSeconds
                : _machine.ShortBreakDuration.TotalSeconds);
        _repo.Log(finished.ToString(), _phaseStartedUtc, seconds, completed: true);

        AlertSound.PlayPhaseDone();
        DoneHint = finished switch
        {
            PomodoroPhase.Focus => _machine.CompletedFocusCount % _machine.RoundsPerLongBreak == 0
                ? "第 4 个专注完成，奖励长休息 🎉"
                : "专注完成！休息一下 ☕",
            _ => "休息结束，开始下一个专注 💪",
        };
        _ = ClearHintSoon();
        _phaseStartedUtc = DateTime.UtcNow; // 新阶段已在状态机内自动流转
        RefreshToday();

        async Task ClearHintSoon()
        {
            await Task.Delay(6000);
            DoneHint = null;
        }
    }

    private void UpdateDisplay()
    {
        Phase = _machine.Phase;
        var remain = _machine.Remaining;
        if (remain < TimeSpan.Zero) remain = TimeSpan.Zero;
        RemainingText = $"{(int)remain.TotalMinutes:00}:{remain.Seconds:00}";
        var dur = _machine.CurrentPhaseDuration;
        Progress = dur <= TimeSpan.Zero ? 0 : Math.Clamp(_machine.Elapsed.TotalSeconds / dur.TotalSeconds, 0, 1);
    }

    public void Dispose()
    {
        _timer.Stop();
        // 专注中途退出应用：留痕一条未完成（统计按时长计入分钟、不计番茄数，见 PomodoroRepository）
        if (_machine.Phase == PomodoroPhase.Focus && _machine.Elapsed >= TimeSpan.FromSeconds(60))
            _repo.Log("Focus", _phaseStartedUtc, (int)_machine.Elapsed.TotalSeconds, completed: false);
    }
}
