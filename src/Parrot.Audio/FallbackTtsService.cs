using Parrot.Core.Abstractions;

namespace Parrot.Audio;

/// <summary>
/// 降级链编排：
/// 句子 → Edge → 系统本地；单词 → 有道 → Edge → 系统。
/// 任一环节成功即返回（各 provider 自带缓存检查，失败原因聚合后一次性抛出供 UI 显示）。
/// </summary>
public sealed class FallbackTtsService : ITtsService
{
    private readonly (string Name, ITtsService Service)[] _wordChain;
    private readonly (string Name, ITtsService Service)[] _sentenceChain;

    public FallbackTtsService(ITtsService youdao, ITtsService edge, ITtsService system)
    {
        _wordChain = [("youdao", youdao), ("edge", edge), ("系统", system)];
        _sentenceChain = [("edge", edge), ("系统", system)];
    }

    public async Task<string> SynthesizeAsync(string text, TtsKind kind, string? voice = null, CancellationToken ct = default)
    {
        var chain = kind == TtsKind.Word ? _wordChain : _sentenceChain;
        var errors = new List<string>();

        foreach (var (name, svc) in chain)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await svc.SynthesizeAsync(text, kind, voice, ct);
            }
            catch (OperationCanceledException)
            {
                throw; // 用户取消不是"失败降级"理由
            }
            catch (NotSupportedException)
            {
                throw; // 编程错误（如对句子请求有道）不该被吞
            }
            catch (Exception ex)
            {
                errors.Add($"{name}：{ex.Message}");
            }
        }

        throw new InvalidOperationException($"发音失败（{string.Join("；", errors)}）");
    }
}
