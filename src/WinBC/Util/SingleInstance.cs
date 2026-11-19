namespace WinBC.Util;

/// <summary>单实例守卫：第二次启动时唤醒已运行的实例，而不是再开一个。</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private RegisteredWaitHandle? _registration;
    private bool _disposed;

    public bool IsOwner { get; }

    public SingleInstance(string instanceName)
    {
        _mutex = new Mutex(true, instanceName + ".mutex", out bool createdNew);
        IsOwner = createdNew;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".show");
    }

    /// <summary>在已运行实例里监听"再来一个"的请求。</summary>
    public void ListenForShowRequest(Action onShowRequested)
    {
        if (!IsOwner) return;
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) =>
            {
                try { onShowRequested(); } catch { }
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>通知已运行的实例把窗口叫出来。</summary>
    public void SignalExisting()
    {
        try
        {
            _showEvent.Set();
            Thread.Sleep(200);
        }
        catch
        {
            // 对方已退出等情况，忽略
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _registration?.Unregister(null); } catch { }
        try { _mutex.ReleaseMutex(); } catch { }
        try { _mutex.Dispose(); } catch { }
        try { _showEvent.Dispose(); } catch { }
    }
}
