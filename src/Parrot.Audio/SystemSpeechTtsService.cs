using System.Diagnostics;
using Parrot.Core.Abstractions;

namespace Parrot.Audio;

/// <summary>
/// 系统本地 TTS（最终兜底，离线可用）：
/// Win 走 SAPI（powershell System.Speech → wav），mac 走 say（→ aiff）。
/// 音质最差但永远可用；产物同样进磁盘缓存（扩展名不同，PathFor 换扩展）。
/// </summary>
public sealed class SystemSpeechTtsService : ITtsService
{
    private readonly TtsCache _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SystemSpeechTtsService(TtsCache cache)
    {
        _cache = cache;
    }

    public async Task<string> SynthesizeAsync(string text, TtsKind kind, string? voice = null, CancellationToken ct = default)
    {
        var normalized = TtsCache.Normalize(text);
        var ext = OperatingSystem.IsWindows() ? "wav" : "aiff";
        // 复用 hash key，但扩展名是系统格式；PathFor 固定 .mp3，这里手动拼
        var target = Path.Combine(_cache.Directory, TtsCache.ComputeKey(normalized, kind.ToString(), "system") + "." + ext);
        if (File.Exists(target))
            return target;

        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(target))
                return target;

            var tmp = Path.Combine(_cache.Directory, Guid.NewGuid() + "." + ext);
            try
            {
                Process? proc;
                if (OperatingSystem.IsWindows())
                {
                    // SetOutputToWaveFile + Speak 是 System.Speech 唯一"写文件"路径；单引号转义为两个单引号
                    var script =
                        "$ErrorActionPreference='Stop';" +
                        "Add-Type -AssemblyName System.Speech;" +
                        "$s=New-Object System.Speech.Synthesis.SpeechSynthesizer;" +
                        $"$s.SetOutputToWaveFile('{tmp.Replace("'", "''")}');" +
                        $"$s.Speak('{normalized.Replace("'", "''")}');";
                    proc = Start("powershell", ["-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", script], ct);
                }
                else
                {
                    // mac：say 离线；-v 指定英语音色（Samantha 缺失时 say 自动回落默认音色，不报错）
                    proc = Start("say", ["-v", voice ?? "Samantha", "-o", tmp, normalized], ct);
                }

                using (proc)
                {
                    await proc.WaitForExitAsync(ct);
                    if (proc.ExitCode != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length < 1024)
                        throw new IOException($"系统 TTS 失败（exit={proc.ExitCode}）");
                    File.Move(tmp, target, overwrite: true);
                }
            }
            finally
            {
                if (File.Exists(tmp))
                    try { File.Delete(tmp); } catch { /* 已 Move */ }
            }

            _cache.TrimToCapacity();
            return target;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Process Start(string fileName, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 " + fileName);
        ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
        return proc;
    }
}
