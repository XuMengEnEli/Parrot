using Parrot.Core.TextProcessing;
using Xunit;

namespace Parrot.Tests;

/// <summary>
/// 短语切分器（纯逻辑）：最长优先、互不重叠、词典外的词跳过。
/// 数据侧只验证候选生成与批量查询的接线，贪心策略全在这里锁死。
/// </summary>
public sealed class PhraseMatcherTests
{
    private static List<string> Match(string text, params string[] dictionary)
    {
        var set = new HashSet<string>(dictionary, StringComparer.Ordinal);
        return PhraseMatcher.Find(PhraseMatcher.Tokenize(text), set.Contains);
    }

    [Fact]
    public void LongestWins_ThenScans_AfterTheMatch()
    {
        Assert.Equal(["put up with"],
            Match("The rules are put up with daily.", "put up", "put up with"));
    }

    [Fact]
    public void MultiplePhrases_InSentenceOrder_NonOverlapping()
        => Assert.Equal(["put up with", "in accordance with"],
            Match("The rules are put up with in accordance with the law.",
                "put up with", "in accordance with", "with in"));

    [Fact]
    public void SixWordCap_CoversFiveWordPhrases_ButNotSeven()
    {
        // 提到 6 词就是为了这类五词搭配不再被截断成 "a matter of"
        Assert.Equal(["as a matter of fact"],
            Match("He gave it some thought as a matter of fact.", "a matter of", "as a matter of fact"));
        Assert.Empty(Match("It happened on the other side of the moon.", "on the other side of the moon")); // 7 词，超上限
    }

    [Fact]
    public void NoPhraseMatch_ReturnsEmpty_CallerFallsBackToWord()
        => Assert.Empty(Match("He took the exam yesterday.", "take over", "look forward to"));

    [Fact]
    public void PunctuationAndCase_AreInvisible_ToTheMatcher()
        => Assert.Equal(["looks forward to"],
            Match("She LOOKED—no, she looks forward to it!", "looks forward to"));

    [Fact]
    public void ArticlesInsidePhrases_AreKept()
        => Assert.Equal(["a variety of"], Match("a variety of reasons", "a variety of", "of reasons"));

    [Fact]
    public void AsWholePhrase_KeepsLectureCollocations_ButNotSentences()
    {
        // 讲义词表行：ECDICT 没收录的教材搭配，整行才是要记的东西（真实讲义第 3 页实测）
        Assert.Equal("social pressure", PhraseMatcher.AsWholePhrase("social pressure 社会压力"));
        Assert.Equal("prosocial behavior", PhraseMatcher.AsWholePhrase("prosocial behavior 亲社会行为"));
        Assert.Equal("asocial robot rats", PhraseMatcher.AsWholePhrase("asocial robot rats 非社交型机器鼠"));
        // 正文句子：有句末标点或代词/疑问词开头，不能整行吞下
        Assert.Null(PhraseMatcher.AsWholePhrase("He took the exam yesterday."));
        Assert.Null(PhraseMatcher.AsWholePhrase("How teenagers develop prosociality"));
        Assert.Null(PhraseMatcher.AsWholePhrase("society n. 社会")); // 词性缩写里的点说明这行是单词条目
        Assert.Null(PhraseMatcher.AsWholePhrase("solo"));
    }

    [Fact]
    public void IsSentence_OnlyFullSentences_EndingWithAWord()
    {
        Assert.True(PhraseMatcher.IsSentence("Leaves change colour in autumn."));
        Assert.False(PhraseMatcher.IsSentence("He took the exam yesterday")); // 没句末标点：段落续行，走短语/兜底判定
        Assert.False(PhraseMatcher.IsSentence("a change in the weather")); // 词组行走短语判定
        // 词表行被 EnglishOnly 剥成 "abandon ə'bændən v."：句点收尾但末尾是词性缩写，不是句子
        Assert.False(PhraseMatcher.IsSentence("abandon ə'bændən v."));
        Assert.False(PhraseMatcher.IsSentence("society n."));
    }

    [Fact]
    public void TwoWordPhrase_StartingWithFunctionWord_IsJunk_Skipped()
    {
        // 真实词库抽查：这些两词组合是为覆盖度凑数的自由组合，不是值得记的搭配
        Assert.Empty(Match("He reads the law every day.", "the law", "a day"));
        Assert.Empty(Match("The fact that many students fail is known.", "that many"));
        // 三词以上的限定词搭配是真固定说法，留
        Assert.Equal(["the number of"], Match("The number of cases rose.", "the number of"));
    }

    [Fact]
    public void Tokenize_KeepsSingleLetterWords_AndSplitsNonLetters()
        => Assert.Equal(["take", "a", "walk", "i", "can't"], PhraseMatcher.Tokenize("take a walk; I can't."));

    [Fact]
    public void Candidates_AreAllWindows_Min2Max6_Distinct()
    {
        var tokens = PhraseMatcher.Tokenize("one two three");
        Assert.Equal(["one two", "one two three", "two three"],
            PhraseMatcher.Candidates(tokens).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Empty(PhraseMatcher.Candidates(PhraseMatcher.Tokenize("solo")));
    }
}
