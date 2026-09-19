using Parrot.Core.TextProcessing;
using Xunit;

namespace Parrot.Tests;

public class SentenceSplitterTests
{
    [Fact]
    public void SplitsOnSentenceEnders()
    {
        var result = SentenceSplitter.Split("The data suggests a change. It works well! Are you sure?");
        Assert.Equal(3, result.Count);
        Assert.Equal("The data suggests a change.", result[0]);
    }

    [Fact]
    public void ProtectsAbbreviations()
    {
        var result = SentenceSplitter.Split("As Mr. Smith noted, e.g. the U.S. case, this holds. Next sentence here.");
        Assert.Equal(2, result.Count);
        Assert.Equal("As Mr. Smith noted, e.g. the U.S. case, this holds.", result[0]);
    }

    [Fact]
    public void StripsCjkBlocksKeepsEnglish()
    {
        var result = SentenceSplitter.Split("【真题复现】 The trend is clear. 这题考图表。 It will rise.");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void QuotedSentenceEnd_BreaksBeforeQuote()
    {
        var result = SentenceSplitter.Split("He said \"stop.\" Then he left.");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void EmptyOrShort_ReturnsNothing()
    {
        Assert.Empty(SentenceSplitter.Split(""));
        Assert.Empty(SentenceSplitter.Split("中文内容"));
    }

    [Fact]
    public void EnglishOnly_FullWidthEnderAfterLetter_BecomesRealSentenceEnd()
    {
        // 讲义第 3 页 "How teenagers develop prosociality？"：OCR 给全角问号，📌 要靠它认出整句
        Assert.Equal("How teenagers develop prosociality?",
            SentenceSplitter.EnglishOnly("How teenagers develop prosociality？"));
    }

    [Fact]
    public void EnglishOnly_ChineseOnlyLine_ItsOwnPeriodIsNotAnEnglishSentenceEnd()
    {
        Assert.Null(SentenceSplitter.EnglishOnly("这行纯中文没有任何英文。"));
    }

    [Fact]
    public void EnglishOnly_MixedLine_KeepsOnlyTheEnglishSentenceEnder()
    {
        // 扫描件里译文常和英文挤在同一行：两个全角问号只有字母后那个是句界
        var speak = SentenceSplitter.EnglishOnly("How teenagers develop prosociality？青少年如何发展亲社会性？");
        Assert.Equal("How teenagers develop prosociality?", speak);
        Assert.True(PhraseMatcher.IsSentence(speak!)); // 📌 因此会整句记入
    }
}
