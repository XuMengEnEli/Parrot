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
        // 尾随句点被 TrimSpeak 剥掉（TTS 不需要），跨行按空格拼接
        Assert.Equal("Technology has changed our daily life in many ways", spot.Speak);
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
        Assert.Equal("modern communication tools work", spot.Speak);
        Assert.Equal(280, spot.RightPt); // 句末行 = 第二行
    }

    [Fact]
    public void Heading_And_Entry_Lines_Get_The_Own_Spot_At_Line_End()
    {
        var lines = new List<PdfLine>
        {
            new("Unit 12", 60, 150, 760, 740, 20, false),        // 大字号 → Heading
            new("abandon v. 放弃", 60, 220, 700, 688, 11, false), // 英中混排行首英文 → Entry
        };
        var spots = OverlayLayout.BuildSpots(lines);
        Assert.Equal(2, spots.Count);
        Assert.Equal("Unit 12", spots[0].Speak);
        Assert.Equal(150, spots[0].RightPt);
        Assert.StartsWith("abandon", spots[1].Speak);
        Assert.Equal(220, spots[1].RightPt);
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
        Assert.Equal("We need to increase productivity", spots[1].Speak);
    }
}
