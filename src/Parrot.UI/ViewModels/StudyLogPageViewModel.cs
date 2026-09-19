using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parrot.Core.Abstractions;
using Parrot.Core.Review;
using Parrot.Data;

namespace Parrot.UI.ViewModels;

/// <summary>
/// 学习记录页（需求 #2 + 记忆曲线）：一个页面两个作用域——
/// 「📌 每日学习」＝🔊 旁记进来的词按天归档（左＝日期列表，右＝所选日期明细）；
/// 「🔁 每日复习」＝按艾宾浩斯固定间隔排出来的当日到期队列 + 后续排期日历。
/// 进入复习 Tab 即把到期词整体推进一档（不需要用户评分），同日重复刷新不重复推进。
/// </summary>
public sealed partial class StudyLogPageViewModel : PageViewModelBase
{
    private readonly StudyLogRepository _log;
    private readonly ReviewRepository? _review;
    private readonly ITtsService? _tts;
    private readonly IAudioPlayer? _audio;

    public StudyLogPageViewModel(StudyLogRepository log, ITtsService? tts = null, IAudioPlayer? audio = null,
        ReviewRepository? review = null)
    {
        _log = log;
        _tts = tts;
        _audio = audio;
        _review = review;
        Refresh();
    }

    public override string Title => "学习记录";
    public override string Glyph => "📒";

    /// <summary>0＝每日学习（📌 记录），1＝每日复习（记忆曲线到期队列）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStudyTab))]
    [NotifyPropertyChangedFor(nameof(IsReviewTab))]
    [NotifyPropertyChangedFor(nameof(Headline))]
    [NotifyPropertyChangedFor(nameof(Subline))]
    [NotifyPropertyChangedFor(nameof(StatValue))]
    [NotifyPropertyChangedFor(nameof(StatLabel))]
    private int _selectedTab;

    public bool IsStudyTab => SelectedTab == 0;
    public bool IsReviewTab => SelectedTab == 1;

    /// <summary>切作用域＝换数据源也换文案，避免"标题说今日已学习、下面却是复习队列"。</summary>
    partial void OnSelectedTabChanged(int value) => Refresh();

    public ObservableCollection<StudyDayItem> Days { get; } = [];
    public ObservableCollection<StudyWordItem> Words { get; } = [];
    public ObservableCollection<ReviewWordItem> TodayDue { get; } = [];
    public ObservableCollection<AgendaItem> Agenda { get; } = [];

    /// <summary>今日之后还要见面的排期总数（复习日历底部一句话）。</summary>
    [ObservableProperty]
    private int _upcomingCount;

    /// <summary>队列里因答错被回退过的词数（统计条上单独报一格）。</summary>
    [ObservableProperty]
    private int _lapsedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private StudyDayItem? _selectedDay;

    public bool HasSelection => SelectedDay is not null;

    [ObservableProperty]
    private string _ttsError = "";

    [ObservableProperty]
    private int _todayCount;

    /// <summary>Avalonia 的 ListBox 没有 EmptyContent：空态用覆盖层，靠这个标志切换。</summary>
    [ObservableProperty]
    private bool _hasNoDays;

    /// <summary>今日无到期词时的说明（有词时为空串，XAML 按非空显示）。</summary>
    [ObservableProperty]
    private string _reviewEmptyHint = "";

    /// <summary>后续排期日历是否为空（左卡空态覆盖层用）。</summary>
    [ObservableProperty]
    private bool _hasNoAgenda;

    /// <summary>今日队列有没有词（Avalonia 的 ListBox 没有 EmptyContent，靠它切居中空态）。</summary>
    [ObservableProperty]
    private bool _hasDue;

    /// <summary>复习 Tab 顶部一句话统计：今日几个 / 其中答错过的 / 后续还排着多少。</summary>
    [ObservableProperty]
    private string _reviewSummary = "";

    public string Headline => IsReviewTab ? "每日复习" : "学习记录";

    public string Subline => IsReviewTab
        ? "📌 钉住的词按 1 / 2 / 4 / 7 / 15 / 30 天自动回访，走完六档后每 60 天一次——打开这个列表即算过了一轮"
        : "阅读时点句子旁的 📌，单词即记入当天列表——复习队列与拼写考试都从这里取词";

    public string StatValue => IsReviewTab ? TodayDue.Count.ToString() : TodayCount.ToString();
    public string StatLabel => IsReviewTab ? "今日待复习" : "今日已学习";

    [RelayCommand]
    private void Reload() => Refresh();

    public void Refresh()
    {
        var now = DateOnly.FromDateTime(DateTime.Now);
        if (IsReviewTab) RefreshReview(now);
        else RefreshStudy(now);
    }

    private void RefreshStudy(DateOnly now)
    {
        var keep = SelectedDay?.Day;
        Days.Clear();
        foreach (var d in _log.Days()) Days.Add(new StudyDayItem(d.Day, d.Count));
        TodayCount = _log.WordsOfDay(now).Count;
        HasNoDays = Days.Count == 0;
        SelectedDay = keep is not null ? Days.FirstOrDefault(x => x.Day == keep) : Days.FirstOrDefault();
        LoadWords();
    }

    /// <summary>
    /// 复习侧刷新：OpenTodayQueue 一步做完"把到期词整体推进一档 + 返回今天该见的词"，
    /// 所以列表里"下次"那一列显示的就是这一轮之后的日期；同日反复刷新不会重复推进。
    /// </summary>
    private void RefreshReview(DateOnly now)
    {
        if (_review is null)
        {
            TodayDue.Clear();
            Agenda.Clear();
            UpcomingCount = LapsedCount = 0;
            HasNoAgenda = true;
            ReviewSummary = "";
            ReviewEmptyHint = "复习排期不可用";
            return;
        }

        TodayDue.Clear();
        foreach (var r in _review.OpenTodayQueue(now))
            TodayDue.Add(new ReviewWordItem(r.Word, r.Meaning, r.Note, r.Visits, r.Due, r.Lapses, now));
        LapsedCount = TodayDue.Count(x => x.Lapses > 0);

        Agenda.Clear();
        int upcoming = 0;
        foreach (var a in _review.Agenda(now))
        {
            Agenda.Add(new AgendaItem(a.Due, a.Count, now));
            upcoming += a.Count;
        }
        UpcomingCount = upcoming;
        TodayCount = _log.WordsOfDay(now).Count; // 📌 Tab 的计数常驻，切回去不必再查一次

        ReviewEmptyHint = TodayDue.Count == 0
            ? upcoming == 0
                ? "还没有排期：阅读时点句子旁的 📌 记几个词，明天开始第一轮"
                : "今天没有到期的词 · 左侧日历是接下来的安排"
            : "";
        HasDue = TodayDue.Count > 0;

        HasNoAgenda = Agenda.Count == 0;
        OnPropertyChanged(nameof(StatValue)); // 绑的是 TodayDue.Count，集合变化不自通知
        // 空队列时由空态那一句话说明（统计条与它同屏会重复），有词时统计条才报数
        ReviewSummary = TodayDue.Count == 0
            ? ""
            : $"今日 {TodayDue.Count} 个" +
              (LapsedCount > 0 ? $" · 其中答错过 {LapsedCount} 个" : "") +
              (upcoming > 0 ? $" · 后续还排着 {upcoming} 个" : "");
    }

    partial void OnSelectedDayChanged(StudyDayItem? value) => LoadWords();

    private void LoadWords()
    {
        Words.Clear();
        if (SelectedDay is null || !DateOnly.TryParseExact(SelectedDay.Day, "yyyy-MM-dd", out var day)) return;
        foreach (var w in _log.WordsOfDay(day))
            Words.Add(new StudyWordItem(w.Word, w.Meaning, w.Note));
    }

    /// <summary>查看：选中该天（右侧即出明细）。也供列表"查看"按钮点击用。</summary>
    [RelayCommand]
    private void ViewDay(StudyDayItem? item)
    {
        if (item is not null) SelectedDay = item;
    }

    /// <summary>移除整天：两步确认（第一次点击变"再点确认"，3 秒后回弹）。</summary>
    [RelayCommand]
    private void RemoveDay(StudyDayItem? item)
    {
        if (item is null || !DateOnly.TryParseExact(item.Day, "yyyy-MM-dd", out var day)) return;
        if (!item.Confirming)
        {
            foreach (var d in Days) d.StopConfirming();
            item.StartConfirming();
            _ = AutoResetConfirm(item);
            return;
        }
        item.StopConfirming();
        _log.RemoveDay(day);
        _review?.PruneUnpinned(); // 没有任何一天记录挂靠的词同时退出复习队列
        Refresh();
    }

    private static async Task AutoResetConfirm(StudyDayItem item)
    {
        await Task.Delay(3000);
        item.StopConfirming();
    }

    /// <summary>明细行内移除单个词。</summary>
    [RelayCommand]
    private void RemoveWord(StudyWordItem? item)
    {
        if (item is null || SelectedDay is null
            || !DateOnly.TryParseExact(SelectedDay.Day, "yyyy-MM-dd", out var day)) return;
        _log.Remove(item.Word, day);
        _review?.PruneUnpinned();
        Refresh();
    }

    [RelayCommand]
    private async Task SpeakAsync(string? word)
    {
        if (_tts is null || _audio is null || string.IsNullOrWhiteSpace(word)) return;
        TtsError = "";
        try
        {
            var file = await _tts.SynthesizeAsync(word.Trim(), TtsKind.Word);
            await _audio.PlayAsync(file);
        }
        catch (Exception ex)
        {
            TtsError = $"发音失败：{ex.Message}";
        }
    }
}

/// <summary>日期行（含两步确认状态）。</summary>
public sealed partial class StudyDayItem : ObservableObject
{
    public StudyDayItem(string day, int count)
    {
        Day = day;
        Count = count;
    }

    public string Day { get; }
    public int Count { get; }

    /// <summary>"2026-09-16 · 周二 / 今天"这种亲切显示。</summary>
    public string Label
    {
        get
        {
            if (!DateOnly.TryParseExact(Day, "yyyy-MM-dd", out var d)) return Day;
            var today = DateOnly.FromDateTime(DateTime.Now);
            var weekday = "日一二三四五六"[(int)d.DayOfWeek];
            int diff = (today.DayNumber - d.DayNumber);
            var rel = diff switch { 0 => " 今天", 1 => " 昨天", _ => "" };
            return $"{Day} 周{weekday}{rel}";
        }
    }

    public string CountText => $"{Count} 个生词";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoveLabel))]
    private bool _confirming;

    public string RemoveLabel => Confirming ? "再点确认" : "移除";

    public void StartConfirming() => Confirming = true;
    public void StopConfirming() => Confirming = false;
}

/// <summary>明细行：词 + 释义快照 + 出处句。</summary>
public sealed class StudyWordItem
{
    public StudyWordItem(string word, string meaning, string note)
    {
        Word = word;
        Meaning = meaning.Length > 0 ? meaning : "（词库暂无释义）";
        Note = note;
    }

    public string Word { get; }
    public string Meaning { get; }
    public string Note { get; }
    public bool HasNote => Note.Length > 0;
}

/// <summary>今日复习队列的一行：词 + 释义 + 出处句 + 本轮档位 + 下次见面日期。</summary>
public sealed class ReviewWordItem
{
    public ReviewWordItem(string word, string meaning, string note, int visits, DateOnly due, int lapses, DateOnly today)
    {
        Word = word;
        Meaning = meaning.Length > 0 ? meaning : "（词库暂无释义）";
        Note = note;
        Visits = visits;
        Due = due;
        Lapses = lapses;
        int gap = due.DayNumber - today.DayNumber;
        NextText = gap <= 0 ? "今日到期"
            : gap == 1 ? "下次 明天"
            : $"下次 {due:MM-dd}（{gap} 天后）";
    }

    public string Word { get; }
    public string Meaning { get; }
    public string Note { get; }
    public bool HasNote => Note.Length > 0;
    public int Visits { get; }
    public int Lapses { get; }
    public DateOnly Due { get; }

    /// <summary>刚过完的第几轮（走完六档后进长期节奏）。</summary>
    public string RoundText => EbbinghausCycle.PhaseLabel(Visits);
    public string NextText { get; }
    public bool IsLapsed => Lapses > 0;
    public string LapseText => IsLapsed ? $"答错 {Lapses} 次" : "";
    public string ToolTip => $"{RoundText} · {NextText}{(IsLapsed ? $" · {LapseText}" : "")}";
}

/// <summary>复习日历的一行：某个到期日 + 相对说法 + 词数。</summary>
public sealed class AgendaItem
{
    public AgendaItem(DateOnly due, int count, DateOnly today)
    {
        Due = due;
        Count = count;
        int diff = due.DayNumber - today.DayNumber;
        var weekday = "日一二三四五六"[(int)due.DayOfWeek];
        Relative = diff switch { <= 0 => "今天", 1 => "明天", 2 => "后天", _ => $"{diff} 天后" };
        Label = $"{due:MM-dd} 周{weekday} · {Relative}";
    }

    public DateOnly Due { get; }
    public int Count { get; }
    public string Relative { get; }
    public string Label { get; }
    public string CountText => $"{Count} 个";
}
