using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
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

        // 工具模式：--ocr <pdf> [页码|逗号分隔页码] [--lines] —— 无界面验证扫描件 OCR 链路（引擎可用性 + 识别质量 + 版式分类）
        // --lines 额外打印识别出的原始行几何（判跨栏合并/漏标必看，只看热点猜不到引擎给了什么）；
        // 多页时同进程并发识别（与应用 OcrPagesAsync 同路数），用于复现"并发下 Vision 行分组漂移"。
        idx = Array.IndexOf(args, "--ocr");
        if (idx >= 0 && idx + 1 < args.Length)
        {
            UseConsole();
            var pages = (idx + 2 < args.Length ? args[idx + 2] : "1")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out int pg) ? pg : 1)
                .ToList();
            RunOcr(args[idx + 1], pages, args.Contains("--lines")).GetAwaiter().GetResult();
            return;
        }

        // 工具模式：--pdflines <pdf> <页码> [--lines] —— dump 文本层路径（GetPageLines→BuildSpots），
        // 与 --ocr（强制 Vision）对照：文本层书的热点走这里，错位bug要在这条路上复现
        idx = Array.IndexOf(args, "--pdflines");
        if (idx >= 0 && idx + 1 < args.Length)
        {
            UseConsole();
            RunPdfLines(args[idx + 1], idx + 2 < args.Length && int.TryParse(args[idx + 2], out int plp) ? plp : 1);
            return;
        }

        // 工具模式：--shot <png> [页签] [库文件] [页面] [pdf路径] —— 把某一页离屏渲染成图片（本机无法点击/截屏时验收 UI）
        idx = Array.IndexOf(args, "--shot");
        if (idx >= 0 && idx + 1 < args.Length)
        {
            UseConsole();
            RunShot(args[idx + 1], idx + 2 < args.Length && int.TryParse(args[idx + 2], out int t) ? t : 0,
                idx + 3 < args.Length ? args[idx + 3] : null,
                idx + 4 < args.Length ? args[idx + 4] : "studylog",
                idx + 5 < args.Length ? args[idx + 5] : null);
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

    private static Core.Abstractions.IOcrService? CreateOcr()
    {
#if WINDOWS_OCR
        return new Parrot.Ocr.Windows.WinRtOcrService();
#elif MAC_OCR
        return new Parrot.Ocr.Mac.MacVisionOcrService();
#else
        return null;
#endif
    }

    private static void RunPdfLines(string path, int page1)
    {
        using var doc = new Parrot.Pdf.PdfDocumentSource(path);
        Console.WriteLine($"文本层路径《{System.IO.Path.GetFileName(path)}》第 {page1} 页：有文本层={doc.PageHasText(page1 - 1)}");
        var lines = doc.GetPageLines(page1 - 1);
        Console.WriteLine($"  {lines.Count} 行：");
        int i = 0;
        foreach (var l in lines)
            Console.WriteLine($"  #{++i:D2} L={l.Left,5:F0} R={l.Right,5:F0} T={l.Top,5:F0} B={l.Bottom,5:F0} fs={l.FontSize,4:F1} | {Short(l.Text)}"
                + (l.Runs is { Count: > 1 } ? $"  跑段[{string.Join(" | ", l.Runs.Select(r => $"{r.Left:F0}~{r.Right:F0} {Short(r.Text)}"))}]" : ""));
        Console.WriteLine("  —— 热点 ——");
        foreach (var s in Core.TextProcessing.OverlayLayout.BuildSpots(lines))
            Console.WriteLine($"  🔊 (x={s.RightPt:F0}pt y={s.MidYPt:F0}pt) {Short(s.Speak)}"
                + (s.Gloss is null ? "" : $" [释义] {Short(s.Gloss)}"));
    }

    private static async Task RunOcr(string path, List<int> pages1, bool dumpLines)
    {
        var ocr = CreateOcr();
        if (ocr is null || !ocr.IsAvailable)
        {
            Console.WriteLine("OCR 引擎不可用（本机无系统 OCR 组件：Windows 需 Windows.Media.Ocr，mac 需 /usr/bin/osascript + Vision）。");
            return;
        }
        using var doc = new Parrot.Pdf.PdfDocumentSource(path);

        if (pages1.Count == 1)
        {
            var (pg, lines, ms) = await OcrOne(doc, ocr, pages1[0]);
            Console.WriteLine($"OCR 引擎可用，最大边长 {ocr.MaxImageDimensionPx}px；识别《{System.IO.Path.GetFileName(path)}》第 {pg} 页…");
            PrintOcr(pg, lines, ms, dumpLines);
            return;
        }

        // 多页 = 同进程并发识别（与应用 OcrPagesAsync 同路数），复现并发下 Vision 的行为
        Console.WriteLine($"OCR 引擎可用；并发识别《{System.IO.Path.GetFileName(path)}》第 {string.Join(',', pages1)} 页…");
        int degree = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        var window = new List<Task<(int Page, IReadOnlyList<Core.TextProcessing.PdfLine>? Lines, long Ms)>>();
        var ordered = new (int Page, IReadOnlyList<Core.TextProcessing.PdfLine>? Lines, long Ms)?[pages1.Count];
        foreach (var (pg, i) in pages1.Select((pg, i) => (pg, i)))
        {
            window.Add(Task.Run(async () =>
            {
                var r = await OcrOne(doc, ocr, pg);
                ordered[i] = r;
                return r;
            }));
            if (window.Count >= degree)
            {
                await Task.WhenAny(window);
                window.RemoveAll(t => t.IsCompleted);
            }
        }
        await Task.WhenAll(window);
        foreach (var r in ordered)
            if (r is { } rr) PrintOcr(rr.Page, rr.Lines, rr.Ms, dumpLines);
    }

    private static async Task<(int Page, IReadOnlyList<Core.TextProcessing.PdfLine>? Lines, long Ms)> OcrOne(
        Parrot.Pdf.PdfDocumentSource doc, Core.Abstractions.IOcrService ocr, int page1)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lines = await doc.OcrPageLinesAsync(page1 - 1, ocr, CancellationToken.None);
        sw.Stop();
        return (page1, lines, sw.ElapsedMilliseconds);
    }

    private static void PrintOcr(int page1, IReadOnlyList<Core.TextProcessing.PdfLine>? lines, long ms, bool dumpLines)
    {
        if (lines is null || lines.Count == 0)
        {
            Console.WriteLine($"第 {page1} 页：未识别出文本（{ms}ms）。");
            return;
        }
        bool worth = Core.TextProcessing.OcrLayout.WorthKeeping(lines);
        var spots = Core.TextProcessing.OverlayLayout.BuildSpots(lines);
        Console.WriteLine($"第 {page1} 页：{lines.Count} 行（{ms}ms）；正文门槛 {(worth ? "通过" : "未过（图形页特征）")}；原图热点 {spots.Count} 个：");
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
    /// page 可选 studylog（默认，📌 学习 / 🔁 复习 两页签）· pomodoro（计时 / 统计 / 专注日历）·
    /// celebrate（到点庆祝卡）· reader（讲义文本视图，需传 pdf 路径；页签 1 = 遮挡模式开，4 = 原图视图后开遮挡）。
    /// </summary>
    private static void RunShot(string outPath, int tab, string? dbPath, string page, string? pdfPath = null)
    {
        _ = AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();

        var db = new LocalDatabase(string.IsNullOrEmpty(dbPath) ? null : dbPath);
        db.EnsureSchema();

        Window win;
        StudyLogPageViewModel? logVm = null;
        ReaderPageViewModel? readerVm = null;
        Parrot.Pdf.PdfDocumentSource? readerDoc = null;
        bool bottomShot = false;
        int jumpPage = 0;
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
        else if (page == "reader" || page.StartsWith("reader:"))
        {
            var vm = new ReaderPageViewModel(new NullTts(), new NullAudio(), CreateOcr(), restoreRecent: false);
            ReaderPage control;
            if (string.IsNullOrEmpty(pdfPath))
            {
                // 没给 PDF：注入假词条验证文本视图的遮挡（真书多为扫描页，走不到文本视图）
                control = new ReaderPage { DataContext = vm };
                control.FindControl<Border>("EmptyState")!.IsVisible = false;
                vm.Blocks.Add(new PageBreakViewModel(12));
                vm.Blocks.Add(new HeadingViewModel("Unit 12 · 词表样例", null));
                vm.Blocks.Add(new EntryViewModel("potential /pəˈtenʃl/ adj. 潜在的，可能的；n. 潜力；潜能", null, vm));
                vm.Blocks.Add(new EntryViewModel("potentiality n. 潜在性；可能性", null, vm));
                vm.Blocks.Add(new EntryViewModel("encourage v. 鼓励；激励；怂恿", null, vm));
                vm.Blocks.Add(new EntryViewModel("encouragement n. 鼓舞；奖励", null, vm));
                vm.Blocks.Add(new EntryViewModel("break down phr. （机器）出故障；（谈判）破裂；分解", null, vm));
                vm.Blocks.Add(new EntryViewModel("overwhelming adj. 压倒性的；令人不知所措的", null, vm));
                vm.Blocks.Add(new EntryViewModel("perspective n. 观点；视角；前景", null, vm));
            }
            else
            {
                var doc = new Parrot.Pdf.PdfDocumentSource(pdfPath);
                readerDoc = doc;
                vm.SelectedDocument = doc;
                // reader:6 形式 = 直接跳到第 6 页的原图卡（验证扫描页遮挡/热点用）；
                // reader:6:bottom = 同上且滚到页底（验证罩层下缘/页码露出）。
                // 只解析不跳：CurrentPages 是异步建的，此刻可能还没好，跳页在 Show 之后做
                if (page.StartsWith("reader:"))
                {
                    var segs = page.Split(':');
                    jumpPage = segs.Length >= 2 && int.TryParse(segs[1], out var j) ? j : 0;
                    bottomShot = segs.Length >= 3 && segs[2] == "bottom";
                }
                control = new ReaderPage { DataContext = vm };
            }
            if (tab is 2 or 3 or 4) vm.ViewModeIndex = 1; // 原图视图
            if (tab is 1 or 3) vm.IsMaskOn = true; // 页签 4 = 加载时遮挡关、截图前才打开（复现"先开页面后开遮挡"的顺序）
            win = new Window
            {
                Content = control,
                Width = 1080,
                Height = 728,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            readerVm = vm;
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

        if (readerDoc is not null && readerVm is not null)
        {
            // 跳页等 CurrentPages 就绪（异步建页，竞态下会悄悄跳失败停在第 1 页）
            if (jumpPage >= 1)
            {
                var swj = System.Diagnostics.Stopwatch.StartNew();
                while ((readerVm.CurrentPages is not { } pp || pp.Count < jumpPage) && swj.ElapsedMilliseconds < 30000)
                    Pump(200);
                if (readerVm.CurrentPages is { } pp2 && pp2.Count >= jumpPage)
                {
                    readerVm.SelectedPage = pp2[jumpPage - 1];
                    Console.WriteLine($"跳页 → 第 {readerVm.SelectedPage.PageIndex + 1} 页（共 {pp2.Count} 页）");
                    Pump(500);
                }
                else
                    Console.WriteLine($"跳页失败：CurrentPages 未就绪（目标第 {jumpPage} 页）");
            }
            // 整册提取是后台任务：等它出块（大书 300+ 页要几十秒），再取首个词条所在的词表页截 40 块
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (readerVm.Blocks.Count == 0 && sw.ElapsedMilliseconds < 90000)
                Pump(200);
            var blocks = readerVm.Blocks.ToList();
            int entry = blocks.FindIndex(b => b is EntryViewModel);
            if (entry > 0)
            {
                int start = 0;
                for (int i = entry; i >= 0; i--)
                    if (blocks[i] is PageBreakViewModel) { start = i; break; }
                var keep = blocks.Skip(start).Take(40).ToList();
                readerVm.Blocks.Clear();
                foreach (var b in keep) readerVm.Blocks.Add(b);
            }
            // 原图视图：等当前页位图真正渲出来再截图
            if (readerVm.ViewModeIndex == 1)
            {
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                while (readerVm.SelectedPage?.Image is null && sw2.ElapsedMilliseconds < 10000)
                    Pump(100);
                if (tab is 1 or 3 or 4)
                {
                    // 遮挡边界跟着 OCR 走（扫描书要等整册提取完才开 OCR）：等它给出（或明确放弃）再截图
                    var sw3 = System.Diagnostics.Stopwatch.StartNew();
                    while (readerVm.SelectedPage?.MaskLeftFraction is null && sw3.ElapsedMilliseconds < 90000)
                        Pump(250);
                    Console.WriteLine(readerVm.SelectedPage?.MaskLeftFraction is { } f
                        ? $"遮挡边界 = 页宽 {f:P1} 处"
                        : "遮挡边界 = OCR 未给出（该页不盖）");
                }
                if (tab == 4)
                {
                    // 页面已带数据渲染完，此刻才打开遮挡：罩层必须立刻出现（回归"后开遮挡不见罩层"）
                    readerVm.IsMaskOn = true;
                    Pump(400);
                }
                if (Environment.GetEnvironmentVariable("PARROT_DUMP_ROWS") == "1")
                    Console.WriteLine(readerVm.SelectedPage?.DebugDumpMask() ?? "无页");
                if (bottomShot)
                {
                    // ScrollViewer 包着整册（47 页连排），滚到底=最后一页；只把目标页卡片的底部条带滚进视口
                    var card = win.GetVisualDescendants().OfType<Control>()
                        .FirstOrDefault(c => ReferenceEquals(c.DataContext, readerVm.SelectedPage) && c.Bounds.Height > 50);
                    if (card is not null)
                    {
                        var strip = Math.Min(600, card.Bounds.Height);
                        card.BringIntoView(new Avalonia.Rect(0, card.Bounds.Height - strip, card.Bounds.Width, strip));
                        Pump(800);
                    }
                }
            }
        }
            // 调试：打印该页实际叠加的热点（对照 --ocr --lines 的坐标，判断错位出在数据还是渲染）
            if (readerVm.SelectedPage is { } dbgPage)
            {
                Console.WriteLine($"—— 第 {dbgPage.PageIndex + 1} 页实际热点（canvas px，图 {dbgPage.ImageWidthPx:F0}×{dbgPage.ImageHeightPx:F0}）——");
                foreach (var o in dbgPage.Overlays)
                    Console.WriteLine($"  ({o.X:F0},{o.Y:F0}) {o.Audio.Text}");
            }
            // PARROT_REVEAL_WORD=田地 → 模拟"悬停在罩层上该词"，验证悬停露词（截图里应只见这一个词）
            if (Environment.GetEnvironmentVariable("PARROT_REVEAL_WORD") is { Length: > 0 } word)
            {
                var hit = readerVm.SelectedPage?.DebugRevealWord(word) == true;
                Console.WriteLine(hit ? $"露词命中：{word}" : $"露词未命中：{word}");
                if (hit && readerVm.SelectedPage is { } hitCard)
                {
                    var rv = hitCard.MaskReveal!;
                    Console.WriteLine($"露出带 Top={rv.Top:F1} H={rv.Height:F1}（按钮 cy=自身Y+14，落在带内才浮出）");
                    foreach (var o in hitCard.Overlays)
                        Console.WriteLine($"  按钮 ({o.X:F0},{o.Y:F0}) 可见={o.MaskVisible}「{o.Audio.Text}」");
                    Pump(400); // 布局/渲染走完再量真实屏幕框
                    foreach (var b in win.GetVisualDescendants().OfType<Button>())
                        if (b.DataContext is Parrot.UI.ViewModels.OverlayItemViewModel oi)
                        {
                            var m = b.TransformToVisual(win) ?? Avalonia.Matrix.Identity;
                            var r = new Avalonia.Rect(b.Bounds.Size).TransformToAABB(m);
                            var panel = b.GetVisualParent() as Avalonia.Controls.StackPanel;
                            Console.WriteLine($"  渲染 ({oi.X:F0},{oi.Y:F0}) MaskVisible={oi.MaskVisible} 面板可见={panel?.IsVisible} 自身IsVisible={b.IsVisible} 有效={b.IsEffectivelyVisible} 屏幕=({r.X:F0},{r.Y:F0},{r.Width:F0}×{r.Height:F0})「{oi.Audio.Text}」");
                        }
                }
                if (hit) Pump(200);
            }
            // PARROT_REAL_HOVER=1 → 构造真实路由事件从 Grid 走全链（不走 VM 直调），自证 XAML 接线 + GetPosition
            if (Environment.GetEnvironmentVariable("PARROT_REAL_HOVER") == "1")
            {
                var pointer = new Pointer(9901, PointerType.Mouse, true);
                if (readerVm.SelectedPage is { } realCard) // 原图视图：PointerMoved 打进 PageHost 的画布 Grid
                {
                    var hoverWord = Environment.GetEnvironmentVariable("PARROT_REVEAL_WORD") ?? "";
                    Avalonia.Point? pt = null;
                    var swR = System.Diagnostics.Stopwatch.StartNew();
                    while (swR.ElapsedMilliseconds < 10000)
                    {
                        pt = realCard.DebugGetRowPoint(hoverWord);
                        if (pt is not null) break;
                        Pump(250);
                    }
                    var grid = win.GetVisualDescendants().OfType<Grid>()
                        .FirstOrDefault(g => ReferenceEquals(g.DataContext, realCard));
                    if (pt is not { } p || grid is null)
                        Console.WriteLine($"REAL_HOVER：就绪失败（Grid找到={grid is not null} 坐标算出={pt is not null}）");
                    else
                    {
                        var evt = new PointerEventArgs(InputElement.PointerMovedEvent, grid, pointer,
                            grid, p, 0, PointerPointProperties.None, KeyModifiers.None);
                        grid.RaiseEvent(evt);
                        Console.WriteLine($"REAL_HOVER：已在画布 ({p.X:F0},{p.Y:F0}) 触发真实路由 PointerMoved，露行={(realCard.MaskReveal is null ? "无" : "有")}");
                        Pump(200);
                    }
                }
                else // 文本视图（无 pdf 假词条）：PointerEntered 打进第一个被遮挡的词条行
                {
                    var grid = win.GetVisualDescendants().OfType<Grid>()
                        .FirstOrDefault(g => g.DataContext is EntryViewModel ev && ev.Masked);
                    if (grid?.DataContext is not EntryViewModel entry)
                        Console.WriteLine("REAL_HOVER：就绪失败（找不到被遮挡的词条行）");
                    else
                    {
                        var evt = new PointerEventArgs(InputElement.PointerEnteredEvent, grid, pointer,
                            grid, default, 0, PointerPointProperties.None, KeyModifiers.None);
                        grid.RaiseEvent(evt);
                        Console.WriteLine($"REAL_HOVER：已对词条「{entry.Headword}」触发真实路由 PointerEntered，露出={entry.Revealed}");
                        Pump(200);
                    }
                }
            }
            Pump(400);

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
        readerDoc?.Dispose();
        win.Close();
    }

    private sealed class NullTts : Parrot.Core.Abstractions.ITtsService
    {
        public Task<string> SynthesizeAsync(string text, Parrot.Core.Abstractions.TtsKind kind,
            string? voice = null, CancellationToken ct = default) => Task.FromResult("");
    }

    private sealed class NullAudio : Parrot.Core.Abstractions.IAudioPlayer
    {
        public Task PlayAsync(string filePath, CancellationToken ct = default) => Task.CompletedTask;
        public void Stop() { }
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
