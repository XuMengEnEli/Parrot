using System.Text;
using Parrot.Core.Abstractions;
using Parrot.Pdf;
using Parrot.UI.ViewModels;
using Xunit;

namespace Parrot.Tests;

/// <summary>
/// 扫描件 OCR 编排的 VM 级集成测试：手工拼一份"无文本层"多页 PDF（扫描件最小替身），
/// 用桩 IOcrService 顶掉引擎，验证整条链路：占位卡保留 → 后台识别 → 句尾 🔊 热点叠加到
/// 原图卡片（OCR 文本只进 TTS，不上屏、不替换块流）。真实 WinRT 引擎由 CLI --ocr 冒烟覆盖。
/// </summary>
public sealed class ReaderOcrOrchestrationTests : IDisposable
{
    private readonly string _pdfPath = Path.Combine(Path.GetTempPath(), $"se-blank-{Guid.NewGuid():N}.pdf");

    private sealed class StubTts : ITtsService
    {
        public Task<string> SynthesizeAsync(string text, TtsKind kind, string? voice = null, CancellationToken ct = default)
            => Task.FromResult("");
    }

    private sealed class StubPlayer : IAudioPlayer
    {
        public Task PlayAsync(string filePath, CancellationToken ct = default) => Task.CompletedTask;
        public void Stop() { }
    }

    private sealed class StubOcr : IOcrService
    {
        public int Calls;
        public bool IsAvailable => true;
        public int MaxImageDimensionPx => 2604;

        public Task<IReadOnlyList<OcrTextLine>?> RecognizePngAsync(byte[] png, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            IReadOnlyList<OcrTextLine> lines =
            [
                new OcrTextLine("Unit Test Heading", 70, 40, 320, 26),   // 大行高 → Heading 热点
                new OcrTextLine("English sentence here.", 70, 90, 300, 15),
                new OcrTextLine("中文句子。", 70, 106, 120, 15),          // 并入同段，但 speak=null 无热点
            ];
            return Task.FromResult<IReadOnlyList<OcrTextLine>?>(lines);
        }
    }

    /// <summary>无 /Contents 文本算子的多页空白 PDF：PdfPig 读到 0 字母 → 全页判为扫描件。</summary>
    private static byte[] BlankPdf(int pages)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        void Obj(string body)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append(body).Append('\n');
        }
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        int firstPageObj = 3;
        var kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{firstPageObj + 2 * i} 0 R"));
        Obj($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pages} >>\nendobj");
        for (int i = 0; i < pages; i++)
        {
            int pageObj = firstPageObj + 2 * i, contObj = pageObj + 1;
            Obj($"{pageObj} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {contObj} 0 R >>\nendobj");
            Obj($"{contObj} 0 obj\n<< /Length 4 >>\nstream\nq Q\nendstream\nendobj");
        }
        int xrefPos = Encoding.Latin1.GetByteCount(sb.ToString());
        int size = offsets.Count + 1;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (var o in offsets)
            sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public async Task Scanned_Majority_Docs_Get_Overlay_Spots_On_Image_Cards()
    {
        File.WriteAllBytes(_pdfPath, BlankPdf(3));
        var ocr = new StubOcr();
        var vm = new ReaderPageViewModel(new StubTts(), new StubPlayer(), ocr, restoreRecent: false);
        var doc = new PdfDocumentSource(_pdfPath);

        vm.SelectedDocument = doc; // 触发异步 RebuildBlocks → ApplyBlocks → OcrFillAsync

        // 等后台 OCR 走完 3 页并把热点落卡（终态信号：完成提示条；上限 20s，正常 <2s）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20_000)
        {
            if (ocr.Calls >= doc.PageCount && vm.OcrStatus is null
                && vm.ScanHint is { } hint && hint.Contains("完成") && !vm.IsExtracting) break;
            await Task.Delay(50);
        }

        Assert.True(ocr.Calls > 0,
            $"OCR 未被触发：ExtractFailed={vm.ExtractFailed}, ViewMode={vm.ViewModeIndex}, Extracting={vm.IsExtracting}, " +
            $"Blocks=[{string.Join(",", vm.Blocks.Select(b => b.GetType().Name))}]");
        Assert.Equal(doc.PageCount, ocr.Calls);
        Assert.Equal(1, vm.ViewModeIndex);       // 原图视图保持不动——版式就是位图本身
        Assert.Null(vm.OcrStatus);               // 进度条已收起
        Assert.NotNull(vm.ScanHint);             // 完成说明条在位

        // 块流不被替换：3 页扫描件占位卡原样保留（OCR 文本不上屏）
        Assert.Equal(3, vm.Blocks.Count(b => b is ScannedPageViewModel));
        Assert.Equal(3, vm.Blocks.Count(b => b is PageBreakViewModel));

        // 每页卡片拿到 2 个热点：标题 + 英文句；中文片段 speak=null 不生成
        var pages = vm.CurrentPages!;
        Assert.Equal(3, pages.Count);
        foreach (var card in pages)
        {
            Assert.Equal(2, card.Overlays.Count);
            Assert.Equal("Unit Test Heading", card.Overlays[0].Audio.Speak);
            Assert.Equal("English sentence here", card.Overlays[1].Audio.Speak); // 尾点被 TrimSpeak 剥掉
            // 页高补偿后坐标必须落在位图内（标题行贴页顶）
            Assert.True(card.Overlays[0].X > 0 && card.Overlays[0].Y is > 0 and < 200,
                $"热点应在页内，实际 X={card.Overlays[0].X} Y={card.Overlays[0].Y}");
        }
    }

    [Fact]
    public async Task No_Ocr_Service_Falls_Back_To_Image_View_Without_Crashing()
    {
        File.WriteAllBytes(_pdfPath, BlankPdf(2));
        var vm = new ReaderPageViewModel(new StubTts(), new StubPlayer(), ocr: null, restoreRecent: false);
        var doc = new PdfDocumentSource(_pdfPath);
        vm.SelectedDocument = doc;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 10_000 && vm.IsExtracting)
            await Task.Delay(50);

        // 无引擎：留在原图视图 + 占位卡 + 提示文案含"不可用"
        Assert.Equal(1, vm.ViewModeIndex);
        Assert.Contains("不可用", vm.ScanHint);
        Assert.Equal(2, vm.Blocks.Count(b => b is ScannedPageViewModel));
        Assert.Null(vm.OcrStatus);
    }

    public void Dispose()
    {
        try { File.Delete(_pdfPath); } catch { /* temp */ }
    }
}
