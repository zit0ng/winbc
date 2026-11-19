using System.Diagnostics;
using System.Text;
using WinBC;
using WinBC.Capture;
using WinBC.Models;
using WinBC.Native;
using WinBC.Restore;

namespace CaptureTest;

/// <summary>
/// 端到端自测：启动目标程序 → 摆到指定位置 → 采集 → 关掉 → 用 RestoreService 复原 → 校验位置。
/// 这是"一键复原"核心链路的真机验证。
/// 用法：CaptureTest.exe restore-test [目标exe全路径]（默认用 System32\charmap.exe，纯 Win32、无单实例问题）
/// </summary>
internal static class RestoreSelfTest
{
    public static void Run(string? targetExe)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (string.IsNullOrWhiteSpace(targetExe))
            targetExe = Path.Combine(Environment.SystemDirectory, "charmap.exe");
        if (!File.Exists(targetExe))
        {
            Console.WriteLine("找不到测试目标: " + targetExe);
            return;
        }

        string procName = Path.GetFileNameWithoutExtension(targetExe);
        Console.WriteLine($"测试目标: {targetExe}（进程名 {procName}）");

        var settings = Settings.Load();
        var scanner = new WindowScanner(settings);
        var monitors = scanner.EnumerateMonitors();
        Console.WriteLine($"显示器 {monitors[0].Width}x{monitors[0].Height} @ {monitors[0].X},{monitors[0].Y}");

        KillAll(procName, 600);

        // ---------- 1. 启动 ----------
        Console.WriteLine("1) 启动目标程序 …");
        try
        {
            Process.Start(new ProcessStartInfo(targetExe) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine("!! 启动失败: " + ex.Message);
            return;
        }

        IntPtr hwnd = IntPtr.Zero;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 30; i++)
        {
            hwnd = FindTargetWindow(procName);
            if (hwnd != IntPtr.Zero) break;
            Thread.Sleep(300);
        }
        Console.WriteLine($"   耗时 {sw.ElapsedMilliseconds}ms，窗口 0x{hwnd:X}");
        if (hwnd == IntPtr.Zero) { Console.WriteLine("!! 没找到目标窗口，测试中止"); return; }

        // 如果沿用上次状态是最大化的，先还原，测试才有意义
        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        if (NativeMethods.IsZoomed(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        Thread.Sleep(400);

        // ---------- 2. 摆位置 ----------
        int tx = 220, ty = 160, tw = 760, th = 520;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, tx, ty, tw, th,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        Thread.Sleep(800);
        Console.WriteLine($"2) 已把窗口摆到 {tx},{ty} {tw}x{th}");

        // ---------- 3. 采集 ----------
        var apps = scanner.Scan();
        var entry = apps.FirstOrDefault(a => a.ProcessName.Equals(procName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            Console.WriteLine("!! 采集结果里没有目标程序（被窗口过滤规则挡掉了？）");
            Console.WriteLine("   采集到的进程: " + string.Join(", ", apps.Select(a => a.ProcessName)));
            return;
        }
        Console.WriteLine($"3) 采集到: state={entry.Window.State} " +
                          $"normal=({entry.Window.NormalX},{entry.Window.NormalY}) {entry.Window.NormalWidth}x{entry.Window.NormalHeight} " +
                          $"rect=({entry.Window.X},{entry.Window.Y}) {entry.Window.Width}x{entry.Window.Height}");

        // ---------- 4. 关掉 ----------
        KillAll(procName, 1200);
        Console.WriteLine($"4) 已关闭，剩余进程 {Process.GetProcessesByName(procName).Length} 个");

        // ---------- 5. 复原 ----------
        var snap = new Snapshot
        {
            Id = "selftest",
            Reason = "manually",
            CreatedAt = DateTimeOffset.Now,
            Monitors = monitors,
        };
        snap.Apps.Add(entry);

        var svc = new RestoreService(settings);
        var selection = new RestoreSelection { Apps = true, Browsers = false, Media = false };
        var progress = new Progress<string>(s => Console.WriteLine("   · " + s));

        Console.WriteLine("5) 调用 RestoreService.Restore …");
        var result = svc.Restore(snap, selection, progress);
        Console.WriteLine($"   结果: {result.Summary}");
        foreach (var f in result.Failed) Console.WriteLine("   失败: " + f);

        Thread.Sleep(1800);

        // ---------- 6. 校验 ----------
        IntPtr hwnd2 = FindTargetWindow(procName);
        if (hwnd2 == IntPtr.Zero)
        {
            Console.WriteLine("!! 复原后没找到目标窗口（程序没起来）");
            return;
        }

        var wp = NativeMethods.GetPlacement(hwnd2);
        Console.WriteLine($"6) 复原后窗口: normal=({wp.rcNormalPosition.Left},{wp.rcNormalPosition.Top}) " +
                          $"{wp.rcNormalPosition.Width}x{wp.rcNormalPosition.Height} showCmd={wp.showCmd}");

        bool posOk = Math.Abs(wp.rcNormalPosition.Left - tx) <= 12 &&
                     Math.Abs(wp.rcNormalPosition.Top - ty) <= 12 &&
                     Math.Abs(wp.rcNormalPosition.Width - tw) <= 24 &&
                     Math.Abs(wp.rcNormalPosition.Height - th) <= 24;

        Console.WriteLine(posOk
            ? "✅ PASS：程序被重新拉起，且窗口位置/大小还原正确"
            : "❌ FAIL：程序起来了，但窗口位置不匹配");

        KillAll(procName, 300);
    }

    private static void KillAll(string procName, int waitMs)
    {
        foreach (var p in Process.GetProcessesByName(procName))
        {
            try { p.Kill(); } catch { }
            finally { p.Dispose(); }
        }
        if (waitMs > 0) Thread.Sleep(waitMs);
    }

    private static IntPtr FindTargetWindow(string procName)
    {
        // 进程 API 最稳
        foreach (var p in Process.GetProcessesByName(procName))
        {
            try
            {
                p.Refresh();
                if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
            }
            catch { }
            finally { p.Dispose(); }
        }

        // 兜底：枚举顶层窗口按 PID 匹配
        IntPtr found = IntPtr.Zero;
        try
        {
            NativeMethods.EnumWindows((h, _) =>
            {
                try
                {
                    if (!NativeMethods.IsWindowVisible(h)) return true;
                    uint pid = NativeMethods.GetPid(h);
                    if (pid == 0) return true;
                    string name;
                    try { name = Process.GetProcessById((int)pid).ProcessName; } catch { return true; }
                    if (!name.Equals(procName, StringComparison.OrdinalIgnoreCase)) return true;
                    if (string.IsNullOrEmpty(NativeMethods.GetWindowText(h))) return true;
                    found = h;
                    return false;
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return found;
    }
}
