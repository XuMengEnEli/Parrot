namespace Parrot.Core;

/// <summary>
/// 单实例锁：Windows 命名 Mutex；macOS/Linux 上 .NET 的 Mutex 以文件实现，行为一致，
/// 打包 .app 后天然单实例（结论：自写 Mutex 足够，不引第三方库）。
/// 用法：Main 入口 `using var lk = new SingleInstanceLock(); if (!lk.TryAcquire()) return;`
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    private const string MutexName = "Global\\Parrot.SingleInstance";

    private Mutex? _mutex;
    private bool _disposed;

    public bool TryAcquire()
    {
        if (_mutex is not null)
            return true;

        try
        {
            _mutex = new Mutex(initiallyOwned: false, MutexName, out bool createdNew);
            if (createdNew)
                return true;

            // 已有实例：尝试短时占有（对端可能正在退出）
            try
            {
                if (_mutex.WaitOne(TimeSpan.FromSeconds(2)))
                    return true;
            }
            catch (AbandonedMutexException)
            {
                return true; // 上一实例异常退出，锁已转移给我
            }

            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        catch
        {
            // Global\ 在无权限环境（少见）可能抛异常 → 降级 Local\
            try
            {
                _mutex?.Dispose();
                _mutex = new Mutex(initiallyOwned: false, "Local\\Parrot.SingleInstance", out bool createdNew2);
                return createdNew2 || _mutex.WaitOne(TimeSpan.FromSeconds(2));
            }
            catch
            {
                return true; // 拿锁机制本身坏了 → 宁可双开也不阻止用户
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { _mutex?.ReleaseMutex(); } catch { /* 未持有时释放会抛，忽略 */ }
        _mutex?.Dispose();
        _mutex = null;
    }
}
