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
}
