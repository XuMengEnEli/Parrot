using Parrot.Audio;
using Xunit;

namespace Parrot.Tests;

public class TtsCacheTests
{
    [Fact]
    public void KeyIsStableAndNormalizesWhitespace()
    {
        var a = TtsCache.ComputeKey("  vocabulary   word ", "Word", "youdao");
        var b = TtsCache.ComputeKey("vocabulary word", "Word", "youdao");
        Assert.Equal(a, b);
        Assert.Equal(24, a.Length);
    }

    [Fact]
    public void KeySeparatesVoiceAndKind()
    {
        Assert.NotEqual(
            TtsCache.ComputeKey("hello", "Word", "youdao"),
            TtsCache.ComputeKey("hello", "Sentence", "youdao"));
        Assert.NotEqual(
            TtsCache.ComputeKey("hello", "Word", "youdao"),
            TtsCache.ComputeKey("hello", "Word", "aria"));
    }

    [Fact]
    public async Task AtomicSaveThenHit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "se-tts-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new TtsCache(dir, maxBytes: 1024);
            var path = cache.PathFor("test", "Word", "youdao");
            Assert.False(File.Exists(path));

            await cache.SaveAtomicAsync(path, [(byte)'I', (byte)'D', (byte)'3', 0]);
            Assert.True(File.Exists(path));
            Assert.Equal(path, cache.PathFor("test", "Word", "youdao"));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Mp3MagicDetection()
    {
        Assert.True(YoudaoTtsService.LooksLikeMp3("ID3..."u8.ToArray()));
        Assert.True(YoudaoTtsService.LooksLikeMp3([0xFF, 0xFB, 0x90, 0x00]));
        Assert.False(YoudaoTtsService.LooksLikeMp3("{\"error\":\"returned null audio\"}"u8.ToArray()));
    }
}
