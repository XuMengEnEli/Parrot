using Parrot.Core.Abstractions;
using Parrot.Core.TextProcessing;
using Xunit;

namespace Parrot.Tests;

/// <summary>OCR 行框 → PdfLine 映射 + 进 LayoutBuilder 的分类联动（扫描件兜底链路，纯函数可测）。</summary>
public class OcrLayoutTests
{
    private const double Scale200 = 72.0 / 200; // 200dpi：px→pt

    [Fact]
    public void Maps_PixelCoords_To_PdfSpace_And_Classifies_Headings_And_Speaks_English()
    {
        // 模拟一页 200dpi OCR 输出（乱序输入）：大行高标题两行 + 英文句 + 中文译文
        var mapped = OcrLayout.ToPdfLines(
        [
            new OcrTextLine("技术已经改变了我们日常生活的很多方面。", 60, 170, 600, 40),
            new OcrTextLine("Unit 12", 60, 40, 200, 60),
            new OcrTextLine("科技与生活", 60, 40, 300, 60),
            new OcrTextLine("Technology has changed our daily life in many ways.", 60, 120, 700, 40),
        ], Scale200);

        // 视觉自上而下（Top 降序）；同 Top 两行（同一 OCR 行带）先后顺序不做承诺
        Assert.True(mapped[0].Text is "科技与生活" or "Unit 12", $"首行应为大字标题，实际「{mapped[0].Text}」");
        Assert.Equal(mapped[1].Top, mapped[0].Top);
        Assert.StartsWith("技术", mapped[^1].Text);

        var blocks = LayoutBuilder.BuildBlocks(1, mapped);

        // 大字标题行 → Heading（两行同字号合并），发音只读英文
        Assert.Equal(PdfBlockKind.Heading, blocks[0].Kind);
        Assert.Equal("Unit 12", blocks[0].Fragments[0].Speak);

        // 英文句 + 中文译文 → 一个正文段，两个片段（中文片段不挂按钮）
        var para = Assert.Single(blocks.Skip(1));
        Assert.Equal(PdfBlockKind.Paragraph, para.Kind);
        Assert.Equal(2, para.Fragments.Count);
        Assert.Equal("Technology has changed our daily life in many ways.", para.Fragments[0].Speak);
        Assert.Null(para.Fragments[1].Speak);
    }

    [Fact]
    public void WhitespaceLines_AreDropped()
    {
        var mapped = OcrLayout.ToPdfLines(
        [
            new OcrTextLine("   ", 0, 0, 10, 10),
            new OcrTextLine("real line", 0, 20, 50, 10),
        ], Scale200);
        var line = Assert.Single(mapped);
        Assert.Equal("real line", line.Text);
    }

    [Fact]
    public void WithPageHeight_Maps_Into_Real_Page_Coords()
    {
        // A4@200dpi 高 2200px；传页高后应落在 (0, 792pt) 页内且视觉靠上的行 Top 更大（原图热点定位依赖此）
        var mapped = OcrLayout.ToPdfLines(
        [
            new OcrTextLine("top line", 60, 40, 200, 15),
            new OcrTextLine("bottom line", 60, 1900, 200, 15),
        ], Scale200, pageHeightPx: 2200);
        Assert.Equal("top line", mapped[0].Text);
        Assert.InRange(mapped[0].Top, 0, 792);
        Assert.InRange(mapped[1].Bottom, 0, 792);
        Assert.True(mapped[0].Top > mapped[^1].Top);
    }

    [Fact]
    public void Normalize_DeSpaces_Between_Cjk_But_Keeps_Latin_Boundaries()
    {
        // 引擎风格："上 策 词 …"；CJK/全角之间去空格，中英边界保留单个空格
        Assert.Equal(
            "上策词（高频词） 21 天搞定 800+ 核心词",
            OcrLayout.Normalize("上 策 词 （ 高 频 词 ） 21 天 搞 定 800+ 核 心 词"));
        // 纯英文行不动
        Assert.Equal("social pressure 社会压力", OcrLayout.Normalize("social pressure 社会压力"));
    }

    [Fact]
    public void WorthKeeping_Accepts_Text_Body_Rejects_Graphic_Fragments()
    {
        // 讲义式：整句正文 → 采纳
        var body = OcrLayout.ToPdfLines(
        [
            new OcrTextLine("Her research is centered on the social effects of unemployment.", 60, 120, 700, 15),
            new OcrTextLine("她的研究课题是失业对社会的影响。", 60, 150, 460, 15),
        ], Scale200);
        Assert.True(OcrLayout.WorthKeeping(body));

        // 思维导图式：散落节点字块（平均行长极短）→ 拒绝，保留扫描件占位卡
        var graphic = OcrLayout.ToPdfLines(
        [
            new OcrTextLine("驴", 60, 120, 20, 15),
            new OcrTextLine("超", 200, 150, 20, 15),
            new OcrTextLine("0 0 0", 300, 180, 60, 15),
            new OcrTextLine("仁", 400, 210, 20, 15),
        ], Scale200);
        Assert.False(OcrLayout.WorthKeeping(graphic));
        Assert.False(OcrLayout.WorthKeeping([]));
    }
}
