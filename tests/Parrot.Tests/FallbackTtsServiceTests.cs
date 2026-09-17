using Parrot.Audio;
using Parrot.Core.Abstractions;
using Xunit;

namespace Parrot.Tests;

/// <summary>降级链纯编排逻辑，用桩 provider 离线验证。</summary>
public class FallbackTtsServiceTests
{
    private sealed class StubTts : ITtsService
    {
        private readonly Func<Task<string>> _impl;
        public int Calls { get; private set; }
        public StubTts(Func<Task<string>> impl) => _impl = impl;
        public Task<string> SynthesizeAsync(string text, TtsKind kind, string? voice = null, CancellationToken ct = default)
        {
            Calls++;
            return _impl();
        }
    }

    private static Task<string> Ok(string path) => Task.FromResult(path);
    private static Task<string> Fail(string why) => Task.FromException<string>(new InvalidOperationException(why));

    [Fact]
    public async Task Word_UsesYoudaoFirst()
    {
        var yd = new StubTts(() => Ok("y.mp3"));
        var edge = new StubTts(() => Ok("e.mp3"));
        var sys = new StubTts(() => Ok("s.mp3"));
        var svc = new FallbackTtsService(yd, edge, sys);

        Assert.Equal("y.mp3", await svc.SynthesizeAsync("word", TtsKind.Word));
        Assert.Equal(1, yd.Calls);
        Assert.Equal(0, edge.Calls);
    }

    [Fact]
    public async Task Word_DegradesYoudaoToEdgeToSystem()
    {
        var yd = new StubTts(() => Fail("youdao down"));
        var edge = new StubTts(() => Fail("edge down"));
        var sys = new StubTts(() => Ok("s.mp3"));
        var svc = new FallbackTtsService(yd, edge, sys);

        Assert.Equal("s.mp3", await svc.SynthesizeAsync("word", TtsKind.Word));
        Assert.Equal(1, yd.Calls); // 每个 provider 各调一次
        Assert.Equal(1, edge.Calls);
        Assert.Equal(1, sys.Calls);
    }

    [Fact]
    public async Task Sentence_NeverTouchesYoudao()
    {
        var yd = new StubTts(() => Ok("y.mp3"));
        var edge = new StubTts(() => Ok("e.mp3"));
        var sys = new StubTts(() => Ok("s.mp3"));
        var svc = new FallbackTtsService(yd, edge, sys);

        // 句子主链路 Edge 成功
        Assert.Equal("e.mp3", await svc.SynthesizeAsync("A sentence.", TtsKind.Sentence));
        Assert.Equal(0, yd.Calls);

        // Edge 挂掉 → 系统兜底，仍不碰有道（有道对句子是确定性 500）
        var edge2 = new StubTts(() => Fail("403"));
        var svc2 = new FallbackTtsService(yd, edge2, sys);
        Assert.Equal("s.mp3", await svc2.SynthesizeAsync("A sentence.", TtsKind.Sentence));
        Assert.Equal(0, yd.Calls);
    }

    [Fact]
    public async Task AllFail_AggregatesReasons()
    {
        var svc = new FallbackTtsService(
            new StubTts(() => Fail("A")), new StubTts(() => Fail("B")), new StubTts(() => Fail("C")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SynthesizeAsync("w", TtsKind.Word));
        Assert.Contains("A", ex.Message);
        Assert.Contains("B", ex.Message);
        Assert.Contains("C", ex.Message);
    }

    [Fact]
    public async Task Cancellation_PropagatesWithoutDegrading()
    {
        using var cts = new CancellationTokenSource();
        var yd = new StubTts(async () =>
        {
            await cts.CancelAsync();
            throw new OperationCanceledException();
        });
        var edge = new StubTts(() => Ok("e.mp3"));
        var svc = new FallbackTtsService(yd, edge, new StubTts(() => Ok("s.mp3")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.SynthesizeAsync("w", TtsKind.Word, ct: cts.Token));
        Assert.Equal(0, edge.Calls);
    }
}
