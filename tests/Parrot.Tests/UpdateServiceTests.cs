using System.Net;
using System.Text;
using Parrot.Core.Update;
using Xunit;

namespace Parrot.Tests;

/// <summary>在线升级（GitHub Releases）纯逻辑 + 服务层（fake handler，不触真网络）。</summary>
public sealed class UpdateServiceTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Responder;
        public readonly List<string> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            var resp = Responder?.Invoke(request)
                       ?? throw new InvalidOperationException("no responder");
            return Task.FromResult(resp);
        }
    }

    private const string ReleaseJson = """
        {
          "tag_name": "v1.4.0",
          "name": "Parrot 1.4.0",
          "body": "## 更新内容\n- 修了点什么",
          "html_url": "https://github.com/o/r/releases/tag/v1.4.0",
          "published_at": "2026-09-20T10:00:00Z",
          "draft": false,
          "prerelease": false,
          "assets": [
            { "name": "Parrot-mac-arm64.zip", "browser_download_url": "https://dl/Parrot-mac-arm64.zip", "size": 52000000 },
            { "name": "Parrot-win-x64.zip",   "browser_download_url": "https://dl/Parrot-win-x64.zip",   "size": 48000000 }
          ]
        }
        """;

    // ---------- 解析 ----------

    [Fact]
    public void FromJson_FullPayload_MapsFields()
    {
        var r = GitHubRelease.FromJson(ReleaseJson)!;
        Assert.NotNull(r);
        Assert.Equal("v1.4.0", r.Tag);
        Assert.Equal("Parrot 1.4.0", r.Name);
        Assert.Contains("更新内容", r.Body);
        Assert.Equal(2, r.Assets.Count);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero), r.PublishedAt);
    }

    [Theory]
    [InlineData("{}")]            // 无 tag_name
    [InlineData("not json at all")] // 网关返回 HTML/纯文本
    [InlineData("[]")]            // 顶层不是对象
    public void FromJson_InvalidPayload_ReturnsNull(string json)
        => Assert.Null(GitHubRelease.FromJson(json));

    // ---------- 版本比较 ----------

    [Theory]
    [InlineData("v1.4.0", 1, 3, 0, true)]   // 正常升级
    [InlineData("v1.3.0", 1, 3, 0, false)]  // 相同
    [InlineData("1.2.9", 1, 3, 0, false)]   // 更低
    [InlineData("v2", 1, 9, 9, true)]       // 单段补零
    [InlineData("v1.3.0-beta.1", 1, 3, 0, false)] // 预发布后缀剥离 → 不比主版本号新就不算
    [InlineData("V1.3.1+build.9", 1, 3, 0, true)] // 大写 V + build 元数据
    public void IsNewerTag_ComparesNumericCore(string tag, int a, int b, int c, bool expected)
    {
        var r = new GitHubRelease(tag, "", "", "", null, Array.Empty<ReleaseAsset>());
        Assert.Equal(expected, r.IsNewerThan(new Version(a, b, c)));
    }

    [Fact]
    public void TryParseTag_RejectsGarbage()
        => Assert.False(GitHubRelease.TryParseTag("v1.x.3", out _));

    // ---------- 平台挑包 ----------

    [Fact]
    public void PickAsset_PrefersExactArchThenLooseOs()
    {
        var r = GitHubRelease.FromJson(ReleaseJson)!;
        var mac = r.PickAsset("mac", "arm64");
        Assert.Equal("Parrot-mac-arm64.zip", mac!.Name);
        Assert.Equal("Parrot-mac-arm64.zip", r.PickAsset("mac", "arm64")!.Name);
        // mac 但只有 x64 时代称不符 → 落 os 宽匹配（loose）
        Assert.Equal("Parrot-mac-arm64.zip", r.PickAsset("mac", "riscv")!.Name);
        Assert.Equal("Parrot-win-x64.zip", r.PickAsset("win", "x64")!.Name);
        Assert.Null(r.PickAsset("linux", "x64")); // 没有该平台的包
    }

    // ---------- 服务层 ----------

    private static (GitHubUpdateService svc, FakeHandler h) Service(Func<HttpRequestMessage, HttpResponseMessage> resp)
    {
        var h = new FakeHandler { Responder = resp };
        return (new GitHubUpdateService(new HttpClient(h)), h);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Check_InvalidRepo_FailsFast_WithoutHttpRequest()
    {
        var (svc, h) = Service(_ => Json(ReleaseJson));
        var r = await svc.CheckAsync("bad repo!!", new Version(1, 0, 0));
        Assert.False(r.Ok);
        Assert.Contains("owner/name", r.Error);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Check_HitsLatestEndpoint_AndDetectsUpdate()
    {
        var (svc, h) = Service(_ => Json(ReleaseJson));
        var r = await svc.CheckAsync("my/parrot", new Version(1, 3, 0));
        Assert.True(r.Ok);
        Assert.Equal("https://api.github.com/repos/my/parrot/releases/latest", h.Requests[0]);
        Assert.True(r.UpdateAvailable);
        Assert.Equal("v1.4.0", r.Release!.Tag);
        // 下载直链优先宿主平台附件（win 测试机命中 win 包，mac CI 命中 mac 包）
        Assert.Contains($"Parrot-{GitHubUpdateService.OsToken}-", r.DownloadUrl);
    }

    [Fact]
    public async Task Check_NoAssets_FallsBackToReleasePage()
    {
        var (svc, _) = Service(_ => Json("""
            {"tag_name":"v2.0.0","name":"x","body":"","html_url":"https://github.com/o/r/releases/tag/v2.0.0","assets":[]}
            """));
        var r = await svc.CheckAsync("my/parrot", new Version(1, 3, 0));
        Assert.True(r.UpdateAvailable);
        Assert.Equal("https://github.com/o/r/releases/tag/v2.0.0", r.DownloadUrl);
    }

    [Fact]
    public async Task Check_UpToDate_OkButNoUpdate()
    {
        var (svc, _) = Service(_ => Json(ReleaseJson));
        var r = await svc.CheckAsync("my/parrot", new Version(9, 9, 9));
        Assert.True(r.Ok);
        Assert.False(r.UpdateAvailable);
    }

    [Fact]
    public async Task Check_NotFound_GivesActionableError()
    {
        var (svc, _) = Service(_ => Json("{}", HttpStatusCode.NotFound));
        var r = await svc.CheckAsync("no/such-repo", new Version(1, 0, 0));
        Assert.False(r.Ok);
        Assert.Contains("找不到", r.Error);
    }

    [Fact]
    public async Task Check_GarbageResponse_ReportsParseFailure()
    {
        var (svc, _) = Service(_ => Json("<html>proxy拦截</html>"));
        var r = await svc.CheckAsync("my/parrot", new Version(1, 0, 0));
        Assert.False(r.Ok);
        Assert.Contains("解析失败", r.Error);
    }

    [Fact]
    public async Task Check_NetworkBlip_DoesNotThrow()
    {
        var (svc, _) = Service(_ => throw new HttpRequestException("unreachable"));
        var r = await svc.CheckAsync("my/parrot", new Version(1, 0, 0));
        Assert.False(r.Ok);
        Assert.Contains("网络", r.Error);
    }
}
