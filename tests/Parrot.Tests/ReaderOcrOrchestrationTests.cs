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
        private readonly IReadOnlyList<OcrTextLine>? _lines;

        public StubOcr(IReadOnlyList<OcrTextLine>? lines = null) => _lines = lines;

        public int Calls;
        private int _inFlight;

        /// <summary>同时在识别中的页数峰值：证明取的是并发而不是逐页串行。</summary>
        public int MaxInFlight;

        public bool IsAvailable => true;
        public int MaxImageDimensionPx => 2604;

        public async Task<IReadOnlyList<OcrTextLine>?> RecognizePngAsync(byte[] png, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            int now = Interlocked.Increment(ref _inFlight);
            for (int seen = Volatile.Read(ref MaxInFlight); now > seen; seen = Volatile.Read(ref MaxInFlight))
                if (Interlocked.CompareExchange(ref MaxInFlight, now, seen) == seen) break;
            await Task.Delay(400, ct); // 不延时的话任务瞬间完成，并发度恒为 1，测不出退化；也须长于冷渲染一页的耗时，否则永远错开
            Interlocked.Decrement(ref _inFlight);
            IReadOnlyList<OcrTextLine> lines = _lines ??
            [
                new OcrTextLine("Unit Test Heading", 70, 150, 320, 26),  // 大行高 → Heading 热点（y=40 贴页顶会被页眉带滤掉，下移到正文区）
                new OcrTextLine("English sentence here.", 70, 200, 300, 15),
                new OcrTextLine("中文句子。", 70, 216, 120, 15),          // 并入同段，但 speak=null 无热点
            ];
            return lines;
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
        // 首次加载并发提速：同时在识别的页数峰值必须 >1，否则退回逐页串行
        Assert.True(ocr.MaxInFlight >= 2, $"OCR 退化成串行：同时最多只识别 {ocr.MaxInFlight} 页");
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
            Assert.Equal("English sentence here.", card.Overlays[1].Audio.Speak); // 句末标点保留（📌 用它认出整句）
            // 页高补偿后坐标必须落在位图内（标题行贴页顶）
            Assert.True(card.Overlays[0].X > 0 && card.Overlays[0].Y is > 0 and < 200,
                $"热点应在页内，实际 X={card.Overlays[0].X} Y={card.Overlays[0].Y}");
        }
    }

    [Fact]
    public async Task Ocr_Completion_Notice_Clears_Itself_After_A_While()
    {
        File.WriteAllBytes(_pdfPath, BlankPdf(2));
        var ocr = new StubOcr();
        var vm = new ReaderPageViewModel(new StubTts(), new StubPlayer(), ocr, restoreRecent: false);
        vm.SelectedDocument = new PdfDocumentSource(_pdfPath);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20_000 && !(vm.ScanHint?.Contains("完成") ?? false))
            await Task.Delay(50);
        Assert.NotNull(vm.ScanHint); // 完成通知先亮出来
        long shownAt = sw.ElapsedMilliseconds;

        while (sw.ElapsedMilliseconds < shownAt + 15_000 && vm.ScanHint is not null)
            await Task.Delay(100);
        long held = sw.ElapsedMilliseconds - shownAt;
        Assert.Null(vm.ScanHint);                    // 到点自己收起，不长期占着阅读区
        Assert.InRange(held, 3_000, 12_000);         // 是"停几秒"，不是闪一下也不是常驻
        Assert.Equal(2, ocr.Calls);                  // 收起与识别结果无关，两页都跑过了
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

    [Fact]
    public void Mask_Toggle_After_Cards_Loaded_Notifies_Cover_Visibility()
    {
        // 用户顺序：先打开书页（遮挡关），再点右上角眼睛开遮挡——
        // PageHost 罩层绑定的是 MaskBlocksVisible / MaskCoverWholeVisible，
        // 这两个通知缺了任何一个，后开遮挡时罩层永远不出现（悬停露词却正常，更具迷惑性）
        File.WriteAllBytes(_pdfPath, BlankPdf(1));
        var vm = new ReaderPageViewModel(new StubTts(), new StubPlayer(), restoreRecent: false);
        var card = new PageCardViewModel(new PdfDocumentSource(_pdfPath), 0, vm);

        var raised = new List<string>();
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.IsMaskOn = true;

        Assert.Contains(nameof(PageCardViewModel.MaskCoverVisible), raised);
        Assert.Contains(nameof(PageCardViewModel.MaskBlocksVisible), raised);
        Assert.Contains(nameof(PageCardViewModel.MaskCoverWholeVisible), raised);
    }

    [Fact]
    public async Task Mask_Hides_Cover_Zone_Buttons_And_Floats_Revealed_Band()
    {
        // 遮挡模式下按钮逐个自管显隐：释义列内容带里的按钮随罩层藏起（罩层面干净），
        // 悬停露出带内的按钮浮出来可点（否则掀开了也点不到例句发音），盖回恢复隐藏
        // 行集全部从释义列起（X=250px@200dpi=90pt）：≥3 行同列 CJK 起点边界才票得出（0.137），
        // 两枚热点按钮右缘都压进罩层 → 开遮挡必须藏起
        File.WriteAllBytes(_pdfPath, BlankPdf(1));
        IReadOnlyList<OcrTextLine> lines =
        [
            new OcrTextLine("Unit Test Heading", 250, 150, 320, 26),      // 大行高 → Heading 热点
            new OcrTextLine("English sentence here.", 250, 200, 300, 15), // 句尾热点
            new OcrTextLine("中文释义一。", 250, 216, 120, 15),           // CJK 行只当释义：无热点、参与边界投票
            new OcrTextLine("中文释义二。", 250, 234, 120, 15),
            new OcrTextLine("中文释义三。", 250, 252, 120, 15),
        ];
        var vm = new ReaderPageViewModel(new StubTts(), new StubPlayer(), new StubOcr(lines), restoreRecent: false);
        vm.SelectedDocument = new PdfDocumentSource(_pdfPath);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20_000)
        {
            if (vm.CurrentPages is { } pp && pp.Count > 0 && pp[0].Overlays.Count > 0) break;
            await Task.Delay(50);
        }
        Assert.True(vm.CurrentPages is { } pages && pages[0].Overlays.Count > 0, "热点未落卡");
        var card = vm.CurrentPages![0];
        Assert.NotNull(card.MaskLeftFraction); // 边界没票出 = 行集不对，按钮显隐无从谈起

        // 位图就位：150dpi A4 = 1275×1650，与热点/内容带的画布坐标同空间（默认 620×876 是占位值）
        card.ImageWidthPx = 1275;
        card.ImageHeightPx = 1650;

        vm.IsMaskOn = true;
        Assert.All(card.Overlays, o => Assert.False(o.MaskVisible,
            $"遮挡开启后释义列按钮应隐藏：({o.X:F0},{o.Y:F0}) {o.Audio.Text}"));

        // 模拟悬停露出：露出带内的按钮浮出来，带外的仍藏着
        var btn = card.Overlays[0];
        var other = card.Overlays[^1];
        card.MaskReveal = new MaskRevealViewModel(0, btn.Y - 6, 500, 40, null!);
        Assert.True(btn.MaskVisible, "露出带内的按钮应浮出来可点");
        if (Math.Abs(other.Y - btn.Y) > 24) // 带高 40±4：行距 24px 以内分不出带内带外
            Assert.False(other.MaskVisible, "露出带外的按钮应保持隐藏");

        card.MaskReveal = null;
        Assert.False(btn.MaskVisible, "盖回后按钮应恢复隐藏");
    }

    public void Dispose()
    {
        try { File.Delete(_pdfPath); } catch { /* temp */ }
    }
}
