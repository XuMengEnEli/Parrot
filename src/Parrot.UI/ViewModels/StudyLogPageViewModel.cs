using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parrot.Core.Abstractions;
using Parrot.Data;

namespace Parrot.UI.ViewModels;

/// <summary>
/// 学习记录页（需求 #2）：🔊 旁 📌 记进来的词按天归档。
/// 左＝日期列表（日期/生词数量/查看/移除），右＝所选日期的单词+释义明细。
/// </summary>
public sealed partial class StudyLogPageViewModel : PageViewModelBase
{
    private readonly StudyLogRepository _log;
    private readonly ITtsService? _tts;
    private readonly IAudioPlayer? _audio;

    public StudyLogPageViewModel(StudyLogRepository log, ITtsService? tts = null, IAudioPlayer? audio = null)
    {
        _log = log;
        _tts = tts;
        _audio = audio;
        Refresh();
    }

    public override string Title => "学习记录";
    public override string Glyph => "📒";

    public ObservableCollection<StudyDayItem> Days { get; } = [];
    public ObservableCollection<StudyWordItem> Words { get; } = [];

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

    [RelayCommand]
    private void Reload() => Refresh();

    public void Refresh()
    {
        var now = DateOnly.FromDateTime(DateTime.Now);
        var keep = SelectedDay?.Day;
        Days.Clear();
        foreach (var d in _log.Days()) Days.Add(new StudyDayItem(d.Day, d.Count));
        TodayCount = _log.WordsOfDay(now).Count;
        HasNoDays = Days.Count == 0;
        SelectedDay = keep is not null ? Days.FirstOrDefault(x => x.Day == keep) : Days.FirstOrDefault();
        LoadWords();
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
