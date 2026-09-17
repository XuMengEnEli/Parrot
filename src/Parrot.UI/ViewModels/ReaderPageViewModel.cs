using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parrot.Core;
using Parrot.Core.Abstractions;
using Parrot.Core.TextProcessing;
using Parrot.Data;
using Parrot.Pdf;

namespace Parrot.UI.ViewModels;

/// <summary>
/// 阅读页（需求 1.1/1.2）：默认**文本视图**——PDF 内容提取后按原版式重排（标题/段落/词行/列表），
/// 每个英文句子后面直接挂 🔊；扫描件页原位提示并可一键跳转"原图视图"（位图页作为兜底模式保留）。
/// </summary>
public sealed partial class ReaderPageViewModel : PageViewModelBase
{
    private static string RecentListPath
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            LegacyUserData.MigrateInto(root);
            return Path.Combine(root, "Parrot", "recent-docs.txt");
        }
    }

    private readonly ITtsService _tts;
    private readonly IAudioPlayer _audio;
    private readonly IOcrService? _ocr;
    private readonly StudyLogRepository? _studyLog;
    private readonly WordbookRepository? _wordbook;
    /// <summary>今日已📌的词（📌按钮初始态 + 重复点去重，切页/重建 VM 不丢状态）。</summary>
    private readonly HashSet<string> _recordedWords = new(StringComparer.Ordinal);
    private CancellationTokenSource? _ocrCts;
    private bool _autoSwitching;    // 程序性 ViewMode 切换不算用户操作

    public ReaderPageViewModel(ITtsService tts, IAudioPlayer audio, IOcrService? ocr = null, bool restoreRecent = true,
        PomodoroPageViewModel? pomodoro = null, StudyLogRepository? studyLog = null, WordbookRepository? wordbook = null)
    {
        _tts = tts;
        _audio = audio;
        _ocr = ocr;
        _studyLog = studyLog;
        _wordbook = wordbook;
        Pomodoro = pomodoro;
        Documents = [];
        if (studyLog is not null)
            foreach (var r in studyLog.WordsOfDay(DateOnly.FromDateTime(DateTime.Now)))
                _recordedWords.Add(r.Word);
        if (restoreRecent)
            RestoreRecentDocs();
    }

    /// <summary>共享的番茄钟 VM（与番茄钟页同一实例）；单测里为 null → 右上角迷你卡隐藏。</summary>
    public PomodoroPageViewModel? Pomodoro { get; }

    public override string Title => "讲义阅读";
    public override string Glyph => "📖";

    public ObservableCollection<PdfDocumentSource> Documents { get; }

    // ---------- 视图模式：0=文本（默认） 1=原图 ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextView))]
    private int _viewModeIndex;

    public bool IsTextView => ViewModeIndex == 0;

    /// <summary>文本字号档位。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextFontSize))]
    private int _textSizeIndex = 1;

    public double TextFontSize => TextSizeIndex switch { 0 => 13, 1 => 15, 2 => 17, _ => 20 };

    // ---------- 文档选择 ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPages))]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    private PdfDocumentSource? _selectedDocument;

    [ObservableProperty]
    private PageCardViewModel? _selectedPage;

    [ObservableProperty]
    private bool _isExtracting;

    [ObservableProperty]
    private bool _extractFailed;

    /// <summary>整册多为扫描件时的说明（非空即显示）。</summary>
    [ObservableProperty]
    private string? _scanHint;

    /// <summary>OCR 进行中状态条文本（非空即显示）。</summary>
    [ObservableProperty]
    private string? _ocrStatus;

    partial void OnViewModeIndexChanged(int value)
    {
        if (_autoSwitching) return;
        if (value == 0) ScanHint = null; // 手动切回文本视图时撤掉说明条
    }

    /// <summary>缩放档位（PDF 点→像素的 DPI），仅原图模式用；改动触发已显示页重渲。</summary>
    [ObservableProperty]
    private int _renderDpi = 150;

    public bool HasDocument => SelectedDocument is not null;
    public IReadOnlyList<PageCardViewModel>? CurrentPages => SelectedDocument is null ? null : _pagesCache[SelectedDocument];

    /// <summary>文本视图的版式块流（标题/段落/词行/扫描占位/分页标记）。</summary>
    public ObservableCollection<BlockViewModel> Blocks { get; } = [];

    internal PageCardViewModel? PageAt(int pageIndex)
        => SelectedDocument is not null
           && _pagesCache.TryGetValue(SelectedDocument, out var pages)
           && pageIndex >= 0 && pageIndex < pages.Count
            ? pages[pageIndex]
            : null;

    private readonly Dictionary<PdfDocumentSource, List<PageCardViewModel>> _pagesCache = new();
    private readonly Dictionary<PdfDocumentSource, IReadOnlyList<PdfBlock>> _blocksCache = new();
    private readonly Dictionary<PdfDocumentSource, System.Collections.Concurrent.ConcurrentDictionary<int, List<SpeakSpot>>> _spotsCache = new();
    private CancellationTokenSource? _blocksCts;

    partial void OnSelectedDocumentChanged(PdfDocumentSource? value)
    {
        if (value is null) return;
        if (!_pagesCache.ContainsKey(value))
        {
            var pages = new List<PageCardViewModel>(value.PageCount);
            for (int i = 0; i < value.PageCount; i++)
                pages.Add(new PageCardViewModel(value, i, this));
            _pagesCache[value] = pages;
        }
        SelectedPage = _pagesCache[value][0];
        RebuildBlocks(value);
        _ = FillTextSpotsAsync(value); // 文本层页：原图上直接标句尾 🔊（不等 OCR）
    }

    partial void OnRenderDpiChanged(int value)
    {
        if (SelectedDocument is not null && _pagesCache.TryGetValue(SelectedDocument, out var pages))
        {
            foreach (var p in pages)
                p.InvalidateRender(); // 仅当前可见页会真正重渲（见 PageCardViewModel）
        }
    }

    private async void RebuildBlocks(PdfDocumentSource doc)
    {
        _blocksCts?.Cancel();
        _ocrCts?.Cancel();
        _ocrCts = null;
        OcrStatus = null;
        var cts = new CancellationTokenSource();
        _blocksCts = cts;
        ExtractFailed = false;
        ScanHint = null;

        if (_blocksCache.TryGetValue(doc, out var cached))
        {
            ApplyBlocks(doc, cached);
            return;
        }

        IsExtracting = true;
        Blocks.Clear();
        try
        {
            var blocks = await Task.Run(doc.ExtractLayout, cts.Token);
            if (cts.IsCancellationRequested) return;
            _blocksCache[doc] = blocks;
            ApplyBlocks(doc, blocks);
        }
        catch (OperationCanceledException) { }
        catch
        {
            ExtractFailed = true; // 提取失败 → UI 提示切原图模式
        }
        finally
        {
            if (!cts.IsCancellationRequested)
                IsExtracting = false;
        }
    }

    /// <summary>
    /// 装入块流并处理扫描页：有 OCR 引擎时后台逐页识别，在原图视图上叠加句尾 🔊 热点
    /// （原排版由位图原样保留，OCR 文本只进 TTS 不上屏）；过半扫描时自动停在原图视图等热点。
    /// </summary>
    private void ApplyBlocks(PdfDocumentSource doc, IReadOnlyList<PdfBlock> blocks)
    {
        LoadBlocks(blocks);
        var scannedPages = blocks
            .Where(b => b.Kind == PdfBlockKind.ScannedPage)
            .Select(b => b.Page).Distinct().ToList();
        if (scannedPages.Count == 0) return;

        bool ocrReady = _ocr?.IsAvailable == true;
        bool majority = doc.PageCount > 0 && scannedPages.Count * 2 >= doc.PageCount;

        // 已识别过的页不重复 OCR（切走再切回同一文档时直接命中缓存）
        var known = GetSpotsMap(doc);
        var todo = scannedPages.Where(p => !known.ContainsKey(p)).ToList();

        if (majority)
        {
            ScanHint = ocrReady
                ? $"本文档 {scannedPages.Count}/{doc.PageCount} 页为扫描件（无文本层），系统 OCR 正在后台把句内 🔊 标注到原图上（离线），稍候即可在原图上逐句点读，也可先睹为快"
                : $"本文档 {scannedPages.Count}/{doc.PageCount} 页为扫描件（无文本层，本机 OCR 不可用），已自动切到原图视图；有文字的页可在“视图→文本视图”阅读";
            SetViewModeAuto(1); // 原图=原版式，热点随 OCR 进度逐页出现
        }
        if (ocrReady && todo.Count > 0)
            _ = OcrFillAsync(doc, todo);
    }

    private void SetViewModeAuto(int value)
    {
        _autoSwitching = true;
        ViewModeIndex = value;
        _autoSwitching = false;
    }

    /// <summary>后台并发 OCR：产出"句尾坐标+发音文本"热点，就地叠加到原图（进度在工具栏）。</summary>
    private async Task OcrFillAsync(PdfDocumentSource doc, List<int> scannedPages)
    {
        var cts = new CancellationTokenSource();
        _ocrCts = cts;
        int done = 0, ok = 0;
        OcrStatus = $"OCR 标注原图🔊 0/{scannedPages.Count}…";
        try
        {
            await foreach (var (page, lines) in OcrPagesAsync(doc, scannedPages, OcrDegree, cts.Token))
            {
                done++;
                // 图形页（思维导图）OCR 只有碎片字块 → 不过"确有正文"门槛就不做任何标注，保留干净原图
                if (lines is { Count: > 0 } && OcrLayout.WorthKeeping(lines))
                {
                    var spots = OverlayLayout.BuildSpots(lines);
                    if (spots.Count > 0)
                    {
                        ok++;
                        GetSpotsMap(doc)[page] = spots;
                        if (ReferenceEquals(SelectedDocument, doc)
                            && _pagesCache.TryGetValue(doc, out var pages)
                            && page >= 1 && page <= pages.Count)
                            pages[page - 1].SetSpots(spots);
                    }
                }
                OcrStatus = done < scannedPages.Count ? $"OCR 标注原图🔊 {done}/{scannedPages.Count}…" : null;
                if (cts.IsCancellationRequested || !ReferenceEquals(SelectedDocument, doc)) break;
            }
        }
        catch (OperationCanceledException) { /* 切走文档/关页：进度条下面统一收起 */ }
        OcrStatus = null;

        if (ok > 0 && ReferenceEquals(SelectedDocument, doc)
            && !cts.IsCancellationRequested && ViewModeIndex == 1)
            ScanHint = $"OCR 完成：已在 {ok} 页的原图上标注句内 🔊，点句子右侧的喇叭即可发音";
    }

    /// <summary>
    /// 识别并发度：Vision 每页是一个 osascript 子进程（各自加载 Vision 模型，内存按页数涨），
    /// WinRT 每页自建 BitmapDecoder，都互不依赖。
    /// 实测 12 页扫描件（16 核机）：4 路 3.18x、6 路 3.98x、8 路 4.32x——6 路之后收益被
    /// PDFium 渲染全局锁与识别进程抢核吃掉，故上限取 6；小核数机器按 CPU/2 退到 2~3 路。
    /// 完整首次加载路径复测（46 页真扫描件）：串行 45.8s → 6 路 13.7s，1022 个热点一字不差。
    /// </summary>
    private static int OcrDegree => Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

    /// <summary>
    /// 按完成顺序产出"页 → OCR 行"：滑动窗口内最多 <paramref name="degree"/> 页在识别。
    /// 页渲染仍在 PDFium 全局锁里串行（见 PdfDocumentSource.PdfiumGate），所以并发省下的是识别那一大段。
    /// 产出方不碰 UI：消费端（OcrFillAsync）在自己的上下文里逐条落卡。
    /// </summary>
    private async IAsyncEnumerable<(int Page, IReadOnlyList<PdfLine>? Lines)> OcrPagesAsync(
        PdfDocumentSource doc, List<int> pages, int degree,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var running = new List<Task<(int, IReadOnlyList<PdfLine>?)>>();
        int next = 0;
        while (next < pages.Count || running.Count > 0)
        {
            while (running.Count < degree && next < pages.Count)
            {
                int page = pages[next++];
                running.Add(RunPageAsync(page));
            }
            var finished = await Task.WhenAny(running);
            running.Remove(finished);
            yield return await finished;
        }

        async Task<(int, IReadOnlyList<PdfLine>?)> RunPageAsync(int page)
            => (page, await doc.OcrPageLinesAsync(page - 1, _ocr!, ct));
    }

    /// <summary>文本层页的原图热点：坐标直接来自 PdfPig 词框，精确且即开即用（不依赖 OCR）。</summary>
    private async Task FillTextSpotsAsync(PdfDocumentSource doc)
    {
        var map = GetSpotsMap(doc);
        if (!map.IsEmpty)
        {
            ApplySpotsToCards(doc, map);
            return;
        }
        Dictionary<int, List<SpeakSpot>> fresh;
        try
        {
            fresh = await Task.Run(() =>
            {
                var m = new Dictionary<int, List<SpeakSpot>>();
                for (int i = 0; i < doc.PageCount; i++)
                {
                    var lines = doc.GetPageLines(i);
                    if (lines.Count == 0) continue;
                    var spots = OverlayLayout.BuildSpots(lines);
                    if (spots.Count > 0) m[i + 1] = spots;
                }
                return m;
            });
        }
        catch { return; }
        foreach (var (page, spots) in fresh) map[page] = spots;
        ApplySpotsToCards(doc, map);
    }

    private void ApplySpotsToCards(PdfDocumentSource doc, IReadOnlyDictionary<int, List<SpeakSpot>> map)
    {
        if (!ReferenceEquals(SelectedDocument, doc)) return; // 缓存已存，只是不动当前视图
        if (!_pagesCache.TryGetValue(doc, out var pages)) return;
        foreach (var (page, spots) in map)
            if (page >= 1 && page <= pages.Count)
                pages[page - 1].SetSpots(spots);
    }

    private System.Collections.Concurrent.ConcurrentDictionary<int, List<SpeakSpot>> GetSpotsMap(PdfDocumentSource doc)
    {
        lock (_spotsCache)
            return _spotsCache.TryGetValue(doc, out var m) ? m : _spotsCache[doc] = new();
    }

    private void LoadBlocks(IReadOnlyList<PdfBlock> blocks)
    {
        Blocks.Clear();
        int lastPage = 0;
        foreach (var b in blocks)
        {
            if (b.Page != lastPage) // 页分隔只在换页处出现一次
            {
                lastPage = b.Page;
                Blocks.Add(new PageBreakViewModel(b.Page));
            }
            var model = ToVm(b);
            if (model is not null) Blocks.Add(model);
        }
    }

    private BlockViewModel? ToVm(PdfBlock b)
    {
        switch (b.Kind)
        {
            case PdfBlockKind.ScannedPage:
                return new ScannedPageViewModel(b.Page);
            case PdfBlockKind.Heading:
            {
                var f = b.Fragments.Count > 0 ? b.Fragments[0] : new PdfFragment("", null);
                return new HeadingViewModel(f.Display, WrapSpeak(f));
            }
            case PdfBlockKind.Entry:
            {
                var f = b.Fragments.Count > 0 ? b.Fragments[0] : new PdfFragment("", null);
                return new EntryViewModel(f.Display, WrapSpeak(f));
            }
            case PdfBlockKind.ListItem:
            case PdfBlockKind.Paragraph:
            {
                if (b.Fragments.Count == 0) return null;
                var items = b.Fragments
                    .Select(f => NewSentence(f.Display, f.Speak))
                    .ToList();
                return b.Kind == PdfBlockKind.ListItem
                    ? new ListBlockViewModel(items)
                    : new ParagraphViewModel(items);
            }
            default:
                return null;
        }
    }

    private SentenceItemViewModel? WrapSpeak(PdfFragment f)
        => f.Speak is null ? null : NewSentence(f.Display, f.Speak);

    /// <summary>原图热点复用同一个发音 VM（Display 不上屏，只给按钮用）。</summary>
    internal SentenceItemViewModel CreateSpeak(string speak) => NewSentence(speak, speak);

    // ---------- 📌 记入当日学习（需求：在小喇叭旁边加个按钮） ----------

    /// <summary>统一入口：带发音的 VM 都从这造，📌 初始态从今日记录缓存恢复。</summary>
    private SentenceItemViewModel NewSentence(string display, string? speak)
    {
        var vm = new SentenceItemViewModel(display, speak, _tts, _audio,
            _studyLog is null ? null : ToggleRecord);
        var w = Headword(speak ?? display);
        if (w is not null) vm.IsRecorded = _recordedWords.Contains(w);
        return vm;
    }

    /// <summary>点📌：没记则记入当天（连释义快照+出处句一起存），已记则从当天移除。</summary>
    private void ToggleRecord(SentenceItemViewModel vm)
    {
        if (_studyLog is null) return;
        var word = Headword(vm.Speak ?? vm.Text);
        if (word is null) return;
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (vm.IsRecorded)
        {
            if (_studyLog.Remove(word, today)) _recordedWords.Remove(word);
        }
        else
        {
            RecordWord(vm.Speak ?? vm.Text, vm.Text);
        }
    }

    /// <summary>把一个句子/词行里的代表词记入今日学习（📌 按钮与单测共用入口）。</summary>
    public void RecordWord(string text, string note)
    {
        if (_studyLog is null) return;
        var word = Headword(text);
        if (word is null) return;
        _studyLog.Add(word, _wordbook?.Lookup(word)?.Translation ?? "", note, DateOnly.FromDateTime(DateTime.Now));
        _recordedWords.Add(word);
    }

    private static readonly Regex WordToken = new(@"[A-Za-z][A-Za-z'’-]*", RegexOptions.Compiled);

    /// <summary>取"这条 🔊 代表的单词"：跳过 the/a/to 等虚词后的第一个实义英文词（词行即词条本身，句子取主语性首词）。</summary>
    public static string? Headword(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string? fallback = null;
        foreach (Match m in WordToken.Matches(text))
        {
            var t = m.Value.Trim('\'', '’', '-');
            if (t.Length < 2) continue;
            fallback ??= t.ToLowerInvariant();
            if (!StopWords.Contains(t.ToLowerInvariant())) return t.ToLowerInvariant();
        }
        return fallback;
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "to", "of", "in", "on", "at", "by", "for", "with", "and", "or", "is", "are",
        "was", "were", "be", "been", "being", "it", "its", "as", "not", "no", "but", "if", "then",
        "than", "so", "do", "does", "did", "have", "has", "had", "will", "would", "can", "could",
        "this", "that", "these", "those", "from", "there", "here", "when", "while", "which", "who",
    };

    /// <summary>扫描件占位块上的"查看原图"→ 切到原图模式并定位该页。</summary>
    [RelayCommand]
    private void OpenPageImage(int page1Based)
    {
        ViewModeIndex = 1;
        if (SelectedDocument is not null
            && _pagesCache.TryGetValue(SelectedDocument, out var pages)
            && page1Based >= 1 && page1Based <= pages.Count)
            SelectedPage = pages[page1Based - 1];
    }

    public async Task ImportAsync(string filePath)
    {
        try
        {
            if (Documents.Any(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
            {
                SelectedDocument = Documents.First(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                return;
            }

            var doc = new PdfDocumentSource(filePath);
            Documents.Add(doc);
            SelectedDocument = doc;
            SaveRecentDocs();
        }
        catch (Exception ex)
        {
            ImportError = $"导入失败：{ex.Message}";
        }
    }

    [ObservableProperty]
    private string? _importError;

    partial void OnImportErrorChanged(string? value)
    {
        if (value is not null)
        {
            // 5 秒后自动清掉
            _ = ClearErrorSoon();
        }

        async Task ClearErrorSoon()
        {
            await Task.Delay(5000);
            if (ImportError == value) ImportError = null;
        }
    }

    private void RestoreRecentDocs()
    {
        try
        {
            if (!File.Exists(RecentListPath)) return;
            foreach (var line in File.ReadAllLines(RecentListPath))
            {
                if (string.IsNullOrWhiteSpace(line) || !File.Exists(line)) continue;
                try
                {
                    Documents.Add(new PdfDocumentSource(line));
                }
                catch
                {
                    // 单份损坏不影响其余
                }
            }
            if (Documents.Count > 0)
                SelectedDocument = Documents[0];
        }
        catch
        {
            // 最近列表不可用不影响启动
        }
    }

    private void SaveRecentDocs()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RecentListPath)!);
            File.WriteAllLines(RecentListPath, Documents.Select(d => d.FilePath));
        }
        catch
        {
            // ignore
        }
    }
}

// ---------- 版式块的视图模型（XAML 按类型选模板） ----------

public abstract class BlockViewModel
{
    protected BlockViewModel(int page) => Page = page;
    public int Page { get; }
}

/// <summary>分页标记（保留"第 N 页"结构感）。</summary>
public sealed class PageBreakViewModel(int page) : BlockViewModel(page);

/// <summary>扫描件页占位（无文本层，可跳原图；OCR 完成后句尾 🔊 标在原图上）。</summary>
public sealed class ScannedPageViewModel(int page) : BlockViewModel(page);

/// <summary>原图叠加项：句尾按钮位置（位图像素坐标）+ 发音 VM。</summary>
public sealed class OverlayItemViewModel(SentenceItemViewModel audio, double x, double y)
{
    public SentenceItemViewModel Audio { get; } = audio;
    public double X { get; } = x;
    public double Y { get; } = y;
}

/// <summary>标题块（英文标题带发音）。</summary>
public sealed class HeadingViewModel(string text, SentenceItemViewModel? audio) : BlockViewModel(0)
{
    public string Text { get; } = text;
    public SentenceItemViewModel? Audio { get; } = audio;
}

/// <summary>词条行块（词表页：英文+音标+中文一行，整行一个发音按钮）。</summary>
public sealed class EntryViewModel(string text, SentenceItemViewModel? audio) : BlockViewModel(0)
{
    public string Text { get; } = text;
    public SentenceItemViewModel? Audio { get; } = audio;
}

/// <summary>正文段：句群流式排布（WrapPanel），每句尾挂 🔊。</summary>
public sealed class ParagraphViewModel(IReadOnlyList<SentenceItemViewModel> sentences) : BlockViewModel(0)
{
    public IReadOnlyList<SentenceItemViewModel> Sentences { get; } = sentences;
}

/// <summary>列表项段（同段排布，前置 •）。</summary>
public sealed class ListBlockViewModel(IReadOnlyList<SentenceItemViewModel> sentences) : BlockViewModel(0)
{
    public IReadOnlyList<SentenceItemViewModel> Sentences { get; } = sentences;
}

/// <summary>阅读页里的一页：懒渲染 + 可见性驱动（虚拟化），改 DPI 重渲。仅"原图模式"使用。</summary>
public sealed partial class PageCardViewModel : ObservableObject
{
    private readonly ReaderPageViewModel _owner;
    private CancellationTokenSource? _cts;
    private bool _everRendered;
    private readonly double _pageHeightPt;
    private IReadOnlyList<SpeakSpot> _spots = [];

    public PageCardViewModel(PdfDocumentSource document, int pageIndex, ReaderPageViewModel owner)
    {
        Document = document;
        PageIndex = pageIndex;
        _owner = owner;
        HasText = document.PageHasText(pageIndex);
        var size = document.GetPageSizePoints(pageIndex);
        AspectRatio = size.Width / Math.Max(1, size.Height);
        _pageHeightPt = Math.Max(1, size.Height);
    }

    public PdfDocumentSource Document { get; }
    public int PageIndex { get; }
    public int DisplayNumber => PageIndex + 1;
    public bool HasText { get; }
    public double AspectRatio { get; }

    [ObservableProperty]
    private WriteableBitmap? _image;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>叠加层画布尺寸（=位图像素；Grid 用它与 Image 完全对齐）。</summary>
    [ObservableProperty]
    private double _imageWidthPx = 620;

    [ObservableProperty]
    private double _imageHeightPx = 876;

    /// <summary>句尾 🔊 热点（原图视图叠加层；坐标随 RenderDpi 换算）。</summary>
    public ObservableCollection<OverlayItemViewModel> Overlays { get; } = [];

    internal void SetSpots(IReadOnlyList<SpeakSpot> spots)
    {
        _spots = spots;
        ApplySpots();
    }

    private void ApplySpots()
    {
        double ppp = _owner.RenderDpi / 72.0;
        Overlays.Clear();
        foreach (var s in _spots)
            Overlays.Add(new OverlayItemViewModel(
                _owner.CreateSpeak(s.Speak),
                s.RightPt * ppp + 6,                 // 句尾右缘外一点
                (_pageHeightPt - s.MidYPt) * ppp - 14)); // y-up→y-down，按钮垂直居中于行
    }

    public void OnVisible()
    {
        _visible = true;
        // 本页 + 前后各一页预渲（性能策略）
        for (int delta = 0; delta <= 1; delta++)
        {
            foreach (var dir in delta == 0 ? new[] { 0 } : new[] { -1, 1 })
            {
                var idx = PageIndex + dir * delta;
                if (idx < 0 || idx >= Document.PageCount) continue;
                _ = RenderPageAsync(idx);
            }
        }
    }

    public void OnInvisible()
    {
        _visible = false;
        _cts?.Cancel();
        _cts = null;
    }

    /// <summary>DPI 变化时由 owner 调用：丢弃位图，若仍可见会重渲；热点坐标随新缩放重排。</summary>
    public void InvalidateRender()
    {
        Image = null;
        _everRendered = false;
        ApplySpots();
        if (_visible)
            OnVisible();
    }

    private bool _visible;

    private async Task RenderPageAsync(int pageIndex)
    {
        var page = pageIndex == PageIndex ? this : _owner.PageAt(pageIndex);
        if (page is null || page._everRendered) return;

        page._cts?.Cancel();
        var cts = new CancellationTokenSource();
        page._cts = cts;
        page.IsLoading = true;
        try
        {
            var rendered = await page.Document.RenderPageAsync(page.PageIndex, _owner.RenderDpi, cts.Token);
            page.Image = ToWriteable(rendered);
            page.ImageWidthPx = rendered.WidthPx;
            page.ImageHeightPx = rendered.HeightPx;
            page._everRendered = true;
        }
        catch (OperationCanceledException)
        {
            // 滚出视口或被新 DPI 取代，正常
        }
        catch
        {
            // 渲染失败静默（该页显示空白占位）
        }
        finally
        {
            page.IsLoading = false;
        }
    }

    private static WriteableBitmap ToWriteable(RenderedPage r)
    {
        var wb = new WriteableBitmap(
            new Avalonia.PixelSize(r.WidthPx, r.HeightPx),
            new Avalonia.Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using var fb = wb.Lock();
        var rowLen = r.WidthPx * 4;
        for (int y = 0; y < r.HeightPx; y++)
            System.Runtime.InteropServices.Marshal.Copy(r.Pixels, y * r.RowBytes, fb.Address + y * fb.RowBytes, rowLen);
        return wb;
    }
}

/// <summary>
/// 一段展示文本 + 可选发音（需求 1.2）：Display 原样显示（含中文注释），
/// Speak 为净化后的英文；Speak=null 时不显示 🔊。
/// </summary>
public sealed partial class SentenceItemViewModel : ObservableObject
{
    private readonly ITtsService _tts;
    private readonly IAudioPlayer _audio;
    private readonly Action<SentenceItemViewModel>? _record;

    public SentenceItemViewModel(string display, string? speak, ITtsService tts, IAudioPlayer audio,
        Action<SentenceItemViewModel>? record = null)
    {
        Text = display;
        Speak = speak;
        _tts = tts;
        _audio = audio;
        _record = record;
    }

    /// <summary>展示文本（原文，含中文/音标）。</summary>
    public string Text { get; }

    /// <summary>实际朗读文本（null = 无英文可发音）。</summary>
    public string? Speak { get; }

    public bool HasAudio => Speak is not null;

    /// <summary>有学习记录仓储才显示 📌（单测/降级场景隐藏）。</summary>
    public bool IsRecordable => _record is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordGlyph))]
    [NotifyPropertyChangedFor(nameof(RecordTip))]
    private bool _isRecorded;

    /// <summary>已记录用 ✅，未记录用 📌（同一按钮二次点击=取消记录）。</summary>
    public string RecordGlyph => IsRecorded ? "✅" : "📌";
    public string RecordTip => IsRecorded ? "已在今日学习列表 · 再点移除" : "记入今日学习列表（弹窗复习与考试从这里取词）";

    [RelayCommand]
    private void Record()
    {
        if (_record is null) return;
        _record(this); // 仓储层按 IsRecorded 旧值决定 添加/移除
        IsRecorded = !IsRecorded;
    }

    [ObservableProperty]
    private bool _isSpeaking;

    [ObservableProperty]
    private string? _error;

    [RelayCommand]
    private async Task SpeakAsync()
    {
        if (IsSpeaking || Speak is null) return;
        IsSpeaking = true;
        Error = null;
        try
        {
            var file = await _tts.SynthesizeAsync(Speak, TtsKind.Sentence, ct: default);
            await _audio.PlayAsync(file);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsSpeaking = false;
        }
    }
}
