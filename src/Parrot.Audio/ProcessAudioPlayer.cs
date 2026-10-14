using System.Diagnostics;

namespace Parrot.Audio;

/// <summary>
/// 进程外播放（不自研跨平台播放器）：mac 用常驻引擎（MacAudioPlayer，设备不冷启动不吞音），
/// 引擎不可用回落 afplay；Win 先用 PowerShell MediaPlayer 一行式。
/// 再次 Play 自动打断上一次（停引擎节点/杀进程）。
/// mac 播放前做预热（WarmAudio + afconvert 转 48k 单声道 wav，*.48.wav）：
/// 背景——每进程/每流重开音频设备会吞开头音频，实测 USB 声卡（EShare/AB13X 唤醒）吞 250–500ms；
/// 常驻引擎为根治，回落 afplay 时垫 600ms 引导静音（*.warm.wav）止血；失败回落原文件。
/// </summary>
public sealed class ProcessAudioPlayer : Parrot.Core.Abstractions.IAudioPlayer
{
    private const int TargetLeadMs = 600;

    private readonly object _sync = new();
    private Process? _current;

    public async Task PlayAsync(string filePath, CancellationToken ct = default)
    {
        Stop();

        if (OperatingSystem.IsMacOS())
        {
            var wav48 = await Ensure48kAsync(filePath, ct);
            if (wav48 is not null && await MacAudioPlayer.PlayAsync(wav48, ct))
                return;
            // 引擎不可用 → afplay 老路：垫足引导静音对冲设备冷启动吞音
            filePath = await PadAsync(filePath, ct) ?? filePath;
        }

        var (fileName, args) = OperatingSystem.IsMacOS()
            ? ("afplay", $"\"{filePath}\"")
            : ("powershell", "-NoProfile -NonInteractive -WindowStyle Hidden -Command " +
               $"\"$p=New-Object System.Windows.Media.MediaPlayer;$p.Open([uri]'{filePath}');$p.Play();" +
               "while($p.IsPlaying){Start-Sleep -m 100}\"");

        var psi = new ProcessStartInfo(fileName, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动播放器进程: {fileName}");

        lock (_sync)
            _current = proc;

        using (ct.Register(() => Kill(proc)))
        {
            try
            {
                await proc.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Kill(proc);
            }
        }

        lock (_sync)
        {
            if (ReferenceEquals(_current, proc))
                _current = null;
        }
    }

    public void Stop()
    {
        if (OperatingSystem.IsMacOS())
            MacAudioPlayer.StopPlayback();

        Process? proc;
        lock (_sync)
        {
            proc = _current;
            _current = null;
        }

        if (proc is not null)
            Kill(proc);
    }

    /// <summary>afconvert 转 48k 单声道 PCM16 wav（引擎输入格式），产物缓存为源文件旁的 *.48.wav；失败返回 null。</summary>
    private static async Task<string?> Ensure48kAsync(string source, CancellationToken ct)
    {
        var dest = source + ".48.wav";
        if (File.Exists(dest))
            return dest;

        var tmp = source + "." + Guid.NewGuid().ToString("N") + ".afconvert.wav";
        try
        {
            if (!await Convert48kMonoAsync(source, tmp, ct))
                return null;
            File.Move(tmp, dest, overwrite: true);
            return dest;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* Move 已收走 */ }
        }
    }

    /// <summary>afplay 回落路：在 48k 产物上垫足 600ms 引导静音（*.warm.wav），已达标的不重做。</summary>
    private static async Task<string?> PadAsync(string source, CancellationToken ct)
    {
        var warm = source + ".warm.wav";
        if (File.Exists(warm) && WarmLeadOk(warm))
            return warm;

        var base48 = await Ensure48kAsync(source, ct);
        if (base48 is null)
            return null;

        try
        {
            var padded = WarmAudio.PrependLeadIn(await File.ReadAllBytesAsync(base48, ct), TargetLeadMs);
            if (padded is null)
                return null;
            await File.WriteAllBytesAsync(warm, padded, ct);
            return warm;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<bool> Convert48kMonoAsync(string source, string dest, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("afconvert")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "-f", "WAVE", "-d", "LEI16@48000", "-c", "1", source, dest })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        if (proc is null)
            return false;
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode == 0 && File.Exists(dest);
    }

    /// <summary>已缓存的预热文件引导静音达标（含目标常量调大后的自动重做）才算新鲜。</summary>
    private static bool WarmLeadOk(string warm)
    {
        try
        {
            return WarmAudio.LeadMs(File.ReadAllBytes(warm)) is double lead && lead >= TargetLeadMs - 1;
        }
        catch (Exception)
        {
            return false; // 读不了/解析不了 → 重做
        }
    }

    private static void Kill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // 已退出/竞态，忽略
        }
        finally
        {
            proc.Dispose();
        }
    }
}
