using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Parrot.Core.Abstractions;
using Parrot.Core.Update;
using Parrot.Data;
using Parrot.UI.ViewModels;

namespace Parrot.App.ViewModels;

/// <summary>主窗壳：左侧导航 + 右侧内容区（解决方案分层：App 只当组合根/窗口壳）。</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>项目主页（导航栏 GitHub 图标点击后浏览器打开）。</summary>
    public string GitHubUrl => GitHubUpdateService.ProjectUrl;

    [RelayCommand]
    private void OpenGitHub() => BrowserLauncher.Open(GitHubUpdateService.ProjectUrl);
    public MainWindowViewModel(IServiceProvider services)
    {
        var tts = services.GetRequiredService<ITtsService>();
        var audio = services.GetRequiredService<IAudioPlayer>();
        var wordbook = services.GetRequiredService<WordbookRepository>();
        var studyLog = services.GetRequiredService<StudyLogRepository>();
        var settings = services.GetRequiredService<SettingsRepository>();
        var pomodoro = new PomodoroPageViewModel(services.GetRequiredService<PomodoroRepository>());
        Pages =
        [
            // 讲义阅读页右上角嵌入的番茄卡与番茄钟页共享同一 VM 实例（同一计时器、两处显示）；
            // studyLog 注入后，🔊 旁才会出现 📌"记入当日学习"按钮
            new ReaderPageViewModel(tts, audio, services.GetService<IOcrService>(), pomodoro: pomodoro,
                studyLog: studyLog, wordbook: wordbook),
            pomodoro,
            new StudyLogPageViewModel(studyLog, tts, audio),
            new ExamPageViewModel(settings, studyLog, wordbook, tts, audio),
            // 生词本页已移除；设置页持有升级服务 + 当前版本（在线升级入口在"关于与升级"卡片）
            new SettingsPageViewModel(settings, tts, audio, services.GetService<IUpdateService>(),
                typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3)),
        ];
        _selectedPage = Pages[0];
    }

    /// <summary>供 MainWindow 订阅设置变更（热键/弹窗重挂）。</summary>
    public SettingsPageViewModel Settings => (SettingsPageViewModel)Pages[^1];

    public IReadOnlyList<PageViewModelBase> Pages { get; }

    [ObservableProperty]
    private PageViewModelBase _selectedPage;
}
