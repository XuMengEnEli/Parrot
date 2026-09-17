namespace Parrot.Core.Abstractions;

/// <summary>
/// 音频播放抽象。实现：Windows = NAudio 进程内；macOS = afplay 子进程（50–100ms 冷启动）。
/// 再次 Play 需自动打断上一次播放。
/// </summary>
public interface IAudioPlayer
{
    Task PlayAsync(string filePath, CancellationToken ct = default);
    void Stop();
}
