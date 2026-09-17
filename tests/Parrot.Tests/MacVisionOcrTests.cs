using System.Runtime.InteropServices;
using Parrot.Core.Abstractions;
using Parrot.Core.TextProcessing;
using Parrot.Ocr.Mac;
using SkiaSharp;
using Xunit;

namespace Parrot.Tests;

/// <summary>
/// mac Vision OCR 的纯函数部分（JSON→行框坐标变换、PNG 尺寸解析、与叠加层链路的衔接）。
/// 这些逻辑不依赖 mac 运行时，在 Windows 上即可回归；osascript 进程侧只在全不可用时降级为 null。
/// </summary>
public class MacVisionOcrTests
{
    private const double W = 1700, H = 2200; // A4 @200dpi

    [Fact]
    public void ParseVisionJson_Maps_Normalized_YUp_Box_To_Pixel_TopLeft()
    {
        // 归一化框：x=0.1, y-up=0.5（原点左下）, w=0.2, h=0.02 → 左上像素 (170, 1056, 340, 44)
        var json = """[{"t":"Hello world","x":0.1,"y":0.5,"w":0.2,"h":0.02,"c":0.9}]""";
        var line = Assert.Single(MacVisionOcrService.ParseVisionJson(json, W, H));
        Assert.Equal("Hello world", line.Text);
        Assert.Equal(170, line.X, 1);
        Assert.Equal(1056, line.Y, 1);
        Assert.Equal(340, line.Width, 1);
        Assert.Equal(44, line.Height, 1);
    }

    [Fact]
    public void ParseVisionJson_Drops_Low_Confidence_And_Blank_Lines()
    {
        var json = """
[{"t":"ok line","x":0.1,"y":0.5,"w":0.2,"h":0.02,"c":0.6},
 {"t":"noise","x":0.1,"y":0.4,"w":0.2,"h":0.02,"c":0.1},
 {"t":"   ","x":0.1,"y":0.3,"w":0.2,"h":0.02,"c":0.9}]
""";
        var lines = MacVisionOcrService.ParseVisionJson(json, W, H);
        Assert.Equal(new[] { "ok line" }, lines.Select(l => l.Text));
    }

    [Fact]
    public void PngSize_Reads_IHDR_BigEndian()
    {
        var png = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        new byte[] { 0x00, 0x00, 0x00, 0x0D }.CopyTo(png, 8);
        new byte[] { (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(png, 12);
        new byte[] { 0x00, 0x00, 0x06, 0xA4 }.CopyTo(png, 16); // 1700
        new byte[] { 0x00, 0x00, 0x08, 0x98 }.CopyTo(png, 20); // 2200
        Assert.Equal((1700, 2200), MacVisionOcrService.PngSize(png));

        // 非法头 → 回退 A4@200dpi
        Assert.Equal((1700, 2200), MacVisionOcrService.PngSize(new byte[4]));
    }

    [Fact]
    public void Vision_Lines_Feed_Overlay_Chain_End_To_End()
    {
        // 页顶标题 + 一行正文（Vision 归一化 y-up）→ 像素行 → PDF pt 行 → 原图热点
        var json = """
[{"t":"Unit Test Heading","x":0.08,"y":0.9,"w":0.4,"h":0.02,"c":0.9},
 {"t":"English sentence here.","x":0.08,"y":0.83,"w":0.7,"h":0.015,"c":0.9}]
""";
        var px = MacVisionOcrService.ParseVisionJson(json, W, H);
        var pt = OcrLayout.ToPdfLines(px, 72.0 / 200, H);
        Assert.True(OcrLayout.WorthKeeping(pt));
        foreach (var l in pt)
        {
            Assert.InRange(l.Top, 0, 792);    // 真实页高内（此前负 y 回归的防线）
            Assert.InRange(l.Bottom, 0, 792);
        }

        var spots = OverlayLayout.BuildSpots(pt);
        Assert.Contains(spots, s => s.Speak == "Unit Test Heading");
        Assert.Contains(spots, s => s.MidYPt is > 0 and < 792);
    }

    /// <summary>
    /// 真机链路回归（osascript + Vision 进程侧）：探测可用性 → 识别一张自己渲染的英文页 → 行框落图内。
    /// 曾经的两处回归都只在这条路上暴露：探测把 ObjC 类当 object 判（IsAvailable 恒 false，压根不调用识别）、
    /// 结果从 handler.results 取（该属性不存在，脚本抛错非 0 退出）。非 mac 平台无 Vision，直接跳过。
    /// </summary>
    [Fact]
    public async Task Vision_RealEngine_Reads_Rendered_Text()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;

        var svc = new MacVisionOcrService();
        Assert.True(svc.IsAvailable, "mac 上 Vision 探测不可用：检查 /usr/bin/osascript 与 Vision 框架");

        var png = RenderEnglishPage("The quick brown fox jumps over the lazy dog");
        var lines = await svc.RecognizePngAsync(png, default);

        Assert.NotNull(lines);
        Assert.NotEmpty(lines);
        var hit = Assert.Single(lines, l => l.Text.Contains("quick", StringComparison.OrdinalIgnoreCase));
        Assert.True(hit is { X: >= 0, Y: >= 0 } && hit.X + hit.Width <= W + 1 && hit.Y + hit.Height <= H + 1,
            $"行框越出页面：{hit}");
    }

    /// <summary>
    /// 中文页回归：Vision 的 recognitionLanguages 顺序有实测影响——["en-US","zh-CN"] 下中文页返回 0 行，
    /// 中文在前才读得出（英文行数不变）。系统无中文字体时跳过（避免断言字体缺失而非 OCR 回归）。
    /// </summary>
    [Fact]
    public async Task Vision_RealEngine_Reads_Chinese_Text()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;
        var typeface = SKFontManager.Default.MatchCharacter('生')
            ?? throw new InvalidOperationException("mac 上找不到中文字体，测试页无法渲染");

        var svc = new MacVisionOcrService();
        Assert.True(svc.IsAvailable, "mac 上 Vision 探测不可用：检查 /usr/bin/osascript 与 Vision 框架");

        using var bmp = new SKBitmap((int)W, (int)H, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var g = new SKCanvas(bmp))
        {
            g.Clear(SKColors.White);
            using var font = new SKFont(typeface, 64);
            using var paint = new SKPaint { Color = SKColors.Black };
            g.DrawText("这个单词表示生产", 120, 200, SKTextAlign.Left, font, paint);
            g.DrawText("考研阅读常考其引申义", 120, 320, SKTextAlign.Left, font, paint);
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 95)!;

        var lines = await svc.RecognizePngAsync(data.ToArray(), default);

        Assert.NotNull(lines);
        Assert.Contains(lines!, l => l.Text.Contains('生'));
    }

    private static byte[] RenderEnglishPage(string text)
    {
        using var bmp = new SKBitmap((int)W, (int)H, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var g = new SKCanvas(bmp))
        {
            g.Clear(SKColors.White);
            using var typeface = SKTypeface.FromFamilyName("Helvetica", SKFontStyle.Bold);
            using var font = new SKFont(typeface, 56);
            using var paint = new SKPaint { Color = SKColors.Black };
            float y = 160;
            foreach (var word in SplitRows(text, 6))
            {
                g.DrawText(word, 120, y, SKTextAlign.Left, font, paint);
                y += 92;
            }
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 95)!;
        return data.ToArray();
    }

    /// <summary>按词换行成几行短句（Vision 对短句识别更稳）。</summary>
    private static IEnumerable<string> SplitRows(string text, int wordsPerRow)
        => text.Split(' ').Chunk(wordsPerRow).Select(row => string.Join(' ', row));
}
