using Parrot.Data;
using Xunit;

namespace Parrot.Tests;

public class CsvReaderTests
{
    private static List<string[]> Parse(string text)
        => CsvReader.ReadRecords(new StringReader(text)).ToList();

    [Fact]
    public void SimpleRows()
    {
        var rows = Parse("a,b,c\n1,2,3\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal(["a", "b", "c"], rows[0]);
    }

    [Fact]
    public void QuotedFieldWithCommaAndNewline()
    {
        var rows = Parse("word,trans\nfoo,\"v. 一,二\nn. 三\"\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal("v. 一,二\nn. 三", rows[1][1]);
    }

    [Fact]
    public void EscapedQuotes()
    {
        var rows = Parse("a,\"he said \"\"hi\"\"\"\n");
        Assert.Equal("he said \"hi\"", rows[0][1]);
    }

    [Fact]
    public void CrlfAndNoTrailingNewline()
    {
        var rows = Parse("a,b\r\nc,d");
        Assert.Equal(2, rows.Count);
        Assert.Equal(["c", "d"], rows[1]);
    }
}
