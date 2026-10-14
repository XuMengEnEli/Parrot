using Parrot.Core.TextProcessing;
using Xunit;

namespace Parrot.Tests;

/// <summary>
/// 原图遮挡边界推算（MaskLayout）：表格版式词典页的释义列起点 = 中文行首 X 的众数，
/// 水印/页眉/页码不参与，候选太少或边界落点可疑时宁可放弃。
/// </summary>
public sealed class MaskLayoutTests
{
    private const double W = 420, H = 595;

    /// <summary>构造一行：y-up 坐标，行高 14pt，Top 为视觉位置（大 = 靠上）。</summary>
    private static PdfLine L(string text, double left, double top)
        => new(text, left, left + 200, top, top - 14, 11, false);

    [Fact]
    public void 表格版式_边界取释义列起点()
    {
        var lines = new List<PdfLine>
        {
            L("advertising", 30, 500),                 // 词头列英文行：不参与
            L("【频次 9】", 34, 470),                   // 词头列下的频次标签：少数派
            L("【释义】n. 广告；广告业", 143, 460),     // 释义列：多数派
            L("每个美国人都有机会接受高等教育", 142.5, 430),
            L("良好的广告宣传活动会增加我们的销售量", 143.8, 400),
            L("① adj. 商业的；贸易的", 143, 340),
            L("A good advertising campaign", 143.2, 310), // 英文例句：不参与
        };
        var f = MaskLayout.ComputeMaskLeftFraction(lines, W, H);
        Assert.NotNull(f);
        Assert.Equal((143 - 6) / W, f.Value, precision: 3);
    }

    [Fact]
    public void 页眉水印与页码不参与推算()
    {
        List<PdfLine> Base() => new()
        {
            L("【释义】n. 广告；广告业", 143, 460),
            L("每个美国人都有机会接受高等教育", 142.5, 430),
            L("良好的广告宣传活动会增加我们的销售量", 143.8, 400),
        };
        var without = MaskLayout.ComputeMaskLeftFraction(Base(), W, H);
        var with = MaskLayout.ComputeMaskLeftFraction(new List<PdfLine>(Base())
        {
            L("MBA大师", 20, 590),  // 页眉水印（视觉最顶部）
            L("12", 200, 8),        // 页码（视觉最底部）
        }, W, H);
        Assert.Equal(without, with);
    }

    [Fact]
    public void 候选太少_放弃遮挡()
    {
        var lines = new List<PdfLine>
        {
            L("【释义】n. 广告", 143, 460),
            L("每个美国人都有机会接受高等教育", 142.5, 430),
            L("potential /pəˈtenʃl/ adj. 潜在的", 30, 400), // 英文行首：不参与
        };
        Assert.Null(MaskLayout.ComputeMaskLeftFraction(lines, W, H));
    }

    [Fact]
    public void 众数落在页左缘附近_视为行内式放弃()
    {
        var lines = new List<PdfLine>();
        for (int i = 0; i < 8; i++)
            lines.Add(L("可能的；n. 潜力", 18, 500 - i * 30)); // 换行回落页左缘（行内式特征）
        Assert.Null(MaskLayout.ComputeMaskLeftFraction(lines, W, H));
    }

    [Fact]
    public void 悬停露行_整行一个框且左缘裁到罩层边界()
    {
        // 表格版式：同一视觉行的词条列+释义列合并成一行，词框分属两列；
        // 露行框只收罩层右侧的词（主词列不进），左缘裁到罩层边界，上下取整行文字带。
        var line = new PdfLine("threaten 【释义】 v. 威胁，恐吓", 68, 251, 723, 710, 6.2, false,
            Words: new[]
            {
                new PdfWord("threaten", 68, 120, 723, 710),
                new PdfWord("【释义】", 145, 190, 723, 710),
                new PdfWord("v.", 196, 208, 723, 710),
                new PdfWord("威胁，恐吓", 214, 251, 723, 710),
            });
        // 边界落在两列之间的空槽（页宽 500 × 0.278 ≈ 139）
        var row = Assert.Single(MaskLayout.ComputeMaskRows([line], 0.278, 500)!);
        Assert.Equal("【释义】 v. 威胁，恐吓", row.Text);
        Assert.Equal(145, row.Left);
        Assert.Equal(251, row.Right);
        Assert.Equal(723, row.Top);
        Assert.Equal(710, row.Bottom);
        Assert.Null(MaskLayout.ComputeMaskRows([line], null, 500)); // 边界推不出的页：不露行
    }

    /// <summary>OCR 行：只有整行几何，无词框。右缘自定义的构造。</summary>
    private static PdfLine Ocr(string text, double left, double right, double top)
        => new(text, left, right, top, top - 14, 11, false);

    [Fact]
    public void OCR行_按右缘伸进罩层判定_左缘裁到边界()
    {
        var lines = new List<PdfLine>
        {
            Ocr("strategy", 30, 95, 500),              // 主词列：没伸进罩层
            Ocr("【频次 16】", 34, 100, 470),            // 频次标签：同上
            Ocr("strategy n. 策略；战略", 30, 320, 440), // 跨栏合并行：伸进罩层
            Ocr("我们要有应对危机的策略", 143, 360, 380),
        };
        var rows = MaskLayout.ComputeOcrMaskRows(lines, 143.0 / W, W);
        Assert.NotNull(rows);
        Assert.Equal(2, rows!.Count);
        Assert.All(rows, r => Assert.True(r.Left >= 143 - 0.01)); // 主词列绝不带出来
        Assert.Equal((143, 320), (rows[0].Left, rows[0].Right));
        Assert.Equal(143, rows[1].Left);
    }

    [Fact]
    public void OCR行_全部没伸进罩层_放弃()
    {
        var lines = new List<PdfLine>
        {
            Ocr("strategy", 30, 95, 500),
            Ocr("【频次 16】", 34, 100, 470),
        };
        Assert.Null(MaskLayout.ComputeOcrMaskRows(lines, 143.0 / W, W));
    }

    [Fact]
    public void 内容带_只统计释义列_跨栏标题与页眉页码留在外面()
    {
        var lines = new List<PdfLine>
        {
            L("上策词（高频词）unit 2", 200, 580), // 页眉（视觉最顶）：超保留带上限
            new("上策词（高频词）", 43, 243, 540, 524, 16, false), // 跨栏大标题：起点在主词列、大字号（正文字号判别不了标题）
            new("Unit 1", 30, 90, 520, 506, 11, false), // 主词列行：短行够不到释义列（真实版式如此）
            L("【释义】vm.改变，变化", 143, 500),   // 释义列首行
            L("我们要有应对危机的策略", 142.5, 400),
            L("change n. 改变", 143, 370),
            L("12", 200, 20),                       // 页码（视觉最底）
        };
        var band = MaskLayout.ComputeMaskVerticalBand(lines, 143.0 / W, W, H);
        Assert.NotNull(band);
        // 带顶 = 释义列首行上缘+2pt，带底 = 末行下缘−2pt；标题（540）页眉（580）页码（20）都不在带内
        Assert.Equal((500 + 2) / H, band.Value.TopFraction, precision: 4);
        Assert.Equal((370 - 14 - 2) / H, band.Value.BottomFraction, precision: 4);
        Assert.True(band.Value.TopFraction < 540.0 / H); // 跨栏标题在带外
        Assert.True(band.Value.BottomFraction > 20.0 / H); // 页码在带外
    }

    [Fact]
    public void 内容带_推不出_放弃()
    {
        // 页眉起点在罩层内但被保留带过滤，页码同理 → 没有释义列行可算
        var lines = new List<PdfLine>
        {
            L("上策词（高频词）unit 2", 200, 580),
            L("12", 200, 20),
        };
        Assert.Null(MaskLayout.ComputeMaskVerticalBand(lines, 143.0 / W, W, H));
        Assert.Null(MaskLayout.ComputeMaskVerticalBand(lines, null, W, H)); // 横向边界都没有：谈不上竖直带
    }

    /// <summary>被盖行框：y-up，行高 14pt。</summary>
    private static PdfRowBox Row(string text, double left, double right, double top)
        => new(text, left, right, top, top - 14);

    /// <summary>发音热点：主词列热点 RightPt≈110，释义列句尾热点 RightPt≈300（边界=143）。</summary>
    private static SpeakSpot Spot(double rightPt, double midYPt) => new(rightPt, midYPt, "t.mp3");

    [Fact]
    public void 词条分组_按主词热点合并多行()
    {
        var rows = new List<PdfRowBox>
        {
            Row("【释义】vm.改变，变化", 143, 350, 500),    // ── change 词条
            Row("我们要有应对危机的策略", 142.5, 340, 470), //
            Row("change n. 改变", 143.2, 330, 440),         // ──
            Row("【释义】adv. 依照", 143, 320, 400),        // ── according 词条
            Row("按照他说的做", 143, 310, 370),             // ──
        };
        var spots = new List<SpeakSpot>
        {
            Spot(110, 503),  // change 主词热点（首行之上）
            Spot(300, 450),  // 释义列句尾热点：绝不能当分界（否则 change 被拦腰切开）
            Spot(110, 405),  // according 主词热点
        };
        var boxes = MaskLayout.ComputeEntryRows(rows, spots, 143.0 / W, W);
        Assert.NotNull(boxes);
        Assert.Equal(2, boxes!.Count);
        Assert.Equal("【释义】vm.改变，变化 / 我们要有应对危机的策略 / change n. 改变", boxes[0].Text);
        Assert.Equal((500, 426), (boxes[0].Top, boxes[0].Bottom)); // 三行并一块：上取最高行顶、下取最低行底
        Assert.Equal("【释义】adv. 依照 / 按照他说的做", boxes[1].Text);
        Assert.Equal((400, 356), (boxes[1].Top, boxes[1].Bottom));
    }

    [Fact]
    public void 词条分组_无主词热点_退回逐行()
    {
        // 句尾热点在释义列（RightPt 越过边界），不算主词锚点 → 分不出词条，保持单行露出
        var rows = new List<PdfRowBox> { Row("【释义】n. 广告", 143, 320, 500) };
        Assert.Null(MaskLayout.ComputeEntryRows(rows, [Spot(300, 493)], 143.0 / W, W));
    }

    [Fact]
    public void 词条分组_页首延续行归一组()
    {
        // 页首几行是上一页词条的延续（最高热点之上），单独成组，不与页内首个词条粘连
        var rows = new List<PdfRowBox>
        {
            Row("【释义】n. 策略", 143, 330, 530),  // ── 上一页词条延续
            Row("战略上藐视敌人", 143, 320, 500),   // ──
            Row("【释义】vt. 估计", 143, 310, 380), // ── estimate 词条
        };
        var boxes = MaskLayout.ComputeEntryRows(rows, [Spot(110, 385)], 143.0 / W, W);
        Assert.NotNull(boxes);
        Assert.Equal(2, boxes!.Count);
        Assert.Equal("【释义】n. 策略 / 战略上藐视敌人", boxes[0].Text); // 延续组自成一框
        Assert.Equal("【释义】vt. 估计", boxes[1].Text);
    }

    [Fact]
    public void 词条分组_行框抖动_释义行底略高于热点仍归本词条()
    {
        // 真实 OCR 抖动（上策 p2 social）：释义行底 120 比热点中点 119.5 高半磅，
        // 没容差该行会被推给下一个词条（悬停下一个词时把 social 的释义露出来）
        var rows = new List<PdfRowBox>
        {
            Row("social adj. 社会的", 143, 330, 134),
            Row("下一个词条的释义", 143, 320, 104),
        };
        var boxes = MaskLayout.ComputeEntryRows(rows, [Spot(110, 119.5), Spot(110, 95)], 143.0 / W, W);
        Assert.NotNull(boxes);
        Assert.Equal(2, boxes!.Count);
        Assert.Equal("social adj. 社会的", boxes[0].Text);
        Assert.Equal("下一个词条的释义", boxes[1].Text);
    }

    [Fact]
    public void 词条分组_变体词头热点不切开多义词条()
    {
        // 中策词 p17 favour 实测：左列 "(= favor)" 变体有自己的发音热点，且与第二释义的例句同行——
        // 它的热点中点落在词条首行之后的行带里，不是新词条的起点；真词头 favour 的中点恰在首行（n. 尾巴行）带内
        PdfRowBox R(string text, double top, double bottom) => new(text, 138, 300, top, bottom);
        var rows = new List<PdfRowBox>
        {
            R("n.", 768.1, 763.3),                                          // 词头行尾巴（favour 首行）
            R("【释义】 ① 帮助", 771.9, 757.3),
            R("He did me a favour by helping me move to my new apartment.", 752.9, 745.7),
            R("他帮我搬到了新公寓，给了我很大的帮助。", 741.8, 729.1),
            R("【释义】 ② 支持；偏爱", 720.4, 711.7),
            R("v.", 716.6, 711.0),
            R("The majority of the committee members favoured the proposal.", 704.7, 694.2), // "(= favor)" 与此行同行
        };
        var spots = new List<SpeakSpot> { new(113, 764.6, "favour.mp3"), new(113, 703.8, "favor.mp3") };
        var boxes = MaskLayout.ComputeEntryRows(rows, spots, 0.232, 595);
        Assert.NotNull(boxes);
        var box = Assert.Single(boxes!); // 释义①②同属一个词条，一次全部露出
        Assert.Contains("【释义】 ① 帮助", box.Text);
        Assert.Contains("【释义】 ② 支持；偏爱", box.Text);
        Assert.Equal(771.9, box.Top);
    }

    [Fact]
    public void 内容带_词性尾巴悬挂在边界左侧的释义行仍算内容()
    {
        // 中策词 p17：释义首行起点因词性尾巴 n. 悬挂偏到边界左侧 18pt，L 过滤把它排掉后
        // 罩层顶边从 n. 碎片行起算，切掉释义首行的上半截；起头深居主词列的跨栏标题（−103pt）仍要排除
        var glossTop = 771.9;
        var lines = new List<PdfLine>
        {
            new("上策词（高频词）", 35, 235, 800, 786, 11, false,
                Words: [new PdfWord("上策词（高频词）", 35, 235, 800, 786)]), // 跨栏标题：伸进罩层但深居主词列
            new("n. 【释义】 ① 帮助", 120, 200, glossTop, glossTop - 14, 11, false,
                Words: [new PdfWord("n.", 120, 135, glossTop, glossTop - 14),
                        new PdfWord("【释义】 ① 帮助", 138, 200, glossTop, glossTop - 14)]),
        };
        var band = MaskLayout.ComputeMaskVerticalBand(lines, 0.232, 595, 842);
        Assert.NotNull(band);
        Assert.Equal((glossTop + 2) / 842, band.Value.TopFraction, precision: 4); // 带顶 = 释义首行上缘
        Assert.True(band.Value.TopFraction > 800.0 / 842 - 0.05);                 // 标题留在带外
    }

    [Fact]
    public void 词条分组_词头热点恰在例句行顶缘仍切开词条()
    {
        // 上策词 p2 实测：according to 词头热点 MidY=281.7 恰在释义行底（282.3）下方 0.6pt、
        // 例句行顶（281.7）上——严格大于会把这 0 宽空隙判成"落进例句行带内"，误当变体词头丢掉，
        // change 与 according to 并成一组（悬停 change 连 according to 一起露出）；
        // changeable 行带音标方括号，按文本补锚另切成组
        PdfRowBox R(string text, double top, double bottom) => new(text, 150, 400, top, bottom);
        var rows = new List<PdfRowBox>
        {
            R("【释义】v./n. 改变，变化", 602.0, 589.0),
            R("changeable ［'tʃeɪndʒəbl］ adj. 可变的", 318.1, 304.1),
            R("【释义】根据", 296.1, 282.3),
            R("According to Paragraph 2, which of the following is true of colours?", 281.7, 267.7),
        };
        var boxes = MaskLayout.ComputeEntryRows(
            rows, [new SpeakSpot(114, 593.4, "c.mp3"), new SpeakSpot(129, 281.7, "a.mp3")], 0.279, 525);
        Assert.NotNull(boxes);
        Assert.Equal(3, boxes!.Count);
        Assert.DoesNotContain("根据", boxes[0].Text);           // change 组不含 according to 的释义
        Assert.StartsWith("changeable", boxes[1].Text);          // 音标行按文本补锚自成一组
        Assert.Contains("【释义】根据", boxes[2].Text);          // according to 自成一组
        Assert.Equal(296.1, boxes[2].Top);
    }

    [Fact]
    public void 词条分组_OCR词头释义并行_按音标行补切()
    {
        // 上策词 p2 实测：exchange/changeable 的词头与释义被 OCR 并成一行，热点锚在行尾
        // （RightPt 深入罩层）过不了左列过滤——按"拉丁开头+音标方括号"的行文本补锚切开；
        // 搭配行（climate change 气候变化）不带音标，留在 change 组不误切
        PdfRowBox R(string text, double top, double bottom) => new(text, 147, 400, top, bottom);
        var rows = new List<PdfRowBox>
        {
            R("【释义】v./n. 改变，变化", 602.0, 589.0),
            R("climate change 气候变化", 472.4, 459.5),
            R("exchange［iks'tʃeɪndʒ］ v./n.交换；交流", 332.1, 319.2),
            R("changeable ［'tʃeɪndʒəbl］ adj. 可变的，易变的", 318.1, 304.1),
        };
        var boxes = MaskLayout.ComputeEntryRows(rows, [new SpeakSpot(342, 325.6, "e.mp3")], 0.279, 525);
        Assert.NotNull(boxes);
        Assert.Equal(3, boxes!.Count);
        Assert.Contains("气候变化", boxes[0].Text);
        Assert.StartsWith("exchange", boxes[1].Text);
        Assert.StartsWith("changeable", boxes[2].Text);
    }

    [Fact]
    public void 内容带_词头与释义同行的跨栏合并行算内容_大字号跨栏标题不算()
    {
        // 中策词 p17 favour 实测：释义首行是"词头+释义"跨栏合并行——L=73 深居主词列但伸进罩层、正文字号，
        // L 过滤排掉它罩层顶边就从释义②起算，切掉释义①上半截；同版式的跨栏大标题（L=43、字号大）仍要排除
        var glossTop = 771.9;
        var lines = new List<PdfLine>
        {
            new("上策词（高频词）", 43, 235, 800, 786, 16, false,
                Words: [new PdfWord("上策词（高频词）", 43, 235, 800, 786)]),
            new("favour 【释义】 ① 帮助", 73, 234, glossTop, glossTop - 14, 11, false,
                Words: [new PdfWord("favour", 73, 118, glossTop, glossTop - 14),
                        new PdfWord("【释义】 ① 帮助", 144, 234, glossTop, glossTop - 14)]),
            new("【释义】 ② 支持；偏爱", 144, 260, glossTop - 51, glossTop - 65, 11, false,
                Words: [new PdfWord("【释义】 ② 支持；偏爱", 144, 260, glossTop - 51, glossTop - 65)]),
        };
        var band = MaskLayout.ComputeMaskVerticalBand(lines, 0.232, 595, 842);
        Assert.NotNull(band);
        Assert.Equal((glossTop + 2) / 842, band.Value.TopFraction, precision: 4); // 带顶 = 释义①（合并行）上缘
        Assert.True(band.Value.TopFraction > 800.0 / 842 - 0.05);                 // 大字号跨栏标题留在带外
    }

    [Fact]
    public void 词条分组_释义行底比热点中线略高_仍归本词条()
    {
        // 中策词 p13 实测：释义列从词头格顶起排，释义行底比主词热点中线高约 4pt——
        // 容差不够时释义行被切给上一词条，露出块从例句开始、释义行仍被盖；
        // 上一词条末行隔着格边框（离热点 ≥22pt），不许被误吞。
        PdfRowBox R(string text, double top, double bottom) => new(text, 138, 300, top, bottom);
        var rows = new List<PdfRowBox>
        {
            R("education [ˌedʒuˈkeɪʃn]【频次 22】n. 教育", 369.1, 360.7), // 上一词条末行
            R("【释义】v. 贡献；促成", 179.3, 171.0),                     // contribute 释义行（底比热点高 4.2pt）
            R("Immigrants have contributed to British culture in many ways.", 160.4, 153.1),
        };
        var boxes = MaskLayout.ComputeEntryRows(rows, [new SpeakSpot(124, 166.8, "t.mp3")], 0.232, 595);
        Assert.NotNull(boxes);
        Assert.Equal(2, boxes!.Count);
        Assert.Equal("education [ˌedʒuˈkeɪʃn]【频次 22】n. 教育", boxes[0].Text); // 上一词条行自成一框
        Assert.Equal("【释义】v. 贡献；促成 / Immigrants have contributed to British culture in many ways.", boxes[1].Text);
        Assert.Equal(179.3, boxes[1].Top); // 露出块顶到释义行上缘
    }

    [Fact]
    public void OCR行_跨栏大字号标题不进罩层行()
    {
        // 上策词换单元处："Unit 1" 类标题横跨主词列与释义列，但它是标题不是释义内容——
        // 混进罩层行会把单元标题也盖上（图三）；正文级"词头+释义"合并行仍要收
        var lines = new List<PdfLine>
        {
            Ocr("strategy", 30, 95, 500),                          // 主词列：没伸进罩层
            new("Unit 1 基础词汇", 30, 400, 520, 506, 15, false),   // 跨栏大标题：伸进罩层但字号大
            Ocr("strategy n. 策略；战略", 30, 320, 440),             // 跨栏合并正文行：收
            Ocr("我们要有应对危机的策略", 143, 360, 380),
        };
        var rows = MaskLayout.ComputeOcrMaskRows(lines, 143.0 / W, W);
        Assert.NotNull(rows);
        Assert.Equal(2, rows!.Count);
        Assert.DoesNotContain(rows, r => r.Text.Contains("Unit"));
    }

    [Fact]
    public void 文本层行_跨栏大字号标题不进罩层行()
    {
        var title = new PdfLine("Unit 1 基础词汇", 30, 400, 520, 506, 15, false,
            Words: [new PdfWord("Unit 1 基础词汇", 30, 400, 520, 506)]);
        var entry = new PdfLine("threaten 【释义】 v. 威胁，恐吓", 68, 251, 723, 710, 6.2, false,
            Words:
            [
                new PdfWord("threaten", 68, 120, 723, 710),
                new PdfWord("【释义】 v. 威胁，恐吓", 145, 251, 723, 710),
            ]);
        var rows = MaskLayout.ComputeMaskRows([title, entry], 0.278, 500);
        Assert.NotNull(rows);
        var row = Assert.Single(rows!);
        Assert.Equal("【释义】 v. 威胁，恐吓", row.Text);
    }

    [Fact]
    public void 罩层分块_空档含标题行才切开_无标题大空档照盖()
    {
        var rows = new List<PdfRowBox>
        {
            Row("【释义】n. 焦点", 143, 320, 500), // ── 词条（行高 14，与下一行空 8pt → 连片）
            Row("战略上藐视敌人", 143, 310, 478),  // ──
            Row("导图下方的行", 143, 300, 300),    // 下方 164pt 大空档（思维导图，无文字行）
            Row("【释义】v. 提交", 143, 320, 150), // 下一词条
        };
        // 无标题行：再大的空档也连成整片盖——思维导图等答案图不漏给回忆中的用户
        var solid = MaskLayout.ComputeCoverBlocks(rows, null, H);
        var block = Assert.Single(solid);
        Assert.Equal((500, 136), (block.Top, block.Bottom));

        // 空档里有 Unit 标题行：只在此处切开露出（自上而下）
        var blocks = MaskLayout.ComputeCoverBlocks(rows, null, H, [(440, 420)]);
        Assert.Equal(2, blocks.Count);
        Assert.Equal((500, 464), (blocks[0].Top, blocks[0].Bottom));
        Assert.Equal((300, 136), (blocks[1].Top, blocks[1].Bottom));
    }

    [Fact]
    public void 罩层分块_与内容带求交_页眉页码行不进块()
    {
        var rows = new List<PdfRowBox>
        {
            Row("MBA大师英语词汇", 143, 320, 580), // 页眉行：带外
            Row("【释义】n. 焦点", 143, 320, 500), // 带内
        };
        var band = ((500.0 + 2) / H, (486.0 - 2) / H); // 内容带 [484, 502]
        var block = Assert.Single(MaskLayout.ComputeCoverBlocks(rows, band, H));
        Assert.Equal(500, block.Top, 4);
        Assert.Equal(486, block.Bottom, 4);
    }

    [Fact]
    public void 标题行区间_单元标题与跨栏大标题入选_正文词条行页眉不选()
    {
        var lines = new List<PdfLine>
        {
            L("MBA大师英语词汇", 35, 580),                        // 页眉：保留带外
            new("上策词（高频词）", 35, 380, 500, 486, 16, false), // 跨栏大标题：伸进罩层但字号大
            L("【释义】n. 焦点", 143, 460),                       // 释义正文：本身就是罩层行
            L("unit n. 单元", 59, 300),                           // 词条行（小写 unit + 词性）
            L(" Unit 11", 59, 200),                               // 单元标题：主词列里够不到罩层
        };
        var spans = MaskLayout.ComputeTitleSpans(lines, 143.0 / W, W, H);
        Assert.Equal(2, spans.Count);
        Assert.Contains((500.0, 486.0), spans); // 跨栏大标题
        Assert.Contains((200.0, 186.0), spans); // 单元标题
    }

    [Fact]
    public void 标题行区间_私用区项目符号打头的单元标题仍入选()
    {
        // 中策 p10 实测：● 项目符号在 PDF 文本层是 U+F06C 私用区码点，剥不掉它
        // "Unit" 就永远匹配不上，Unit 11 标题行进不了标题区间、空档切不开
        var lines = new List<PdfLine>
        {
            new("\uF06C Unit 11", 58.9, 137.4, 264.2, 253.4, 8.1, false),
            L("【释义】 ① 田地；领域", 144, 238),
        };
        var spans = MaskLayout.ComputeTitleSpans(lines, 143.0 / W, W, H);
        var span = Assert.Single(spans);
        Assert.Equal((264.2, 253.4), span);
    }

    /// <summary>造一页 BGRA 位图并在 [x0,x1)×[y0,y1)（px，y-down）涂黑，返回 (像素, 行字节数)。</summary>
    private static (byte[] Pixels, int RowBytes) Bgra(int w, int h, params (int X0, int X1, int Y0, int Y1)[] darks)
    {
        var px = new byte[w * h * 4];
        Array.Fill(px, (byte)255); // 白底，再涂黑
        foreach (var (x0, x1, y0, y1) in darks)
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int off = y * w * 4 + x * 4;
                    px[off] = px[off + 1] = px[off + 2] = 0;
                }
        return (px, w * 4);
    }

    [Fact]
    public void 分隔线检测_长线与短线入选_厚块与碎游程排除()
    {
        // ppp=2（150dpi）：minRun = max(40, 60) = 60px。全宽线 360px、框内短线 80px 都算线；
        // 6px 厚的填充块与 30px 碎游程（文字行特征）排除
        var (px, rowBytes) = Bgra(400, 300,
            (20, 380, 50, 51),     // 全宽线 1
            (20, 380, 100, 101),   // 全宽线 2
            (200, 280, 150, 151),  // 释义列内短线 80px
            (20, 380, 200, 206),   // 厚 6px：填充色块，排除
            (20, 50, 250, 251), (60, 90, 250, 251), (100, 130, 250, 251)); // 碎游程
        var rules = MaskLayout.DetectUnderlines(px, 400, 300, rowBytes, 2.0);
        Assert.Equal(3, rules.Count);
        Assert.Equal((125.0, 124.5, 10.0, 190.0), (rules[0].Top, rules[0].Bottom, rules[0].Left, rules[0].Right));
        Assert.Equal((100.0, 99.5, 10.0, 190.0), (rules[1].Top, rules[1].Bottom, rules[1].Left, rules[1].Right));
        Assert.Equal((75.0, 74.5, 100.0, 140.0), (rules[2].Top, rules[2].Bottom, rules[2].Left, rules[2].Right));
    }

    [Fact]
    public void 分隔线检测_相邻行合并成一条()
    {
        // 扫描件/高 dpi 下 1pt 线占 2-3px：纵向 ≤3px 且横向搭接的游程并成一条线
        var (px, rowBytes) = Bgra(400, 300, (20, 380, 50, 53));
        var rules = MaskLayout.DetectUnderlines(px, 400, 300, rowBytes, 2.0);
        var rule = Assert.Single(rules);
        Assert.Equal(125.0, rule.Top, 4);
        Assert.Equal((300 - 53) * 0.5, rule.Bottom, 4);
    }

    [Fact]
    public void 分隔线检测_坏参数返回空()
    {
        Assert.Empty(MaskLayout.DetectUnderlines([], 0, 0, 0, 2));
        Assert.Empty(MaskLayout.DetectUnderlines(new byte[16], 2, 2, 8, 0)); // ppp 非法
    }

    [Fact]
    public void 词条分组_下划线围出整词条框_框内小节线不切()
    {
        // 讲义表格版式（上策/下策实测）：全宽线（Left=35 在主词列）围出词条框；
        // 释义列起线（Left=145 ≥ 边界−10）只是框内"释义/真题/词形联想"小节分隔，不参与分组——
        // 悬停露出整框（含小节线两侧的行），组界 = 线内缘（Top=上线底、Bottom=下线顶）
        var rows = new List<PdfRowBox>
        {
            Row("【释义】v./n. 改变，变化", 143, 350, 480),
            Row("【词形联想】exchange 交换", 143, 350, 380),  // 小节线下方的行：仍属上面词条
            Row("【释义】根据", 143, 350, 250),
            Row("12", 143, 155, 30),                          // 页码：任何区域之外
        };
        var rules = new List<MaskRule>
        {
            new(500, 499, 35, 400),   // 全宽：change 框顶
            new(400, 399, 145, 400),  // 框内小节线：不切
            new(300, 299, 35, 400),   // 全宽：change 框底 = according 框顶
            new(100, 99, 35, 400),    // 全宽：according 框底
        };
        var boxes = MaskLayout.ComputeEntryRowsByUnderlines(rows, rules, 143.0 / W, W);
        Assert.NotNull(boxes);
        Assert.Equal(2, boxes!.Count);
        Assert.Contains("改变", boxes[0].Text);
        Assert.Contains("exchange", boxes[0].Text); // 小节线两侧同行框
        Assert.Equal((499, 300), (boxes[0].Top, boxes[0].Bottom)); // 组界=线内缘，非行文字带
        Assert.Contains("根据", boxes[1].Text);
        Assert.Equal((299, 100), (boxes[1].Top, boxes[1].Bottom));
        Assert.DoesNotContain(boxes, b => b.Text.Contains("12")); // 页码不进组
    }

    [Fact]
    public void 词条分组_下划线版_内容带外行先剔除()
    {
        var rows = new List<PdfRowBox> { Row("【释义】改变", 143, 350, 480) };
        var rules = new List<MaskRule> { new(500, 499, 35, 400), new(300, 299, 35, 400) };
        var band = (460.0 / H, 300.0 / H); // 带顶压到行顶之下：行整体在带外（页眉方向）
        Assert.Null(MaskLayout.ComputeEntryRowsByUnderlines(rows, rules, 143.0 / W, W, H, band));
    }

    [Fact]
    public void 词条分组_下划线版_全宽线不足两条退回锚点()
    {
        var rows = new List<PdfRowBox> { Row("【释义】改变", 143, 350, 480) };
        Assert.Null(MaskLayout.ComputeEntryRowsByUnderlines(
            rows, [new MaskRule(500, 499, 35, 400)], 143.0 / W, W));
        Assert.Null(MaskLayout.ComputeEntryRowsByUnderlines(
            rows, [new MaskRule(500, 499, 145, 400), new MaskRule(300, 299, 145, 400)], // 全在释义列：框内线
            143.0 / W, W));
    }
}
