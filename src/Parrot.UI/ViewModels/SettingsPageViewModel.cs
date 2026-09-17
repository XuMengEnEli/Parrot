using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parrot.Core.Abstractions;
using Parrot.Core.Update;
using Parrot.Data;

namespace Parrot.UI.ViewModels;

/// <summary>设置页：直读写 meta 表（改动即持久化）。热键/托盘/弹窗/音色/考试/在线升级。</summary>
public sealed partial class SettingsPageViewModel : PageViewModelBase
{
    private readonly SettingsRepository _settings;
    private readonly ITtsService? _tts;
    private readonly IAudioPlayer? _audio;
    private readonly IUpdateService? _update;

    public SettingsPageViewModel(SettingsRepository settings, ITtsService? tts = null, IAudioPlayer? audio = null,
        IUpdateService? update = null, string? currentVersion = null)
    {
        _settings = settings;
        _tts = tts;
        _audio = audio;
        _update = update;
        CurrentVersionText = "v" + (currentVersion is { Length: > 0 } ? currentVersion : "0.0.0");
        _bossKeyGlobal = settings.BossKeyGlobal;
        _closeToTray = settings.CloseToTray;
        _popupEnabled = settings.PopupEnabled;
        _popupIntervalMinutes = settings.PopupIntervalMinutes;
        _voice = settings.Voice;
        _examBlanks = settings.ExamBlanks;
        _updateAuto = settings.UpdateAutoCheck;
    }

    public override string Title => "设置";
    public override string Glyph => "⚙️";

    /// <summary>设置变更广播（App 订阅以重挂全局热键/弹窗定时器）。</summary>
    public event Action<string>? SettingChanged;

    [ObservableProperty]
    private bool _bossKeyGlobal;

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private bool _popupEnabled;

    [ObservableProperty]
    private int _popupIntervalMinutes;

    [ObservableProperty]
    private string _voice;

    [ObservableProperty]
    private int _examBlanks;

    partial void OnBossKeyGlobalChanged(bool value) { _settings.BossKeyGlobal = value; SettingChanged?.Invoke(nameof(BossKeyGlobal)); }
    partial void OnCloseToTrayChanged(bool value) => _settings.CloseToTray = value;
    partial void OnPopupEnabledChanged(bool value) { _settings.PopupEnabled = value; SettingChanged?.Invoke(nameof(PopupEnabled)); }
    partial void OnPopupIntervalMinutesChanged(int value) { _settings.PopupIntervalMinutes = Math.Clamp(value, 1, 240); SettingChanged?.Invoke(nameof(PopupIntervalMinutes)); }
    partial void OnVoiceChanged(string value) { _settings.Voice = value; SettingChanged?.Invoke(nameof(Voice)); }
    partial void OnExamBlanksChanged(int value) => _settings.ExamBlanks = Math.Clamp(value, 1, 6);

    public static string[] VoiceOptions { get; } =
    [
        "en-US-AriaNeural", "en-US-JennyNeural", "en-US-GuyNeural",
        "en-GB-SoniaNeural", "en-GB-RyanNeural", "en-AU-NatashaNeural",
    ];

    public static int[] PopupIntervalOptions { get; } = [5, 10, 15, 30, 60];

    public static int[] ExamBlankOptions { get; } = [1, 2, 3, 4, 5, 6];

    /// <summary>试听状态提示（合成中/失败原因），空则不显示。</summary>
    [ObservableProperty]
    private string _previewHint = "";

    /// <summary>用当前所选音色合成一句样例并播放（链路：Edge→系统，与句子发音一致）。</summary>
    [RelayCommand]
    private async Task PreviewVoiceAsync()
    {
        if (_tts is null || _audio is null) { PreviewHint = "试听不可用"; return; }
        if (PreviewHint == "合成中…") return; // 防连点
        PreviewHint = "合成中…";
        try
        {
            var file = await _tts.SynthesizeAsync(
                "Hello! This is the reading voice for your vocabulary lessons.", TtsKind.Sentence, Voice);
            await _audio.PlayAsync(file);
            PreviewHint = "";
        }
        catch (Exception e)
        {
            PreviewHint = "试听失败：" + e.Message;
        }
    }

    // ———————————————— 关于与在线升级（GitHub Releases，仓库写死） ————————————————

    /// <summary>官方仓库（写死只读，升级检查固定打这里；见 GitHubUpdateService.ParrotRepo）。</summary>
    public string RepoDisplay => GitHubUpdateService.ParrotRepo;

    [ObservableProperty]
    private bool _updateAuto;

    partial void OnUpdateAutoChanged(bool value) => _settings.UpdateAutoCheck = value;

    /// <summary>当前版本（比较基准来自 App 程序集 Version）。</summary>
    public string CurrentVersionText { get; }

    [ObservableProperty]
    private bool _checking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenDownload))]
    private string _downloadUrl = "";

    public bool CanOpenDownload => DownloadUrl.Length > 0;

    /// <summary>发现新版本的 tag（App 用它改窗口标题提示），无则空。</summary>
    [ObservableProperty]
    private string _latestTag = "";

    /// <summary>升级状态行（检查结果/错误），空则不显示。</summary>
    [ObservableProperty]
    private string _updateStatus = "";

    [ObservableProperty]
    private bool _updateAvailable;

    [RelayCommand]
    private Task CheckUpdateAsync() => RunCheckAsync(silentWhenBusy: false);

    /// <summary>启动自动检查（App 调）：静默失败，不打扰；发现新版本才吱声。</summary>
    public Task AutoCheckAsync() => RunCheckAsync(silentWhenBusy: true);

    private async Task RunCheckAsync(bool silentWhenBusy)
    {
        if (_update is null)
        {
            if (!silentWhenBusy) UpdateStatus = "升级服务不可用";
            return;
        }
        if (Checking) return;
        Checking = true;
        if (!silentWhenBusy) UpdateStatus = "检查中…";
        try
        {
            var cur = Version.TryParse(CurrentVersionText.TrimStart('v'), out var v) ? v : new Version(0, 0);
            var r = await _update.CheckAsync(GitHubUpdateService.ParrotRepo, cur);
            if (!r.Ok)
            {
                UpdateAvailable = false;
                LatestTag = "";
                DownloadUrl = "";
                if (!silentWhenBusy || r.Error is not null) UpdateStatus = r.Error!;
                return;
            }
            if (r.UpdateAvailable)
            {
                UpdateAvailable = true;
                LatestTag = r.Release!.Tag;
                DownloadUrl = r.DownloadUrl ?? "";
                var when = r.Release.PublishedAt?.LocalDateTime.ToString("yyyy-MM-dd") ?? "近期";
                UpdateStatus = $"发现新版本 {r.Release.Tag}（发布于 {when}）— 点「前往下载」获取安装包";
            }
            else
            {
                UpdateAvailable = false;
                LatestTag = "";
                DownloadUrl = "";
                UpdateStatus = $"已是最新（当前 {CurrentVersionText}，GitHub 最新 {r.Release!.Tag}）";
            }
        }
        finally
        {
            Checking = false;
        }
    }

    [RelayCommand]
    private void OpenDownload()
    {
        if (CanOpenDownload && !BrowserLauncher.Open(DownloadUrl))
            UpdateStatus = "打不开浏览器，请手动访问：" + DownloadUrl;
    }
}
