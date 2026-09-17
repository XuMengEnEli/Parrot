using System.Diagnostics;

namespace Parrot.Audio;

/// <summary>
/// 进程外播放（不自研跨平台播放器）：mac 用 afplay（50–100ms 冷启动），
/// Win 先用 PowerShell MediaPlayer 一行式，v2 可升级 NAudio 进程内（包已就位）。
/// 再次 Play 自动打断上一次（杀进程）。
/// </summary>
public sealed class ProcessAudioPlayer : Parrot.Core.Abstractions.IAudioPlayer
{
    private readonly object _sync = new();
    private Process? _current;

    public async Task PlayAsync(string filePath, CancellationToken ct = default)
    {
        Stop();

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
        Process? proc;
        lock (_sync)
        {
            proc = _current;
            _current = null;
        }

        if (proc is not null)
            Kill(proc);
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
