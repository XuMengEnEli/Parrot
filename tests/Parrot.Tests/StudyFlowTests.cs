using Parrot.Core.Abstractions;
using Parrot.Data;
using Parrot.UI.ViewModels;
using Xunit;

namespace Parrot.Tests;

/// <summary>
/// 新三件套的 VM/纯逻辑流：📌 取词（Headword）、记入当日学习 → 弹窗词源切换、
/// SentenceItem 记录态切换、拼写考试整轮出题→检查→判分。
/// </summary>
public sealed class StudyFlowTests : IDisposable
{
    // 取系统当天：链路里查"当日"用的是真实时钟，写死日期过一天就失效
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Now);
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"se-flow-{Guid.NewGuid():N}.db");
    private readonly LocalDatabase _db;
    private readonly SettingsRepository _settings;
    private readonly WordbookRepository _words;
    private readonly StudyLogRepository _log;

    private sealed class StubTts : ITtsService
    {
        public Task<string> SynthesizeAsync(string text, TtsKind kind, string? voice = null, CancellationToken ct = default)
            => Task.FromResult("");
    }

    private sealed class StubPlayer : IAudioPlayer
    {
        public Task PlayAsync(string filePath, CancellationToken ct = default) => Task.CompletedTask;
        public void Stop() { }
    }

    public StudyFlowTests()
    {
        _db = new LocalDatabase(_dbPath);
        _db.EnsureSchema();
        _settings = new SettingsRepository(_db);
        _words = new WordbookRepository(_db);
        _log = new StudyLogRepository(_db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* temp 自清 */ }
    }

    // ---------- 📌 取词 ----------

    [Fact]
    public void Headword_EntryLine_TakesLemma_NotPhoneticOrChinese()
        => Assert.Equal("abandon", ReaderPageViewModel.Headword("abandon ə'bændən v. 放弃"));

    [Fact]
    public void Headword_Sentence_SkipsStopwords()
    {
        Assert.Equal("committee", ReaderPageViewModel.Headword("The committee decided to act."));
        Assert.Equal("focus", ReaderPageViewModel.Headword("A focus group of 25 students."));
    }

    [Fact]
    public void Headword_AllStopwords_FallsBackToFirstWord()
        => Assert.Equal("the", ReaderPageViewModel.Headword("The and of"));

    [Fact]
    public void Headword_EmptyOrNullish_ReturnsNull()
    {
        Assert.Null(ReaderPageViewModel.Headword(null));
        Assert.Null(ReaderPageViewModel.Headword("   123 45.6 "));
    }

    [Fact]
    public void SentenceItem_RecordToggles_GlyphAndFlag()
    {
        int calls = 0;
        var vm = new SentenceItemViewModel("abandon", "abandon", new StubTts(), new StubPlayer(),
            _ => calls++);
        Assert.True(vm.IsRecordable);
        Assert.Equal("📌", vm.RecordGlyph);
        vm.RecordCommand.Execute(null);
        Assert.True(vm.IsRecorded);
        Assert.Equal("✅", vm.RecordGlyph);
        vm.RecordCommand.Execute(null);
        Assert.False(vm.IsRecorded);
        Assert.Equal(2, calls);

        var noLog = new SentenceItemViewModel("x", "x", new StubTts(), new StubPlayer());
        Assert.False(noLog.IsRecordable);
    }

    [Fact]
    public void ReaderRecord_FullChain_LogsWordWithMeaningSnapshot_PopupServesIt()
    {
        _words.AddManual("abandon", "v. 放弃", Today);
        var reader = new ReaderPageViewModel(new StubTts(), new StubPlayer(), restoreRecent: false,
            studyLog: _log, wordbook: _words);
        reader.RecordWord("abandon ə'bændən v. 放弃", "abandon ə'bændən v. 放弃");
        var row = Assert.Single(_log.WordsOfDay(Today));
        Assert.Equal("abandon", row.Word);
        Assert.Equal("v. 放弃", row.Meaning);
        Assert.Contains("放弃", row.Note);

        // 弹窗词源即刻切换：今天的记录优先
        var card = new WordbookCardSource(_words, _log).NextCard()!;
        Assert.Equal("abandon", card.Word);
    }

    // ---------- ✍️ 考试 ----------

    [Fact]
    public void Exam_FullRound_FillAllCheckPerfectScore()
    {
        for (int i = 0; i < 12; i++) _words.AddManual($"word{i}extra", "n. 测试", Today);
        var exam = new ExamPageViewModel(_settings, _log, _words);
        Assert.Equal(ExamPageViewModel.RoundSize, exam.Questions.Count);
        Assert.False(exam.IsPoolEmpty);

        foreach (var q in exam.Questions)
            foreach (var b in q.Slots.OfType<Core.Exam.ExamSlot.Blank>())
                q.Blanks[b.Index].Input = b.Answer.ToString();

        exam.CheckAllCommand.Execute(null);
        Assert.True(exam.Checked);
        Assert.All(exam.Questions, q => Assert.True(q.AllCorrect));
        Assert.Contains("10/10", exam.ScoreText);
        Assert.All(exam.Questions, q => Assert.False(q.ShowReveal));
    }

    [Fact]
    public void Exam_WrongInput_MarksRedAndRevealsAnswer()
    {
        for (int i = 0; i < 12; i++) _words.AddManual($"word{i}extra", "n. 测试", Today);
        var exam = new ExamPageViewModel(_settings, _log, _words);
        var q = exam.Questions.First(x => x.Blanks.Count > 0);
        q.Blanks[0].Input = "¿"; // 明显错
        foreach (var b in q.Slots.OfType<Core.Exam.ExamSlot.Blank>().Skip(1))
            q.Blanks[b.Index].Input = b.Answer.ToString();

        exam.CheckAllCommand.Execute(null);
        Assert.False(q.AllCorrect);
        Assert.True(q.ShowReveal);
        Assert.Equal(2, q.Blanks[0].State);
        Assert.True(q.Blanks[0].ShowAnswerHint);
    }

    [Fact]
    public void Exam_BlanksCountFollowsSetting()
    {
        _settings.ExamBlanks = 5;
        for (int i = 0; i < 12; i++) _words.AddManual("communication", "n. 交流", Today.AddDays(1));
        _words.AddManual("cat", "n. 猫", Today);
        _words.AddManual("extra", "n. 额外", Today);
        var exam = new ExamPageViewModel(_settings, _log, _words);
        var longQ = exam.Questions.First(q => q.Word.Length >= 7);
        Assert.True(longQ.Blanks.Count <= 5);
        var shortQ = exam.Questions.First(q => q.Word.Length <= 3);
        Assert.True(shortQ.Blanks.Count <= shortQ.Word.Length - 2); // 至少露 2 个字母
    }

    [Fact]
    public void Exam_PoolPrefersTodayStudyLog()
    {
        _log.Add("abandon", "v. 放弃", "ctx", Today);
        for (int i = 0; i < 30; i++) _words.AddManual($"fill{i}x", "", Today);
        var exam = new ExamPageViewModel(_settings, _log, _words);
        Assert.Contains(exam.Questions, q => q.Word == "abandon");
    }
}
