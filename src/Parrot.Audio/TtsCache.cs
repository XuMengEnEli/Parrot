using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Parrot.Audio;

/// <summary>
/// TTS 文件缓存（缓存策略）：
/// key = sha256("{voice}|{kind}|{规范化文本}") 前 24 hex；原子落盘（.tmp + Move）防半截 mp3；
/// 按最后访问时间做 LRU 上限清理。缓存命中则离线可用。
/// </summary>
public sealed class TtsCache
{
    private const int KeyHexLength = 24;

    public TtsCache(string? directory = null, long maxBytes = 300L * 1024 * 1024)
    {
        Directory = directory ?? DefaultDirectory();
        MaxBytes = maxBytes;
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }
    public long MaxBytes { get; }

    /// <summary>有道限流经验值（连发全 500，停 2-3s 恢复）——串行请求时建议每请求间隔该值。</summary>
    public static TimeSpan WordRequestMinInterval => TimeSpan.FromMilliseconds(250);

    public string PathFor(string text, string kind, string voice)
        => System.IO.Path.Combine(Directory, ComputeKey(text, kind, voice) + ".mp3");

    public static string ComputeKey(string text, string kind, string voice)
    {
        var material = $"{voice}|{kind}|{Normalize(text)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash)[..KeyHexLength].ToLowerInvariant();
    }

    public static string Normalize(string text)
        => Regex.Replace(text.Trim(), @"\s+", " ");

    public async Task SaveAtomicAsync(string targetPath, byte[] data, CancellationToken ct = default)
    {
        var tmp = targetPath + ".tmp";
        await File.WriteAllBytesAsync(tmp, data, ct);
        File.Move(tmp, targetPath, overwrite: true);
    }

    /// <summary>超过 MaxBytes 时按最后访问时间从旧到新删除。</summary>
    public void TrimToCapacity()
    {
        var entries = new DirectoryInfo(Directory)
            .EnumerateFiles("*.mp3")
            .Select(f => (File: f, LastAccess: f.LastAccessTimeUtc))
            .ToList();
        long total = entries.Sum(e => e.File.Length);
        if (total <= MaxBytes)
            return;

        foreach (var (file, _) in entries.OrderBy(e => e.LastAccess))
        {
            try
            {
                file.Delete();
                total -= file.Length;
            }
            catch (IOException)
            {
                // 正在播的文件删不掉，跳过
            }

            if (total <= MaxBytes)
                break;
        }
    }

    public static string DefaultDirectory()
    {
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches");
        Core.LegacyUserData.MigrateInto(root);
        return System.IO.Path.Combine(root, "Parrot", "tts-cache");
    }
}
