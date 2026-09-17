using Parrot.Core.TextProcessing;
using Xunit;

namespace Parrot.Tests;

/// <summary>版式重建（需求 1.1 文本化显示 + 1.2 句内发音的数据源）离线测试。</summary>
public class PdfLayoutTests
{
    private static PdfLine L(string text, double top, double size = 10, bool bold = false)
        => new(text, 50, 500, top, top - size * 1.2, size, bold);

    [Fact]
    public void BodyLines_MergeIntoParagraph_AndSplitPerSentence()
    {
        var blocks = LayoutBuilder.BuildBlocks(1,
        [
            L("This is the first sentence. This is the second", 700),
            L("one which continues here.", 690),
        ]);

        var para = Assert.Single(blocks);
        Assert.Equal(PdfBlockKind.Paragraph, para.Kind);
        Assert.Equal(2, para.Fragments.Count);
        Assert.Equal("This is the first sentence.", para.Fragments[0].Display);
        Assert.Equal("This is the first sentence.", para.Fragments[0].Speak); // 全英文 → display==speak
        Assert.Equal("This is the second one which continues here.", para.Fragments[1].Display);
    }

    [Fact]
    public void HyphenLineBreak_JoinsWord()
    {
        var blocks = LayoutBuilder.BuildBlocks(1, [L("informa-", 700), L("tion theory", 690)]);
        var para = Assert.Single(blocks);
        Assert.Equal("information theory", Assert.Single(para.Fragments).Display);
    }

    [Fact]
    public void BigShortLines_MergeIntoSingleHeading_MidCjkOnlyLine_NoSpeak()
    {
        var blocks = LayoutBuilder.BuildBlocks(1,
        [
            L("UNIT 1", 700, size: 16),                    // 大字
            L("考研核心词汇", 660, size: 16, bold: true),     // 同字号粗体中文副题 → 并入同一标题
            L("This is the normal size body paragraph with enough characters.", 620), // 基准正文（10pt）
        ]);

        Assert.Equal(2, blocks.Count);
        Assert.Equal(PdfBlockKind.Heading, blocks[0].Kind);
        Assert.Equal("UNIT 1 考研核心词汇", blocks[0].Fragments[0].Display);
        Assert.Equal("UNIT 1", blocks[0].Fragments[0].Speak); // 发音只读英文部分

        Assert.Equal(PdfBlockKind.Paragraph, blocks[1].Kind);
    }

    [Fact]
    public void PureCjkBodyLine_HasNoSpeak()
    {
        var blocks = LayoutBuilder.BuildBlocks(1, [L("这个国家贫富差距很大。", 700)]);
        var para = Assert.Single(blocks);
        Assert.Null(para.Fragments[0].Speak);
    }

    [Fact]
    public void WordEntryLine_StaysPerLine_SpeakKeepsOnlyHeadword()
    {
        var blocks = LayoutBuilder.BuildBlocks(1,
        [
            L("abandon /ə'bændən/ v. 放弃；抛弃 n. 放任", 700),
            L("absorb /əb'sɔːb/ v. 吸收", 690),
        ]);

        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(PdfBlockKind.Entry, b.Kind));
        // 发音只留词头：音标、中文、词性缩写（"v." 念成 "v dot"）都是噪音
        Assert.Equal("abandon", blocks[0].Fragments[0].Speak);
        // 展示保留原文（含音标/词性/中文）
        Assert.Contains("放弃", blocks[0].Fragments[0].Display);
    }

    [Fact]
    public void VerticalGap_BreaksIntoSeparateParagraphs()
    {
        var blocks = LayoutBuilder.BuildBlocks(1,
        [
            L("first paragraph text", 700),
            L("second paragraph text", 640), // 大空行
        ]);

        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(PdfBlockKind.Paragraph, b.Kind));
    }

    [Fact]
    public void TwoColumn_Lines_OnTheSameRow_StayApart()
    {
        // 双栏表/双栏正文：两格同一 y、x 区间不相交。按 y 排序后它们相邻，
        // 若照"字号一致 + 行距正常"合并就会拼出左右交错的串句
        static PdfLine Cell(string text, double left, double top)
            => new(text, left, left + 200, top, top - 12, 10, false);

        var blocks = LayoutBuilder.BuildBlocks(1,
        [
            Cell("the climate is warming", 50, 700), Cell("a rapid trend worldwide", 300, 700),
            Cell("and this matters a lot", 50, 686), Cell("for every living thing", 300, 686),
        ]);

        Assert.Equal(4, blocks.Count);
        Assert.Equal("the climate is warming", blocks[0].Fragments[0].Display);
        Assert.Equal("a rapid trend worldwide", blocks[1].Fragments[0].Display);
    }

    [Fact]
    public void NumberedOrBulletedLine_IsListItem()
    {
        var blocks = LayoutBuilder.BuildBlocks(1, [L("1. analyze the data carefully", 700)]);
        Assert.Equal(PdfBlockKind.ListItem, Assert.Single(blocks).Kind);
    }

    [Fact]
    public void MixedChineseAnnotation_SplitsDisplayKeepsSpeak()
    {
        var frags = SentenceSplitter.SplitWithDisplay(
            "It is a well-known fact. 【真题复现】Another sentence follows.");

        Assert.Equal(2, frags.Count); // 【…】前断一截；中文尾巴与后句同行（无独立句末标点）
        Assert.Equal("It is a well-known fact.", frags[0].Display);
        Assert.Equal("It is a well-known fact.", frags[0].Speak);
        Assert.Contains("真题复现", frags[1].Display);              // 展示保留中文
        Assert.Equal("Another sentence follows.", frags[1].Speak); // 发音只读英文
    }

    [Fact]
    public void AbbreviationDot_NotTreatedAsSentenceEnd_InDisplaySplit()
    {
        var frags = SentenceSplitter.SplitWithDisplay("Use e.g. this one. Done.");
        Assert.Equal(2, frags.Count);
        Assert.Equal("Use e.g. this one.", frags[0].Speak);
    }

    [Fact]
    public void EnglishOnly_RemovesPunctuationVariants()
    {
        Assert.Equal("hello world", SentenceSplitter.EnglishOnly("hello world，世界"));
        Assert.Null(SentenceSplitter.EnglishOnly("【真题复现】"));
        Assert.Null(SentenceSplitter.EnglishOnly("123 456"));
    }

    [Fact]
    public void EnglishOnly_Removes_UnbalancedPhoneticResidue()
    {
        // Vision/WinRT 常把一条注音撕成两个行框：闭合的那段能被 PhoneticSpan 吃掉，
        // 剩下的 "ikri:s］" 碎片和落单的词性字母必须另行剔除
        Assert.Equal("increase productivity.",
            SentenceSplitter.EnglishOnly("increase ［ɪn'kri:s］ ikri:s］ productivity. n"));
        Assert.Equal("increase productivity.",
            SentenceSplitter.EnglishOnly("increase [ɪn'kri:s] ɪkri:s] productivity. v"));
    }

    [Fact]
    public void EnglishOnly_KeepsApostrophes_AndSlashLists()
    {
        // 两条都是"清洗规则误伤正文"的实测回归
        Assert.Equal("It's not as hard for them.", SentenceSplitter.EnglishOnly("It's not as hard for them."));
        Assert.Equal("to deal with enquiries/issues/complaints",
            SentenceSplitter.EnglishOnly("to deal with enquiries/issues/complaints"));
        // 斜杠形式仍要能当音标剥掉（前不接字母、内部无空白）
        Assert.Equal("abandon", SentenceSplitter.EnglishOnly("abandon /ə'bændən/"));
    }
}
