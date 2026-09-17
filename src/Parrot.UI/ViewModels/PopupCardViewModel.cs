using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parrot.Core.Abstractions;

namespace Parrot.UI.ViewModels;

/// <summary>
/// 右下角"伪广告"弹窗卡（需求 2.3）：外观像广告、实为复习卡。
/// 词/释义来自 ICardSource（当日 📌 学习记录优先 → 内嵌词典随机）；🔊 走单词降级链。
/// </summary>
public sealed partial class PopupCardViewModel : ObservableObject
{
    private readonly ITtsService _tts;
    private readonly IAudioPlayer _audio;
    private readonly ICardSource _source;

    public PopupCardViewModel(ITtsService tts, IAudioPlayer audio, ICardSource source)
    {
        _tts = tts;
        _audio = audio;
        _source = source;
    }

    [ObservableProperty]
    private string _word = "";

    [ObservableProperty]
    private string _meaning = "";

    [ObservableProperty]
    private bool _isWrongAnswer;

    [ObservableProperty]
    private bool _speaking;

    [ObservableProperty]
    private string? _ttsError;

    /// <summary>取下一张卡填充；无内容返回 false（View 据此隐藏窗口）。</summary>
    public bool PullNext()
    {
        var card = _source.NextCard();
        if (card is null) return false;
        Word = card.Word;
        Meaning = card.Meaning;
        IsWrongAnswer = card.IsWrongAnswer;
        TtsError = null;
        return true;
    }

    /// <summary>窗口关闭（× 或点卡片外）后广播，App 层负责重新计时。</summary>
    public event Action? Closed;

    [RelayCommand]
    private void Close() => Closed?.Invoke();

    [RelayCommand]
    private async Task SpeakAsync()
    {
        if (Speaking || string.IsNullOrWhiteSpace(Word)) return;
        Speaking = true;
        TtsError = null;
        try
        {
            var file = await _tts.SynthesizeAsync(Word, TtsKind.Word);
            await _audio.PlayAsync(file);
        }
        catch (Exception ex)
        {
            TtsError = ex.Message;
        }
        finally
        {
            Speaking = false;
        }
    }
}
