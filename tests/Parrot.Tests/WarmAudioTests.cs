using Parrot.Audio;
using Xunit;

namespace Parrot.Tests;

/// <summary>
/// 发音预热（WarmAudio/ProcessAudioPlayer 预热链）：
/// 引导静音垫足、已足不重复垫、坏格式回落 null、真实 afconvert 转码链（仅 mac）。
/// </summary>
public sealed class WarmAudioTests
{
    private const int Threshold = WarmAudio.LeadThreshold;

    // ———————————————— WAV 构建/解析小工具（测试自用） ————————————————

    private static byte[] Wav(int sampleRate, int channels, short[] interleaved)
    {
        var dataSize = interleaved.Length * 2;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataSize);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(dataSize);
        foreach (var s in interleaved) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>返回第一个超过阈值的采样点时间（ms）与总时长（ms）。按块遍历——afconvert 会在 fmt 后插 FLLR 填充块。</summary>
    private static (double FirstLoudMs, double TotalMs) Probe(byte[] wav)
    {
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        int channels = 0, rate = 0, dataOff = -1, dataSize = 0, pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            var size = BitConverter.ToInt32(wav, pos + 4);
            if (id == "fmt ")
            {
                channels = BitConverter.ToUInt16(wav, pos + 10);
                rate = (int)BitConverter.ToUInt32(wav, pos + 12);
            }
            else if (id == "data")
            {
                dataOff = pos + 8;
                dataSize = size;
                break;
            }
            pos += 8 + size + (size & 1);
        }
        Assert.True(dataOff > 0 && channels > 0 && rate > 0, "缺 fmt/data 块");
        var total = dataSize / 2 / channels * 1000.0 / rate;
        for (var i = 0; i < dataSize / 2; i++)
        {
            if (Math.Abs(BitConverter.ToInt16(wav, dataOff + i * 2)) > Threshold)
                return (i / channels * 1000.0 / rate, total);
        }
        return (double.PositiveInfinity, total);
    }

    private static short[] Silence(int rate, int channels, double ms)
        => new short[(int)(rate * channels * ms / 1000)];

    private static short[] Burst(int rate, int channels, double ms)
        => Enumerable.Range(0, (int)(rate * channels * ms / 1000)).Select(i => (short)(i % 2 == 0 ? 12000 : -12000)).ToArray();

    // ———————————————— 纯函数行为 ————————————————

    [Fact]
    public void 短引导静音_垫足到目标()
    {
        var src = Wav(48000, 1, Silence(48000, 1, 100).Concat(Burst(48000, 1, 60)).ToArray());
        var outBytes = WarmAudio.PrependLeadIn(src, 300);

        Assert.NotNull(outBytes);
        var (firstLoud, total) = Probe(outBytes!);
        Assert.Equal(300, firstLoud, 0); // 首个有声点垫到 ~300ms（整帧对齐误差 <1ms）
        Assert.Equal(360, total, 0);     // 原 160ms（100 静音 + 60 有声）+ 补 200ms 缺口
    }

    [Fact]
    public void 引导已足_原样返回不重复垫()
    {
        var src = Wav(48000, 1, Silence(48000, 1, 300).Concat(Burst(48000, 1, 50)).ToArray());
        Assert.Same(src, WarmAudio.PrependLeadIn(src, 300));
    }

    [Fact]
    public void 立即出声_从头垫满()
    {
        var src = Wav(44100, 2, Burst(44100, 2, 40));
        var outBytes = WarmAudio.PrependLeadIn(src, 300);

        Assert.NotNull(outBytes);
        var (firstLoud, _) = Probe(outBytes!);
        Assert.Equal(300, firstLoud, 0);
    }

    [Fact]
    public void 低于阈值_仍按静音算()
    {
        // 100ms 静音 + 两个半幅样本（约 1.5%，低于 3% 阈值）+ 有声：半幅样本须被当静音垫掉
        var src = Wav(48000, 1, Silence(48000, 1, 100).Concat(new short[] { 500, 500 }).Concat(Burst(48000, 1, 20)).ToArray());
        var outBytes = WarmAudio.PrependLeadIn(src, 300);
        Assert.NotNull(outBytes);
        var (firstLoud, _) = Probe(outBytes!);
        Assert.True(firstLoud >= 300, $"firstLoud={firstLoud}（若半幅样本被判有声，会停在 ~100ms）");
    }

    [Fact]
    public void 坏格式_返回null()
    {
        Assert.Null(WarmAudio.PrependLeadIn([], 300));
        Assert.Null(WarmAudio.PrependLeadIn(new byte[] { 1, 2, 3, 4 }, 300));
        Assert.Null(WarmAudio.PrependLeadIn("not a wav at all........"u8.ToArray(), 300));
    }

    [Fact]
    public void LeadMs_量测引导静音_坏文件返回null()
    {
        var src = Wav(48000, 1, Silence(48000, 1, 50).Concat(Burst(48000, 1, 30)).ToArray());
        Assert.Equal(50, WarmAudio.LeadMs(src)!.Value, 0);

        var padded = WarmAudio.PrependLeadIn(src, 600)!;
        Assert.Equal(600, WarmAudio.LeadMs(padded)!.Value, 0);

        Assert.Null(WarmAudio.LeadMs([1, 2, 3, 4]));
    }

    [Fact]
    public void MonoPcm16_单声道还原采样_多声道拒绝()
    {
        var mono = new short[] { 0, -5, 12000, -12000, 3 };
        Assert.Equal(mono, WarmAudio.MonoPcm16(Wav(48000, 1, mono)));

        // afconvert 会插 FLLR 填充块，解析必须按块走
        var withFiller = FllrWav(48000, 1, mono);
        Assert.Equal(mono, WarmAudio.MonoPcm16(withFiller));

        Assert.Null(WarmAudio.MonoPcm16(Wav(48000, 2, mono))); // 立体声不是引擎输入
        Assert.Null(WarmAudio.MonoPcm16([1, 2, 3, 4]));
    }

    [Fact]
    public void FirstLoud_首个有声下标_全静音返回长度()
    {
        var samples = new short[] { 0, 0, 999, -999, 2000, -3 };
        Assert.Equal(4, WarmAudio.FirstLoud(samples)); // 2000 超过阈值 1000，±999 不算
        Assert.Equal(4, WarmAudio.FirstLoud(samples[..4])); // 全不超阈值 → 返回长度
    }

    /// <summary>模拟 afconvert 的 FLLR 填充块（fmt 之后、data 之前）。</summary>
    private static byte[] FllrWav(int sampleRate, int channels, short[] interleaved)
    {
        var dataSize = interleaved.Length * 2;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + 8 + 4096 + dataSize);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write("FLLR"u8.ToArray());
        w.Write(4096);
        w.Write(new byte[4096]);
        w.Write("data"u8.ToArray());
        w.Write(dataSize);
        foreach (var s in interleaved) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }

    // ———————————————— 真实转码链（afconvert 仅 mac 存在） ————————————————

    [Fact]
    public async Task mac_afconvert转48k后_垫足并结构合法()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var dir = Path.Combine(Path.GetTempPath(), $"warm-audio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "in.wav");
            await File.WriteAllBytesAsync(src, Wav(22050, 1, Silence(22050, 1, 30).Concat(Burst(22050, 1, 80)).ToArray()));

            var conv = Path.Combine(dir, "conv.wav");
            var psi = new System.Diagnostics.ProcessStartInfo("afconvert") { UseShellExecute = false };
            foreach (var a in new[] { "-f", "WAVE", "-d", "LEI16@48000", "-c", "1", src, conv })
                psi.ArgumentList.Add(a);
            using (var proc = System.Diagnostics.Process.Start(psi)!)
            {
                await proc.WaitForExitAsync();
                Assert.Equal(0, proc.ExitCode);
            }

            var outBytes = WarmAudio.PrependLeadIn(await File.ReadAllBytesAsync(conv), 300);
            Assert.NotNull(outBytes);
            var (firstLoud, total) = Probe(outBytes!);
            Assert.Equal(300, firstLoud, 0);
            Assert.Equal(30 + 80 + 270, total, 0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
