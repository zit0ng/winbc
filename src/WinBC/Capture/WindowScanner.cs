using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WinBC.Models;
using WinBC.Native;
using WinBC.Util;

namespace WinBC.Capture;

/// <summary>枚举当前所有"用户真正打开着的"桌面窗口，产出 AppEntry 列表。</summary>
public sealed class WindowScanner
{
    /// <summary>系统壳 / 输入法 / 驱动浮层 / 任务栏预览等窗口类，永远不记录。</summary>
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "Windows.UI.Input.InputSite.WindowClass",
        "Windows.UI.Composition.DesktopWindowContentBridge",
        "ApplicationManager_DesktopShellWindow", "MultitaskingViewFrame",
        "ForegroundStaging", "XamlExplorerHostIslandWindow", "TaskListThumbnailWnd",
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "Windows.Internal.Shell.TabProxyWindow", "tooltips_class32",
        "IME", "MSCTFIME UI", "Default IME", "GDI+ Hook Window Class",
        "Chrome_SystemMessageWindow", "CEF-OSC-WIDGET", "Base_PowerMessageWindow",
        "SysShadow", "DV2ControlHost", "NarratorHelperWindow", "MediaContextNotificationWindow",
        "NetUI Tool Window", "MozillaWindowClass_ignore", "SysHeader32",
        "Windows.UI.Core.CoreMessagingWindow", "Shell_InputSwitchTopLevelWindow",
    };

    private static readonly HashSet<string> NoisyTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program Manager", "Windows 输入体验", "Windows Input Experience",
        "NVIDIA GeForce Overlay", "Default IME", "MSCTFIME UI",
        "Microsoft Text Input Application", "Cortana",
    };

    private readonly Settings _settings;

    public WindowScanner(Settings settings) => _settings = settings;

    public List<MonitorInfo> EnumerateMonitors()
    {
        var list = new List<MonitorInfo>();
        try
        {
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref NativeMethods.RECT rc, IntPtr data) =>
            {
                try
                {
                    var mi = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
                    if (NativeMethods.GetMonitorInfoW(hMon, ref mi))
                    {
                        list.Add(new MonitorInfo
                        {
                            DeviceName = mi.szDevice ?? "",
                            X = mi.rcWork.Left,
                            Y = mi.rcWork.Top,
                            Width = mi.rcWork.Width,
                            Height = mi.rcWork.Height,
                            IsPrimary = (mi.dwFlags & 1) != 0,
                        });
                    }
                }
                catch (Exception ex) { Log.Warn("枚举显示器失败: " + ex.Message); }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("EnumDisplayMonitors 失败", ex);
        }

        if (list.Count == 0)
        {
            var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
                         ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
            list.Add(new MonitorInfo { DeviceName = @"\\.\DISPLAY1", X = 0, Y = 0, Width = bounds.Width, Height = bounds.Height, IsPrimary = true });
        }
        return list;
    }

    public List<AppEntry> Scan()
    {
        var monitors = EnumerateMonitors();
        var result = new List<AppEntry>();
        int zorder = 0;
        int myPid = Environment.ProcessId;

        try
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                try
                {
                    var entry = Inspect(hwnd, monitors, zorder, myPid);
                    if (entry is not null)
                    {
                        entry.ZOrder = zorder++;
                        result.Add(entry);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("检查窗口时出错: " + ex.Message);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("EnumWindows 失败", ex);
        }

        return result;
    }

    private AppEntry? Inspect(IntPtr hwnd, List<MonitorInfo> monitors, int zorder, int myPid)
    {
        if (!NativeMethods.IsWindowVisible(hwnd)) return null;
        if (NativeMethods.IsCloaked(hwnd)) return null;

        string title = NativeMethods.GetWindowText(hwnd);
        if (string.IsNullOrWhiteSpace(title)) return null;

        string cls = NativeMethods.GetClassName(hwnd);
        if (string.IsNullOrEmpty(cls)) return null;
        if (IgnoredClasses.Contains(cls)) return null;
        if (_settings.ExtraIgnoredClasses.Any(c => string.Equals(c, cls, StringComparison.OrdinalIgnoreCase))) return null;
        if (NoisyTitles.Contains(title)) return null;

        long exStyle = NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        bool toolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
        bool appWindow = (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0;
        if (toolWindow && !appWindow) return null;

        uint winPid = NativeMethods.GetPid(hwnd);
        if (winPid == 0 || winPid == 4) return null;
        if ((int)winPid == myPid) return null;

        bool isUwp = false;
        string? aumid = null;
        uint realPid = winPid;
        IntPtr hostHwnd = hwnd;

        // UWP：ApplicationFrameHost 托管，真正的应用在子窗口里
        if (string.Equals(cls, "ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase))
        {
            IntPtr core = FindUwpCoreWindow(hwnd);
            if (core == IntPtr.Zero)
                return null; // 空的框架窗口（应用已退出），不是用户可见的应用

            isUwp = true;
            realPid = NativeMethods.GetPid(core);
            aumid = UwpHelper.TryGetAppUserModelId(hwnd) ?? UwpHelper.TryGetAppUserModelId(core);
            if (realPid == 0 || (int)realPid == myPid) return null;
        }

        string? exePath = NativeMethods.GetProcessImagePath((int)realPid);
        string processName = exePath is not null
            ? Path.GetFileNameWithoutExtension(exePath)
            : SafeProcessName((int)realPid);
        if (string.IsNullOrEmpty(processName)) return null;

        if (_settings.IsProcessIgnored(processName))
        {
            // 仍然记录，但标记为忽略（默认不勾选、不恢复）
        }

        string? cmdLine = null;
        if (!isUwp) cmdLine = ProcessCommandLine.TryGet((int)realPid);

        var wp = NativeMethods.GetPlacement(hwnd);
        // 窗口最小化时 GetWindowRect 会给 (-21333,-21333) 这类垃圾值，改用还原位置
        bool isMinimized = wp.showCmd == NativeMethods.SW_SHOWMINIMIZED || NativeMethods.IsIconic(hwnd);
        var rect = isMinimized && wp.rcNormalPosition.Width > 0
            ? wp.rcNormalPosition
            : NativeMethods.GetExtendedFrameBounds(hwnd);

        var windowBox = new WindowBox
        {
            X = rect.Left,
            Y = rect.Top,
            Width = rect.Width,
            Height = rect.Height,
            NormalX = wp.rcNormalPosition.Left,
            NormalY = wp.rcNormalPosition.Top,
            NormalWidth = wp.rcNormalPosition.Width,
            NormalHeight = wp.rcNormalPosition.Height,
            State = wp.showCmd switch
            {
                NativeMethods.SW_SHOWMAXIMIZED => "maximized",
                NativeMethods.SW_SHOWMINIMIZED => "minimized",
                _ => "normal",
            },
            MonitorIndex = ResolveMonitorIndex(hostHwnd, monitors),
        };

        var entry = new AppEntry
        {
            ProcessName = processName,
            Title = title,
            ClassName = cls,
            ExePath = exePath,
            CommandLine = cmdLine,
            Pid = (int)realPid,
            IsUwp = isUwp,
            UwpAppId = aumid,
            Window = windowBox,
            LaunchArgs = isUwp ? null : CommandLineUtil.StripExecutable(cmdLine, exePath),
            WorkingDirectory = exePath is not null ? Path.GetDirectoryName(exePath) : null,
            StartTime = SafeStartTime((int)realPid),
        };

        entry.CanRestart = isUwp
            ? !string.IsNullOrEmpty(aumid)
            : !string.IsNullOrEmpty(exePath);

        // 忽略判定
        if (_settings.IsProcessIgnored(processName))
        {
            entry.IsIgnored = true;
            entry.IgnoreReason = "设置中已忽略该进程";
        }
        else if (isUwp && _settings.IncludeUwpApps && string.IsNullOrEmpty(aumid))
        {
            entry.IsIgnored = true;
            entry.IgnoreReason = "无法取得商店应用标识，无法自动重启";
        }
        else if (string.Equals(processName, "Widgets", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(processName, "ShellHost", StringComparison.OrdinalIgnoreCase))
        {
            entry.IsIgnored = true;
            entry.IgnoreReason = "系统小组件";
        }

        return entry;
    }

    private static IntPtr FindUwpCoreWindow(IntPtr frameHwnd)
    {
        IntPtr found = IntPtr.Zero;
        try
        {
            NativeMethods.EnumChildWindows(frameHwnd, (h, _) =>
            {
                var cn = NativeMethods.GetClassName(h);
                if (cn.StartsWith("Windows.UI.Core", StringComparison.OrdinalIgnoreCase) ||
                    cn.Equals("ApplicationFrameInputSinkWindow", StringComparison.OrdinalIgnoreCase))
                {
                    found = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return found;
    }

    private static int ResolveMonitorIndex(IntPtr hwnd, List<MonitorInfo> monitors)
    {
        try
        {
            IntPtr hMon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (hMon == IntPtr.Zero) return 0;
            var mi = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (!NativeMethods.GetMonitorInfoW(hMon, ref mi)) return 0;
            for (int i = 0; i < monitors.Count; i++)
                if (string.Equals(monitors[i].DeviceName, mi.szDevice, StringComparison.OrdinalIgnoreCase))
                    return i;
            return 0;
        }
        catch { return 0; }
    }

    private static string SafeProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch { return ""; }
    }

    private static DateTimeOffset SafeStartTime(int pid)
    {
        try { return new DateTimeOffset(Process.GetProcessById(pid).StartTime); }
        catch { return DateTimeOffset.Now; }
    }
}
