using Microsoft.Win32;
using WinBC.Capture;
using WinBC.Models;
using WinBC.Storage;
using WinBC.Util;

namespace WinBC.Triggers;

/// <summary>
/// 两路保险：
/// 1) 监听系统注销/关机事件，关机瞬间采集；
/// 2) 周期性滚动快照（每 N 秒），即使关机事件被系统掐断也能恢复到最后一刻。
/// </summary>
public sealed class ShutdownWatcher : IDisposable
{
    private readonly Settings _settings;
    private readonly SnapshotService _snapshotService;
    private readonly object _gate = new();

    private System.Threading.Timer? _timer;
    private int _rollingBusy;
    private bool _capturedForShutdown;
    private bool _started;
    private bool _disposed;

    /// <summary>用户空闲超过这么久就跳过滚动采集。</summary>
    private static readonly TimeSpan IdleSkipThreshold = TimeSpan.FromMinutes(2);

    /// <summary>采集成功后触发（只在滚动/手动采集时回调，关机采集时不保证能回调到）。</summary>
    public event Action<Snapshot>? Captured;
    public event Action<string>? Failed;

    public ShutdownWatcher(Settings settings, SnapshotService snapshotService)
    {
        _settings = settings;
        _snapshotService = snapshotService;
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;

        if (_settings.WatchSessionEnding)
        {
            try
            {
                SystemEvents.SessionEnding += OnSessionEnding;
                Log.Info("已注册关机/注销监听");
            }
            catch (Exception ex)
            {
                Log.Error("注册关机监听失败", ex);
            }
        }

        if (_settings.EnableRollingSnapshot)
        {
            var period = TimeSpan.FromSeconds(Math.Max(15, _settings.RollingIntervalSeconds));
            _timer = new System.Threading.Timer(_ => RollOnce(), null, period, period);
            Log.Info($"已启动滚动快照，间隔 {period.TotalSeconds:0}s");
        }
    }

    public void StopRolling()
    {
        try { _timer?.Dispose(); } catch { }
        _timer = null;
    }

    /// <summary>关机/注销时同步采集（阻塞，带超时）。</summary>
    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        string reason = e.Reason == SessionEndReasons.Logoff ? "logoff" : "shutdown";
        Log.Info($"收到系统 {reason} 通知，开始采集最后状态…");

        lock (_gate)
        {
            if (_capturedForShutdown) return;
            _capturedForShutdown = true;
        }

        // 不要取消关机（e.Cancel 保持 false），只做尽职采集
        try
        {
            var task = Task.Run(() => CaptureSafely(reason));
            if (!task.Wait(TimeSpan.FromSeconds(12)))
                Log.Warn("关机采集超时；滚动快照仍可用");
        }
        catch (Exception ex)
        {
            Log.Error("关机采集异常", ex);
        }
    }

    private void RollOnce()
    {
        if (Interlocked.Exchange(ref _rollingBusy, 1) == 1) return;
        try
        {
            // 用户已经好几分钟没碰键鼠了，桌面状态不会变 —— 这轮直接跳过，
            // 后台常驻程序不该在没人用的时候还在那儿扫窗口。
            if (ResourceTuning.GetIdleTime() > IdleSkipThreshold)
                return;

            CaptureSafely("rolling");
        }
        finally
        {
            Interlocked.Exchange(ref _rollingBusy, 0);
        }
    }

    private void CaptureSafely(string reason)
    {
        try
        {
            var snap = _snapshotService.Capture(reason);
            try { Captured?.Invoke(snap); } catch { }
        }
        catch (Exception ex)
        {
            Log.Error($"采集失败 reason={reason}", ex);
            try { Failed?.Invoke(ex.Message); } catch { }
        }
    }

    /// <summary>供"立即记录"按钮使用（异步执行，不阻塞 UI）。</summary>
    public Task<Snapshot?> CaptureNowAsync(string reason = "manually") =>
        Task.Run<Snapshot?>(() =>
        {
            try { return _snapshotService.Capture(reason); }
            catch (Exception ex)
            {
                Log.Error("手动采集失败", ex);
                return null;
            }
        });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRolling();

        if (_started && _settings.WatchSessionEnding)
        {
            try { SystemEvents.SessionEnding -= OnSessionEnding; } catch { }
        }
    }
}
