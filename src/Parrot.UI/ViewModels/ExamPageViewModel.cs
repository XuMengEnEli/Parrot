using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parrot.Core.Abstractions;
using Parrot.Core.Exam;
using Parrot.Data;

namespace Parrot.UI.ViewModels;

/// <summary>
/// 拼写考试（需求 #3）：随机把单词里几个字母"干掉"，用户回填后一键检查。
/// 取词优先今日学习记录（📌 列表），不足一轮的词数再从内嵌词库随机补；
/// 挖空个数在 设置→考试 里配（exam.blanks）。
/// </summary>
public sealed partial class ExamPageViewModel : PageViewModelBase
{
    public const int RoundSize = 10;

    private readonly SettingsRepository _settings;
    private readonly StudyLogRepository _log;
    private readonly WordbookRepository _wordbook;
    private readonly ITtsService? _tts;
    private readonly IAudioPlayer? _audio;
    private readonly Random _rng = new();

    public ExamPageViewModel(SettingsRepository settings, StudyLogRepository log, WordbookRepository wordbook,
        ITtsService? tts = null, IAudioPlayer? audio = null)
    {
        _settings = settings;
        _log = log;
        _wordbook = wordbook;
        _tts = tts;
        _audio = audio;
        NewRound();
    }

    public override string Title => "拼写考试";
    public override string Glyph => "✍️";

    public ObservableCollection<ExamQuestionViewModel> Questions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuestions))]
    private string _subTitle = "";

    public bool HasQuestions => Questions.Count > 0;

    [ObservableProperty]
    private bool _checked;

    [ObservableProperty]
    private string _scoreText = "";

    [ObservableProperty]
    private string _emptyHint = "";

    [ObservableProperty]
    private bool _isPoolEmpty;

    /// <summary>开新一轮：取词 → 出题。</summary>
    [RelayCommand]
    private void NewRound()
    {
        Checked = false;
        ScoreText = "";
        Questions.Clear();

        var today = DateOnly.FromDateTime(DateTime.Now);
        var pool = _log.RandomOfDay(today, RoundSize)
            .Select(r => (r.Word, Meaning: r.Meaning))
            .ToList();
        if (pool.Count < RoundSize)
        {
            // 当天记录不足 → 内嵌词库随机补齐（排除已选）
            var taken = pool.Select(p => p.Word).ToHashSet(StringComparer.Ordinal);
            foreach (var w in _wordbook.AllWords(5000).OrderBy(_ => _rng.Next()))
            {
                if (pool.Count >= RoundSize) break;
                if (w.Word.Length < 3 || taken.Contains(w.Word)) continue;
                pool.Add((w.Word, w.Translation));
                taken.Add(w.Word);
            }
        }

        IsPoolEmpty = pool.Count == 0;
        EmptyHint = IsPoolEmpty
            ? "没有可考的词：阅读时在句子旁的 📌 记几个词，或先生成词库。"
            : "";
        foreach (var (word, meaning) in pool)
            Questions.Add(new ExamQuestionViewModel(word, meaning, _settings.ExamBlanks, _rng));

        SubTitle = pool.Count == 0
            ? "词库为空"
            : $"本轮 {pool.Count} 题 · 每题挖 {_settings.ExamBlanks} 个字母 · 词源：今日学习记录优先";
    }

    /// <summary>检查：逐空核对，题与卷都出分。</summary>
    [RelayCommand]
    private void CheckAll()
    {
        if (Questions.Count == 0) return;
        int right = 0, blanks = 0;
        foreach (var q in Questions)
        {
            q.Check();
            if (q.AllCorrect) right++;
            blanks += q.Blanks.Count;
        }
        Checked = true;
        ScoreText = $"全部填对 {right}/{Questions.Count} 题 · 命中字母 {Questions.Sum(x => x.Blanks.Count(b => b.State == 1))}/{blanks}";
    }

    [RelayCommand]
    private async Task SpeakAsync(string? word)
    {
        if (_tts is null || _audio is null || string.IsNullOrWhiteSpace(word)) return;
        try
        {
            var file = await _tts.SynthesizeAsync(word.Trim(), TtsKind.Word);
            await _audio.PlayAsync(file);
        }
        catch { /* 试听失败不打断考试 */ }
    }
}

/// <summary>一道题：挖空的单词 + 逐空输入 + 检查结果。</summary>
public sealed partial class ExamQuestionViewModel : ObservableObject
{
    public ExamQuestionViewModel(string word, string meaning, int blanksConfig, Random rng)
    {
        Word = word;
        Meaning = string.IsNullOrWhiteSpace(meaning) ? "（无释义）" : meaning;
        Slots = ExamQuizer.Make(word, blanksConfig, rng);
        int n = Slots.OfType<ExamSlot.Blank>().Select(b => b.Index + 1).DefaultIfEmpty(0).Max();
        for (int i = 0; i < n; i++) Blanks.Add(new ExamBlankViewModel());
        Segments = Slots.Select(s => s switch
        {
            ExamSlot.Blank b => ExamSegmentViewModel.ForBlank(Blanks[b.Index], b.Answer),
            _ => ExamSegmentViewModel.ForLetter(((ExamSlot.Letter)s).Ch),
        }).ToList();
    }

    public string Word { get; }
    public string Meaning { get; }
    public IReadOnlyList<ExamSlot> Slots { get; }
    public ObservableCollection<ExamBlankViewModel> Blanks { get; } = [];
    /// <summary>题面渲染序列：字母段与空段交替（XAML 按类型选模板）。</summary>
    public IReadOnlyList<ExamSegmentViewModel> Segments { get; }

    public string Rendered => ExamQuizer.Render(Slots);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(AllCorrect))]
    [NotifyPropertyChangedFor(nameof(VerdictBrush))]
    [NotifyPropertyChangedFor(nameof(ShowReveal))]
    private bool _checked;

    [ObservableProperty]
    private int _correctBlanks;

    public bool AllCorrect => Checked && CorrectBlanks == Blanks.Count;
    public string StateText => !Checked ? "" : AllCorrect ? "全对" : $"对 {CorrectBlanks}/{Blanks.Count} 空";

    private static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#30A46C"));
    private static readonly IBrush BadBrush = new SolidColorBrush(Color.Parse("#E5484D"));

    /// <summary>题卡左侧色条：未检查透明 / 全对绿 / 有错红。</summary>
    public IBrush VerdictBrush => !Checked ? Brushes.Transparent : AllCorrect ? OkBrush : BadBrush;
    public bool ShowReveal => Checked && !AllCorrect;

    public void Check()
    {
        var res = ExamQuizer.Check(Slots, Blanks.Select(b => b.Input).ToList());
        for (int i = 0; i < Blanks.Count; i++)
        {
            Blanks[i].State = res.BlankCorrect[i] ? 1 : 2;
            Blanks[i].Checked = true;
        }
        CorrectBlanks = res.CorrectBlanks;
        Checked = true;
    }
}

/// <summary>题面片段：可见字母或填空格（单一类型，XAML 用 IsVisible 切换，避免隐式模板查找）。</summary>
public sealed class ExamSegmentViewModel
{
    private ExamSegmentViewModel(bool isLetter, char ch, ExamBlankViewModel? blank, char answer)
    {
        IsLetter = isLetter; Ch = ch; Blank = blank; Answer = answer;
    }

    public static ExamSegmentViewModel ForLetter(char ch) => new(true, ch, null, '\0');
    public static ExamSegmentViewModel ForBlank(ExamBlankViewModel blank, char answer) => new(false, '\0', blank, answer);

    public bool IsLetter { get; }
    public bool IsBlank => !IsLetter;
    public char Ch { get; }
    public ExamBlankViewModel? Blank { get; }
    public char Answer { get; }
}

/// <summary>一个填空格：单字符输入 + 三态外观（待填/对/错）。</summary>
public sealed partial class ExamBlankViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateBrush))]
    [NotifyPropertyChangedFor(nameof(StateBg))]
    private string _input = "";

    /// <summary>0 待填 · 1 正确 · 2 错误。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateBrush))]
    [NotifyPropertyChangedFor(nameof(StateBg))]
    [NotifyPropertyChangedFor(nameof(ShowAnswerHint))]
    private int _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAnswerHint))]
    private bool _checked;

    /// <summary>检查后仍错的格子：下方露出正确字母。</summary>
    public bool ShowAnswerHint => State == 2 && Checked;

    private static readonly IBrush Pending = new SolidColorBrush(Color.Parse("#54707070"));
    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#30A46C"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush OkBg = new SolidColorBrush(Color.Parse("#1A30A46C"));
    private static readonly IBrush BadBg = new SolidColorBrush(Color.Parse("#1AE5484D"));
    private static readonly IBrush PendingBg = new SolidColorBrush(Color.Parse("#0F808080"));

    public IBrush StateBrush => State switch { 1 => Ok, 2 => Bad, _ => Pending };
    public IBrush StateBg => State switch { 1 => OkBg, 2 => BadBg, _ => PendingBg };
}
