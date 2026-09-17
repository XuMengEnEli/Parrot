namespace Parrot.Core.Abstractions;

/// <summary>
/// TTS 合成统一抽象。链路编排（缓存 + 降级）：
/// 句子 → Edge TTS → 系统本地；单词 → 有道 dictvoice → Edge TTS。
/// </summary>
public interface ITtsService
{
    /// <summary>把 <paramref name="text"/> 合成为本地音频文件（命中缓存则不发请求），返回 mp3 路径。</summary>
    Task<string> SynthesizeAsync(string text, TtsKind kind, string? voice = null, CancellationToken ct = default);
}

public enum TtsKind
{
    /// <summary>单词（可走有道 dictvoice，秒回）。</summary>
    Word,

    /// <summary>句子（只走 Edge TTS/系统，有道对长句大量确定性 500，禁止兜底有道）。</summary>
    Sentence,
}
