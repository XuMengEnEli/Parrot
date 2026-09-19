namespace Parrot.Core.Review;

/// <summary>
/// 艾宾浩斯固定间隔排期（纯函数，无 IO，便于把"哪天再见"这条规则单测锁死）。
///
/// 档位用"已回访次数 visits"表达：钉住当天算建档（visits=0），首次回访在 1 天后；
/// 每过一轮取下一档间隔，第 6 轮之后进入长期节奏（每 60 天一次），间隔封顶所以频率不会失控。
/// 唯一能把进度往回压的事件是"拼写考试答错"：退回上一档并次日再见。
/// </summary>
public static class EbbinghausCycle
{
    /// <summary>前 6 档的间隔（天）：1/2/4/7/15/30，即第 n 次回访后再隔几天见第 n+1 次。</summary>
    public static readonly int[] Intervals = [1, 2, 4, 7, 15, 30];

    /// <summary>走完 6 档后的长期间隔（天）。</summary>
    public const int LongTermIntervalDays = 60;

    /// <summary>总共几档会走到长期节奏。</summary>
    public static int PhaseCount => Intervals.Length;

    /// <summary>已回访 <paramref name="visits"/> 次的词，下次之间隔多少天。</summary>
    public static int GapDays(int visits)
        => visits < 0 ? Intervals[0]
           : visits < Intervals.Length ? Intervals[visits]
           : LongTermIntervalDays;

    /// <summary>钉住当天的建档排期：还没有回访过，所以首访在 1 天后。</summary>
    public static DateOnly FirstDue(DateOnly pinnedOn) => pinnedOn.AddDays(GapDays(0));

    /// <summary>过完一轮回访：档位数 +1，下次到期 = 回访日 + 新档间隔。</summary>
    public static (int Visits, DateOnly Due) AfterReview(DateOnly reviewedOn, int visits)
    {
        int next = Math.Max(visits, 0) + 1;
        return (next, reviewedOn.AddDays(GapDays(next)));
    }

    /// <summary>答错：退回上一档（最低停在首档），次日再见。</summary>
    public static (int Visits, DateOnly Due) AfterLapse(DateOnly day, int visits)
        => (Math.Max(visits - 1, 0), day.AddDays(1));

    /// <summary>界面文案：已完成 <paramref name="visits"/> 次回访 →"第 3/6 轮"，走完六档叫"长期"。</summary>
    public static string PhaseLabel(int visits)
        => visits <= 0 ? "还没复习过"
           : visits >= Intervals.Length ? "长期"
           : $"第 {visits}/{Intervals.Length} 轮";
}
