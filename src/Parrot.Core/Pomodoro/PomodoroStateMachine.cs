namespace Parrot.Core.Pomodoro;

public enum PomodoroPhase
{
    Idle,
    Focus,
    ShortBreak,
    LongBreak,
}

/// <summary>
/// 番茄钟纯状态机：Focus 25' → 短休 5'，每 4 个专注后长休 15–20'。
/// 时钟源由外部注入（单调时钟/挂钟差值校正，托盘态与系统睡眠的恢复逻辑），
/// 本类只负责阶段流转，可在测试中确定性地驱动。
/// </summary>
public sealed class PomodoroStateMachine
{
    public static readonly TimeSpan DefaultFocus = TimeSpan.FromMinutes(25);
    public static readonly TimeSpan DefaultShortBreak = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultLongBreak = TimeSpan.FromMinutes(15);
    public const int DefaultRoundsPerLongBreak = 4;

    public PomodoroStateMachine(
        TimeSpan? focus = null,
        TimeSpan? shortBreak = null,
        TimeSpan? longBreak = null,
        int roundsPerLongBreak = DefaultRoundsPerLongBreak)
    {
        FocusDuration = focus ?? DefaultFocus;
        ShortBreakDuration = shortBreak ?? DefaultShortBreak;
        LongBreakDuration = longBreak ?? DefaultLongBreak;
        RoundsPerLongBreak = Math.Max(1, roundsPerLongBreak);
    }

    public PomodoroPhase Phase { get; private set; } = PomodoroPhase.Idle;

    /// <summary>已完成的专注次数（用于统计页）。</summary>
    public int CompletedFocusCount { get; private set; }

    /// <summary>当前阶段内已进行的秒数（Tick 累加；暂停时不 Tick）。</summary>
    public TimeSpan Elapsed { get; private set; }

    public bool IsRunning { get; private set; }

    public TimeSpan FocusDuration { get; }
    public TimeSpan ShortBreakDuration { get; }
    public TimeSpan LongBreakDuration { get; }
    public int RoundsPerLongBreak { get; }

    public TimeSpan Remaining => Phase == PomodoroPhase.Idle
        ? TimeSpan.Zero
        : CurrentPhaseDuration - Elapsed;

    public TimeSpan CurrentPhaseDuration => Phase switch
    {
        PomodoroPhase.Focus => FocusDuration,
        PomodoroPhase.ShortBreak => ShortBreakDuration,
        PomodoroPhase.LongBreak => LongBreakDuration,
        _ => TimeSpan.Zero,
    };

    /// <summary>从 Idle/暂停恢复开始。</summary>
    public void Start()
    {
        if (Phase == PomodoroPhase.Idle)
            Phase = PomodoroPhase.Focus;
        IsRunning = true;
    }

    public void Pause() => IsRunning = false;

    public void Reset()
    {
        Phase = PomodoroPhase.Idle;
        Elapsed = TimeSpan.Zero;
        IsRunning = false;
    }

    /// <summary>推进 <paramref name="delta"/>（外部用单调时钟测得）。阶段结束自动流转。</summary>
    /// <returns>本次调用造成的阶段迁移事件（可能为 null）。</returns>
    public PomodoroPhase? Tick(TimeSpan delta)
    {
        if (!IsRunning || Phase == PomodoroPhase.Idle)
            return null;

        Elapsed += delta;
        if (Elapsed < CurrentPhaseDuration)
            return null;

        var finished = Phase;
        OnPhaseCompleted(finished);
        return finished;
    }

    private void OnPhaseCompleted(PomodoroPhase finished)
    {
        Elapsed = TimeSpan.Zero;

        switch (finished)
        {
            case PomodoroPhase.Focus:
                CompletedFocusCount++;
                Phase = CompletedFocusCount % RoundsPerLongBreak == 0
                    ? PomodoroPhase.LongBreak
                    : PomodoroPhase.ShortBreak;
                break;
            case PomodoroPhase.ShortBreak:
            case PomodoroPhase.LongBreak:
                Phase = PomodoroPhase.Focus;
                break;
        }
    }
}
