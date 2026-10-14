namespace Parrot.Audio;

/// <summary>
/// WAV 引导静音预热：把 PCM16 WAV 开头的静音垫足到目标时长。
/// 背景：每次 afplay 新进程重开音频设备（USB/蓝牙声卡唤醒）会吞掉开头音频，
/// 实测 EShare/AB13X 唤醒吞 250–500ms；源文件引导静音长短不一
/// （Edge ~180ms、有道 ~60ms、say 0ms），垫足后首音不再被吃。
/// public 供单测。
/// </summary>
public static class WarmAudio
{
    /// <summary>判定"有声"的幅度阈值（/32768，约 3%），低于它的开头都算静音。</summary>
    public const int LeadThreshold = 1000;

    /// <summary>现有 WAV 的引导静音时长（ms）；格式不合或解析失败返回 null。</summary>
    public static double? LeadMs(byte[] wav)
    {
        if (!TryParse(wav, out var channels, out var sampleRate, out _, out var dataSize, out var dataOff))
            return null;
        var first = FirstLoudSample(wav, dataOff, dataSize);
        return first / channels * 1000.0 / sampleRate;
    }

    /// <summary>解析**单声道** PCM16 WAV 的采样数组（进程内播放引擎的输入）；非单声道/解析失败返回 null。</summary>
    public static short[]? MonoPcm16(byte[] wav)
    {
        if (!TryParse(wav, out var channels, out _, out _, out var dataSize, out var dataOff) || channels != 1)
            return null;
        var samples = new short[dataSize / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)ReadU16(wav, dataOff + i * 2);
        return samples;
    }

    /// <summary>采样数组里第一个超过阈值的下标（全静音返回长度）。</summary>
    public static int FirstLoud(short[] samples)
    {
        for (var i = 0; i < samples.Length; i++)
            if (Math.Abs(samples[i]) > LeadThreshold)
                return i;
        return samples.Length;
    }

    /// <summary>
    /// 解析 PCM16 WAV，若开头静音不足 <paramref name="targetLeadMs"/> 则补零垫足（返回新数组）；
    /// 已足则原样返回；格式不合（非 PCM16/解析失败）返回 null。
    /// </summary>
    public static byte[]? PrependLeadIn(byte[] wav, int targetLeadMs)
    {
        if (!TryParse(wav, out var channels, out var sampleRate, out var dataSizePos, out var dataSize, out var dataOff))
            return null;

        var first = FirstLoudSample(wav, dataOff, dataSize);
        var leadMs = first / channels * 1000.0 / sampleRate;
        var padMs = targetLeadMs - leadMs;
        if (padMs <= 0)
            return wav;

        var padBytes = (int)(sampleRate * channels * 2 * padMs / 1000);
        padBytes -= padBytes % (channels * 2); // 整帧对齐

        var result = new byte[dataOff + dataSize + padBytes];
        Array.Copy(wav, result, dataOff);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(result.Length - 8));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(dataSizePos), (uint)(dataSize + padBytes));
        // data 之后若还有块则被丢弃（afconvert 产物 data 就是最后一块）
        Array.Copy(wav, dataOff, result, dataOff + padBytes, dataSize);
        Array.Clear(result, dataOff, padBytes);
        return result;
    }

    private static int FirstLoudSample(byte[] wav, int dataOff, int dataSize)
    {
        var totalSamples = dataSize / 2;
        for (var i = 0; i < totalSamples; i++)
        {
            var v = (short)ReadU16(wav, dataOff + i * 2);
            if (Math.Abs(v) > LeadThreshold)
                return i;
        }
        return totalSamples;
    }

    private static bool TryParse(byte[] wav, out int channels, out int sampleRate,
        out int dataSizePos, out int dataSize, out int dataOff)
    {
        channels = sampleRate = dataSizePos = dataSize = dataOff = 0;
        if (wav.Length < 12 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            return false;

        var bits = 0;
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            var size = (int)ReadU32(wav, pos + 4);
            if (id == "fmt " && size >= 16)
            {
                channels = ReadU16(wav, pos + 10);
                sampleRate = (int)ReadU32(wav, pos + 12);
                bits = ReadU16(wav, pos + 22);
            }
            else if (id == "data")
            {
                dataSizePos = pos + 4;
                dataOff = pos + 8;
                dataSize = Math.Min(size, wav.Length - dataOff);
                break;
            }
            pos += 8 + size + (size & 1);
        }

        return channels is >= 1 and <= 8 && sampleRate >= 8000 && bits == 16 && dataOff > 0 && dataSize >= 2;
    }

    private static uint ReadU32(byte[] b, int off)
        => (uint)(b[off] | b[off + 1] << 8 | b[off + 2] << 16 | b[off + 3] << 24);

    private static ushort ReadU16(byte[] b, int off)
        => (ushort)(b[off] | b[off + 1] << 8);
}
