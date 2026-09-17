using Parrot.Core.Pomodoro;
using Xunit;

namespace Parrot.Tests;

public class PomodoroStateMachineTests
{
    private static PomodoroStateMachine Fast() => new(
        focus: TimeSpan.FromSeconds(25),
        shortBreak: TimeSpan.FromSeconds(5),
        longBreak: TimeSpan.FromSeconds(15));

    [Fact]
    public void Idle_DoesNotTick()
    {
        var m = Fast();
        Assert.Null(m.Tick(TimeSpan.FromSeconds(1)));
        Assert.Equal(PomodoroPhase.Idle, m.Phase);
    }

    [Fact]
    public void Start_EnterFocus_AndTickRunsDownRemaining()
    {
        var m = Fast();
        m.Start();
        Assert.Equal(PomodoroPhase.Focus, m.Phase);
        Assert.Equal(TimeSpan.FromSeconds(25), m.Remaining);
        m.Tick(TimeSpan.FromSeconds(5));
        Assert.Equal(PomodoroPhase.Focus, m.Phase);
        Assert.Equal(TimeSpan.FromSeconds(20), m.Remaining);
    }

    [Fact]
    public void Pause_StopsProgress()
    {
        var m = Fast();
        m.Start();
        m.Pause();
        Assert.Null(m.Tick(TimeSpan.FromSeconds(99)));
        Assert.Equal(TimeSpan.FromSeconds(25), m.Remaining);
    }

    [Fact]
    public void FocusCompleted_GoesToShortBreak()
    {
        var m = Fast();
        m.Start();
        var finished = m.Tick(TimeSpan.FromSeconds(25));
        Assert.Equal(PomodoroPhase.Focus, finished);
        Assert.Equal(PomodoroPhase.ShortBreak, m.Phase);
        Assert.Equal(1, m.CompletedFocusCount);
    }

    [Fact]
    public void FourthFocus_GoesToLongBreak()
    {
        var m = Fast();
        m.Start();
        PomodoroPhase? last = null;
        // Focus→短休→Focus… 共 4 个 Focus
        for (int i = 0; i < 7; i++)
            last = RunToEndOfPhase(m);

        Assert.Equal(PomodoroPhase.LongBreak, m.Phase);
        Assert.Equal(4, m.CompletedFocusCount);
        _ = last;
    }

    [Fact]
    public void LongBreakEnds_BackToFocus()
    {
        var m = Fast();
        m.Start();
        for (int i = 0; i < 7; i++)
            RunToEndOfPhase(m); // 结束 4 个 Focus + 3 个短休，进入 LongBreak
        Assert.Equal(PomodoroPhase.LongBreak, m.Phase);
        RunToEndOfPhase(m);
        Assert.Equal(PomodoroPhase.Focus, m.Phase);
    }

    /// <summary>把当前阶段跑满（Tick 到刚够触发流转）。</summary>
    private static PomodoroPhase? RunToEndOfPhase(PomodoroStateMachine m)
        => m.Tick(m.CurrentPhaseDuration);
}
