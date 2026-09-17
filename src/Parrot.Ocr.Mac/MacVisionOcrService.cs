using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Parrot.Core.Abstractions;

namespace Parrot.Ocr.Mac;

/// <summary>
/// macOS 系统 OCR：Vision 框架（VNRecognizeTextRequest，en-US + zh-CN，完全离线）。
/// 通过 osascript -l JavaScript（JXA 的 ObjC 运行时桥）调用 —— 零原生依赖、零 P/Invoke ABI 风险，
/// 与 afplay/say 走同一进程调用思路。osascript 被禁/组件缺失时 IsAvailable=false 或识别返回 null，
/// 阅读页自动退回"扫描页只有原图"，不崩溃。
/// </summary>
public sealed class MacVisionOcrService : IOcrService
{
    private const string Osa = "/usr/bin/osascript";
    private static readonly object ProbeLock = new();
    private static volatile bool _probed, _probeStarted;
    private static bool _probeResult; // 由 _probed（volatile）安全发布

    public MacVisionOcrService() => StartProbe(); // 构造即后台预热，UI 线程首问最多等 6s

    public bool IsAvailable
    {
        get
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return false;
            if (!_probed)
            {
                StartProbe();
                lock (ProbeLock)
                {
                    if (!_probed) Monitor.Wait(ProbeLock, 6000); // 探测通常 <2s；超时按不可用（原图+占位卡降级）
                }
            }
            return _probeResult;
        }
    }

    private static void StartProbe()
    {
        lock (ProbeLock)
        {
            if (_probeStarted) return;
            _probeStarted = true;
        }
        _ = Task.Run(() =>
        {
            bool ok = false;
            try { ok = CheckAvailable(); } catch { }
            lock (ProbeLock) { _probeResult = ok; _probed = true; Monitor.PulseAll(ProbeLock); }
        });
    }

    /// <summary>Vision 无图像边长限制（0=不限 → 渲染侧保持 200dpi）。</summary>
    public int MaxImageDimensionPx => 0;

    public Task<IReadOnlyList<OcrTextLine>?> RecognizePngAsync(byte[] png, CancellationToken ct)
        => Task.Run(() => Recognize(png, ct), ct);

    // ---------- 可用性探测（一次） ----------

    private static bool CheckAvailable()
    {
        try
        {
            if (!File.Exists(Osa)) return false;
            var psi = Mk("-l", "JavaScript", "-e", """
ObjC.import('Vision');
function run() {
  try {
    if (typeof $.VNImageRequestHandler !== 'function') return 'no';
    if (typeof $.VNRecognizeTextRequest !== 'function') return 'no';
    var req = $.VNRecognizeTextRequest.alloc.init;
    return (!req || req.isNil()) ? 'no' : 'ok';
  } catch (e) { return 'no'; }
}
""");
            psi.RedirectStandardOutput = true;
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(15_000)) { try { p.Kill(true); } catch { } return false; }
            return p.StandardOutput.ReadToEnd().Trim() == "ok";
        }
        catch { return false; }
    }

    // ---------- 识别：PNG → 临时文件 → JXA/Vision → JSON 行框 ----------

    private static IReadOnlyList<OcrTextLine>? Recognize(byte[] png, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"se-ocr-{Guid.NewGuid():N}.png");
        Process? proc = null;
        try
        {
            File.WriteAllBytes(tmp, png);
            var psi = Mk("-l", "JavaScript", "-e", VisionJs, tmp);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            proc = Process.Start(psi);
            if (proc is null) return null;
            using var reg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } });
            if (!proc.WaitForExit(90_000))
            {
                try { proc.Kill(true); } catch { }
                Console.Error.WriteLine("[ocr] Vision 识别超时（>90s）");
                return null;
            }
            string stdout = proc.StandardOutput.ReadToEnd().Trim();
            string stderr = proc.StandardError.ReadToEnd().Trim();
            if (proc.ExitCode != 0 || stdout is "" or "ERR")
            {
                Console.Error.WriteLine($"[ocr] Vision 识别失败：{(stderr.Length > 200 ? stderr[..200] : stderr)}");
                return null;
            }
            ct.ThrowIfCancellationRequested();
            var (w, h) = PngSize(png);
            return ParseVisionJson(stdout, w, h);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ocr] Vision 识别异常：{ex.Message}");
            return null;
        }
        finally
        {
            proc?.Dispose();
            try { File.Delete(tmp); } catch { /* 临时文件尽力删 */ }
        }
    }

    private static ProcessStartInfo Mk(params string[] args)
    {
        var psi = new ProcessStartInfo(Osa) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>
    /// JXA 脚本：NSData → VNImageRequestHandler(initWithData:options:) → VNRecognizeTextRequest
    /// （accurate 级、中英双语、语言纠错开）→ 逐行输出归一化包围盒 JSON。
    /// </summary>
    internal const string VisionJs = """
ObjC.import('Vision');
function run(argv) {
  function warn(m) { console.log(m); } // JXA 只有 console.log，输出到 stderr
  try {
    var data = $.NSData.dataWithContentsOfFile(argv[0]);
    if (data.isNil()) { warn('cannot read image'); return 'ERR'; }
    var handler = $.VNImageRequestHandler.alloc.initWithDataOptions(data, $());
    if (handler.isNil()) { warn('handler init failed'); return 'ERR'; }
    var req = $.VNRecognizeTextRequest.alloc.init;
    if (!req || req.isNil()) { warn('request init failed'); return 'ERR'; }
    // 顺序有实测影响：en-US 在前时中文页返回 0 行，zh-CN 在前时中英文都能读出（英文结果不受影响）
    req.setRecognitionLanguages($.NSArray.arrayWithArray($(["zh-CN", "en-US"])));
    req.setUsesLanguageCorrection(true);
    var ok = handler.performRequestsError($.NSArray.arrayWithArray($([req])), $());
    if (!ok) { warn('performRequests failed'); return 'ERR'; }
    var results = req.results; // 结果挂在请求上（VNRequest.results），handler 上没有该属性
    var out = [];
    var n = +results.count;
    for (var i = 0; i < n; i++) {
      var obs = results.objectAtIndexedSubscript(i);
      var cands = obs.topCandidates(1);
      if (!cands || +cands.count < 1) continue;
      var cand = cands.objectAtIndexedSubscript(0);
      var box = obs.boundingBox;
      var s = cand.string.js;
      if (!s) continue;
      out.push({ t: s, x: +box.origin.x, y: +box.origin.y, w: +box.size.width, h: +box.size.height, c: +cand.confidence });
    }
    return JSON.stringify(out);
  } catch (e) { warn('vision error: ' + String((e && e.message) ? e.message : e)); return 'ERR'; }
}
""";

    /// <summary>
    /// Vision 归一化行框（y-up，0-1）→ OcrTextLine（像素、y 向下左上原点）。
    /// 低置信度(&lt;0.25)与空白行丢弃。内部可见，跨平台可单测。
    /// </summary>
    internal static IReadOnlyList<OcrTextLine> ParseVisionJson(string json, double imgW, double imgH)
    {
        var list = new List<OcrTextLine>();
        using var doc = JsonDocument.Parse(json);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var text = el.GetProperty("t").GetString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (el.GetProperty("c").GetDouble() < 0.25) continue;
            double x = el.GetProperty("x").GetDouble();
            double y = el.GetProperty("y").GetDouble();
            double w = el.GetProperty("w").GetDouble();
            double h = el.GetProperty("h").GetDouble();
            list.Add(new OcrTextLine(
                text,
                x * imgW,
                (1 - y - h) * imgH, // Vision y-up → 像素 y-down 左上原点
                w * imgW,
                h * imgH));
        }
        return list;
    }

    /// <summary>PNG IHDR 头取像素宽高（无第三方解码）；异常回退 A4@200dpi。</summary>
    internal static (int W, int H) PngSize(byte[] png)
    {
        if (png.Length >= 24 && png[12] == (byte)'I' && png[13] == (byte)'H' && png[14] == (byte)'D' && png[15] == (byte)'R')
        {
            int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            if (w > 0 && h > 0) return (w, h);
        }
        return (1700, 2200);
    }
}
