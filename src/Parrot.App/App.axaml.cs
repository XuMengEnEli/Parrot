using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using SharpHook;
using SharpHook.Data;
using Parrot.Audio;
using Parrot.Core.Abstractions;
using Parrot.Core.Review;
using Parrot.Core.Update;
using Parrot.Data;
using Parrot.UI.ViewModels;

namespace Parrot.App;

public partial class App : Application
{
    /// <summary>组合根。服务逐步骤充实（OCR/TTS/升级各自按平台条件注册）。</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>真正退出（绕过"关窗进托盘"）。</summary>
    public static bool IsExiting { get; private set; }

    /// <summary>主窗关闭行为：托盘常驻（需求 2.2）。</summary>
    public static bool ShouldMinimizeOnClose
        => !IsExiting
           && Services?.GetService(typeof(SettingsRepository)) is SettingsRepository s
           && s.CloseToTray;

    private MainWindow? _main;
    private PopupCardWindow? _popup;
    private PopupCardViewModel? _popupVm;
    private DispatcherTimer? _popupTimer;
    private DispatcherTimer? _popupAutoHide;
    private IGlobalHook? _hook;
    private CancellationTokenSource? _hookCts;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            Services = ConfigureServices();

            // 首次启动：内嵌 seed 词表落库（完整 ECDICT 用 scripts/fetch-ecdict 下载后同导入器入库）
            try
            {
                WordbookImporter.SeedIfEmpty(
                    Services.GetRequiredService<WordbookRepository>(), DateOnly.FromDateTime(DateTime.Now));
            }
            catch
            {
                // 词库播种失败不阻塞启动（弹窗有示例兜底）
            }

            _main = new MainWindow
            {
                DataContext = new ViewModels.MainWindowViewModel(Services),
            };
            desktop.MainWindow = _main;

            var popupVm = new PopupCardViewModel(
                Services.GetRequiredService<ITtsService>(),
                Services.GetRequiredService<IAudioPlayer>(),
                Services.GetRequiredService<ICardSource>());
            popupVm.Closed += () => _popup?.Hide();
            _popupVm = popupVm;
            _popup = new PopupCardWindow { DataContext = popupVm };

            WireTray();
            WirePopupCycle();
            WireBossKeyHook();

            _main.Show();
            _ = AutoUpdateCheckAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>启动静默检查更新（设置可关）：发现新版本只改窗口标题提示一行，不弹窗打扰。</summary>
    private async Task AutoUpdateCheckAsync()
    {
        try
        {
            var settings = Services.GetRequiredService<SettingsRepository>();
            if (!settings.UpdateAutoCheck) return; // 仓库写死 XuMengEnEli/Parrot，无需再判"未配置"
            if (_main?.DataContext is not ViewModels.MainWindowViewModel vm) return;
            await vm.Settings.AutoCheckAsync();
            if (vm.Settings.UpdateAvailable && _main is not null)
                Dispatcher.UIThread.Post(() =>
                    _main.Title = $"鹦鹉 Parrot · 发现新版本 {vm.Settings.LatestTag}（设置→关于与升级）");
        }
        catch
        {
            // 自动检查失败绝不影响启动
        }
    }

    // ————————————————— 托盘 / 老板键 / 弹窗 —————————————————

    private void WireTray()
    {
        // App.axaml 里 TrayIcon 子项不生成 x:Name 字段（非控件树命名域），运行时按索引接线
        var tray = TrayIcon.GetIcons(this)?.FirstOrDefault();
        if (tray is null) return;
        tray.Clicked += (_, _) => ShowMainWindow();
        if (tray.Menu?.Items is { Count: >= 4 } menu)
        {
            // 0 显示主窗口 / 1 弹复习卡 / 2 分隔线 / 3 退出（按 App.axaml 声明顺序）
            ((NativeMenuItem)menu[0]).Click += (_, _) => ShowMainWindow();
            ((NativeMenuItem)menu[1]).Click += (_, _) => ShowPopupCard();
            ((NativeMenuItem)menu[3]).Click += (_, _) => ExitApp();
        }
    }

    private void WirePopupCycle()
    {
        var settings = Services.GetRequiredService<SettingsRepository>();
        _popupTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(Math.Max(1, settings.PopupIntervalMinutes)) };
        _popupTimer.Tick += (_, _) =>
        {
            if (Services.GetRequiredService<SettingsRepository>().PopupEnabled)
                ShowPopupCard();
        };
        _popupTimer.Start();

        _popupAutoHide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _popupAutoHide.Tick += (_, _) => { _popupAutoHide!.Stop(); _popup?.Hide(); };
    }

    private void ShowPopupCard()
    {
        if (_popup is null || _popupVm is null) return;
        if (!_popupVm.PullNext()) return; // 词库空（异常情况），静默跳过本次
        _popup.ShowAtCorner();
        _popupAutoHide?.Stop();
        _popupAutoHide?.Start();
    }

    private void WireBossKeyHook()
    {
        if (_main?.DataContext is not ViewModels.MainWindowViewModel vm) return;
        vm.Settings.SettingChanged += key =>
        {
            if (key == nameof(SettingsPageViewModel.BossKeyGlobal)) RestartGlobalHook();
            if (key is nameof(SettingsPageViewModel.PopupIntervalMinutes) or nameof(SettingsPageViewModel.PopupEnabled))
            {
                var s = Services.GetRequiredService<SettingsRepository>();
                if (_popupTimer is { } t) t.Interval = TimeSpan.FromMinutes(Math.Max(1, s.PopupIntervalMinutes));
            }
        };
        RestartGlobalHook();
    }

    /// <summary>
    /// 全局 Ctrl+Shift+H（SharpHook 8）：v8 事件不携带修饰键状态（与 v5/6 不同，
    /// 已反射核实），自行跟踪修饰键按下集合。mac 无辅助功能权限时静默失败，应用内 KeyDown 兜底始终可用。
    /// </summary>
    private void RestartGlobalHook()
    {
        StopGlobalHook();
        if (!Services.GetRequiredService<SettingsRepository>().BossKeyGlobal) return;

        var down = new HashSet<KeyCode>();
        var hook = new SimpleGlobalHook();
        hook.KeyPressed += (_, e) =>
        {
            var k = e.Data.KeyCode;
            lock (down)
            {
                if (k is KeyCode.VcLeftShift or KeyCode.VcRightShift
                    or KeyCode.VcLeftControl or KeyCode.VcRightControl
                    or KeyCode.VcLeftAlt or KeyCode.VcRightAlt)
                {
                    down.Add(k);
                    return;
                }
                if (k == KeyCode.VcH
                    && (down.Contains(KeyCode.VcLeftControl) || down.Contains(KeyCode.VcRightControl))
                    && (down.Contains(KeyCode.VcLeftShift) || down.Contains(KeyCode.VcRightShift)))
                {
                    e.SuppressEvent = true; // 老板键吞掉，防止透传给前台应用
                    Dispatcher.UIThread.Post(CollapseAll);
                }
            }
        };
        hook.KeyReleased += (_, e) => { lock (down) down.Remove(e.Data.KeyCode); };
        hook.HookDisabled += (_, _) => { lock (down) down.Clear(); };

        _hook = hook;
        _hookCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await hook.RunAsync(GlobalHookType.Keyboard); }
            catch
            {
                // 平台权限缺失（mac 辅助功能）——静默，应用内兜底仍可用
            }
        });
    }

    private void StopGlobalHook()
    {
        _hookCts?.Cancel();
        _hookCts?.Dispose();
        _hookCts = null;
        if (_hook is not null)
        {
            try { _hook.Stop(); } catch { /* 尚未启动 */ }
            if (_hook is IDisposable d) d.Dispose();
        }
        _hook = null;
    }

    /// <summary>老板键收起：主窗 + 弹窗全部隐藏（托盘可唤回）。</summary>
    public static void CollapseAll()
    {
        var app = Current as App;
        app?._main?.Hide();
        app?._popup?.Hide();
    }

    private void ShowMainWindow()
    {
        if (_main is null) return;
        _main.Show();
        _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private void ExitApp()
    {
        IsExiting = true;
        StopGlobalHook();
        _desktop?.Shutdown();
    }

    // ————————————————— DI —————————————————

    private static ServiceProvider ConfigureServices()
    {
        var sc = new ServiceCollection();

        sc.AddSingleton<LocalDatabase>(sp =>
        {
            var db = new LocalDatabase();
            db.EnsureSchema();
            return db;
        });

        sc.AddSingleton<TtsCache>();
        sc.AddSingleton(sp => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        sc.AddSingleton<PomodoroRepository>();
        sc.AddSingleton<SettingsRepository>();
        sc.AddSingleton<WordbookRepository>();
        sc.AddSingleton<StudyLogRepository>();
        // 降级链：句子→Edge→系统；单词→有道→Edge→系统
        sc.AddSingleton<YoudaoTtsService>();
        sc.AddSingleton<EdgeTtsService>();
        sc.AddSingleton<SystemSpeechTtsService>();
        sc.AddSingleton<ITtsService>(sp => new FallbackTtsService(
            sp.GetRequiredService<YoudaoTtsService>(),
            sp.GetRequiredService<EdgeTtsService>(),
            sp.GetRequiredService<SystemSpeechTtsService>()));
        sc.AddSingleton<IAudioPlayer, ProcessAudioPlayer>();
        // 在线升级：GitHub releases/latest（仓库 owner/repo 写死在 UpdateService.ParrotRepo，设置页只有检查/下载入口）
        sc.AddSingleton<IUpdateService>(sp => new GitHubUpdateService(sp.GetRequiredService<HttpClient>()));

#if WINDOWS_OCR
        // 扫描件 OCR（需求 1.1 最后一公里）：Windows 用系统内置 WinRT 引擎；
        // 语言包缺失/组件被裁剪时 IsAvailable=false，阅读页自动退回"原图视图 + 占位卡"。
        sc.AddSingleton<IOcrService, Parrot.Ocr.Windows.WinRtOcrService>();
#elif MAC_OCR
        // mac 用系统 Vision 框架（osascript/JXA 桥，离线）；osascript 不可用时同样退回"原图 + 占位卡"。
        sc.AddSingleton<IOcrService, Parrot.Ocr.Mac.MacVisionOcrService>();
#endif

        // 弹窗卡片源：今日学习记录（📌）优先 → 内嵌词库随机 → 内置示例兜底
        sc.AddSingleton<ICardSource>(sp =>
        {
            var wb = new WordbookCardSource(
                sp.GetRequiredService<WordbookRepository>(),
                sp.GetRequiredService<StudyLogRepository>());
            return new DelegatingCardSource(() => wb.NextCard() ?? SampleCards.RandomCard());
        });

        return sc.BuildServiceProvider();
    }
}
