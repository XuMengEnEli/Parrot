using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Parrot.Core.Update;

/// <summary>一次在线检查的结论。Ok=false 时看 Error；Ok=true 时 Release 必有。</summary>
public sealed record UpdateCheckResult(
    bool Ok, GitHubRelease? Release, bool UpdateAvailable, string? Error, string? DownloadUrl)
{
    public static UpdateCheckResult Fail(string error) => new(false, null, false, error, null);
}

/// <summary>在线升级检查（面向 GitHub Releases；实现永不抛异常，全部折算进结果）。</summary>
public interface IUpdateService
{
    Task<UpdateCheckResult> CheckAsync(string repo, Version current, CancellationToken ct = default);
}

/// <summary>
/// 在线升级（需求：资源指向 GitHub）：拉取
/// <c>https://api.github.com/repos/{owner}/{repo}/releases/latest</c>，
/// 与当前版本比较 tag_name，按宿主平台从 Release 附件里挑安装包（mac-arm64 / mac-x64 / win-x64 命名约定）。
/// 升级动作 = 打开附件直链（浏览器接管下载），不自动覆盖正在运行的程序。
/// </summary>
public sealed partial class GitHubUpdateService(HttpClient http) : IUpdateService
{
    public const string ApiTemplate = "https://api.github.com/repos/{0}/releases/latest";

    /// <summary>官方发布仓库（写死，用户在设置里只读可见）：升级检查与 GitHub 入口都指这里。</summary>
    public const string ParrotRepo = "XuMengEnEli/Parrot";

    /// <summary>项目主页（GitHub 图标与手动下载的去向）。</summary>
    public static string ProjectUrl => "https://github.com/" + ParrotRepo;

    public async Task<UpdateCheckResult> CheckAsync(string repo, Version current, CancellationToken ct = default)
    {
        repo = repo.Trim();
        if (!RepoPattern().IsMatch(repo))
            return UpdateCheckResult.Fail("GitHub 仓库要填成 owner/name，例如 myname/parrot");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, string.Format(ApiTemplate, repo));
            req.Headers.TryAddWithoutValidation("User-Agent", "parrot-desktop-updater");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await http.SendAsync(req, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return UpdateCheckResult.Fail(
                    "GitHub 上找不到该仓库或没有公开 Release（核对 owner/name、确认已发布且仓库公开）");
            if (!resp.IsSuccessStatusCode)
                return UpdateCheckResult.Fail($"GitHub 返回 HTTP {(int)resp.StatusCode}");

            var json = await resp.Content.ReadAsStringAsync(ct);
            var release = GitHubRelease.FromJson(json);
            if (release is null)
                return UpdateCheckResult.Fail("GitHub 响应解析失败（接口格式可能已变化）");

            bool newer = release.IsNewerThan(current);
            var asset = release.PickAsset(OsToken, ArchToken);
            var url = asset?.DownloadUrl ?? release.HtmlUrl;
            return new UpdateCheckResult(true, release, newer, null, url.Length > 0 ? url : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return UpdateCheckResult.Fail("检查已取消");
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Fail($"网络不可用：{ex.Message}");
        }
    }

    [GeneratedRegex(@"^[\w.\-]+/[\w.\-]+$")]
    private static partial Regex RepoPattern();

    /// <summary>宿主平台资产名 token（与 bundle 命名约定 mac-arm64/mac-x64/win-x64 对齐）。</summary>
    public static string OsToken =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "mac" :
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" : "win";

    public static string ArchToken =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
}

/// <summary>用系统默认浏览器打开 URL（Win ShellExecute / mac open）。失败静默，调用方已把 URL 展示为可复制文本。</summary>
public static class BrowserLauncher
{
    public static bool Open(string url)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
