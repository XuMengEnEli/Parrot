using System.Net;
using System.Text;
using Parrot.Core.Update;
using Parrot.Data;
using Parrot.UI.ViewModels;
using Xunit;

namespace Parrot.Tests;

/// <summary>
/// 设置页「关于与升级」VM 链路（GUI 冒烟替代）：真 SettingsRepository（临时 SQLite）+
/// 真 GitHubUpdateService（fake handler，不触网）走完整检查→状态→持久化流程。
/// </summary>
public sealed class SettingsUpdateVmTests : IDisposable
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Responder;
        public readonly List<string> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(Responder?.Invoke(request)
                                   ?? throw new InvalidOperationException("no responder"));
        }
    }

    private static HttpResponseMessage Release(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    /// <summary>三平台附件齐全：任何宿主上都能挑到本机包，真正覆盖 PickAsset 而非退回 release 页。</summary>
    private const string NewerJson = """
        { "tag_name": "v1.4.0", "name": "Parrot 1.4.0", "html_url": "https://github.com/o/r/releases/tag/v1.4.0",
          "published_at": "2026-09-20T10:00:00Z", "assets": [
            { "name": "Parrot-win-x64.zip", "browser_download_url": "https://dl/Parrot-win-x64.zip", "size": 48000000 },
            { "name": "Parrot-mac-arm64.zip", "browser_download_url": "https://dl/Parrot-mac-arm64.zip", "size": 51000000 },
            { "name": "Parrot-mac-x64.zip", "browser_download_url": "https://dl/Parrot-mac-x64.zip", "size": 52000000 } ] }
        """;

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"se-vm-{Guid.NewGuid():N}.db");
    private readonly LocalDatabase _db;
    private readonly SettingsRepository _settings;

    public SettingsUpdateVmTests()
    {
        _db = new LocalDatabase(_dbPath);
        _db.EnsureSchema();
        _settings = new SettingsRepository(_db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* temp 自清 */ }
    }

    private SettingsPageViewModel MakeVmWithUpdate(IUpdateService update, string version = "1.3.0")
        => new(_settings, update: update, currentVersion: version);

    [Fact]
    public async Task ManualCheck_NewerRelease_SetsFlagsAndStatus()
    {
        var h = new FakeHandler { Responder = _ => Release(NewerJson) };
        var vm = MakeVmWithUpdate(new GitHubUpdateService(new HttpClient(h)));
        Assert.Equal("XuMengEnEli/Parrot", vm.RepoDisplay); // 仓库写死只读

        await vm.CheckUpdateCommand.ExecuteAsync(null);

        Assert.True(vm.UpdateAvailable);
        Assert.Equal("v1.4.0", vm.LatestTag);
        // 挑到本机平台的附件（固件含 win/mac 两平台包；Linux 非目标平台故无附件，退回 release 页）
        if (GitHubUpdateService.OsToken != "linux")
            Assert.Contains($"Parrot-{GitHubUpdateService.OsToken}", vm.DownloadUrl);
        Assert.True(vm.CanOpenDownload);
        Assert.Contains("发现新版本", vm.UpdateStatus);
        Assert.Contains("2026-09", vm.UpdateStatus); // 发布日期
        Assert.False(vm.Checking);                   // finally 复位，按钮不再禁用
    }

    [Fact]
    public async Task ManualCheck_UpToDate_ClearsFlags_ShowsLatest()
    {
        var h = new FakeHandler { Responder = _ => Release(NewerJson.Replace("v1.4.0", "v1.3.0")) };
        var vm = MakeVmWithUpdate(new GitHubUpdateService(new HttpClient(h)));

        await vm.CheckUpdateCommand.ExecuteAsync(null);

        Assert.False(vm.UpdateAvailable);
        Assert.Equal("", vm.LatestTag);
        Assert.Equal("", vm.DownloadUrl);
        Assert.False(vm.CanOpenDownload);
        Assert.Contains("已是最新", vm.UpdateStatus);
    }

    [Fact]
    public async Task ManualCheck_404_ShowsReadableError()
    {
        var h = new FakeHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
        var vm = MakeVmWithUpdate(new GitHubUpdateService(new HttpClient(h)));

        await vm.CheckUpdateCommand.ExecuteAsync(null);

        Assert.False(vm.UpdateAvailable);
        Assert.Contains("找不到", vm.UpdateStatus);
    }

    [Fact]
    public async Task AutoCheck_NetworkBlip_DoesNotThrow_ClearsFlags()
    {
        // App 启动静默检查路径：任何异常都不许冒到启动流程，只留状态行
        var h = new FakeHandler { Responder = _ => throw new HttpRequestException("boom") };
        var vm = MakeVmWithUpdate(new GitHubUpdateService(new HttpClient(h)));

        await vm.AutoCheckAsync();

        Assert.False(vm.UpdateAvailable);
        Assert.Equal("", vm.LatestTag);
        Assert.False(vm.Checking);
    }

    [Fact]
    public async Task ManualCheck_HitsHardcodedParrotRepo_NotUserConfigurable()
    {
        // 仓库写死：无论设置里有没有残留 update.repo，检查都打 XuMengEnEli/Parrot
        var h = new FakeHandler { Responder = _ => Release(NewerJson) };
        var vm = MakeVmWithUpdate(new GitHubUpdateService(new HttpClient(h)));

        await vm.CheckUpdateCommand.ExecuteAsync(null);

        Assert.Equal("https://api.github.com/repos/XuMengEnEli/Parrot/releases/latest", h.Requests[0]);
    }

    [Fact]
    public async Task NullService_ManualCheck_ReportsUnavailable_AutoStaysSilent()
    {
        var vm = new SettingsPageViewModel(_settings); // update 默认 null（单测/降级环境）
        await vm.CheckUpdateCommand.ExecuteAsync(null);
        Assert.Equal("升级服务不可用", vm.UpdateStatus);
        Assert.False(vm.UpdateAvailable);

        vm.UpdateStatus = "";
        await vm.AutoCheckAsync(); // 静默路径不得抛
        Assert.Equal("", vm.UpdateStatus);
    }

    [Fact]
    public void AutoToggle_Persists_ThroughToNewRepositoryInstance()
    {
        var vm = new SettingsPageViewModel(_settings);
        vm.UpdateAuto = false;

        var reloaded = new SettingsRepository(_db);
        Assert.False(reloaded.UpdateAutoCheck);
    }

    [Fact]
    public void CurrentVersionText_PrefixedWithV_FallsBackWhenMissing()
    {
        Assert.Equal("v1.3.0", new SettingsPageViewModel(_settings, currentVersion: "1.3.0").CurrentVersionText);
        Assert.Equal("v0.0.0", new SettingsPageViewModel(_settings).CurrentVersionText);
    }
}
