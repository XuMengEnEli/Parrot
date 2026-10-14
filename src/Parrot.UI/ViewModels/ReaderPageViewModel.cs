using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly ReviewRepository? _review;
    /// <summary>今日已📌的词条（词或短语：📌按钮初始态 + 重复点去重，切页/重建 VM 不丢状态）。</summary>
    private readonly HashSet<string> _recordedWords = new(StringComparer.Ordinal);
    private CancellationTokenSource? _ocrCts;
    private bool _autoSwitching;    // 程序性 ViewMode 切换不算用户操作
    private string? _ocrPendingHint; // "正在后台识别"那句进行态说明，收尾时按原文精确撤销

    public ReaderPageViewModel(ITtsService tts, IAudioPlayer audio, IOcrService? ocr = null, bool restoreRecent = true,
        PomodoroPageViewModel? pomodoro = null, StudyLogRepository? studyLog = null, WordbookRepository? wordbook = null,
        ReviewRepository? review = null)
    {
        _tts = tts;
        _audio = audio;
        _ocr = ocr;
        _studyLog = studyLog;
        _wordbook = wordbook;
        _review = review;
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

    // ---------- 遮挡模式：词行只留主词，右侧释义盖灰条，看英文自测回忆 ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaskGlyph))]
    private bool _isMaskOn;

    public string MaskGlyph => IsMaskOn ? "👁" : "🙈";

    [RelayCommand]
    private void ToggleMask() => IsMaskOn = !IsMaskOn;

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
    private readonly Dictionary<PdfDocumentSource, System.Collections.Concurrent.ConcurrentDictionary<int, PageOcrData>> _spotsCache = new();
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
                ? _ocrPendingHint = $"本文档 {scannedPages.Count}/{doc.PageCount} 页为扫描件（无文本层），系统 OCR 正在后台把句内 🔊 标注到原图上（离线），稍候即可在原图上逐句点读，也可先睹为快"
                : $"本文档 {scannedPages.Count}/{doc.PageCount} 页为扫描件（无文本层，本机 OCR 不可用），已自动切到原图视图；有文字的页可在“视图→文本视图”阅读";
            SetViewModeAuto(1); // 原图=原版式，热点随 OCR 进度逐页出现
        }
        if (ocrReady && todo.Count > 0)
            _ = OcrFillAsync(doc, todo);
        else if (ScanHint == _ocrPendingHint)
            ScanHint = null; // 扫描页早识别过了：没有"稍候"这回事，别挂着进行态
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
                    var size = doc.GetPageSizePoints(page - 1);
                    var mask = MaskLayout.ComputeMaskLeftFraction(lines, size.Width, size.Height);
                    var data = new PageOcrData(OverlayLayout.BuildSpots(lines, size.Height), mask,
                        MaskLayout.ComputeOcrMaskRows(lines, mask, size.Width),
                        MaskLayout.ComputeMaskVerticalBand(lines, mask, size.Width, size.Height),
                        MaskLayout.ComputeTitleSpans(lines, mask, size.Width, size.Height));
                    ok += data.Spots.Count > 0 ? 1 : 0;
                    GetSpotsMap(doc)[page] = data;
                    if (ReferenceEquals(SelectedDocument, doc)
                        && _pagesCache.TryGetValue(doc, out var pages)
                        && page >= 1 && page <= pages.Count)
                        pages[page - 1].SetOcr(data.Spots, data.MaskLeftFraction, data.MaskRows, data.MaskBand, data.TitleSpans);
                }
                OcrStatus = done < scannedPages.Count ? $"OCR 标注原图🔊 {done}/{scannedPages.Count}…" : null;
                if (cts.IsCancellationRequested || !ReferenceEquals(SelectedDocument, doc)) break;
            }
        }
        catch (OperationCanceledException) { /* 切走文档/关页：进度条下面统一收起 */ }
        OcrStatus = null;

        if (ok > 0 && ReferenceEquals(SelectedDocument, doc)
            && !cts.IsCancellationRequested && ViewModeIndex == 1)
            await ShowNoticeAsync($"OCR 完成：已在 {ok} 页的原图上标注句内 🔊，点句子右侧的喇叭即可发音");
        else if (ReferenceEquals(SelectedDocument, doc) && ScanHint == _ocrPendingHint)
            ScanHint = null; // 一页都没标出来（整册图形页）：进行态说明同样要撤掉
    }

    /// <summary>
    /// 顶部说明条的"通知"用法：亮一会儿就自己收起，不长期占着阅读区
    /// （扫描件说明是状态、会一直在；完成/结果类只报一嗓子）。
    /// 期间若已被别的文案顶掉（切文档、手动切回文本视图）就不动它。
    /// </summary>
    private async Task ShowNoticeAsync(string text, int seconds = OcrNoticeSeconds)
    {
        ScanHint = text;
        await Task.Delay(seconds * 1000);
        if (ScanHint == text) ScanHint = null;
    }

    private const int OcrNoticeSeconds = 6;

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
        Dictionary<int, PageOcrData> fresh;
        try
        {
            fresh = await Task.Run(() =>
            {
                var m = new Dictionary<int, PageOcrData>();
                for (int i = 0; i < doc.PageCount; i++)
                {
                    var lines = doc.GetPageLines(i);
                    if (lines.Count == 0) continue;
                    var spots = OverlayLayout.BuildSpots(lines, doc.GetPageSizePoints(i).Height);
                    var size = doc.GetPageSizePoints(i);
                    var mask = MaskLayout.ComputeMaskLeftFraction(lines, size.Width, size.Height);
                    if (spots.Count == 0 && mask is null) continue;
                    m[i + 1] = new PageOcrData(spots, mask,
                        MaskLayout.ComputeMaskRows(lines, mask, size.Width),
                        MaskLayout.ComputeMaskVerticalBand(lines, mask, size.Width, size.Height),
                        MaskLayout.ComputeTitleSpans(lines, mask, size.Width, size.Height)); // 热点与遮挡边界互不绑定，有谁算出来就存谁
                }
                return m;
            });
        }
        catch { return; }
        foreach (var (page, spots) in fresh) map[page] = spots;
        ApplySpotsToCards(doc, map);
    }

    private void ApplySpotsToCards(PdfDocumentSource doc, IReadOnlyDictionary<int, PageOcrData> map)
    {
        if (!ReferenceEquals(SelectedDocument, doc)) return; // 缓存已存，只是不动当前视图
        if (!_pagesCache.TryGetValue(doc, out var pages)) return;
        foreach (var (page, data) in map)
            if (page >= 1 && page <= pages.Count)
                pages[page - 1].SetOcr(data.Spots, data.MaskLeftFraction, data.MaskRows, data.MaskBand, data.TitleSpans);
    }

    private System.Collections.Concurrent.ConcurrentDictionary<int, PageOcrData> GetSpotsMap(PdfDocumentSource doc)
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
                return new EntryViewModel(f.Display, WrapSpeak(f), this);
            }
            case PdfBlockKind.ListItem:
            case PdfBlockKind.Paragraph:
            {
                if (b.Fragments.Count == 0) return null;
                var frags = b.Fragments;
                var items = new List<SentenceItemViewModel>(frags.Count);
                for (int i = 0; i < frags.Count; i++)
                    items.Add(NewSentence(frags[i].Display, frags[i].Speak, ChineseAfter(frags, i)));
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

    /// <summary>原图热点复用同一个发音 VM（Display 不上屏，只给按钮用）；gloss 是同页紧跟其后的中文译文。</summary>
    internal SentenceItemViewModel CreateSpeak(string speak, string? gloss = null) => NewSentence(speak, speak, gloss);

    // ---------- 📌 记入当日学习（需求：在小喇叭旁边加个按钮） ----------

    /// <summary>📌 的初始态：这句要记的那批词条（短语优先）是否都已在今日表里。</summary>
    private SentenceItemViewModel NewSentence(string display, string? speak, string? gloss = null)
    {
        if (_studyLog is null) return new SentenceItemViewModel(display, speak, _tts, _audio, terms: [], gloss: gloss);
        var terms = ComputeRecordTerms(speak ?? display);
        var vm = new SentenceItemViewModel(display, speak, _tts, _audio, terms, TogglePinned, gloss);
        vm.IsRecorded = terms.Count > 0 && terms.All(_recordedWords.Contains);
        return vm;
    }

    /// <summary>
    /// 讲义里例句的译文通常单独成行（纯中文，不挂 🔊）：整句记入时这句中文就是它的释义。
    /// 从第 i 段往后收连续的中文片段，遇到下一个带英文的片段即停。
    /// </summary>
    private static string? ChineseAfter(IReadOnlyList<PdfFragment> frags, int i)
    {
        var parts = new List<string>();
        for (int j = i + 1; j < frags.Count && frags[j].Speak is null; j++)
        {
            var text = frags[j].Display.Trim();
            if (!text.Any(c => c >= 0x4e00 && c <= 0x9fff)) break;
            parts.Add(text);
        }
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    /// <summary>
    /// 📌 单击：没记则把这句的词条整批记入当天，已记则整批移除；返回新的记录态。
    /// 记哪几条只在 <see cref="ComputeRecordTerms"/> 一处决定。
    /// </summary>
    public bool TogglePinned(SentenceItemViewModel vm)
    {
        if (_studyLog is null) return vm.IsRecorded;
        var terms = vm.Terms;
        if (terms.Count == 0) return vm.IsRecorded; // 纯中文/纯数字句：没东西可记
        if (!vm.IsRecorded)
        {
            foreach (var term in terms) RecordTerm(term, vm.Text, vm.Gloss);
            return true;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        bool removed = false;
        foreach (var term in terms)
        {
            if (!_studyLog.Remove(term, today)) continue;
            _recordedWords.Remove(term);
            removed = true;
        }
        if (removed) _review?.PruneUnpinned(); // 没有任何一天记录挂靠了就退出复习队列
        return false;
    }

    /// <summary>
    /// 把一个句子/词行整批记入今日学习：批次只由 <see cref="ComputeRecordTerms"/> 决定。
    /// 📌 走 <see cref="TogglePinned"/>（能同时撤销），这条给"只记不撤"的调用方与单测。
    /// </summary>
    public void RecordSentence(string text, string note, string? gloss = null)
    {
        foreach (var term in ComputeRecordTerms(text)) RecordTerm(term, note, gloss);
    }

    /// <summary>
    /// 这一行要记哪几条：完整例句整句记入（"Leaves change colour in autumn." 记全句，
    /// 不该被词典命中切成 "in autumn" 这种碎片）；词组行/词条行才走三级判定——
    /// ① <see cref="WordbookRepository.MatchPhrases"/> 命中词典的短语（最长优先、互不重叠）；
    /// ② 整行本身就是一个搭配（讲义词表行，如 "social pressure"——教材搭配 ECDICT 常不收录）；
    /// ③ 都没有才退回代表词。"put up with" 不该只记成 "put"，"social pressure" 也不该只记成 "social"。
    /// 📌 的记入、初始 ✅ 态、悬停说明都以这里为准（全量 76 万词库实测逐句 0.083ms）。
    /// </summary>
    public IReadOnlyList<string> ComputeRecordTerms(string text)
    {
        if (PhraseMatcher.IsSentence(text)) return [text.Trim()];
        if (_wordbook is null) return Headword(text) is { } w ? [w] : [];
        var phrases = _wordbook.MatchPhrases(text);
        if (phrases.Count > 0) return phrases;
        if (PhraseMatcher.AsWholePhrase(text) is { } whole) return [whole];
        return Headword(text) is { } word ? [word] : [];
    }

    private void RecordTerm(string term, string note, string? gloss = null)
    {
        if (_studyLog is null) return;
        var today = DateOnly.FromDateTime(DateTime.Now);
        // 整句记入时词典必然查不到，释义用紧跟其后的中文行（GlossInLine 只认同一行里的中文）
        _studyLog.Add(term, _wordbook?.Lookup(term)?.Translation ?? gloss ?? GlossInLine(term, note), note, today);
        _review?.ScheduleFirst(term, today); // 钉住即起锚：首访排在 1 天后
        _recordedWords.Add(term);
    }

    private static readonly Regex CjkGloss = new(@"[\u4e00-\u9fff][\u4e00-\u9fffA-Za-z0-9，、；：（）\s]*", RegexOptions.Compiled);

    /// <summary>
    /// 词库里查不到的讲义搭配，释义就地取同一行后面的中文注释（"social class 社会阶层" → 社会阶层）。
    /// 取不到就是空——原图视图的热点文本只有英文，中文注释不在同一段里。
    /// </summary>
    private static string GlossInLine(string term, string note)
    {
        int at = note.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return "";
        return CjkGloss.Match(note, at + term.Length) is { Success: true } m ? m.Value.Trim() : "";
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
        "what", "where", "why", "how",
        "he", "him", "his", "she", "her", "they", "them", "their", "we", "us", "our", "you", "your",
        "i", "me", "my", "mine", "itself", "himself", "herself", "themselves", "oneself", "should",
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

public abstract class BlockViewModel : ObservableObject
{
    protected BlockViewModel(int page) => Page = page;
    public int Page { get; }
}

/// <summary>分页标记（保留"第 N 页"结构感）。</summary>
public sealed class PageBreakViewModel(int page) : BlockViewModel(page);

/// <summary>扫描件页占位（无文本层，可跳原图；OCR 完成后句尾 🔊 标在原图上）。</summary>
public sealed class ScannedPageViewModel(int page) : BlockViewModel(page);

/// <summary>原图叠加项：句尾按钮位置（位图像素坐标）+ 发音 VM。
/// 必须声明 INotifyPropertyChanged：MaskVisible 在遮挡开合/悬停露出时改，绑定靠它刷新。</summary>
public sealed class OverlayItemViewModel(SentenceItemViewModel audio, double x, double y) : System.ComponentModel.INotifyPropertyChanged
{
    public SentenceItemViewModel Audio { get; } = audio;
    public double X { get; } = x;
    public double Y { get; } = y;

    private bool _maskVisible = true;

    /// <summary>遮挡模式下此按钮是否可见：释义列内容带里的按钮随罩层隐藏，悬停露出带内的浮出来可点。</summary>
    public bool MaskVisible
    {
        get => _maskVisible;
        internal set
        {
            if (_maskVisible == value) return;
            _maskVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MaskVisible)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>遮挡罩层上的"露行"：该行覆盖区的位图裁片按原位整行贴在罩层上方（坐标=画布 px）。</summary>
public sealed class MaskRevealViewModel(double left, double top, double width, double height, IImage crop)
{
    public double Width { get; } = width;
    public double Height { get; } = height;
    public double Top { get; } = top;
    public IImage Crop { get; } = crop;
    public Avalonia.Thickness Margin => new(left, top, 0, 0);
}

/// <summary>标题块（英文标题带发音）。</summary>
public sealed class HeadingViewModel(string text, SentenceItemViewModel? audio) : BlockViewModel(0)
{
    public string Text { get; } = text;
    public SentenceItemViewModel? Audio { get; } = audio;
}

/// <summary>分块遮挡罩层的一块：按行块归并出的竖直覆盖段（画布 px），单元标题的空档留出不盖。</summary>
public sealed class MaskBlockViewModel(double left, double top, double width, double height)
{
    public double LeftPx { get; } = left;
    public double TopPx { get; } = top;
    public double WidthPx { get; } = width;
    public double HeightPx { get; } = height;
    public bool ShowWatermark => HeightPx >= 48; // 太窄的块放不下水印字
}

/// <summary>词条行块（词表页：英文+音标+中文一行，整行一个发音按钮）。遮挡模式下只留主词。</summary>
public sealed class EntryViewModel : BlockViewModel
{
    public EntryViewModel(string text, SentenceItemViewModel? audio, ReaderPageViewModel page) : base(0)
    {
        Text = text;
        Audio = audio;
        Headword = ExtractHeadword(text);
        _page = page;
        page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(page.IsMaskOn))
            {
                OnPropertyChanged(nameof(Masked));
                OnPropertyChanged(nameof(BarsVisible));
            }
        };
    }

    public string Text { get; }
    public SentenceItemViewModel? Audio { get; }

    /// <summary>行首主词（连续英文词，词性缩写/音标/中文前）；中文开头等解析不出为空串 → 该行不参与遮挡。</summary>
    public string Headword { get; }

    public bool Masked => Headword.Length > 0 && _page.IsMaskOn;
    private readonly ReaderPageViewModel _page;

    private bool _revealed;

    /// <summary>遮挡悬停（文本视图）：鼠标盖在灰条上时整行原样露出，移开盖回。</summary>
    public bool Revealed
    {
        get => _revealed;
        set
        {
            if (SetProperty(ref _revealed, value))
            {
                OnPropertyChanged(nameof(BarsVisible));
                if (HoverLog.Enabled)
                    HoverLog.Write($"文本视图 {(value ? "悬停露出" : "盖回")}「{Headword}」");
            }
        }
    }

    /// <summary>遮挡态的可视部分：主词 + 灰条；悬停露出整行时隐藏。</summary>
    public bool BarsVisible => Masked && !_revealed;

    /// <summary>主词 = 行首连续的"纯英文词"（可含 - ' ·）；遇到词性缩写(n./adj.)、音标(/)、括号或中文即停。</summary>
    private static string ExtractHeadword(string line)
    {
        var words = new List<string>();
        foreach (var tok in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (words.Count >= 5 || !char.IsAsciiLetter(tok[0])) break;
            bool pure = true;
            foreach (var c in tok)
            {
                if (char.IsAsciiLetter(c) || c is '-' or '\'' or '·') continue;
                pure = false;
                break;
            }
            if (!pure) break;
            words.Add(tok);
        }
        return string.Join(' ', words);
    }
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

/// <summary>悬停链路诊断日志：Debug 构建默认开（PARROT_HOVER_DEBUG=0 可关），Release 只认显式 =1；写 /tmp/parrot_hover.log。</summary>
internal static class HoverLog
{
    public static readonly bool Enabled =
#if DEBUG
        Environment.GetEnvironmentVariable("PARROT_HOVER_DEBUG") != "0";
#else
        Environment.GetEnvironmentVariable("PARROT_HOVER_DEBUG") == "1";
#endif
    private const string LogPath = "/tmp/parrot_hover.log";
    private static readonly object Gate = new();

    public static void Write(string line)
    {
        lock (Gate)
        {
            try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); }
            catch { /* 诊断日志失败不影响功能 */ }
        }
    }
}

/// <summary>阅读页里的一页：懒渲染 + 可见性驱动（虚拟化），改 DPI 重渲。仅"原图模式"使用。</summary>
public sealed partial class PageCardViewModel : ObservableObject
{
    private readonly ReaderPageViewModel _owner;
    private CancellationTokenSource? _cts;
    private bool _everRendered;
    private readonly double _pageHeightPt;
    private readonly double _pageWidthPt;
    private IReadOnlyList<SpeakSpot> _spots = [];
    private IReadOnlyList<PdfRowBox>? _maskRows;
    private IReadOnlyList<MaskRule>? _underlines; // 位图检出的水平分隔线（讲义表格词条框），悬停整框露出用
    private IReadOnlyList<PdfRowBox>? _revealRows; // 词条级合并行框（悬停露出单位=整个词条）；null = 待懒算
    private PdfRowBox? _revealRow;
    private IReadOnlyList<MaskBlockViewModel>? _maskBlocks; // 分块罩层；null = 无行框分块，退回整带单块
    private PageCardViewModel? _continuationPeer; // 跨页词条联动时我方点亮、待随盖回清除的邻居卡
    private bool _revealFromPeer; // 当前露行是邻居卡联动亮起的（而不是用户悬停在自己页上）

    public PageCardViewModel(PdfDocumentSource document, int pageIndex, ReaderPageViewModel owner)
    {
        Document = document;
        PageIndex = pageIndex;
        _owner = owner;
        owner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(owner.IsMaskOn))
            {
                // 三个可见性都依赖 IsMaskOn：只通知 MaskCoverVisible 的话，"先开页面后开遮挡"时
                // 罩层绑定（PageHost 绑的是下面两个）收不到变更，罩层永远不出现
                OnPropertyChanged(nameof(MaskCoverVisible));
                OnPropertyChanged(nameof(MaskBlocksVisible));
                OnPropertyChanged(nameof(MaskCoverWholeVisible));
                ClearMaskReveal(); // 罩层一关，露词必须跟着消失
                UpdateOverlayMasking(); // MaskReveal 已空时上面不会发通知，这里必须显式重算按钮显隐
            }
        };
        HasText = document.PageHasText(pageIndex);
        var size = document.GetPageSizePoints(pageIndex);
        AspectRatio = size.Width / Math.Max(1, size.Height);
        _pageHeightPt = Math.Max(1, size.Height);
        _pageWidthPt = Math.Max(1, size.Width);
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

    partial void OnImageWidthPxChanged(double value)
    {
        OnPropertyChanged(nameof(MaskCoverWidth));
        RebuildMaskBlocks(); // 块几何是画布 px，位图就位/缩放后要重算
        UpdateOverlayMasking(); // 边界/内容带也是画布 px，一并重算
    }

    partial void OnImageHeightPxChanged(double value)
    {
        OnPropertyChanged(nameof(MaskCoverTopPx));
        OnPropertyChanged(nameof(MaskCoverHeightPx));
        OnPropertyChanged(nameof(MaskCoverMargin));
        RebuildMaskBlocks();
        UpdateOverlayMasking();
    }

    /// <summary>句尾 🔊 热点（原图视图叠加层；坐标随 RenderDpi 换算）。</summary>
    public ObservableCollection<OverlayItemViewModel> Overlays { get; } = [];

    private double? _maskLeftFraction;
    private (double Top, double Bottom)? _maskBand; // 竖直内容带（页高分数，y-up）；null = 盖整高
    private IReadOnlyList<(double Top, double Bottom)>? _titleSpans; // 罩层空档只在这些标题行处切开

    /// <summary>OCR 推算出的释义列起始（页宽分数，遮挡罩层左缘）；null = 推算不出。</summary>
    public double? MaskLeftFraction => _maskLeftFraction;

    /// <summary>原图遮挡罩层（不透明）：开且边界可信才显示。</summary>
    public bool MaskCoverVisible => _owner.IsMaskOn && _maskLeftFraction.HasValue;

    /// <summary>罩层宽度 = 页宽 − 释义列起始（位图像素，Viewbox 随列宽统一缩放）。</summary>
    public double MaskCoverWidth => (1 - (_maskLeftFraction ?? 0)) * ImageWidthPx;

    /// <summary>罩层上缘距画布顶（px）：内容带顶之上不盖（页眉露出来）；无带 = 盖整高。</summary>
    public double MaskCoverTopPx => _maskBand is { } band ? (1 - band.Top) * ImageHeightPx : 0;

    /// <summary>罩层高度（px）：内容带高（页眉/页码留在外面）；无带 = 整页高。</summary>
    public double MaskCoverHeightPx
        => _maskBand is { } band ? (band.Top - band.Bottom) * ImageHeightPx : ImageHeightPx;

    public Avalonia.Thickness MaskCoverMargin => new(0, MaskCoverTopPx, 0, 0);

    /// <summary>分块罩层（按行块归并，单元标题空档露出）；null = 退回整带单块罩层。</summary>
    public IReadOnlyList<MaskBlockViewModel>? MaskBlocks => _maskBlocks;

    /// <summary>分块罩层可用：至少有一块可盖。</summary>
    public bool MaskBlocksVisible => MaskCoverVisible && _maskBlocks is { Count: > 0 };

    /// <summary>整带单块罩层（退回用）：有边界但推不出行框分块。</summary>
    public bool MaskCoverWholeVisible => MaskCoverVisible && _maskBlocks is not { Count: > 0 };

    /// <summary>悬停露词：光标所在词的原位裁片；null = 当前没有露词。</summary>
    [ObservableProperty]
    private MaskRevealViewModel? _maskReveal;

    partial void OnMaskRevealChanged(MaskRevealViewModel? value) => UpdateOverlayMasking();

    internal void SetOcr(IReadOnlyList<SpeakSpot> spots, double? maskLeftFraction,
        IReadOnlyList<PdfRowBox>? maskRows = null, (double Top, double Bottom)? maskBand = null,
        IReadOnlyList<(double Top, double Bottom)>? titleSpans = null)
    {
        _spots = spots;
        _maskLeftFraction = maskLeftFraction;
        _maskRows = maskRows;
        _maskBand = maskBand;
        _titleSpans = titleSpans;
        _revealRows = null;
        _revealRow = null;
        _continuationPeer = null;
        _revealFromPeer = false;
        MaskReveal = null;
        ApplySpots();
        OnPropertyChanged(nameof(MaskCoverVisible));
        OnPropertyChanged(nameof(MaskCoverWidth));
        OnPropertyChanged(nameof(MaskLeftFraction));
        OnPropertyChanged(nameof(MaskCoverTopPx));
        OnPropertyChanged(nameof(MaskCoverHeightPx));
        OnPropertyChanged(nameof(MaskCoverMargin));
        RebuildMaskBlocks();
    }

    private void ApplySpots()
    {
        double ppp = _owner.RenderDpi / 72.0;
        Overlays.Clear();
        foreach (var s in _spots)
            Overlays.Add(new OverlayItemViewModel(
                _owner.CreateSpeak(s.Speak, s.Gloss),
                s.RightPt * ppp + 6,                 // 句尾右缘外一点
                (_pageHeightPt - s.MidYPt) * ppp - 14)); // y-up→y-down，按钮垂直居中于行
        UpdateOverlayMasking();
    }

    /// <summary>遮挡模式下按钮逐个自管显隐：释义列内容带里的按钮随罩层隐藏（罩层面干净），
    /// 悬停露出带内的浮出来可点（否则掀开了也点不到例句发音）。替代"罩层压按钮"的整层画法，
    /// 因为露出裁片比罩层更高，单靠叠放顺序无法同时满足"静止被盖、露出可点"。</summary>
    private void UpdateOverlayMasking()
    {
        double boundaryPx = (_maskLeftFraction ?? 0) * ImageWidthPx;
        double bandTop = MaskCoverTopPx, bandBottom = bandTop + MaskCoverHeightPx;
        var reveal = MaskReveal;
        foreach (var o in Overlays)
        {
            double cy = o.Y + 14; // 按钮中心（ApplySpots 里按行中线放置）
            bool inCoverZone = _owner.IsMaskOn && _maskLeftFraction.HasValue
                && cy > bandTop && cy < bandBottom && o.X + 14 >= boundaryPx;
            bool inRevealBand = reveal is { } rv
                && cy >= rv.Top - 4 && cy <= rv.Top + rv.Height + 4;
            o.MaskVisible = !inCoverZone || inRevealBand;
        }
    }

    /// <summary>按行块重建分块罩层几何（画布 px）：空档只在单元/节标题行处切开露出；位图尺寸变化时重建。</summary>
    private void RebuildMaskBlocks()
    {
        IReadOnlyList<MaskBlockViewModel>? blocks = null;
        if (_maskRows is { Count: > 0 } rows && _maskLeftFraction is { } fraction)
        {
            var list = new List<MaskBlockViewModel>();
            foreach (var b in MaskLayout.ComputeCoverBlocks(rows, _maskBand, _pageHeightPt, _titleSpans))
                list.Add(new MaskBlockViewModel(
                    fraction * ImageWidthPx,
                    (1 - b.Top / _pageHeightPt) * ImageHeightPx,
                    (1 - fraction) * ImageWidthPx,
                    (b.Top - b.Bottom) / _pageHeightPt * ImageHeightPx));
            if (list.Count > 0) blocks = list;
        }
        _maskBlocks = blocks;
        OnPropertyChanged(nameof(MaskBlocks));
        OnPropertyChanged(nameof(MaskBlocksVisible));
        OnPropertyChanged(nameof(MaskCoverWholeVisible));
    }

    /// <summary>悬停露出的行框（下划线围出的整词条框优先——含框内思维导图等无文字区；推不出再退
    /// 主词锚点分组、逐行）。懒算：热点/位图/分隔线在悬停时才都齐。</summary>
    private IReadOnlyList<PdfRowBox> RevealRows
        => _revealRows ??= (_underlines is { Count: > 0 }
                ? MaskLayout.ComputeEntryRowsByUnderlines(_maskRows!, _underlines, _maskLeftFraction,
                    _pageWidthPt, _pageHeightPt, _maskBand)
                : null)
            ?? MaskLayout.ComputeEntryRows(_maskRows!, _spots, _maskLeftFraction, _pageWidthPt,
                _pageHeightPt, _maskBand)
            ?? _maskRows!;

    /// <summary>鼠标在罩层上移动：按光标高度找到所属词条，整个词条的原位裁片露出（多义词的几条释义一起看）。</summary>
    public void UpdateMaskReveal(double canvasX, double canvasY)
    {
        if (!_owner.IsMaskOn || _maskLeftFraction is not { } fraction
            || _maskRows is null || Image is null)
        {
            HoverVerdict("拦截（前提不满足）");
            return;
        }
        double ppp = _owner.RenderDpi / 72.0;
        double xPt = canvasX / ppp, yUp = _pageHeightPt - canvasY / ppp;
        if (xPt < fraction * _pageWidthPt - 2)
        {
            HoverVerdict($"光标在主词列 xPt≈{Math.Round(xPt)}pt 边界≈{fraction * _pageWidthPt:F0}pt");
            ClearMaskReveal(); return;
        } // 光标不在罩层上（主词列/按钮区）
        if (_maskBand is { } band
            && (yUp > band.Top * _pageHeightPt || yUp < band.Bottom * _pageHeightPt))
        {
            HoverVerdict("内容带外（页眉/页码区）");
            ClearMaskReveal(); return;
        } // 页眉/页码露在外面，罩层不响应
        var row = FindMaskRow(yUp);
        if (row is null)
        {
            HoverVerdict($"行未命中 yUp≈{yUp:F0}pt");
            ClearMaskReveal(); return;
        }
        if (MaskReveal is not null && ReferenceEquals(_revealRow, row))
        {
            _revealFromPeer = false; // 用户接管：联动亮起的露出转为自己悬停，邻居盖回时不再牵连
            return; // 还是同一行，保持现状
        }
        _revealRow = row;
        _revealFromPeer = false;
        MaskReveal = BuildReveal(row, fraction, ppp);
        HoverVerdict($"命中词条「{(row.Text.Length > 48 ? row.Text[..48] + "…" : row.Text)}」");
        UpdateContinuationReveal(row);
    }

    public void ClearMaskReveal()
    {
        ClearContinuationPeer(); // 我方的露出撤了，联动亮起的邻居页露出也要跟着撤
        if (MaskReveal is null) return;
        _revealRow = null;
        MaskReveal = null;
        HoverVerdict("盖回");
    }

    /// <summary>跨页词条联动（图二）：词条排满到页底时下半截接在下一页页首——悬停贴着带底的本页末组，
    /// 就把下一页贴着带顶的首组一起亮起；反向对称（悬停页首组亮起上页末组）。两头都贴边才联动，判据保守。</summary>
    private void UpdateContinuationReveal(PdfRowBox row)
    {
        ClearContinuationPeer();
        if (_maskBand is not { } band || RevealRows.Count == 0) return;
        var rows = RevealRows;
        bool last = ReferenceEquals(row, rows[^1]), firstRow = ReferenceEquals(row, rows[0]);
        if (!last && !firstRow) return;
        if (last && TryContinue(rows[^1].Bottom, band.Bottom * _pageHeightPt, NextCard, revealFirst: true)) return;
        if (firstRow)
        {
            // 我方首组若带着主词锚，说明词条从本页顶头开始（product 这类），不是上页词条的续段 → 不联动
            if (HasHeadwordAnchor(rows[0]))
            {
                if (HoverDebugEnabled)
                    HoverLog.Write("联动判否：我方首组左列有主词锚（顶头新词条非续段）→ 不亮上页");
            }
            else
                TryContinue(rows[0].Top, band.Top * _pageHeightPt, PrevCard, revealFirst: false);
        }
    }

    /// <summary>组范围里有没有主词列发音热点（主词锚）：续段（词条被页切断的下半截）左列是空的，
    /// 而顶头新词条（product 这类）左列必有主词热点——跨页联动靠它区分"真延续"与"恰好在页顶的新词条"。</summary>
    private bool HasHeadwordAnchor(PdfRowBox group)
    {
        if (_maskLeftFraction is not { } f) return true; // 判不出边界就保守当有（宁可不联动）
        double boundary = f * _pageWidthPt;
        return _spots.Any(s => s.RightPt < boundary - 2
            && s.MidYPt >= group.Bottom - 4 && s.MidYPt <= group.Top + 4);
    }

    private bool TryContinue(double myEdge, double bandEdge, PageCardViewModel? peer, bool revealFirst)
    {
        if (peer is null || Math.Abs(myEdge - bandEdge) > 12)
        {
            if (HoverDebugEnabled)
                HoverLog.Write($"联动判否：我方{(revealFirst ? "首组顶" : "末组底")}={myEdge:F1} 带边={bandEdge:F1} 差={Math.Abs(myEdge - bandEdge):F1}pt{(peer is null ? "，邻居卡未加载" : "")} → 不亮{(revealFirst ? "下" : "上")}页");
            return false;
        }
        if (!peer.TryRevealContinuation(revealFirst))
        {
            if (HoverDebugEnabled)
                HoverLog.Write($"联动判否：邻居{(revealFirst ? "下" : "上")}页贴边组推不出（未加载/带边不符）→ 不亮");
            return false;
        }
        if (HoverDebugEnabled)
            HoverLog.Write($"联动亮起：我方{(revealFirst ? "末组底" : "首组顶")}={myEdge:F1} 带边={bandEdge:F1} → {(revealFirst ? "下" : "上")}页贴边组露出");
        _continuationPeer = peer;
        return true;
    }

    /// <summary>邻居卡联动调用：亮起我这边贴着内容带边的那组（上页词条的延续 / 下页词条的起点）。</summary>
    internal bool TryRevealContinuation(bool revealFirst)
    {
        if (!_owner.IsMaskOn || _maskLeftFraction is not { } fraction
            || _maskRows is null || Image is null || _maskBand is not { } band
            || RevealRows.Count == 0)
            return false;
        var row = revealFirst ? RevealRows[0] : RevealRows[^1];
        double edge = revealFirst ? row.Top : row.Bottom;
        double bandEdge = (revealFirst ? band.Top : band.Bottom) * _pageHeightPt;
        if (Math.Abs(edge - bandEdge) > 12) return false;
        if (revealFirst && HasHeadwordAnchor(row))
        {
            // 邻居页的首组带着主词锚 = 顶头新词条，不是上一页词条的续段 → 不亮
            if (HoverDebugEnabled)
                HoverLog.Write($"联动判否：下页首组左列有主词锚「{(row.Text.Length > 20 ? row.Text[..20] + "…" : row.Text)}」（顶头新词条非延续）→ 不亮");
            return false;
        }
        ClearContinuationPeer(); // 自己先前联动的更远一页随之撤掉，露出链不断在半空
        _revealFromPeer = true;
        _revealRow = row;
        MaskReveal = BuildReveal(row, fraction, _owner.RenderDpi / 72.0);
        return true;
    }

    private void ClearContinuationPeer()
    {
        if (_continuationPeer is not { } peer) return;
        _continuationPeer = null;
        peer.ClearPeerReveal();
    }

    private void ClearPeerReveal()
    {
        if (!_revealFromPeer) return;
        _revealFromPeer = false;
        _revealRow = null;
        MaskReveal = null;
    }

    private PageCardViewModel? NextCard
        => _owner.CurrentPages is { } pages && PageIndex + 1 < pages.Count ? pages[PageIndex + 1] : null;

    private PageCardViewModel? PrevCard
        => _owner.CurrentPages is { } pages && PageIndex - 1 >= 0 ? pages[PageIndex - 1] : null;

    private PdfRowBox? FindMaskRow(double yUpPt)
    {
        // 行槽 = 相邻词条块文字带的中点分割：行间/行尾空白也归属所在词条，整片罩层悬停都有回应
        var sorted = RevealRows.OrderBy(r => r.Bottom).ToList(); // y-up：自下而上
        for (int i = 0; i < sorted.Count; i++)
        {
            double lower = i == 0 ? double.NegativeInfinity : (sorted[i - 1].Top + sorted[i].Bottom) / 2;
            double upper = i == sorted.Count - 1 ? double.PositiveInfinity : (sorted[i].Top + sorted[i + 1].Bottom) / 2;
            if (yUpPt >= lower && yUpPt <= upper) return sorted[i];
        }
        return null;
    }

    private MaskRevealViewModel BuildReveal(PdfRowBox w, double fraction, double ppp)
    {
        double boundary = fraction * _pageWidthPt;
        // 行条带横跨整个罩层（左缘=罩层边界，右缘=页右缘），上下取行文字带 ±2pt：
        // 观感是"这一行的遮挡整条掀开"，而不是浮在页中间的一小截
        double l = boundary, r = _pageWidthPt - 1;
        double b = Math.Max(w.Bottom - 2, 1), t = Math.Min(w.Top + 2, _pageHeightPt - 1);
        var ps = Image!.PixelSize; // 画布 px == 位图 px（同一 RenderDpi），直接按像素裁
        int x0 = Math.Clamp((int)(l * ppp), 0, ps.Width - 1);
        int y0 = Math.Clamp((int)((_pageHeightPt - t) * ppp), 0, ps.Height - 1);
        int cw = Math.Clamp((int)Math.Ceiling((r - l) * ppp), 1, ps.Width - x0);
        int ch = Math.Clamp((int)Math.Ceiling((t - b) * ppp), 1, ps.Height - y0);
        var crop = new CroppedBitmap(Image, new Avalonia.PixelRect(x0, y0, cw, ch));
        return new MaskRevealViewModel(l * ppp, (_pageHeightPt - t) * ppp, cw, ch, crop);
    }

    /// <summary>截图 harness 用：模拟"悬停在文本包含指定内容的行上"，返回是否命中。</summary>
    internal bool DebugRevealWord(string contains)
    {
        if (!_owner.IsMaskOn || _maskLeftFraction is not { } || _maskRows is null) return false;
        var w = RevealRows.FirstOrDefault(x => x.Text.Contains(contains, StringComparison.OrdinalIgnoreCase));
        if (w is null) return false;
        double ppp = _owner.RenderDpi / 72.0;
        UpdateMaskReveal((w.Left + w.Right) / 2 * ppp, (_pageHeightPt - (w.Top + w.Bottom) / 2) * ppp);
        return MaskReveal is not null;
    }

    // ---- 悬停链路诊断（设施见 HoverLog；节流状态在本卡实例上）----
    internal static readonly bool HoverDebugEnabled = HoverLog.Enabled;
    private long _lastArrivalTick = -10_000;
    private string _lastHoverState = "";

    /// <summary>真实鼠标事件入口：诊断模式下先记"事件到达"，再走正常处理。</summary>
    internal void DebugPointerMoved(double x, double y)
    {
        if (HoverDebugEnabled)
        {
            var now = Environment.TickCount64;
            if (now - _lastArrivalTick >= 300)
            {
                _lastArrivalTick = now;
                HoverLog.Write($"到达 p{PageIndex + 1} ({x:F0},{y:F0}) 《{Document.Title}》");
            }
        }
        UpdateMaskReveal(x, y);
    }

    private void HoverVerdict(string reason)
    {
        if (!HoverDebugEnabled) return;
        var msg = $"p{PageIndex + 1} {reason} mask={_owner.IsMaskOn} 边界={_maskLeftFraction:0.###} 行数={_maskRows?.Count.ToString() ?? "无"} 位图={(Image is null ? "无" : "有")}";
        if (_lastHoverState == msg) return;
        _lastHoverState = msg;
        HoverLog.Write(msg);
    }

    /// <summary>截图 harness 用：dump 罩层行框与发音热点（诊断词条分组）。</summary>
    internal string DebugDumpMask()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"页 {PageIndex + 1} 边界={_maskLeftFraction:0.###} 带={(_maskBand is { } b ? $"{b.Top:0.##}/{b.Bottom:0.##}" : "无")} 行数={_maskRows?.Count ?? 0} 热点数={_spots.Count} 标题区间={_titleSpans?.Count ?? 0}{(_titleSpans is { Count: > 0 } ts ? " @" + string.Join(",", ts.Select(s => $"{s.Top:0.#}-{s.Bottom:0.#}")) : "")}");
        if (_maskRows is { } rows)
            for (int i = 0; i < rows.Count; i++)
                sb.AppendLine($"  行[{i:00}] T={rows[i].Top:F1} B={rows[i].Bottom:F1}「{rows[i].Text}」");
        if (_maskBlocks is { } blocks)
            foreach (var blk in blocks)
                sb.AppendLine($"  块 L={blk.LeftPx:F1} T={blk.TopPx:F1} W={blk.WidthPx:F1} H={blk.HeightPx:F1} 印={blk.ShowWatermark}");
        var reveal = RevealRows;
        if (reveal.Count > 0 && reveal.Count < _maskRows?.Count)
        {
            for (int i = 0; i < reveal.Count; i++)
                sb.AppendLine($"  组[{i:00}] T={reveal[i].Top:F1} B={reveal[i].Bottom:F1}「{(reveal[i].Text.Length > 36 ? reveal[i].Text[..36] + "…" : reveal[i].Text)}」");
            if (_maskLeftFraction is { } fNow)
            {
                var anchorsNow = _spots.Where(s => s.RightPt < fNow * _pageWidthPt - 2)
                    .Select(s => s.MidYPt).OrderByDescending(y => y).ToList();
                var fresh = MaskLayout.ComputeEntryRows(_maskRows, _spots, _maskLeftFraction, _pageWidthPt, _pageHeightPt, _maskBand);
                sb.AppendLine($"  缓存组数={reveal.Count} 现算组数={fresh?.Count.ToString() ?? "null"} 锚点({anchorsNow.Count})={string.Join(",", anchorsNow.Select(y => y.ToString("F1")))}");
            }
        }
        else if (reveal.Count >= _maskRows?.Count)
            sb.AppendLine($"  组=逐行退回（{reveal.Count}）");
        foreach (var s in _spots.OrderByDescending(s => s.MidYPt))
            sb.AppendLine($"  热点 R={s.RightPt:F0} MidY={s.MidYPt:F1}「{s.Speak}」");
        foreach (var ln in Document.GetPageLines(PageIndex).OrderByDescending(l => l.Top))
            sb.AppendLine($"  原始行 T={ln.Top:F1} B={ln.Bottom:F1} L={ln.Left:F1} R={ln.Right:F1} F={ln.FontSize:0.#}「{(ln.Text.Length > 40 ? ln.Text[..40] : ln.Text)}」");
        return sb.ToString();
    }

    /// <summary>截图 harness 用：返回"悬停到文本包含指定内容的行"的画布坐标（供真实路由事件注入）。</summary>
    internal Avalonia.Point? DebugGetRowPoint(string contains)
    {
        if (!_owner.IsMaskOn || _maskLeftFraction is not { } || _maskRows is null)
        {
            HoverLog.Write($"DebugGetRowPoint：前提不满足 mask={_owner.IsMaskOn} 边界={_maskLeftFraction:0.###} 行数={_maskRows?.Count.ToString() ?? "null"} 文档《{Document.Title}》p{PageIndex + 1}");
            return null;
        }
        var w = RevealRows.FirstOrDefault(x => x.Text.Contains(contains, StringComparison.OrdinalIgnoreCase));
        if (w is null)
        {
            HoverLog.Write($"DebugGetRowPoint：没找到含「{contains}」的行，共 {_maskRows.Count} 行，首行「{_maskRows[0].Text}」");
            return null;
        }
        double ppp = _owner.RenderDpi / 72.0;
        return new Avalonia.Point((w.Left + w.Right) / 2 * ppp, (_pageHeightPt - (w.Top + w.Bottom) / 2) * ppp);
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
        ClearMaskReveal(); // 裁片源自旧位图，位图一丢露词跟着撤
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
            // 分隔线检测与热点到达顺序不定（文本层页先 SetOcr、扫描页先渲染），谁后到谁重置懒算缓存
            page._underlines = MaskLayout.DetectUnderlines(
                rendered.Pixels, rendered.WidthPx, rendered.HeightPx, rendered.RowBytes, _owner.RenderDpi / 72.0);
            page._revealRows = null;
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
    private readonly Func<SentenceItemViewModel, bool>? _record;

    public SentenceItemViewModel(string display, string? speak, ITtsService tts, IAudioPlayer audio,
        IReadOnlyList<string> terms, Func<SentenceItemViewModel, bool>? record = null, string? gloss = null)
    {
        Text = display;
        Speak = speak;
        _tts = tts;
        _audio = audio;
        _record = record;
        Terms = terms;
        Gloss = gloss;
    }

    /// <summary>这句要记入的那批词条（短语优先，可能多条）：创建方算好后一次性注入，不可变。</summary>
    public IReadOnlyList<string> Terms { get; }

    /// <summary>紧随其后的中文译文行（整句记入时当释义用），没有则 null。</summary>
    public string? Gloss { get; }

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

    /// <summary>已记录用 ✅，未记录用 📌（同一按钮二次点击=取消整批记录）。</summary>
    public string RecordGlyph => IsRecorded ? "✅" : "📌";

    /// <summary>悬停说清"这句会记哪几条"：多条短语一起记、一起撤。</summary>
    public string RecordTip => Terms.Count switch
    {
        0 => IsRecorded ? "已在今日学习列表 · 再点移除"
                        : "记入今日学习列表（句中的词典短语优先，弹窗复习与考试从这里取词）",
        1 => IsRecorded ? $"已记入今日学习：{Terms[0]} · 再点移除" : $"记入今日学习：{Terms[0]}",
        _ => IsRecorded ? $"已记入今日学习 {Terms.Count} 条：{string.Join(" · ", Terms)} · 再点一起移除"
                        : $"记入今日学习 {Terms.Count} 条：{string.Join(" · ", Terms)}",
    };

    [RelayCommand]
    private void Record()
    {
        if (_record is null) return;
        // 记入还是撤销、记哪几条全由阅读器定，返回值即新的记录态（没词条可记时不会被点成 ✅）
        IsRecorded = _record(this);
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
