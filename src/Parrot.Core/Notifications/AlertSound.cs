using System.Diagnostics;

namespace Parrot.Core.Notifications;

/// <summary>
/// 阶段结束提示音：Win 用 Windows 自带声音文件（AudioElement 无法在纯 .NET 里出声，
/// 借系统播放器的命令行最稳）；mac 用 afplay + 系统音效。失败静默（非关键路径）。
/// </summary>
public static class AlertSound
{
    public static void PlayPhaseDone()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var winMedia = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Alarm01.wav");
                if (!File.Exists(winMedia))
                    winMedia = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify System.wav");
                Play(winMedia);
            }
            else
            {
                Play("/System/Library/Sounds/Glass.aiff");
            }
        }
        catch
        {
            // 提示音失败不影响计时
        }
    }

    private static void Play(string file)
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell",
                $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"(New-Object Media.SoundPlayer '{file.Replace("'", "''")}').PlaySync()\"")
            : new ProcessStartInfo("afplay", $"\"{file}\"");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        Process.Start(psi);
    }
}
