using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Parrot.Core;
using Parrot.Data;
using Parrot.UI.ViewModels;
using Parrot.UI.Views;

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

        // 工具模式：--shot <png> [页签] [库文件] [页面] —— 把某一页离屏渲染成图片（本机无法点击/截屏时验收 UI）
        idx = Array.IndexOf(args, "--shot");
        if (idx >= 0 && idx + 1 < args.Length)
        {
            UseConsole();
            RunShot(args[idx + 1], idx + 2 < args.Length && int.TryParse(args[idx + 2], out int t) ? t : 0,
                idx + 3 < args.Length ? args[idx + 3] : null,
                idx + 4 < args.Length ? args[idx + 4] : "studylog");
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
            Console.WriteLine($"  🔊 (x={s.RightPt:F0}pt y={s.MidYPt:F0}pt) {Short(s.Speak)}"
                + (Core.TextProcessing.PhraseMatcher.IsSentence(s.Speak) ? " [整句]" : "")
                + (s.Gloss is null ? "" : $" [释义] {Short(s.Gloss)}"));
            if (++shown >= (dumpLines ? 999 : 15)) break;
        }
    }

    /// <summary>
    /// 离屏渲染某一页成图片：SetupWithoutStarting 只装载 App 的样式与模板，不建主窗也不挂托盘；
    /// 默认吃本机真库（可传库文件路径看造出来的状态），所以图上排的就是真实数据。
    /// page 可选 studylog（默认，📌 学习 / 🔁 复习 两页签）· pomodoro（计时 / 统计 / 专注日历）· celebrate（到点庆祝卡）。
    /// </summary>
    private static void RunShot(string outPath, int tab, string? dbPath, string page)
    {
        _ = AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();

        var db = new LocalDatabase(string.IsNullOrEmpty(dbPath) ? null : dbPath);
        db.EnsureSchema();

        Window win;
        StudyLogPageViewModel? logVm = null;
        if (page == "celebrate")
        {
            // 庆祝卡自带尺寸（420×268），直接渲染它自己；文案用一组有代表性的真值。
            // 页签参数在这里当"哪一张卡"用：1 = 休息结束（冷色），其余 = 番茄完成（暖色）。
            var card = new CelebrationWindow();
            card.ShowCard(tab != 1
                ? new PomodoroCelebration("🎉", "第 6 个番茄完成", "起来活动一下，喝口水",
                    "今日 6 个番茄 · 专注 150 分钟", "🍅🍅🍅", "接下来短休息，让眼睛歇会儿", Focus: true)
                : new PomodoroCelebration("☕", "休息结束", "电充好了，回到书桌前",
                    "今日 6 个番茄 · 专注 150 分钟", "🍅🍅🍅🍅🍅🍅", "下一个专注已经在计时了", Focus: false));
            win = card;
        }
        else if (page == "pomodoro")
        {
            var pomodoro = new PomodoroPage { DataContext = new PomodoroPageViewModel(new PomodoroRepository(db)) };
            win = new Window
            {
                Content = pomodoro,
                Width = 1080,
                Height = 728,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            pomodoro.FindControl<TabControl>("Tabs")?.SelectedIndex = tab;
        }
        else
        {
            logVm = new StudyLogPageViewModel(new StudyLogRepository(db),
                review: new ReviewRepository(db)) { SelectedTab = tab };
            win = new Window
            {
                Content = new StudyLogPage { DataContext = logVm },
                Width = 1080,
                Height = 728,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
        }
        if (page != "celebrate") win.Show(); // 庆祝卡由 ShowCard 自己首次弹出，走的是应用里同一条路径
        Pump(1500); // 样式、字体与布局得走完一帧才有可渲染内容

        // 庆祝卡必须 1x 出图：无边框透明窗画进 192dpi 的 RTB 时字会翻倍（布局没错，只有文字放大），
        // 420×268 本身够小，1x 也看得清。
        double scale = page == "celebrate" ? 1 : 2;
        var rtb = new RenderTargetBitmap(
            new PixelSize((int)(win.Width * scale), (int)(win.Height * scale)),
            new Vector(96 * scale, 96 * scale));
        rtb.Render(win);
        using (var fs = File.Create(outPath)) rtb.Save(fs, new PngBitmapEncoderOptions());
        Console.WriteLine($"已渲染 {outPath}：{page} 页签 {tab}" +
            (logVm is null ? "" : $" · 日期 {logVm.Days.Count} 组 / 今日复习 {logVm.TodayDue.Count} 个 / 排期 {logVm.Agenda.Count} 天"));
        win.Close();
    }

    private static void Pump(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
