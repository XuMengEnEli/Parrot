using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Parrot.Audio;

/// <summary>
/// Edge TTS 在线合成客户端（自研极简实现，对齐 rany2/edge-tts 2026-10 协议）：
/// 2026-10 起旧域 speech.platform.bing.com 全球 403，Edge 155 改用
/// wss://api.msedgeservices.com/tts/cognitiveservices/websocket/v1，
/// 令牌作 Ocp-Apim-Subscription-Key 查询参数，Sec-MS-GEC/Version 与 MUID cookie 照旧。
/// 令牌 = SHA256(Windows 文件时间取整到 5 分钟 + TrustedClientToken) 大写 hex。
/// 流程：连上后发 speech.config + ssml 两条文本帧，收二进制帧（前 2 字节大端=头长，Path:audio 为音频），
/// 直到 Path:turn.end。音色列表仍走旧域（本工程不用）。
/// </summary>
internal static class EdgeSpeechClient
{
    public const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    public const string ChromiumFullVersion = "143.0.3650.75";
    public const string SecMsGecVersion = "1-" + ChromiumFullVersion;

    private const string WssUrl =
        "wss://api.msedgeservices.com/tts/cognitiveservices/websocket/v1?Ocp-Apim-Subscription-Key=" +
        TrustedClientToken;

    /// <summary>握手超时；整个收流（含合成）另给总兜底。</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(60);

    public static async Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        ws.Options.SetRequestHeader("User-Agent",
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            $"Chrome/{ChromiumFullVersion.Split('.')[0]}.0.0.0 Safari/537.36 Edg/{ChromiumFullVersion.Split('.')[0]}.0.0.0");
        ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        ws.Options.SetRequestHeader("Cookie", $"muid={GenerateMuid()};");

        var uri = new Uri($"{WssUrl}&ConnectionId={Hex32()}" +
                          $"&Sec-MS-GEC={GenerateSecMsGec(DateTimeOffset.UtcNow)}&Sec-MS-GEC-Version={SecMsGecVersion}");
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(ConnectTimeout);
            await ws.ConnectAsync(uri, connectCts.Token);
        }

        await SendTextAsync(ws, SpeechConfig(), ct);
        await SendTextAsync(ws, SsmlMessage(text, voice), ct);

        var audio = new MemoryStream();
        using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        recvCts.CancelAfter(TotalTimeout);
        while (true)
        {
            var payload = await ReceiveMessageAsync(ws, recvCts.Token);
            if (payload is null) // 对端 Close
                break;
            if (payload.Value.IsText)
            {
                if (GetHeader(payload.Value.Data, "Path") == "turn.end")
                    break;
                continue; // response / turn.start / audio.metadata 一律略过
            }

            var bin = payload.Value.Data;
            if (bin.Length < 2)
                throw new IOException("Edge TTS 二进制帧过短");
            var headerLen = (bin[0] << 8) | bin[1];
            if (2 + headerLen > bin.Length)
                throw new IOException("Edge TTS 二进制帧头长非法");
            var path = Encoding.ASCII.GetString(bin, 2, headerLen).Split("\r\n")
                .Select(l => l.Split(':', 2)).FirstOrDefault(p => p.Length == 2 && p[0] == "Path")?[1];
            if (path != "audio")
                throw new IOException($"Edge TTS 意外的二进制路径 {path}");
            audio.Write(bin, 2 + headerLen, bin.Length - 2 - headerLen);
        }

        if (audio.Length == 0)
            throw new IOException("Edge TTS 未返回音频（参数或服务端问题）");
        return audio.ToArray();
    }

    // ———————————————— 消息构造 ————————————————

    private static string SpeechConfig() =>
        $"X-Timestamp:{DateString()}\r\n" +
        "Content-Type:application/json; charset=utf-8\r\n" +
        "Path:speech.config\r\n\r\n" +
        "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{" +
        "\"sentenceBoundaryEnabled\":\"true\",\"wordBoundaryEnabled\":\"false\"" +
        "},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n";

    private static string SsmlMessage(string text, string voice) =>
        $"X-RequestId:{Hex32()}\r\n" +
        "Content-Type:application/ssml+xml\r\n" +
        $"X-Timestamp:{DateString()}Z\r\n" +
        "Path:ssml\r\n\r\n" +
        "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
        $"<voice name='{voice}'><prosody pitch='+0Hz' rate='+0%' volume='+0%'>" +
        System.Security.SecurityElement.Escape(text) +
        "</prosody></voice></speak>";

    private static string DateString() =>
        DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'",
            System.Globalization.CultureInfo.InvariantCulture);

    // ———————————————— 收发 ————————————————

    private static async Task SendTextAsync(ClientWebSocket ws, string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    /// <summary>收一条完整消息（聚合跨帧分段）；对端 Close 返回 null。</summary>
    private static async Task<(bool IsText, byte[] Data)?> ReceiveMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var chunks = new List<byte[]>();
        while (true)
        {
            var buffer = new byte[16 * 1024];
            var seg = await ws.ReceiveAsync(buffer, ct);
            if (seg.MessageType == WebSocketMessageType.Close)
                return chunks.Count == 0 ? null : throw new IOException("Edge TTS 消息未收完即关闭");
            var data = new byte[seg.Count];
            Array.Copy(buffer, data, seg.Count);
            chunks.Add(data);
            if (seg.EndOfMessage)
                return (seg.MessageType == WebSocketMessageType.Text, chunks.SelectMany(c => c).ToArray());
        }
    }

    private static string? GetHeader(byte[] utf8, string name)
    {
        var s = Encoding.UTF8.GetString(utf8);
        var end = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headers = end >= 0 ? s[..end] : s;
        return headers.Split("\r\n")
            .Select(l => l.Split(':', 2))
            .FirstOrDefault(p => p.Length == 2 && p[0].Trim() == name)?[1].Trim();
    }

    // ———————————————— DRM 令牌（对齐 edge-tts drm.py） ————————————————

    /// <summary>SHA256(Windows 文件时间取整到 5 分钟 + TrustedClientToken) 大写 hex；public 于单测对拍已知向量。</summary>
    public static string GenerateSecMsGec(DateTimeOffset now)
    {
        const long winEpoch = 11644473600;
        var ticks = now.ToUnixTimeSeconds() + winEpoch;
        ticks -= ticks % 300;
        ticks *= 10_000_000; // 100ns 间隔（Windows FILETIME）
        var material = $"{ticks}{TrustedClientToken}";
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(material)));
    }

    private static string GenerateMuid() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private static string Hex32() => Guid.NewGuid().ToString("N");
}
