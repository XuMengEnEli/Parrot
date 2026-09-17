using Parrot.Core.Abstractions;

namespace Parrot.Data;

/// <summary>
/// 弹窗卡源（需求 2.3 供数）：**今日学习记录优先**（🔊 旁 📌 记进来的词，读什么复什么），
/// 当天没记录则从内嵌词库随机一张，词库也空才返回 null。
/// 线程安全：SQLite 每调用一连接。
/// </summary>
public sealed class WordbookCardSource(WordbookRepository repo, StudyLogRepository? studyLog = null) : ICardSource
{
    public ReviewCard? NextCard()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

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
