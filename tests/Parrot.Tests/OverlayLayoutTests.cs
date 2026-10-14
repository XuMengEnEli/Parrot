using Parrot.Core.TextProcessing;
using Xunit;

namespace Parrot.Tests;

/// <summary>原图 🔊 热点提取（OverlayLayout）：锚点=句末所属行的右缘/垂直中线，行为纯函数可测。</summary>
public class OverlayLayoutTests
{
    [Fact]
    public void Wrapped_Paragraph_Anchors_At_Last_Line_Of_Sentence()
    {
        var lines = new List<PdfLine>
        {
            new("Technology has changed our", 60, 300, 700, 685, 11, false),
            new("daily life in many ways.", 60, 260, 682, 667, 11, false), // 句尾在这一行 → 锚它
        };
        var spot = Assert.Single(OverlayLayout.BuildSpots(lines));
        Assert.Equal(260, spot.RightPt);
        Assert.Equal(674.5, spot.MidYPt, 3);
        // 跨行按空格拼接；句末标点保留（📌 靠它认出这是整句），只剥 OCR 挂在句界的残留
        Assert.Equal("Technology has changed our daily life in many ways.", spot.Speak);
    }

    [Fact]
    public void Hyphen_Linebreak_Joins_The_Word()
    {
        var lines = new List<PdfLine>
        {
            new("modern communi-", 60, 300, 700, 685, 11, false),
            new("cation tools work.", 60, 280, 682, 667, 11, false),
        };
        var spot = Assert.Single(OverlayLayout.BuildSpots(lines));
        Assert.Equal("modern communication tools work.", spot.Speak);
        Assert.Equal(280, spot.RightPt); // 句末行 = 第二行
    }

    [Fact]
    public void Heading_Lines_Get_No_Spot_Entry_Lines_Do()
    {
        // Unit 小标题不用翻译（用户要求）：大字号 Heading 不再产按钮，词条行照旧
        var lines = new List<PdfLine>
        {
            new("Unit 12", 60, 150, 760, 740, 20, false),         // 大字号 → Heading：不产热点
            new("abandon v. 放弃", 60, 220, 700, 688, 11, false), // 英中混排行首英文 → Entry
        };
        var spot = Assert.Single(OverlayLayout.BuildSpots(lines));
        Assert.StartsWith("abandon", spot.Speak);
        Assert.Equal(220, spot.RightPt);
    }

    [Fact]
    public void Header_And_PageNumber_Lines_Produce_No_Spots()
    {
        // 页眉书名/页码不产热点（不用翻译发音，用户要求）；不传页高时不过滤（调试 dump 仍能看全量）
        var lines = new List<PdfLine>
        {
            new("MBA大师英语词汇", 60, 200, 810, 796, 11, false), // 页眉（视觉最顶）：y-up 810 > 842×0.94
            new("abandon v. 放弃", 60, 220, 700, 688, 11, false),
            new("12", 300, 320, 30, 20, 11, false),               // 页码（视觉最底）
        };
        var spot = Assert.Single(OverlayLayout.BuildSpots(lines, 842));
        Assert.StartsWith("abandon", spot.Speak);
    }

    [Fact]
    public void Unit_Title_Lines_Get_No_Spots()
    {
        // Unit 小标题不挂按钮（用户要求，不用翻译）；"unit n." 词条行要求 Unit 后跟数字，不受影响
        var lines = new List<PdfLine>
        {
            new("Unit 3", 60, 200, 700, 680, 12, false),
            new("● Unit 4", 60, 200, 700, 680, 12, false),
            new("change v. 改变", 60, 220, 700, 688, 11, false),
            new("unit n. 单元", 60, 220, 700, 688, 11, false),
        };
        var spots = OverlayLayout.BuildSpots(lines, 842);
        Assert.Equal(2, spots.Count);
        Assert.StartsWith("change", spots[0].Speak);
        Assert.StartsWith("unit", spots[1].Speak);
    }

    [Fact]
    public void Chinese_Only_Lines_Produce_Nothing_And_Edge_Junk_Is_Trimmed()
    {
        var lines = new List<PdfLine>
        {
            new("MBA 大师", 60, 160, 700, 688, 11, false),   // Entry：中文被剥，只剩 MBA
            new("这行纯中文没有任何英文。", 60, 300, 680, 668, 11, false), // 无 speak → 无热点
        };
        var spot = Assert.Single(OverlayLayout.BuildSpots(lines));
        Assert.Equal("MBA", spot.Speak);
    }

    [Fact]
    public void Chinese_Translation_Line_Becomes_Previous_Spot_Gloss()
    {
        var lines = new List<PdfLine>
        {
            new("Leaves change colour in autumn.", 60, 300, 480, 685, 11, false),
            new("树叶在秋天改变颜色。", 60, 280, 300, 667, 11, false), // 不产热点，但它是上一句的释义
            new("a change in the weather", 60, 250, 420, 640, 11, false),
            new("天气的变化", 60, 230, 200, 622, 11, false),
            new("【真题复现】", 60, 200, 260, 600, 12, false), // 符号标题：不挂到任何句子上
            new("Company profits were lower.", 60, 180, 430, 580, 11, false),
            new("【释义】n.公司", 60, 160, 250, 562, 11, false), // 下一个词条的释义栏，不是上一句的译文
        };
        var spots = OverlayLayout.BuildSpots(lines);
        Assert.Equal(3, spots.Count);
        Assert.Equal("树叶在秋天改变颜色。", spots[0].Gloss);
        Assert.Equal("天气的变化", spots[1].Gloss);
        Assert.Null(spots[2].Gloss);
    }

    [Fact]
    public void Two_Column_Table_Cells_Do_Not_Merge_Across_Columns()
    {
        // 实扫双栏词表：左栏词头+音标，右栏例句，两格同 y、x 区间不相交。
        // 合并成一段会同时踩两个用户报的坑：① 未闭合的音标残片被拼进句尾（多余字符）
        // ② 整段只剩一个锚点且落在最右缘 → 词头旁边没有 🔊（"没识别到"）
        var lines = new List<PdfLine>
        {
            new("increase ［ɪn'kri:s］ ikri:s］ n", 50, 210, 700, 688, 11, false),
            new("We need to increase productivity.", 300, 520, 700, 688, 11, false),
        };
        var spots = OverlayLayout.BuildSpots(lines);

        Assert.Equal(2, spots.Count);
        Assert.Equal("increase", spots[0].Speak);
        Assert.Equal(210, spots[0].RightPt); // 锚在自己的格子右缘
        Assert.Equal("We need to increase productivity.", spots[1].Speak);
    }

    [Fact]
    public void Merged_Table_Row_Anchors_At_First_Run()
    {
        // 文本层表格书（中策词讲义）ClusterLines 把同视觉行的词条列+释义列并成一行：
        // "threaten 【释义】 威胁，恐吓" 整行 R=251（按钮会压到"恐吓"后），
        // "field n." 的 n. 是释义列漂过来的（R=208 按钮压"田地"）。
        // 行带 Runs 时锚点与发音文本都取首跑段。正文基准行把 body 字号压到 6.0（真书实况），
        // field 行（fs 7.1）才走 Heading/Entry 分支而不是段落分支。
        var lines = new List<PdfLine>
        {
            new("政府尚未对他的言论作出回应，表示正在研究。", 145, 313, 750, 740, 6.0, false),
            new("如果你试图让他们集中注意力，可能会得到一心多用的回答。", 145, 490, 730, 720, 6.0, false),
            new("threaten 【释义】 威胁，恐吓", 68, 251, 723, 710, 6.2, false,
                [new PdfRun("threaten", 68, 120), new PdfRun("【释义】 威胁，恐吓", 145, 251)]),
            new("field n.", 80, 208, 235, 222, 7.1, false,
                [new PdfRun("field", 80, 112), new PdfRun("n.", 199, 208)]),
        };
        var spots = OverlayLayout.BuildSpots(lines);

        Assert.Equal(2, spots.Count);
        Assert.Equal("threaten", spots[0].Speak);
        Assert.Equal(120, spots[0].RightPt);
        Assert.Equal("field", spots[1].Speak);
        Assert.Equal(112, spots[1].RightPt);
    }
}
