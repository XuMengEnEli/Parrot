using System.Runtime.InteropServices;

namespace Parrot.Audio;

/// <summary>
/// mac 进程内播放（AVAudioEngine + AVAudioPlayerNode，objc_msgSend P/Invoke，零第三方依赖）：
/// 引擎随首次播放启动并**常驻**，音频设备不再随每次播放冷启动——实测 EShare/AB13X 会议坞
/// 冷启动吞 250–500ms 开头音频（垫静音只能止血），常驻引擎为根治。
/// 输入只吃 48k 单声道 PCM16 wav（ProcessAudioPlayer 的 afconvert 产物）；
/// 引擎起不来/中途异常返回 false，由调用方回落 afplay。
/// 仅限 mac 调用（objc 句柄收在嵌套类惰性初始化，Windows 触不到）。
/// </summary>
public static class MacAudioPlayer
{
    /// <summary>引擎冷启动那次播放的引导静音（设备唤醒吞音兜底）。</summary>
    private const int ColdLeadMs = 600;

    /// <summary>引擎已热后的引导静音（避免 0ms 起播贴太死）。</summary>
    private const int SteadyLeadMs = 100;

    private const int SampleRate = 48000;

    private static readonly object Gate = new();
    private static nint _engine, _player, _format;
    private static bool _everPlayed;

    /// <summary>播放 48k 单声道 PCM16 wav；false = 引擎不可用（回落 afplay）。返回即播完（或被 Stop/取消）。</summary>
    public static async Task<bool> PlayAsync(string wavPath, CancellationToken ct = default)
    {
        try
        {
            var samples = WarmAudio.MonoPcm16(await File.ReadAllBytesAsync(wavPath, ct));
            if (samples is null || samples.Length < SampleRate / 10)
                return false;

            nint buffer;
            lock (Gate)
            {
                if (!EnsureEngineLocked())
                    return false;
                buffer = BuildBufferLocked(samples, _everPlayed ? SteadyLeadMs : ColdLeadMs);
                _everPlayed = true;
            }

            try
            {
                Objc.MsgVoid(_player, Objc.SelSchedule, buffer, 0);
                Objc.MsgVoid(_player, Objc.SelPlay);
                Objc.MsgVoid(buffer, Objc.SelRelease); // scheduleBuffer 已 retain，放开持有

                var totalFrames = (double)BufferFrames(buffer);
                var expectMs = (long)(totalFrames * 1000 / SampleRate) + 3000;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // 播放位置是节点累计值（跨次播放不归零），必须先取本次起播基准再判到位
                double baseFrames = -1;
                while (true)
                {
                    var pt = PlayerFrames();
                    if (baseFrames < 0 && pt >= 0)
                        baseFrames = pt;
                    if (baseFrames >= 0 && pt < 0) break; // 位置消失 = 节点已停
                    if (baseFrames >= 0 && pt >= baseFrames + totalFrames - SampleRate / 20)
                        break; // 播到位（50ms 容差）
                    if (baseFrames < 0 && sw.ElapsedMilliseconds > 200)
                        break; // 起播 200ms 内拿不到位置 → 只剩超时兜底
                    if (ct.IsCancellationRequested) { StopPlayback(); return true; }
                    if (sw.ElapsedMilliseconds > expectMs) break;
                    await Task.Delay(30, CancellationToken.None);
                }
                return true;
            }
            catch
            {
                StopPlayback();
                throw;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false; // 引擎/解码任何异常 → 调用方回落 afplay
        }
    }

    /// <summary>打断当前播放（引擎保持运行，随时可播下一个）。</summary>
    public static void StopPlayback()
    {
        lock (Gate)
        {
            if (_player != 0)
                Objc.MsgVoid(_player, Objc.SelStop);
        }
    }

    private static bool EnsureEngineLocked()
    {
        if (!Objc.Loaded)
            return false; // AVFAudio 都载不进来，别做无谓的 nil 消息

        if (_engine != 0 && Objc.MsgBool(_engine, Objc.SelIsRunning) != 0)
            return true;

        if (_engine == 0)
        {
            _engine = Objc.Msg(Objc.Msg(Objc.ClsEngine, Objc.SelAlloc), Objc.SelInit);
            _player = Objc.Msg(Objc.Msg(Objc.ClsPlayer, Objc.SelAlloc), Objc.SelInit);
            _format = Objc.MsgFmt(Objc.Msg(Objc.ClsFormat, Objc.SelAlloc), Objc.SelInitStdFormat, SampleRate, 1);
            Objc.MsgVoid(_engine, Objc.SelAttachNode, _player);
            var mixer = Objc.Msg(_engine, Objc.SelMainMixer);
            var output = Objc.Msg(_engine, Objc.SelOutputNode);
            Objc.Msg(_engine, Objc.SelConnect, _player, mixer, _format);
            Objc.Msg(_engine, Objc.SelConnect, mixer, output, 0); // format=nil → 用设备原生格式
        }

        if (Objc.MsgBool(_engine, Objc.SelStart, 0) != 0)
        {
            // 渲染线程要先空转几个 IO 周期，play() 立刻打会抛 "player did not see an IO cycle"
            // （ObjC 异常 .NET 接不住，直接杀进程），冷启动后垫 100ms 让图先跑起来。
            Thread.Sleep(100);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 样本 → Float32 非交错 PCM buffer；引导静音不足 targetLeadMs 时垫零补足，**从不裁剪**：
    /// 3% 幅度阈值会把弱起音（轻爆破音/气声）当静音，裁剪会切掉真实首音（用户实测"弱音听不全"即此因），
    /// 文件自带引导静音最多 ~200ms，不裁也无碍。
    /// </summary>
    private static nint BuildBufferLocked(short[] samples, int targetLeadMs)
    {
        var lead = WarmAudio.FirstLoud(samples);
        var target = SampleRate * targetLeadMs / 1000;
        var pad = Math.Max(0, target - lead);
        var frames = pad + samples.Length;

        var buffer = Objc.Msg(Objc.Msg(Objc.ClsBuffer, Objc.SelAlloc), Objc.SelInitPcmBuffer, _format, (nint)(uint)frames);
        unsafe
        {
            var channel = *(float**)Objc.Msg(buffer, Objc.SelFloatChannelData);
            for (var i = 0; i < pad; i++)
                channel[i] = 0f;
            for (var i = 0; i < samples.Length; i++)
                channel[pad + i] = samples[i] / 32768f;
        }
        Objc.MsgVoid(buffer, Objc.SelSetFrameLength, (nuint)frames);
        return buffer;
    }

    private static bool IsPlaying()
    {
        lock (Gate)
            return _player != 0 && Objc.MsgBool(_player, Objc.SelIsPlaying) != 0;
    }

    /// <summary>当前播放位置（帧，playerTime sampleTime）；节点未播/已停返回 -1。</summary>
    private static double PlayerFrames()
    {
        lock (Gate)
        {
            if (_player == 0) return -1;
            var nodeTime = Objc.Msg(_player, Objc.SelLastRenderTime);
            if (nodeTime == 0) return -1;
            var playerTime = Objc.Msg(_player, Objc.SelPlayerTimeForNodeTime, nodeTime);
            if (playerTime == 0) return -1;
            return Objc.MsgDbl(playerTime, Objc.SelSampleTime);
        }
    }

    private static long BufferFrames(nint buffer)
    {
        // frameLength getter，只用于超时兜底；读不到就按 5s 估
        var v = Objc.Msg(buffer, Objc.SelFrameLength);
        return v > 0 ? v : SampleRate * 5L;
    }

    /// <summary>objc 运行时：类/选择器句柄收在这，类型初始化推迟到首次真用（Windows 上永不触发）。</summary>
    private static class Objc
    {
        private const string LibObjC = "/usr/lib/libobjc.dylib";

        [DllImport(LibObjC)] internal static extern nint objc_getClass(string name);
        [DllImport(LibObjC)] internal static extern nint sel_registerName(string name);

        [DllImport("/usr/lib/libSystem.dylib")] private static extern nint dlopen(string path, int mode);

        // 字段按声明序初始化：先 dlopen 把 AVFAudio 载入进程（.NET 程序不链它，不 load 则 objc_getClass 全空），
        // 再取类/选择器句柄；dlopen 失败 → 句柄全 0 → 引擎起不来 → 调用方回落 afplay。
        private static readonly bool FrameworkLoaded =
            dlopen("/System/Library/Frameworks/AVFAudio.framework/AVFAudio", RtldNow) != 0
            || dlopen("/System/Library/Frameworks/AVFoundation.framework/AVFoundation", RtldNow) != 0;

        internal static bool Loaded => FrameworkLoaded;

        private const int RtldNow = 2;

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] internal static extern nint Msg(nint rcv, nint sel);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] internal static extern nint Msg(nint rcv, nint sel, nint a1);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] internal static extern nint Msg(nint rcv, nint sel, nint a1, nint a2);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] internal static extern nint Msg(nint rcv, nint sel, nint a1, nint a2, nint a3);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] internal static extern byte MsgBool(nint rcv, nint sel);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] internal static extern byte MsgBool(nint rcv, nint sel, nint a1);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] internal static extern void MsgVoid(nint rcv, nint sel);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] internal static extern void MsgVoid(nint rcv, nint sel, nint a1);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] internal static extern void MsgVoid(nint rcv, nint sel, nint a1, nint a2);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] internal static extern void MsgVoid(nint rcv, nint sel, nuint a1);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] internal static extern nint MsgFmt(nint rcv, nint sel, double a1, nuint a2);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] internal static extern double MsgDbl(nint rcv, nint sel);

        // arm64 ABI：objc_msgSend 的 double 参数走 d0、整型从 x2 起排 —— MsgFmt 的 (double, nuint) 签名正好对上
        // initWithStandardFormatWithSampleRate:channels:；BOOL 返回按 byte 读（arm64 无 stret 问题）。

        internal static readonly nint ClsEngine = objc_getClass("AVAudioEngine");
        internal static readonly nint ClsPlayer = objc_getClass("AVAudioPlayerNode");
        internal static readonly nint ClsFormat = objc_getClass("AVAudioFormat");
        internal static readonly nint ClsBuffer = objc_getClass("AVAudioPCMBuffer");

        internal static readonly nint SelAlloc = sel_registerName("alloc");
        internal static readonly nint SelInit = sel_registerName("init");
        internal static readonly nint SelRelease = sel_registerName("release");
        internal static readonly nint SelInitStdFormat = sel_registerName("initStandardFormatWithSampleRate:channels:");
        internal static readonly nint SelInitPcmBuffer = sel_registerName("initWithPCMFormat:frameCapacity:");
        internal static readonly nint SelAttachNode = sel_registerName("attachNode:");
        internal static readonly nint SelConnect = sel_registerName("connect:to:format:");
        internal static readonly nint SelStart = sel_registerName("startAndReturnError:");
        internal static readonly nint SelIsRunning = sel_registerName("isRunning");
        internal static readonly nint SelMainMixer = sel_registerName("mainMixerNode");
        internal static readonly nint SelOutputNode = sel_registerName("outputNode");
        internal static readonly nint SelSchedule = sel_registerName("scheduleBuffer:completionHandler:"); // 无单参 scheduleBuffer:，handler 传 nil
        internal static readonly nint SelPlay = sel_registerName("play");
        internal static readonly nint SelStop = sel_registerName("stop");
        internal static readonly nint SelIsPlaying = sel_registerName("isPlaying");
        internal static readonly nint SelLastRenderTime = sel_registerName("lastRenderTime");
        internal static readonly nint SelPlayerTimeForNodeTime = sel_registerName("playerTimeForNodeTime:");
        internal static readonly nint SelSampleTime = sel_registerName("sampleTime");
        internal static readonly nint SelSetFrameLength = sel_registerName("setFrameLength:");
        internal static readonly nint SelFloatChannelData = sel_registerName("floatChannelData");
        internal static readonly nint SelFrameLength = sel_registerName("frameLength");
    }
}
