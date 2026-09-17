using System.Globalization;
using System.Text.Json;

namespace Parrot.Core.Update;

/// <summary>Release 的一个附件（安装包）。</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long SizeBytes);

/// <summary>
/// GitHub <c>releases/latest</c> 的精简模型 + 纯逻辑（版本比较、按平台挑安装包）。
/// 解析器对字段缺失宽容：拿不到 tag_name 视为无效响应（FromJson 返回 null）。
/// </summary>
public sealed record GitHubRelease(
    string Tag, string Name, string Body, string HtmlUrl,
    DateTimeOffset? PublishedAt, IReadOnlyList<ReleaseAsset> Assets)
{
    public static GitHubRelease? FromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("tag_name", out var tagEl) || tagEl.ValueKind != JsonValueKind.String)
                return null;
            var tag = tagEl.GetString() ?? "";
            if (tag.Length == 0) return null;

            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
                foreach (var a in assetsEl.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    var name = Str(a, "name");
                    var url = Str(a, "browser_download_url");
                    if (url is null) continue;
                    long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
                    assets.Add(new ReleaseAsset(name ?? "", url, size));
                }

            DateTimeOffset? published = null;
            var p = Str(root, "published_at");
            if (p is not null && DateTimeOffset.TryParse(p, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var dto)) published = dto;

            return new GitHubRelease(tag, Str(root, "name") ?? tag, Str(root, "body") ?? "",
                Str(root, "html_url") ?? "", published, assets);
        }
        catch (JsonException)
        {
            return null; // 代理/网关返回 HTML 等非 JSON 内容时按无效处理
        }
    }

    private static string? Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>tag → Version：去 v/V 前缀与 "-beta"/"+build" 后缀，不足两段补 .0。</summary>
    public static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0);
        var s = tag.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
        int cut = s.IndexOfAny(['-', '+']);
        if (cut >= 0) s = s[..cut];
        var parts = s.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Length > 4) return false;
        foreach (var p in parts)
            if (p.Length == 0 || !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                return false;
        if (parts.Length == 1) s += ".0";
        return Version.TryParse(s, out version!);
    }

    public bool IsNewerThan(Version current)
        => TryParseTag(Tag, out var v) && v > current;

    /// <summary>按平台挑安装包：osToken（"mac"/"win"）匹配名字，优先再匹配 archToken（"arm64"/"x64"）。</summary>
    public ReleaseAsset? PickAsset(string osToken, string archToken)
    {
        ReleaseAsset? loose = null;
        foreach (var a in Assets)
        {
            if (!a.Name.Contains(osToken, StringComparison.OrdinalIgnoreCase)) continue;
            loose ??= a;
            if (a.Name.Contains(archToken, StringComparison.OrdinalIgnoreCase)) return a;
        }
        return loose;
    }
}
