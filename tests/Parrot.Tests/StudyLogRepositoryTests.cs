using Parrot.Data;
using Xunit;

namespace Parrot.Tests;

/// <summary>学习记录仓储 + 弹窗取词优先级（真 SQLite 临时库，测毕即删）。</summary>
public class StudyLogRepositoryTests : IDisposable
{
    // 取系统当天：WordbookCardSource 按真实日期查"当日学习记录"，写死日期过一天就取不到
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Now);
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"se-log-{Guid.NewGuid():N}.db");
    private readonly LocalDatabase _db;
    private readonly WordbookRepository _words;
    private readonly StudyLogRepository _log;

    public StudyLogRepositoryTests()
    {
        _db = new LocalDatabase(_dbPath);
        _db.EnsureSchema();
        _words = new WordbookRepository(_db);
        _log = new StudyLogRepository(_db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* temp 自清 */ }
    }

    [Fact]
    public void Add_DedupesSameDay_AndTracksNew()
    {
        Assert.True(_log.Add("abandon", "v. 放弃", "abandon n.", Today));
        Assert.False(_log.Add("ABANDON ", "", "", Today)); // 规范化 + 同日去重
        Assert.True(_log.Add("abandon", "", "", Today.AddDays(-1))); // 不同日可再记
        Assert.Single(_log.WordsOfDay(Today));
        Assert.True(_log.HasWord("abandon", Today));
    }

    [Fact]
    public void Add_BackfillsMeaningSnapshotWhenEmpty()
    {
        _log.Add("focus", "", "ctx", Today);
        _log.Add("focus", "n. 焦点", "", Today); // 空→补，已有不覆盖
        var row = _log.WordsOfDay(Today).Single();
        Assert.Equal("n. 焦点", row.Meaning);
        Assert.Equal("ctx", row.Note); // 空出处不覆盖旧出处
    }

    [Fact]
    public void Days_GroupsCountsDescending()
    {
        _log.Add("a", "", "", Today);
        _log.Add("b", "", "", Today);
        _log.Add("c", "", "", Today.AddDays(-2));
        var days = _log.Days();
        Assert.Equal(2, days.Count);
        Assert.Equal(Today.ToString("yyyy-MM-dd"), days[0].Day);
        Assert.Equal(2, days[0].Count);
        Assert.Equal(1, days[1].Count);
    }

    [Fact]
    public void Remove_SingleWord_AndWholeDay()
    {
        _log.Add("x", "", "", Today);
        _log.Add("y", "", "", Today);
        Assert.True(_log.Remove("X", Today));
        Assert.False(_log.Remove("x", Today));
        Assert.Single(_log.WordsOfDay(Today));
        Assert.Equal(1, _log.RemoveDay(Today));
        Assert.Empty(_log.Days());
    }

    [Fact]
    public void CardSource_PrefersTodaysStudyList_ThenDictionary()
    {
        _words.AddManual("dictword", "n. 词典词", Today);
        var src = new WordbookCardSource(_words, _log);

        // 当天列表为空 → 内嵌词库随机（这里词库只有一个词，随机也必中）
        Assert.Equal("dictword", src.NextCard()!.Word);

        // 记一个词 → 弹窗词源切到当日学习记录（带释义快照）
        _log.Add("logged", "adj. 已记录", "note", Today);
        Assert.Equal("logged", src.NextCard()!.Word);
        Assert.Equal("adj. 已记录", src.NextCard()!.Meaning);

        // 当天记录清空 → 回词库随机
        _log.RemoveDay(Today);
        Assert.Equal("dictword", src.NextCard()!.Word);
    }

    [Fact]
    public void RandomOfDay_CapsAndReturnsSubset()
    {
        for (int i = 0; i < 5; i++) _log.Add($"w{i}", "", "", Today);
        Assert.Equal(3, _log.RandomOfDay(Today, 3).Count);
        Assert.Equal(5, _log.RandomOfDay(Today, 10).Count); // 不足 n 返回全部
    }
}
