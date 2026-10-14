using Parrot.Audio;
using Xunit;

namespace Parrot.Tests;

/// <summary>Edge TTS 自研客户端的 DRM 令牌：与 rany2/edge-tts drm.py 算法对拍的已知向量。</summary>
public sealed class EdgeSpeechClientTests
{
    [Fact]
    public void SecMsGec_已知时间_对拍向量()
    {
        // 向量由独立实现（node crypto，BigInt 全程无精度损失）对同一时刻计算
        var gec = EdgeSpeechClient.GenerateSecMsGec(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("1D1F24A42B558B542C95B5AB88BE54019FE16CB5EEC6E4EED5148C90A4740708", gec);
    }

    [Fact]
    public void SecMsGec_五分钟窗口内_结果相同()
    {
        var a = EdgeSpeechClient.GenerateSecMsGec(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero));
        var b = EdgeSpeechClient.GenerateSecMsGec(new DateTimeOffset(2026, 10, 13, 0, 4, 59, TimeSpan.Zero));
        var c = EdgeSpeechClient.GenerateSecMsGec(new DateTimeOffset(2026, 10, 13, 0, 5, 0, TimeSpan.Zero));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
