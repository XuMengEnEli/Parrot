using Parrot.Core.Abstractions;

namespace Parrot.Data;

/// <summary>
/// 弹窗卡源（需求 2.3 供数）：**记忆曲线今日到期词优先**（到点该见的先见），
/// 其次今日学习记录（🔊 旁 📌 记进来的新词，读什么复什么），当天没记录则从内嵌词库随机一张，
/// 词库也空才返回 null。
/// 弹窗只展示不推进档位——推进发生在「每日复习」页把队列呈现出来那一刻，避免"没看见就过期"。
/// 线程安全：SQLite 每调用一连接。
/// </summary>
public sealed class WordbookCardSource(
    WordbookRepository repo, StudyLogRepository? studyLog = null, ReviewRepository? review = null) : ICardSource
{
    public ReviewCard? NextCard()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

        // 到期队列随机一张：弹窗是提醒，不该把曲线节奏拉快（推进只发生在复习页 OpenTodayQueue）
        if (review is not null)
        {
            var due = review.DueQueue(today);
            if (due.Count > 0)
            {
                var hit = due[Random.Shared.Next(due.Count)];
                return new ReviewCard(hit.Word, hit.Meaning.Length > 0 ? hit.Meaning : "（无释义）", hit.Lapses > 0);
            }
        }

        // 当日学习列表（需求 #2）：弹窗随机词从这里抽
        if (studyLog is not null)
        {
            var logged = studyLog.RandomOneOfDay(today);
            if (logged is not null)
                return new ReviewCard(logged.Word,
                    logged.Meaning.Length > 0 ? logged.Meaning : "（无释义）", false);
        }

        var random = repo.RandomCard(today);
        return random is null ? null : ToCard(random);
    }

    private static ReviewCard ToCard(WordEntry w)
        => new(w.Word, w.Translation.Length > 0 ? w.Translation : "（无释义）", w.WrongCount > 0);
}
