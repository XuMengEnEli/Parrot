using System.Net;
using Parrot.Core.Abstractions;

namespace Parrot.Audio;

/// <summary>
/// 有道 dictvoice：免费、无 key、大陆直连，仅用于**单词**（实测句子大量确定性 HTTP 500）。
/// 串行队列 + 请求间隔防限流；失败抛异常由上层降级链处理；会话内负缓存确定性失败的文本。
/// </summary>
public sealed class YoudaoTtsService : ITtsService
{
    private readonly HttpClient _http;
    private readonly TtsCache _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _negativeCache = new();
    private DateTime _lastRequestAt = DateTime.MinValue;

    public YoudaoTtsService(HttpClient http, TtsCache cache)
    {
        _http = http;
        _cache = cache;
    }

    public async Task<string> SynthesizeAsync(string text, TtsKind kind, string? voice = null, CancellationToken ct = default)
    {
        if (kind != TtsKind.Word)
            throw new NotSupportedException("有道 dictvoice 对长句大量确定性 HTTP 500（2026-09-15 实测），句子请走 Edge TTS");

        var word = text.Trim();
        var cachedPath = _cache.PathFor(word, nameof(TtsKind.Word), "youdao");
        if (File.Exists(cachedPath))
            return cachedPath;

        lock (_negativeCache)
        {
            if (_negativeCache.Contains(word))
                throw new InvalidOperationException($"有道对 \"{word}\" 此前确定性失败，直接降级");
        }

        await _gate.WaitAsync(ct);
        try
        {
            // 二次检查（排队期间可能已被预取）
            if (File.Exists(cachedPath))
                return cachedPath;

            // 限速：距上次请求不足间隔则补齐
            var since = DateTime.UtcNow - _lastRequestAt;
            if (since < TtsCache.WordRequestMinInterval)
                await Task.Delay(TtsCache.WordRequestMinInterval - since, ct);

            var url = $"https://dict.youdao.com/dictvoice?audio={Uri.EscapeDataString(word)}&type=2";
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
            _lastRequestAt = DateTime.UtcNow;

            if (!resp.IsSuccessStatusCode)
            {
                RememberFailure(word);
                throw new HttpRequestException($"youdao dictvoice HTTP {(int)resp.StatusCode}");
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (!LooksLikeMp3(bytes))
            {
                RememberFailure(word);
                throw new IOException("youdao 返回非 mp3（可能是限流页/错误体）");
            }

            await _cache.SaveAtomicAsync(cachedPath, bytes, ct);
            _cache.TrimToCapacity();
            return cachedPath;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RememberFailure(string word)
    {
        lock (_negativeCache)
            _negativeCache.Add(word);
    }

    /// <summary>ID3v2 头或 MPEG 帧同步头（实测有效响应均带 LAME/ID3，"returned null audio" 时是错误页文本）。public 供单测。</summary>
    public static bool LooksLikeMp3(byte[] data)
    {
        if (data.Length < 4)
            return false;
        if (data[0] == 'I' && data[1] == 'D' && data[2] == '3')
            return true;
        return data[0] == 0xFF && (data[1] & 0xE0) == 0xE0;
    }
}
