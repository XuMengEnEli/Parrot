using Parrot.Core;
using Xunit;

namespace Parrot.Tests;

/// <summary>
/// 品牌改名（StudyEnglish → Parrot）后的本地数据搬移：只在新目录不存在、旧目录存在时搬一次，
/// 库文件一并改名，且重复调用与"新目录已有数据"都不能碰已有内容。
/// </summary>
public sealed class LegacyUserDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"parrot-mig-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp 自清 */ }
    }

    [Fact]
    public void Moves_OldDir_AndRenamesLegacyDb()
    {
        var old = Directory.CreateDirectory(Path.Combine(_root, "StudyEnglish")).FullName;
        File.WriteAllText(Path.Combine(old, "study-english.db"), "data");
        File.WriteAllText(Path.Combine(old, "recent-docs.txt"), "list");
        Directory.CreateDirectory(Path.Combine(old, "tts-cache"));

        LegacyUserData.MigrateInto(_root);

        var neu = Path.Combine(_root, "Parrot");
        Assert.False(Directory.Exists(old));
        Assert.Equal("data", File.ReadAllText(Path.Combine(neu, "parrot.db")));
        Assert.Equal("list", File.ReadAllText(Path.Combine(neu, "recent-docs.txt")));
        Assert.True(Directory.Exists(Path.Combine(neu, "tts-cache")));
    }

    [Fact]
    public void Second_Call_Is_Noop()
    {
        LegacyUserData.MigrateInto(_root); // 两个目录都不存在
        Assert.False(Directory.Exists(Path.Combine(_root, "Parrot")));

        var neu = Directory.CreateDirectory(Path.Combine(_root, "Parrot")).FullName;
        File.WriteAllText(Path.Combine(neu, "parrot.db"), "new");
        Directory.CreateDirectory(Path.Combine(_root, "StudyEnglish")); // 残留空旧目录

        LegacyUserData.MigrateInto(_root);

        Assert.Equal("new", File.ReadAllText(Path.Combine(neu, "parrot.db")));
        Assert.True(Directory.Exists(Path.Combine(_root, "StudyEnglish"))); // 不覆盖、不合并
    }

    [Fact]
    public void Keeps_LegacyDb_When_Newer_Database_Already_Inside()
    {
        var old = Directory.CreateDirectory(Path.Combine(_root, "StudyEnglish")).FullName;
        File.WriteAllText(Path.Combine(old, "study-english.db"), "legacy");
        File.WriteAllText(Path.Combine(old, "parrot.db"), "current");

        LegacyUserData.MigrateInto(_root);

        var neu = Path.Combine(_root, "Parrot");
        Assert.Equal("current", File.ReadAllText(Path.Combine(neu, "parrot.db")));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(neu, "study-english.db")));
    }
}
