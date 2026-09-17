using SkiaSharp;

namespace Parrot.Pdf;

/// <summary>一页的 BGRA8888 预乘像素，供 UI 层零拷贝进 WriteableBitmap。</summary>
public sealed record RenderedPage(byte[] Pixels, int WidthPx, int HeightPx, int RowBytes);

/// <summary>
/// 单个 PDF 文档的渲染与文本提取源。
/// - PDFium 非线程安全 → 渲染经全局锁串行；每文档只保 byte[]（讲义都在 50MB 以下，换 zoom 重渲即够）。
/// - 文本提取走 PdfPig(custom-5=0.1.8) 的 GetWords()（词级 BoundingBox + FontName），
///   按坐标聚类成行、滤中文（SimSun 等 CJK 字体/字符），再交 Core 的 SentenceSplitter 断句。
/// </summary>
public sealed class PdfDocumentSource : IDisposable
{
    // PDFium 文档句柄全进程互斥（多文档共用同一库实例）
    internal static readonly SemaphoreSlim PdfiumGate = new(1, 1);

    private readonly byte[] _data;
    private readonly UglyToad.PdfPig.PdfDocument? _pig;
    private readonly object _pigGate = new();

    public PdfDocumentSource(string filePath)
    {
        FilePath = filePath;
        Title = Path.GetFileNameWithoutExtension(filePath);
        _data = File.ReadAllBytes(filePath);
        PageCount = PDFtoImage.Conversion.GetPageCount(_data);
        try
        {
            _pig = UglyToad.PdfPig.PdfDocument.Open(new MemoryStream(_data, writable: false));
        }
        catch
        {
            _pig = null; // 提取失败只影响发音按钮，不影响显示
        }
    }

    public string FilePath { get; }
    public string Title { get; }
    public int PageCount { get; }

    /// <summary>页尺寸（PDF 点，1/72 英寸）。</summary>
    public SkiaSharp.SKSizeI GetPageSizePoints(int pageIndex)
    {
        var size = PDFtoImage.Conversion.GetPageSize(_data, pageIndex);
        return new SKSizeI((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
    }

    /// <summary>把第 <paramref name="pageIndex"/> 页（0-based）渲染成指定 DPI 的 BGRA 像素。</summary>
    public async Task<RenderedPage> RenderPageAsync(int pageIndex, int dpi, CancellationToken ct)
    {
        await PdfiumGate.WaitAsync(ct);
        try
        {
            // 渲染在后台线程：SKBitmap→byte[] 拷贝后 PDFium 句柄及时释放
            var (pixels, w, h, stride) = await Task.Run(() =>
            {
                using var bmp = PDFtoImage.Conversion.ToImage(_data, pageIndex, options: new PDFtoImage.RenderOptions { Dpi = dpi });
                using var bgra = EnsureBgra(bmp);
                var buf = new byte[(long)bgra.RowBytes * bgra.Height];
                System.Runtime.InteropServices.Marshal.Copy(bgra.GetPixels(), buf, 0, buf.Length);
                return (buf, bgra.Width, bgra.Height, bgra.RowBytes);
            }, ct);
            return new RenderedPage(pixels, w, h, stride);
        }
        finally
        {
            PdfiumGate.Release();
        }
    }

    private static SKBitmap EnsureBgra(SKBitmap bmp)
    {
        if (bmp.ColorType == SKColorType.Bgra8888 && bmp.AlphaType == SKAlphaType.Premul)
            return bmp;
        var converted = new SKBitmap(new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (!bmp.CopyTo(converted))
            throw new InvalidOperationException("SKBitmap 像素格式转换失败");
        return converted;
    }

    /// <summary>把一页渲染成 PNG（OCR 输入用），返回字节与像素尺寸。</summary>
    public async Task<(byte[] Png, int WidthPx, int HeightPx)> RenderPagePngAsync(int pageIndex, int dpi, CancellationToken ct)
    {
        await PdfiumGate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                using var bmp = PDFtoImage.Conversion.ToImage(_data, pageIndex, options: new PDFtoImage.RenderOptions { Dpi = dpi });
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Png, 90)
                    ?? throw new InvalidOperationException("PNG 编码失败");
                return (data.ToArray(), bmp.Width, bmp.Height);
            }, ct);
        }
        finally
        {
            PdfiumGate.Release();
        }
    }

    /// <summary>
    /// 扫描页 → OCR 文字行（PDF 坐标系，可直接进 LayoutBuilder）。
    /// DPI 反推到引擎限长以内（A4@200dpi≈2339px 常超 WinRT 上限，按页高/宽缩到 ~170dpi）。
    /// 引擎不可用或识别失败返回 null → 调用方保留占位卡。耗时操作，后台调用。
    /// </summary>
    public async Task<IReadOnlyList<Core.TextProcessing.PdfLine>?> OcrPageLinesAsync(
        int pageIndex, Core.Abstractions.IOcrService ocr, CancellationToken ct)
    {
        if (!ocr.IsAvailable) return null;
        var size = GetPageSizePoints(pageIndex);
        int dpi = 200;
        if (ocr.MaxImageDimensionPx > 0)
            dpi = Math.Min(dpi, (int)(72.0 * ocr.MaxImageDimensionPx / Math.Max(1, Math.Max(size.Width, size.Height))) - 1);
        dpi = Math.Max(dpi, 96);

        try
        {
            var (png, _, heightPx) = await RenderPagePngAsync(pageIndex, dpi, ct);
            var lines = await ocr.RecognizePngAsync(png, ct);
            if (lines is null || lines.Count == 0) return null;
            // 传位图像素高 → 真页内坐标（原图 🔊 热点定位用）
            return Core.TextProcessing.OcrLayout.ToPdfLines(lines, 72.0 / dpi, heightPx);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ocr] 页 {pageIndex + 1} 识别管线失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>该页是否有文本层（扫描件页 false → UI 隐藏发音按钮并提示 降级）。</summary>
    public bool PageHasText(int pageIndex)
    {
        if (_pig is null) return false;
        lock (_pigGate)
        {
            try
            {
                return _pig.GetPage(pageIndex + 1).Letters.Count >= 10;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 整文档版式重建（需求 1.1 的核心输出）：每页 词→行→块，双栏页按"左栏读完读右栏"排序；
    /// 无文本层页输出 ScannedPage 占位块。耗时操作，请在后台线程调用。
    /// </summary>
    public IReadOnlyList<Core.TextProcessing.PdfBlock> ExtractLayout()
    {
        var all = new List<Core.TextProcessing.PdfBlock>();
        if (_pig is null) return all;
        for (int p = 1; p <= PageCount; p++)
        {
            var lines = GetPageLines(p - 1);

            if (lines.Count == 0)
            {
                if (!PageHasText(p - 1))
                    all.Add(new Core.TextProcessing.PdfBlock(
                        Core.TextProcessing.PdfBlockKind.ScannedPage, p, []));
                continue; // 有文本层但聚不出行（纯图注/旋转字）→ 静默跳过
            }
            all.AddRange(Core.TextProcessing.LayoutBuilder.BuildBlocks(p, lines));
        }
        return all;
    }

    private readonly Dictionary<int, List<Core.TextProcessing.PdfLine>> _linesCache = new();

    /// <summary>某页重建行（词→行→分栏，pt 坐标，带缓存）。原图 🔊 热点与文本视图共用。</summary>
    public IReadOnlyList<Core.TextProcessing.PdfLine> GetPageLines(int pageIndex)
    {
        if (_pig is null) return [];
        lock (_pigGate)
        {
            if (_linesCache.TryGetValue(pageIndex, out var cached)) return cached;
            List<Core.TextProcessing.PdfLine> lines;
            try { lines = ExtractPageLines(_pig.GetPage(pageIndex + 1)); } // PdfPig 1-based
            catch { lines = []; }
            _linesCache[pageIndex] = lines;
            return lines;
        }
    }

    private static List<Core.TextProcessing.PdfLine> ExtractPageLines(UglyToad.PdfPig.Content.Page page)
    {
        var ws = new List<WordBox>();
        foreach (var w in page.GetWords())
        {
            if (w.TextOrientation.ToString() != "Horizontal") continue; // 思维导图的旋转字不做版式
            var bb = w.BoundingBox;
            double h = bb.Top - bb.Bottom;
            double size = w.Letters.Count > 0 ? w.Letters[0].FontSize : 0;
            if (size is < 1 or > 200) size = Math.Max(6, h * 0.75); // 字号异常时按框高估算
            ws.Add(new WordBox(w.Text, bb.Left, bb.Right, bb.Top, bb.Bottom, size, IsBoldFont(w.FontName)));
        }
        if (ws.Count == 0) return [];

        // 双栏检测：中部存在纵向空白带 → 左栏整体先于右栏
        var groups = new List<List<WordBox>> { ws };
        if (TryColumnSplit(ws, page.Width, out double splitX))
        {
            var left = ws.Where(x => (x.Left + x.Right) / 2 < splitX).ToList();
            var right = ws.Where(x => (x.Left + x.Right) / 2 >= splitX).ToList();
            if (left.Count * 4 >= ws.Count && right.Count * 4 >= ws.Count)
                groups = [left, right];
        }

        var lines = new List<Core.TextProcessing.PdfLine>();
        foreach (var g in groups)
            lines.AddRange(ClusterLines(g));
        return lines;
    }

    private sealed record WordBox(
        string Text, double Left, double Right, double Top, double Bottom, double Size, bool Bold);

    /// <summary>词框 → 行（Top 降序聚带、行内 x 排序），输出视觉自上而下。</summary>
    private static IEnumerable<Core.TextProcessing.PdfLine> ClusterLines(List<WordBox> ws)
    {
        var sorted = ws.OrderByDescending(w => w.Top).ToList();
        var bands = new List<List<WordBox>>();
        foreach (var w in sorted)
        {
            double h = Math.Max(w.Top - w.Bottom, 4);
            var band = bands.FirstOrDefault(b => Math.Abs(b[0].Top - w.Top) <= h * 0.6);
            if (band is null) bands.Add(new List<WordBox> { w });
            else band.Add(w);
        }

        foreach (var band in bands.OrderByDescending(b => b[0].Top))
        {
            var o = band.OrderBy(b => b.Left).ToList();
            yield return new Core.TextProcessing.PdfLine(
                string.Join(' ', o.Select(x => x.Text)),
                o[0].Left, o[^1].Right,
                o.Max(x => x.Top), o.Min(x => x.Bottom),
                Median(o.Select(x => x.Size)),
                o.Count(x => x.Bold) * 2 > o.Count);
        }
    }

    private static double Median(IEnumerable<double> xs)
    {
        var a = xs.OrderBy(x => x).ToArray();
        return a[a.Length / 2];
    }

    /// <summary>词中心 x 直方图找页面中部最长空白带：够宽则判定为双栏，返回分界 x。</summary>
    private static bool TryColumnSplit(List<WordBox> ws, double pageWidth, out double splitX)
    {
        splitX = 0;
        if (pageWidth < 200 || ws.Count < 40) return false; // 词太少不必分栏

        const int bins = 48;
        var hist = new int[bins];
        foreach (var w in ws)
        {
            int b = (int)((w.Left + w.Right) / 2 / pageWidth * bins);
            if (b is >= 0 and < bins) hist[b]++;
        }

        int bestStart = -1, bestLen = 0, runStart = -1;
        for (int i = 0; i <= bins; i++)
        {
            bool empty = i < bins && hist[i] == 0;
            if (empty)
            {
                if (runStart < 0) runStart = i;
            }
            else if (runStart >= 0)
            {
                int len = i - runStart;
                double center = (runStart + i) / 2.0 / bins;
                if (len > bestLen && center is > 0.3 and < 0.7)
                {
                    bestLen = len;
                    bestStart = runStart;
                }
                runStart = -1;
            }
        }

        if (bestLen < 3) return false; // 空白带不足页宽 ~6% → 单栏
        splitX = (bestStart + bestLen / 2.0) / bins * pageWidth;
        return true;
    }

    private static bool IsBoldFont(string fontName) =>
        fontName.Contains("Bold", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Black", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Heavy", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        lock (_pigGate)
            _pig?.Dispose();
    }
}
