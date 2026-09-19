using Parrot.Data;
using Xunit;

namespace Parrot.Tests;

/// <summary>词库仓储端到端（真 SQLite，临时文件库，测毕即删）。</summary>
public class WordbookRepositoryTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Now);
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"se-test-{Guid.NewGuid():N}.db");
    private readonly LocalDatabase _db;
    private readonly WordbookRepository _repo;

    public WordbookRepositoryTests()
    {
        _db = new LocalDatabase(_dbPath);
        _db.EnsureSchema();
        _repo = new WordbookRepository(_db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* temp 自清 */ }
    }

    [Fact]
    public void Import_AddsThenUpserts_PreservingExistingFields()
    {
        Assert.True(_repo.Import("abandon", "ə'bændən", "v. 放弃", "cet4", Today));
        Assert.False(_repo.Import("abandon", "", "新释义", "cet4", Today)); // 重导不重复、不覆盖已有
        var w = _repo.Lookup("abandon")!;
        Assert.Equal("ə'bændən", w.Phonetic); // 空音标不冲掉旧的
        Assert.Equal("v. 放弃", w.Translation); // 已有释义不被新值覆盖
    }

    [Fact]
    public void SeedIfEmpty_ImportsEmbeddedCsv_OnlyWhenEmpty()
    {
        var added = WordbookImporter.SeedIfEmpty(_repo, Today);
        Assert.True(added >= 40);
        Assert.Equal(0, WordbookImporter.SeedIfEmpty(_repo, Today));
        Assert.NotNull(_repo.Lookup("abandon"));
        Assert.NotNull(_repo.Lookup("withstand"));
    }

    [Fact]
    public void MatchPhrases_SkipsWebMinedFragments_KeepsRealCollocations()
    {
        _repo.Import("colour in", "", "[网络] 颜色", null, Today);
        _repo.Import("in autumn", "", "在秋天", null, Today);
        _repo.Import("put up with", "", "忍受, 容忍", null, Today);

        // "colour in" 是 ECDICT 里网页抓取凑数的碎片，命中它会把句子切成无意义的两三个词
        Assert.Equal(["in autumn"], _repo.MatchPhrases("Leaves change colour in autumn."));
        Assert.Equal(["put up with"], _repo.MatchPhrases("The rules are put up with in accordance with the law."));
    }

    [Fact]
    public void CardSource_PrefersStudyLog_ThenDictionary()
    {
        var src = new WordbookCardSource(_repo);
        Assert.Null(src.NextCard()); // 空库 null（App 层再兜底示例）

        _repo.AddManual("alpha", "n. 阿尔法", Today);
        var card = src.NextCard()!;
        Assert.Equal("alpha", card.Word);
        Assert.False(card.IsWrongAnswer);
    }
}
