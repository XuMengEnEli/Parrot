using Parrot.Core.Review;
using Parrot.Data;
using Parrot.UI.ViewModels;
using Xunit;

namespace Parrot.Tests;

/// <summary>
/// 艾宾浩斯档位表（纯函数）：钉住日 → 六档回访 → 长期封顶的整条链，以及答错回退。
/// 这里把"哪天再见"锁死，仓储层只验证落库与推进，不再重复算规则。
/// </summary>
public sealed class EbbinghausCycleTests
{
    private static readonly DateOnly PinDay = new(2026, 1, 1);

    [Fact]
    public void GapDays_FollowsTableThenCaps()
    {
        Assert.Equal([1, 2, 4, 7, 15, 30], Enumerable.Range(0, 6).Select(EbbinghausCycle.GapDays));
        Assert.Equal(60, EbbinghausCycle.GapDays(6)); // 走完六档进长期节奏
        Assert.Equal(60, EbbinghausCycle.GapDays(99));
        Assert.Equal(1, EbbinghausCycle.GapDays(-3)); // 脏数据不往表外倒着取
    }

    [Fact]
    public void FirstDue_LandsOnNextDay()
        => Assert.Equal(new DateOnly(2026, 1, 2), EbbinghausCycle.FirstDue(PinDay));

    [Fact]
    public void ReviewChain_SixPhasesThenLongTerm()
    {
        var due = EbbinghausCycle.FirstDue(PinDay);
        int visits = 0;
        var gaps = new List<int>();
        for (int round = 1; round <= 7; round++)
        {
            var (next, nextDue) = EbbinghausCycle.AfterReview(due, visits);
            gaps.Add(nextDue.DayNumber - due.DayNumber);
            Assert.Equal(round, next); // 每过一轮档位数 +1
            (visits, due) = (next, nextDue);
        }
        Assert.Equal([2, 4, 7, 15, 30, 60, 60], gaps);
        Assert.Equal("长期", EbbinghausCycle.PhaseLabel(visits));
    }

    [Fact]
    public void Lapse_DropsOnePhaseAndMeetsNextDay()
    {
        var (visits, due) = EbbinghausCycle.AfterLapse(new DateOnly(2026, 3, 10), 3);
        Assert.Equal(2, visits);
        Assert.Equal(new DateOnly(2026, 3, 11), due);

        // 已经在第一档：不再往下退，只保证次日再见
        Assert.Equal(0, EbbinghausCycle.AfterLapse(new DateOnly(2026, 3, 10), 0).Visits);
    }

    [Theory]
    [InlineData(0, "还没复习过")]
    [InlineData(1, "第 1/6 轮")]
    [InlineData(5, "第 5/6 轮")]
    [InlineData(6, "长期")]
    public void PhaseLabel_ForUi(int visits, string expected)
        => Assert.Equal(expected, EbbinghausCycle.PhaseLabel(visits));
}

/// <summary>
/// 复习进度仓储（真 SQLite 临时库，测毕即删）：建档不重置、打开队列推进一档且同日幂等、
/// 到期排序、答错回退、老记录回填、移除钉词后清进度。
/// </summary>
public sealed class ReviewRepositoryTests : IDisposable
{
    // 取系统当天：仓储按"今天"排期，写死日期过一天就取不到队列
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Now);
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"se-review-{Guid.NewGuid():N}.db");
    private readonly LocalDatabase _db;
    private readonly WordbookRepository _words;
    private readonly StudyLogRepository _log;
    private readonly ReviewRepository _review;

    public ReviewRepositoryTests()
    {
        _db = new LocalDatabase(_dbPath);
        _db.EnsureSchema();
        _words = new WordbookRepository(_db);
        _log = new StudyLogRepository(_db);
        _review = new ReviewRepository(_db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* temp 自清 */ }
    }

    [Fact]
    public void ScheduleFirst_AnchorsTomorrow_NotToday()
    {
        _review.ScheduleFirst("abandon", Today);
        Assert.Equal(1, _review.Count());
        Assert.Empty(_review.DueQueue(Today)); // 刚钉住的当天不催复习（当天归 📌 列表管）
        var row = Assert.Single(_review.DueQueue(Today.AddDays(1)));
        Assert.Equal("abandon", row.Word);
        Assert.Equal(0, row.Visits);
        Assert.Equal("还没复习过", row.PhaseText);
    }

    [Fact]
    public void ScheduleFirst_KeepsExistingProgress_WhenRepinnedLater()
    {
        _review.ScheduleFirst("focus", Today.AddDays(-6)); // 一周前钉住
        Assert.Equal(1, _review.OpenTodayQueue(Today).Single().Visits); // 已复习过一轮

        _review.ScheduleFirst("FOCUS ", Today); // 重复钉住（规范化 + 已有进度）
        var again = _review.TodayRows(Today).Single();
        Assert.Equal(1, again.Visits); // 档位没被冲掉：复习进度是用户资产
        Assert.Equal(Today.AddDays(2), again.Due);
        Assert.Equal(Today.AddDays(-6).ToString("yyyy-MM-dd"), FirstDay("focus"));
    }

    [Fact]
    public void OpenTodayQueue_AdvancesOnceEvenIfOpenedTwiceSameDay()
    {
        _review.ScheduleFirst("adopt", Today.AddDays(-1));

        var first = _review.OpenTodayQueue(Today).Single();
        Assert.Equal(1, first.Visits);
        Assert.Equal(Today.AddDays(2), first.Due);

        // 同日再刷新：队列不能空掉，也不能再推进一档（否则每刷新一次曲线就跑快一格）
        var second = _review.OpenTodayQueue(Today).Single();
        Assert.Equal(1, second.Visits);
        Assert.Equal(Today.AddDays(2), second.Due);

        Assert.Empty(_review.OpenTodayQueue(Today.AddDays(1))); // 下一档还没到
        Assert.Equal(2, _review.OpenTodayQueue(Today.AddDays(2)).Single().Visits);
    }

    [Fact]
    public void DueQueue_OrdersOverdueFirst_ThenLapsedWithinSameDay()
    {
        _review.ScheduleFirst("zeta", Today.AddDays(-1)); // 今天到期
        _review.ScheduleFirst("alpha", Today.AddDays(-1)); // 与 zeta 同日到期 → 按词序
        _review.ScheduleFirst("overdue", Today.AddDays(-5)); // 早就欠着

        Assert.Equal(["overdue", "alpha", "zeta"], _review.DueQueue(Today).Select(r => r.Word));

        // 昨天答错的 zeta 今天回炉，与 alpha 同日到期 → 薄弱项排前面
        _review.MarkLapse("zeta", Today.AddDays(-1));
        Assert.Equal(["overdue", "zeta", "alpha"], _review.DueQueue(Today).Select(r => r.Word));
    }

    [Fact]
    public void MarkLapse_GoesBackOnePhase_AndShowsInTodaysListWithTomorrowDue()
    {
        _review.ScheduleFirst("persist", Today.AddDays(-4));
        Assert.Equal(1, _review.OpenTodayQueue(Today.AddDays(-3)).Single().Visits); // 第 1 轮回访
        Assert.Equal(2, _review.OpenTodayQueue(Today.AddDays(-1)).Single().Visits); // 第 2 轮回访

        _review.MarkLapse("persist", Today);
        var row = _review.TodayRows(Today).Single();
        Assert.Equal(1, row.Visits); // 退一档
        Assert.Equal(1, row.Lapses);
        Assert.Equal(Today.AddDays(1), row.Due); // 次日再见
        Assert.Contains(_review.DueQueue(Today.AddDays(1)), r => r.Word == "persist");
    }

    [Fact]
    public void OpenTodayQueue_KeepsUrgentWordsFirst_AfterAdvancing()
    {
        _review.ScheduleFirst("fresh", Today.AddDays(-1));
        _review.ScheduleFirst("weak", Today.AddDays(-1));
        _review.ScheduleFirst("overdue", Today.AddDays(-5)); // 欠了 4 天
        _review.MarkLapse("weak", Today.AddDays(-1)); // 昨天答错 → 今天回炉，与 fresh 同日到期

        // 推进后各词的"下次"日期互不相同，但展示顺序仍是：欠得最久 > 答错过 > 刚到期
        Assert.Equal(["overdue", "weak", "fresh"], _review.OpenTodayQueue(Today).Select(r => r.Word));
    }

    [Fact]
    public void Agenda_GroupsFutureDays_AndSkipsTodayAndOverdue()
    {
        _review.ScheduleFirst("a", Today);
        _review.ScheduleFirst("b", Today);
        _review.ScheduleFirst("c", Today.AddDays(-3)); // 逾期，属于今日队列而非日历

        var tomorrow = Assert.Single(_review.Agenda(Today));
        Assert.Equal(Today.AddDays(1), tomorrow.Due);
        Assert.Equal(2, tomorrow.Count);
    }

    [Fact]
    public void Backfill_AnchorsPreFeaturePins_IntoTodaysQueue()
    {
        _log.Add("ancient", "adj. 古代的", "ctx", Today.AddDays(-30)); // 曲线上线之前钉的词
        _log.Add("fresh", "adj. 新的", "ctx", Today);

        Assert.Equal(2, _review.BackfillFromStudyLog(Today));
        Assert.Equal(0, _review.BackfillFromStudyLog(Today)); // 幂等：只补缺失词

        var due = _review.DueQueue(Today);
        Assert.Contains(due, r => r.Word == "ancient"); // 老词一升上来就该复习
        Assert.DoesNotContain(due, r => r.Word == "fresh"); // 今天才钉的排在明天
        Assert.Equal(Today.AddDays(-30).ToString("yyyy-MM-dd"), FirstDay("ancient"));
    }

    [Fact]
    public void PruneUnpinned_DropsProgress_WhenNoPinLeft()
    {
        _log.Add("keep", "", "", Today.AddDays(-2));
        _review.ScheduleFirst("keep", Today.AddDays(-2));
        _review.ScheduleFirst("ghost", Today); // 没有任何钉词挂靠的进度行（脏数据）

        Assert.Equal(1, _review.PruneUnpinned());
        Assert.Equal(1, _review.Count());
        Assert.Equal(0, _review.PruneUnpinned()); // 幂等

        _log.RemoveDay(Today.AddDays(-2)); // 唯一那天的记录也撤了
        Assert.Equal(1, _review.PruneUnpinned());
        Assert.Empty(_review.TodayRows(Today));
    }

    [Fact]
    public void ReviewRows_FallBackToWordbook_WhenSnapshotMeaningEmpty()
    {
        _words.Import("increase", "ɪn'kri:s", "v. 增长", "ky", Today);
        _log.Add("increase", "", "ctx", Today.AddDays(-1));
        _review.BackfillFromStudyLog(Today);
        Assert.Equal("v. 增长", Assert.Single(_review.DueQueue(Today)).Meaning);
    }

    [Fact]
    public void CardSource_PrefersDueReview_ThenStudyLog_ThenDictionary()
    {
        _words.AddManual("dictword", "n. 词典词", Today);
        _log.Add("logged", "adj. 已记录", "ctx", Today);
        var src = new WordbookCardSource(_words, _log, _review);
        Assert.Equal("logged", src.NextCard()!.Word); // 还没有排期 → 当日记录优先

        _review.ScheduleFirst("dueword", Today.AddDays(-1));
        Assert.Equal("dueword", Assert.Single(_review.DueQueue(Today)).Word);
        for (int i = 0; i < 8; i++)
            Assert.Equal("dueword", src.NextCard()!.Word); // 到期队列压过当日记录

        _review.OpenTodayQueue(Today); // 今日队列走完 → 回落到当日记录
        Assert.Equal("logged", src.NextCard()!.Word);

        _log.RemoveDay(Today);
        Assert.Equal("dictword", src.NextCard()!.Word);
    }

    private string FirstDay(string word)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT first_day FROM review_state WHERE word=$w";
        cmd.Parameters.AddWithValue("$w", word);
        return (string)cmd.ExecuteScalar()!;
    }
}

/// <summary>
/// 曲线接线：📌 钉住即建档 → 复习页签打开即推进 → 考试答错回退，以及学习/复习双作用域的页签切换。
/// </summary>
public sealed class ReviewWiringTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Now);
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"se-wire-{Guid.NewGuid():N}.db");
    private readonly LocalDatabase _db;
    private readonly SettingsRepository _settings;
    private readonly WordbookRepository _words;
    private readonly StudyLogRepository _log;
    private readonly ReviewRepository _review;

    private sealed class SilentTts : Core.Abstractions.ITtsService
    {
        public Task<string> SynthesizeAsync(string text, Core.Abstractions.TtsKind kind, string? voice = null,
            CancellationToken ct = default) => Task.FromResult("");
    }

    private sealed class SilentPlayer : Core.Abstractions.IAudioPlayer
    {
        public Task PlayAsync(string filePath, CancellationToken ct = default) => Task.CompletedTask;
        public void Stop() { }
    }

    public ReviewWiringTests()
    {
        _db = new LocalDatabase(_dbPath);
        _db.EnsureSchema();
        _settings = new SettingsRepository(_db);
        _words = new WordbookRepository(_db);
        _log = new StudyLogRepository(_db);
        _review = new ReviewRepository(_db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* temp 自清 */ }
    }

    [Fact]
    public void ReaderPin_AnchorsTheCurve_AndUnpinDropsIt()
    {
        var reader = new ReaderPageViewModel(new SilentTts(), new SilentPlayer(), restoreRecent: false,
            studyLog: _log, wordbook: _words, review: _review);
        reader.RecordSentence("abandon ə'bændən v. 放弃", "abandon ə'bændən v. 放弃");

        Assert.Equal(1, _review.Count());
        Assert.Empty(_review.DueQueue(Today)); // 当天不催：第一轮排在明天
        Assert.Equal(Today.AddDays(1), Assert.Single(_review.DueQueue(Today.AddDays(1))).Due);

        _log.RemoveDay(Today); // 📌 撤销 → 曲线上的锚也没了
        _review.PruneUnpinned();
        Assert.Equal(0, _review.Count());
    }

    [Fact]
    public void StudyTab_And_ReviewTab_SwapCopyAndQueueTogether()
    {
        _words.AddManual("dictword", "n. 词典词", Today);
        _log.Add("logged", "adj. 已记", "ctx", Today);
        _review.ScheduleFirst("duetoday", Today.AddDays(-1));

        var vm = new StudyLogPageViewModel(_log, new SilentTts(), new SilentPlayer(), _review);
        Assert.True(vm.IsStudyTab);
        Assert.Equal("学习记录", vm.Headline);
        Assert.Equal("今日已学习", vm.StatLabel);
        Assert.Equal("1", vm.StatValue); // 今天钉了 1 个

        vm.SelectedTab = 1; // 切作用域：换数据源也换标题、说明与统计口径
        Assert.True(vm.IsReviewTab);
        Assert.Equal("每日复习", vm.Headline);
        Assert.Equal("今日待复习", vm.StatLabel);
        Assert.Contains("1 / 2 / 4 / 7 / 15 / 30", vm.Subline);

        var due = Assert.Single(vm.TodayDue);
        Assert.Equal("duetoday", due.Word);
        Assert.Equal(1, due.Visits); // 打开列表即算过了一轮
        Assert.Equal("第 1/6 轮", due.RoundText);
        Assert.Contains("下次", due.NextText);
        Assert.Contains("今日 1 个", vm.ReviewSummary);
        Assert.Equal("", vm.ReviewEmptyHint);
        Assert.Equal("1", vm.StatValue);

        // 复习页签不碰 📌 列表：切回学习页签仍是当天那两个词的归档视角
        vm.SelectedTab = 0;
        Assert.Equal("学习记录", vm.Headline);
        Assert.Single(vm.Days);
    }

    [Fact]
    public void Exam_WrongAnswer_DropsTheWordOnePhase()
    {
        _review.ScheduleFirst("endure", Today.AddDays(-3));
        _review.OpenTodayQueue(Today.AddDays(-2)); // 前天过了一轮 → 今天再次到期

        _words.AddManual("endure", "v. 忍受", Today);
        var exam = new ExamPageViewModel(_settings, _log, _words, new SilentTts(), new SilentPlayer(), _review);
        var q = exam.Questions.Single(x => x.Word == "endure"); // 到期词优先入卷
        Assert.Contains("词源：今日到期复习优先", exam.SubTitle);
        q.Blanks[0].Input = "¿";

        exam.CheckAllCommand.Execute(null);

        var row = _review.TodayRows(Today).Single();
        Assert.Equal(0, row.Visits); // 从第 1 档退回起点
        Assert.Equal(1, row.Lapses);
        Assert.Equal(Today.AddDays(1), row.Due); // 次日再见
    }

    [Fact]
    public void Exam_RightAnswer_LeavesTheCurveAlone()
    {
        _review.ScheduleFirst("endure", Today.AddDays(-1)); // 今天到期，但还没进复习页
        _words.AddManual("endure", "v. 忍受", Today);
        var exam = new ExamPageViewModel(_settings, _log, _words, null, null, _review);
        var q = exam.Questions.Single(x => x.Word == "endure");
        foreach (var b in q.Slots.OfType<Core.Exam.ExamSlot.Blank>())
            q.Blanks[b.Index].Input = b.Answer.ToString();

        exam.CheckAllCommand.Execute(null);

        var row = _review.TodayRows(Today).Single();
        Assert.Equal(0, row.Visits); // 考试答对不推进档位——推进只属于复习页
        Assert.Equal(0, row.Lapses);
    }
}
