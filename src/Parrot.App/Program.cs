using Avalonia;
using Parrot.Core;
using Parrot.Data;

namespace Parrot.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 工具模式：--import-ecdict <csv> —— 导入 ECDICT 全量词表后退出（scripts/fetch-ecdict.ps1 调用）
        int idx = Array.IndexOf(args, "--import-ecdict");
        if (idx >= 0 && idx + 1 < args.Length)
        {
            UseConsole();
            var db = new LocalDatabase();
            db.EnsureSchema();
            var added = WordbookImporter.ImportCsvFile(
                new WordbookRepository(db), args[idx + 1], DateOnly.FromDateTime(DateTime.Now));
            Console.WriteLine($"ECDICT 导入完成：新增 {added} 条。");
            return;
        }

        // 工具模式：--preview <pdf> —— 无界面跑一遍"文本提取+版式重建"（需求 1.1/1.2 的离线自检）
        idx = Array.IndexOf(args, "--preview");
        if (idx >= 0 && idx + 1 < args.Length)
        {
            UseConsole();
            RunPreview(args[idx + 1]);
            return;
        }

        // 工具模式：--ocr <pdf> [页码] [--lines] —— 无界面验证扫描件 OCR 链路（引擎可用性 + 识别质量 + 版式分类）
        // --lines 额外打印识别出的原始行几何（判跨栏合并/漏标必看，只看热点猜不到引擎给了什么）
        idx = Array.IndexOf(args, "--ocr");
        if (idx >= 0 && idx + 1 < args.Length)
        {
            UseConsole();
            RunOcr(args[idx + 1], idx + 2 < args.Length && int.TryParse(args[idx + 2], out int pg) ? pg : 1,
                args.Contains("--lines"));
            return;
        }

        using var singleInstance = new SingleInstanceLock();
        if (!singleInstance.TryAcquire())
        {
            Console.Error.WriteLine("Parrot 已在运行（单实例）。");
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void UseConsole()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 重定向/无控制台时忽略 */ }
    }

    private static void RunPreview(string path)
    {
        var doc = new Parrot.Pdf.PdfDocumentSource(path);
        Console.WriteLine($"《{doc.Title}》共 {doc.PageCount} 页，提取版式块：");
        int heading = 0, para = 0, list = 0, entry = 0, scan = 0, speak = 0, frag = 0;
        foreach (var b in doc.ExtractLayout())
        {
            switch (b.Kind)
            {
                case Core.TextProcessing.PdfBlockKind.Heading: heading++; break;
                case Core.TextProcessing.PdfBlockKind.Paragraph: para++; break;
                case Core.TextProcessing.PdfBlockKind.ListItem: list++; break;
                case Core.TextProcessing.PdfBlockKind.Entry: entry++; break;
                default: scan++; continue;
            }
            foreach (var f in b.Fragments)
            {
                frag++;
                if (f.Speak is not null) speak++;
            }
        }
        Console.WriteLine($"  标题 {heading} · 正文段 {para} · 列表 {list} · 词条 {entry} · 扫描页 {scan}；" +
                          $"片段 {frag}（可发音 {speak}）");
        // 抽样打印前几块的展示/发音，肉眼核对"原版式 + 句内发音"
        int shown = 0;
        foreach (var b in doc.ExtractLayout())
        {
            if (b.Kind == Core.TextProcessing.PdfBlockKind.ScannedPage) continue;
            foreach (var f in b.Fragments)
            {
                Console.WriteLine($"  [{b.Kind} p{b.Page}] {Short(f.Display)}");
                if (f.Speak is not null && f.Speak != f.Display)
                    Console.WriteLine($"      🔊 {Short(f.Speak)}");
                if (++shown >= 12) return;
            }
        }
    }

    private static string Short(string s) => s.Length <= 70 ? s : s[..70] + "…";

    private static void RunOcr(string path, int page1, bool dumpLines = false)
    {
        Core.Abstractions.IOcrService? ocr = null;
#if WINDOWS_OCR
        ocr = new Parrot.Ocr.Windows.WinRtOcrService();
#elif MAC_OCR
        ocr = new Parrot.Ocr.Mac.MacVisionOcrService();
#endif
        if (ocr is null || !ocr.IsAvailable)
        {
            Console.WriteLine("OCR 引擎不可用（本机无系统 OCR 组件：Windows 需 Windows.Media.Ocr，mac 需 /usr/bin/osascript + Vision）。");
            return;
        }
        Console.WriteLine($"OCR 引擎可用，最大边长 {ocr.MaxImageDimensionPx}px；识别《{System.IO.Path.GetFileName(path)}》第 {page1} 页…");
        var doc = new Parrot.Pdf.PdfDocumentSource(path);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lines = doc.OcrPageLinesAsync(page1 - 1, ocr, CancellationToken.None).GetAwaiter().GetResult();
        sw.Stop();
        if (lines is null || lines.Count == 0)
        {
            Console.WriteLine($"  未识别出文本（{sw.ElapsedMilliseconds}ms）。");
            return;
        }
        bool worth = Core.TextProcessing.OcrLayout.WorthKeeping(lines);
        var spots = Core.TextProcessing.OverlayLayout.BuildSpots(lines);
        Console.WriteLine($"  {lines.Count} 行（{sw.ElapsedMilliseconds}ms）；正文门槛 {(worth ? "通过" : "未过（图形页特征）")}；原图热点 {spots.Count} 个：");
        if (!worth) return;
        if (dumpLines)
        {
            Console.WriteLine("  —— 识别原始行（视觉自上而下；L/R/T/B 为 PDF pt，y 越大越靠上）——");
            int i = 0;
            foreach (var l in lines)
                Console.WriteLine($"  #{++i:D2} L={l.Left,5:F0} R={l.Right,5:F0} T={l.Top,5:F0} B={l.Bottom,5:F0} fs={l.FontSize,4:F1} | {Short(l.Text)}");
            Console.WriteLine("  —— 热点 ——");
        }
        int shown = 0;
        foreach (var s in spots)
        {
            Console.WriteLine($"  🔊 (x={s.RightPt:F0}pt y={s.MidYPt:F0}pt) {Short(s.Speak)}");
            if (++shown >= (dumpLines ? 999 : 15)) break;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
