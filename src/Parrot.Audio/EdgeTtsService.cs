using EdgeTTS.DotNet;
using Parrot.Core.Abstractions;

namespace Parrot.Audio;

/// <summary>
/// Edge TTS（句子主链路）：微软在线神经音色，免费无 key。
/// 用 EdgeTTS.DotNet 0.4.0 的 Communicate.SaveAsync（内置 Sec-MS-GEC 时钟纠偏 + 403 重试）。
/// 命中缓存不发请求；并发限 2 防被限流；产出非 mp3（如错误页）视为失败交降级链。
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

            // SaveAsync 直接写文件：先落 .tmp 再原子替换，半截文件不会进缓存
            var tmp = target + ".tmp";
            try
            {
                var communicate = new Communicate(normalized, usedVoice);
                await communicate.SaveAsync(tmp, ct);

                var bytes = await File.ReadAllBytesAsync(tmp, ct);
                if (!YoudaoTtsService.LooksLikeMp3(bytes))
                    throw new IOException("Edge TTS 产出非 mp3（可能 403/限流）");

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
