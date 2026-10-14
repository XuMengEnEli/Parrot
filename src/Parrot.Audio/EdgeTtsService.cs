using Parrot.Core.Abstractions;

namespace Parrot.Audio;

/// <summary>
/// Edge TTS（句子主链路）：微软在线神经音色，免费无 key。
/// 走自研 EdgeSpeechClient（对齐 rany2/edge-tts 2026-03 协议；2025-12 起握手必须带 MUID cookie，
/// EdgeTTS.DotNet 0.4.0 因此 403 全灭已弃用）。命中缓存不发请求；并发限 2 防被限流；
/// 产出非 mp3（如错误页）视为失败交降级链。
/// </summary>
public sealed class EdgeTtsService : ITtsService, IDisposable
{
    public const string DefaultVoice = "en-US-AriaNeural";

    private readonly TtsCache _cache;
    private readonly SemaphoreSlim _gate = new(2, 2);

    public EdgeTtsService(TtsCache cache)
    {
        _cache = cache;
    }

    public async Task<string> SynthesizeAsync(string text, TtsKind kind, string? voice = null, CancellationToken ct = default)
    {
        var normalized = TtsCache.Normalize(text);
        var usedVoice = voice ?? DefaultVoice;
        var target = _cache.PathFor(normalized, kind.ToString(), usedVoice);
        if (File.Exists(target))
            return target;

        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(target))
                return target;

            // 先写 .tmp 再原子替换，半截文件不会进缓存
            var tmp = target + ".tmp";
            try
            {
                var bytes = await EdgeSpeechClient.SynthesizeAsync(normalized, usedVoice, ct);
                if (!YoudaoTtsService.LooksLikeMp3(bytes))
                    throw new IOException("Edge TTS 产出非 mp3（可能 403/限流）");

                await File.WriteAllBytesAsync(tmp, bytes, ct);
                File.Move(tmp, target, overwrite: true);
                _cache.TrimToCapacity();
                return target;
            }
            finally
            {
                if (File.Exists(tmp))
                    try { File.Delete(tmp); } catch { /* 已被 Move 或竞态 */ }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
