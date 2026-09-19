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
        var review = services.GetRequiredService<ReviewRepository>();
        var settings = services.GetRequiredService<SettingsRepository>();
        var pomodoro = new PomodoroPageViewModel(services.GetRequiredService<PomodoroRepository>());
        Pages =
        [
            // 讲义阅读页右上角嵌入的番茄卡与番茄钟页共享同一 VM 实例（同一计时器、两处显示）；
            // studyLog 注入后，🔊 旁才会出现 📌"记入当日学习"按钮；review 让钉住即起锚记忆曲线
            new ReaderPageViewModel(tts, audio, services.GetService<IOcrService>(), pomodoro: pomodoro,
                studyLog: studyLog, wordbook: wordbook, review: review),
            pomodoro,
            new StudyLogPageViewModel(studyLog, tts, audio, review),
            new ExamPageViewModel(settings, studyLog, wordbook, tts, audio, review),
            // 生词本页已移除；设置页持有升级服务 + 当前版本（在线升级入口在"关于与升级"卡片）
            new SettingsPageViewModel(settings, tts, audio, services.GetService<IUpdateService>(),
                typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3)),
        ];
        SelectedPage = Pages[0];
    }

    /// <summary>供 MainWindow 订阅设置变更（热键/弹窗重挂）。</summary>
    public SettingsPageViewModel Settings => (SettingsPageViewModel)Pages[^1];

    public IReadOnlyList<PageViewModelBase> Pages { get; }

    // 五个页面在主窗里常驻（见 MainWindow.axaml），XAML 按名字直连 DataContext
    public ReaderPageViewModel Reader => (ReaderPageViewModel)Pages[0];
    public PomodoroPageViewModel Pomodoro => (PomodoroPageViewModel)Pages[1];
    public StudyLogPageViewModel StudyLog => (StudyLogPageViewModel)Pages[2];
    public ExamPageViewModel Exam => (ExamPageViewModel)Pages[3];

    [ObservableProperty]
    private PageViewModelBase _selectedPage;

    /// <summary>切页只改各页的 IsSelected：视图不重建，阅读页滚到哪儿下次回来还在哪儿。</summary>
    partial void OnSelectedPageChanged(PageViewModelBase value)
    {
        foreach (var p in Pages)
            p.IsSelected = ReferenceEquals(p, value);
    }
}
