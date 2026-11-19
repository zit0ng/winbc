using System.Diagnostics;
using WinBC.Native;

namespace WinBC.Util;

/// <summary>
/// 后台资源调优：这是个辅助工具，用户不看它的时候必须尽量"不存在"。
/// - 窗口全部收起时切到 PROCESS_MODE_BACKGROUND：CPU/IO 优先级最低、内存页优先换出
/// - 用户长时间没操作时暂停滚动采集
/// </summary>
internal static class ResourceTuning
{
    private static bool _inBackground;

    /// <summary>进入后台模式（所有窗口都收起来时调用）。</summary>
    public static void EnterBackground()
    {
        if (_inBackground) return;
        _inBackground = true;
        try
        {
            NativeMethods.SetPriorityClass(NativeMethods.GetCurrentProcess(),
                NativeMethods.PROCESS_MODE_BACKGROUND_BEGIN);
            Log.Info("已切换到后台低优先级模式");
        }
        catch (Exception ex)
        {
            Log.Warn("切换到后台模式失败: " + ex.Message);
        }
    }

    /// <summary>恢复正常优先级（要展示窗口时调用）。</summary>
    public static void ExitBackground()
    {
        if (!_inBackground) return;
        _inBackground = false;
        try
        {
            NativeMethods.SetPriorityClass(NativeMethods.GetCurrentProcess(),
                NativeMethods.PROCESS_MODE_BACKGROUND_END);
            Log.Info("已恢复正常优先级");
        }
        catch (Exception ex)
        {
            Log.Warn("恢复正常优先级失败: " + ex.Message);
        }
    }

    /// <summary>用户已经多久没碰键鼠了。</summary>
    public static TimeSpan GetIdleTime()
    {
        try
        {
            var info = new NativeMethods.LASTINPUTINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LASTINPUTINFO>(),
            };
            if (!NativeMethods.GetLastInputInfo(ref info)) return TimeSpan.Zero;

            uint now = (uint)Environment.TickCount;
            uint elapsed = unchecked(now - info.dwTime);
            // TickCount 回绕或异常值保护
            if (elapsed > int.MaxValue) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(elapsed);
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>把工作集还给系统（隐藏窗口后调用）。</summary>
    public static void TrimWorkingSet()
    {
        try
        {
            bool ok = SetProcessWorkingSetSize(GetCurrentProcessHandle(), new IntPtr(-1), new IntPtr(-1));
            Log.Info($"回收工作集: {(ok ? "成功" : "失败")}");
        }
        catch (Exception ex)
        {
            Log.Warn("回收工作集异常: " + ex.Message);
        }

        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, false, false);
        }
        catch { }
    }

    private static IntPtr GetCurrentProcessHandle()
    {
        try { return NativeMethods.GetCurrentProcess(); }
        catch { return new IntPtr(-1); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);
}
