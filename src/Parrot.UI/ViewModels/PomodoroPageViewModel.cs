using System.Collections.ObjectModel;
using Avalonia;
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

    // ---------- 专注月历（统计页第 3 个页签）：完成一个番茄，当天那格数字就 +1 ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalendarTitle))]
    [NotifyPropertyChangedFor(nameof(IsThisMonth))]
    private DateOnly _calendarMonth = FirstOfMonth(DateOnly.FromDateTime(DateTime.Now));

    [ObservableProperty]
    private string _calendarSummary = "";

    public ObservableCollection<FocusDayCell> CalendarCells { get; } = [];

    public string CalendarTitle => $"{CalendarMonth.Year} 年 {CalendarMonth.Month} 月";

    /// <summary>已经翻到别的月份时才显示"回到本月"。</summary>
    public bool IsThisMonth => CalendarMonth == FirstOfMonth(DateOnly.FromDateTime(DateTime.Now));

    // 无参而非 ShiftMonth(int)：XAML 的 CommandParameter 是字符串，Avalonia 不会自动转 int，会在命令执行时炸
    [RelayCommand]
    private void PrevMonth() => MoveMonth(-1);

    [RelayCommand]
    private void NextMonth() => MoveMonth(1);

    private void MoveMonth(int delta)
    {
        CalendarMonth = CalendarMonth.AddMonths(delta);
        RebuildCalendar();
    }

    [RelayCommand]
    private void BackToThisMonth()
    {
        CalendarMonth = FirstOfMonth(DateOnly.FromDateTime(DateTime.Now));
        RebuildCalendar();
    }

    private static DateOnly FirstOfMonth(DateOnly d) => new(d.Year, d.Month, 1);

    private void RebuildCalendar()
    {
        var days = _repo.MonthFocus(CalendarMonth);
        var today = DateOnly.FromDateTime(DateTime.Now);

        CalendarCells.Clear();
        foreach (var cell in FocusDayCell.BuildMonth(CalendarMonth, days, today))
            CalendarCells.Add(cell);

        CalendarSummary = days.Count > 0
            ? $"本月 {days.Sum(d => d.Sessions)} 个番茄 · 专注 {days.Sum(d => d.TotalMinutes)} 分钟 · {days.Count(d => d.Sessions > 0)} 天坐下来过"
            : "";
    }

    public void RefreshToday()
    {
        var (sessions, minutes) = _repo.TodayFocus();
        TodaySessions = sessions;
        TodayMinutes = minutes;
        RebuildCalendar();
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
        CelebrationRequested?.Invoke(BuildCelebration(finished)); // 数字要含刚记下的这一轮，故排在 RefreshToday 之后

        async Task ClearHintSoon()
        {
            await Task.Delay(6000);
            DoneHint = null;
        }
    }

    /// <summary>阶段完成 → 通知 App 层弹庆祝窗（本 VM 不认识 Window，样式细节留在视图层）。</summary>
    public event Action<PomodoroCelebration>? CelebrationRequested;

    private PomodoroCelebration BuildCelebration(PomodoroPhase finished)
    {
        bool focus = finished == PomodoroPhase.Focus;
        bool longBreakDue = focus && CompletedFocusTotal % _machine.RoundsPerLongBreak == 0;
        return new PomodoroCelebration(
            Glyph: focus ? "🎉" : "☕",
            Title: focus ? $"第 {CompletedFocusTotal} 个番茄完成" : "休息结束",
            Subtitle: focus
                ? (longBreakDue ? "本轮跑满，奖励一次长休息" : "起来活动一下，喝口水")
                : "电充好了，回到书桌前",
            StatsLine: $"今日 {TodaySessions} 个番茄 · 专注 {TodayMinutes} 分钟",
            MarkRow: TomatoRow(TodaySessions),
            NextPhase: finished switch
            {
                PomodoroPhase.Focus when _machine.Phase == PomodoroPhase.LongBreak => "接下来长休息，去离屏幕远一点的地方",
                PomodoroPhase.Focus => "接下来短休息，让眼睛歇会儿",
                _ => "下一个专注已经在计时了",
            },
            Focus: focus);
    }

    /// <summary>今日番茄排：最多画 8 个，多出来的折成 "+n"（一屏卡片放不下 20 个番茄）。</summary>
    private static string TomatoRow(int sessions)
        => string.Concat(Enumerable.Repeat("🍅", Math.Min(sessions, 8)))
           + (sessions > 8 ? $" +{sessions - 8}" : "");

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

/// <summary>到点庆祝弹窗要展示的一份内容：文案全在 VM 里算好，App 层只负责把它画成一张卡。</summary>
public sealed record PomodoroCelebration(
    string Glyph,
    string Title,
    string Subtitle,
    string StatsLine,
    string MarkRow,
    string NextPhase,
    bool Focus)
{
}

/// <summary>
/// 专注月历里的一格。月首偏移与月末补位也要占一格，7 列的 UniformGrid 才能对齐。
/// 底色深浅 = 当天完成了几颗番茄（0 / 1 / 2 / 3–4 / 5+），数字直接写在格子中间。
/// </summary>
public sealed class FocusDayCell(FocusDaySummary? day, bool isToday)
{
    private static readonly IBrush[] Heat =
    [
        new SolidColorBrush(Color.FromArgb(0x12, 0x8C, 0x91, 0x99)), // 没坐下来过：极淡的灰（跟着主题透气）
        new SolidColorBrush(Color.FromRgb(0xEF, 0x9A, 0x9A)),
        new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73)),
        new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50)),
        new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),
    ];

    // 最浅那档上白字会糊，用深红；第 2 档往上底色够深，换白字。0 不写数字，取哪个都一样。
    private static readonly IBrush OnLightest = new SolidColorBrush(Color.FromRgb(0xB7, 0x1C, 0x1C));

    private static FocusDayCell Blank() => new(null, false);

    /// <summary>
    /// 整月排成 7 列格子表：月首按其星期几补空位（周一为第一列，DateOnly 里周日=0 故 +6 取模），
    /// 月末补齐整周——UniformGrid 只有凑满 7 的倍数才不会错位。
    /// </summary>
    public static List<FocusDayCell> BuildMonth(DateOnly month, IReadOnlyList<FocusDaySummary> days, DateOnly today)
    {
        int offset = ((int)new DateTime(month.Year, month.Month, 1).DayOfWeek + 6) % 7;
        var cells = new List<FocusDayCell>(offset + days.Count + 6);
        for (int i = 0; i < offset; i++) cells.Add(Blank());
        foreach (var d in days) cells.Add(new FocusDayCell(d, d.Date == today));
        while (cells.Count % 7 != 0) cells.Add(Blank());
        return cells;
    }

    public FocusDaySummary? Day { get; } = day;
    public bool IsEmpty => Day is null;
    public bool IsToday { get; } = isToday;

    public int Sessions => Day?.Sessions ?? 0;
    public string DayNumber => Day?.Date.Day.ToString() ?? "";

    /// <summary>格子里的次数：一个也没有就留空（整月排满 0 读起来像错误码，不如不写）。</summary>
    public string CountText => Sessions > 0 ? Sessions.ToString() : "";

    public IBrush Fill => IsEmpty ? Brushes.Transparent : Heat[Sessions switch { 0 => 0, 1 => 1, 2 => 2, <= 4 => 3, _ => 4 }];

    /// <summary>今天给一圈描边：满屏色块里得能一眼找到"今天"。</summary>
    public Thickness Edge => IsToday ? new Thickness(2) : new Thickness(0);

    /// <summary>数字颜色跟底色走：浅色档用深红、深色档用白，两种主题下都保证读得清。</summary>
    public IBrush CountBrush => Sessions >= 2 ? Brushes.White : OnLightest;

    public string Tip => Day is null ? "" : $"{Day.Date:yyyy-MM-dd} · {Day.Sessions} 个番茄 · 专注 {Day.TotalMinutes} 分钟";
}
