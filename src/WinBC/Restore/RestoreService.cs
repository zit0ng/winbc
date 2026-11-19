using System.Diagnostics;
using System.Runtime.InteropServices;
using WinBC.Models;
using WinBC.Native;
using WinBC.Util;

namespace WinBC.Restore;

/// <summary>把一份快照"重放"回桌面：程序、浏览器标签页、播放器进度、窗口位置。</summary>
public sealed class RestoreService
{
    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "vivaldi", "opera", "firefox", "chromium",
        "360se", "360chrome", "qqbrowser", "sogouexplorer", "2345explorer",
    };

    private readonly Settings _settings;

    public RestoreService(Settings settings) => _settings = settings;

    public RestoreResult Restore(Snapshot snapshot, RestoreSelection selection,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new RestoreResult();
        selection ??= new RestoreSelection();

        if (snapshot is null)
        {
            result.Summary = "没有可恢复的快照";
            return result;
        }

        progress?.Report($"开始复原：{snapshot.CreatedAt:yyyy-MM-dd HH:mm:ss} 的快照");

        var restoredIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var browsers = snapshot.Browsers ?? new List<BrowserWindowEntry>();
        bool handleBrowsersHere = selection.Browsers && _settings.RestoreBrowserTabs;

        // ---------- 1. 应用程序 ----------
        if (selection.Apps)
        {
            foreach (var app in snapshot.Apps ?? new List<AppEntry>())
            {
                if (ct.IsCancellationRequested) { result.Skipped++; continue; }
                if (app.IsIgnored || !app.CanRestart)
                {
                    result.Skipped++;
                    continue;
                }
                if (selection.ExcludedAppKeys.Contains(RestoreSelection.AppKey(app)))
                {
                    result.Skipped++;
                    continue;
                }
                // 浏览器交给标签页逻辑统一处理，避免重复拉起
                if (handleBrowsersHere && browsers.Count > 0 && IsBrowserProcess(app.ExePath, app.ProcessName))
                {
                    continue;
                }

                try
                {
                    RestoreApp(app, snapshot, result, restoredIndex, progress, ct);
                }
                catch (Exception ex)
                {
                    result.Failed.Add($"{app.ProcessName}: {ex.Message}");
                    Log.Error($"恢复应用 {app.ProcessName} 失败", ex);
                }

                Sleep(120, ct);
            }
        }

        // ---------- 2. 浏览器标签页 ----------
        if (handleBrowsersHere)
        {
            try { RestoreBrowsers(snapshot, selection, result, progress, ct); }
            catch (Exception ex)
            {
                result.Failed.Add("浏览器: " + ex.Message);
                Log.Error("恢复浏览器失败", ex);
            }
        }

        // ---------- 3. 视频 / 音频 ----------
        if (selection.Media)
        {
            try { RestoreMedia(snapshot, selection, result, progress, ct); }
            catch (Exception ex)
            {
                result.Failed.Add("媒体: " + ex.Message);
                Log.Error("恢复媒体失败", ex);
            }
        }

        result.Summary = BuildSummary(result);
        progress?.Report(result.Summary);
        Log.Info(result.Summary);
        return result;
    }

    // ---------------- 应用 ----------------

    private void RestoreApp(AppEntry app, Snapshot snapshot, RestoreResult result,
        Dictionary<string, int> restoredIndex, IProgress<string>? progress, CancellationToken ct)
    {
        var before = EnumerateTopWindows().Select(w => w.Hwnd).ToHashSet();

        int wanted = restoredIndex.TryGetValue(app.ProcessName, out int n) ? n : 0;
        restoredIndex[app.ProcessName] = wanted + 1;

        // 该进程已有窗口：优先复用，避免把单实例应用（微信/QQ/Spotify 等）反复拉起
        var existing = FindExistingWindows(app);
        if (wanted < existing.Count)
        {
            var hwnd0 = existing[wanted];
            if (_settings.RestoreWindowPositions && app.Window.HasGeometry)
                ApplyPlacement(hwnd0, app.Window, snapshot.Monitors);
            result.Started.Add($"{app.ProcessName}（复用已运行窗口）");
            result.AppsRestored++;
            progress?.Report($"{app.ProcessName} 已在运行，直接复位窗口");
            return;
        }

        string fileName;
        string arguments;

        if (app.IsUwp && !string.IsNullOrEmpty(app.UwpAppId))
        {
            fileName = Path.Combine(Environment.SystemDirectory, "explorer.exe");
            arguments = CommandLineUtil.Quote(Capture.UwpHelper.ToShellTarget(app.UwpAppId));
        }
        else
        {
            var exe = app.ExePath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                result.Failed.Add($"{app.ProcessName}: 找不到程序文件（{exe}）");
                return;
            }
            fileName = exe;
            arguments = app.LaunchArgs ?? "";
        }

        progress?.Report($"启动 {app.ProcessName} …");

        var workingDir = !string.IsNullOrEmpty(app.WorkingDirectory) && Directory.Exists(app.WorkingDirectory)
            ? app.WorkingDirectory
            : Path.GetDirectoryName(fileName) ?? "";

        Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
            WorkingDirectory = workingDir,
        });

        IntPtr hwnd = WaitForNewWindow(before, app, TimeSpan.FromSeconds(app.IsUwp ? 30 : 18), ct);
        if (hwnd != IntPtr.Zero)
        {
            Sleep(450, ct); // 等应用自己摆好窗口，再覆盖成快照里的位置
            if (_settings.RestoreWindowPositions && app.Window.HasGeometry)
                ApplyPlacement(hwnd, app.Window, snapshot.Monitors);
            result.Started.Add(app.ProcessName);
            result.AppsRestored++;
            progress?.Report($"{app.ProcessName} 已恢复");
        }
        else
        {
            result.Started.Add($"{app.ProcessName}（已启动，未等到窗口）");
            result.AppsRestored++;
            progress?.Report($"{app.ProcessName} 已启动，但没有等到窗口出现");
        }
    }

    private static bool IsBrowserProcess(string? exePath, string processName)
    {
        var name = string.IsNullOrEmpty(exePath) ? processName : Path.GetFileNameWithoutExtension(exePath);
        return BrowserProcesses.Contains(name);
    }

    private List<IntPtr> FindExistingWindows(AppEntry app)
    {
        var list = new List<IntPtr>();
        foreach (var w in EnumerateTopWindows())
        {
            if (app.IsUwp)
            {
                if (string.Equals(w.ClassName, "ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase))
                {
                    var id = Capture.UwpHelper.TryGetAppUserModelId(w.Hwnd);
                    if (!string.IsNullOrEmpty(app.UwpAppId) &&
                        string.Equals(id, app.UwpAppId, StringComparison.OrdinalIgnoreCase))
                        list.Add(w.Hwnd);
                }
                continue;
            }

            if (string.Equals(w.ProcessName, app.ProcessName, StringComparison.OrdinalIgnoreCase))
                list.Add(w.Hwnd);
        }

        // 按 z-order（EnumWindows 顺序）返回，与原快照顺序一致性最好
        return list;
    }

    private IntPtr WaitForNewWindow(HashSet<IntPtr> before, AppEntry app, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (ct.IsCancellationRequested) return IntPtr.Zero;
            try
            {
                foreach (var w in EnumerateTopWindows())
                {
                    if (before.Contains(w.Hwnd)) continue;

                    if (app.IsUwp)
                    {
                        if (string.Equals(w.ClassName, "ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase))
                            return w.Hwnd;
                        continue;
                    }

                    if (string.Equals(w.ProcessName, app.ProcessName, StringComparison.OrdinalIgnoreCase))
                        return w.Hwnd;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("等待窗口时出错: " + ex.Message);
            }
            Thread.Sleep(250);
        }
        return IntPtr.Zero;
    }

    private static void ApplyPlacement(IntPtr hwnd, WindowBox box, List<MonitorInfo> monitors)
    {
        try
        {
            if (!NativeMethods.IsWindow(hwnd)) return;

            int w = box.NormalWidth > 0 ? box.NormalWidth : box.Width;
            int h = box.NormalHeight > 0 ? box.NormalHeight : box.Height;
            int x = box.NormalWidth > 0 ? box.NormalX : box.X;
            int y = box.NormalWidth > 0 ? box.NormalY : box.Y;
            if (w <= 0 || h <= 0) return;

            // 分辨率变了 / 副屏拔掉了：把窗口挪回主屏中央，而不是丢到看不见的地方
            if (!IntersectsAnyMonitor(x, y, w, h, monitors))
            {
                var primary = monitors?.FirstOrDefault(m => m.IsPrimary) ?? monitors?.FirstOrDefault();
                if (primary is not null)
                {
                    x = primary.X + Math.Max(0, (primary.Width - w) / 2);
                    y = primary.Y + Math.Max(0, (primary.Height - h) / 2);
                }
                else
                {
                    x = 60; y = 60;
                }
            }

            var wp = new NativeMethods.WINDOWPLACEMENT
            {
                length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>(),
                showCmd = box.State switch
                {
                    "maximized" => NativeMethods.SW_SHOWMAXIMIZED,
                    "minimized" => NativeMethods.SW_SHOWMINIMIZED,
                    _ => NativeMethods.SW_SHOWNORMAL,
                },
                rcNormalPosition = new NativeMethods.RECT
                {
                    Left = x,
                    Top = y,
                    Right = x + w,
                    Bottom = y + h,
                },
            };

            if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            NativeMethods.SetWindowPlacement(hwnd, ref wp);
            if (box.State == "maximized") NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWMAXIMIZED);
        }
        catch (Exception ex)
        {
            Log.Warn("设置窗口位置失败: " + ex.Message);
        }
    }

    private static bool IntersectsAnyMonitor(int x, int y, int w, int h, List<MonitorInfo>? monitors)
    {
        if (monitors is null || monitors.Count == 0) return true;
        foreach (var m in monitors)
        {
            int left = Math.Max(x, m.X);
            int top = Math.Max(y, m.Y);
            int right = Math.Min(x + w, m.X + m.Width);
            int bottom = Math.Min(y + h, m.Y + m.Height);
            // 至少 120x80 可见才算"还在屏幕上"
            if (right - left >= 120 && bottom - top >= 80) return true;
        }
        return false;
    }

    // ---------------- 浏览器 ----------------

    private void RestoreBrowsers(Snapshot snapshot, RestoreSelection selection, RestoreResult result,
        IProgress<string>? progress, CancellationToken ct)
    {
        foreach (var bw in snapshot.Browsers ?? new List<BrowserWindowEntry>())
        {
            if (ct.IsCancellationRequested) return;

            var urls = bw.Tabs
                .Where(t => !string.IsNullOrWhiteSpace(t.Url))
                .Select(t => t.Url.Trim())
                .Where(u => !selection.ExcludedUrls.Contains(u))
                .Where(u => !IsBlankPage(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (urls.Count == 0)
            {
                result.Skipped++;
                continue;
            }

            var exe = ResolveBrowserExe(bw);
            if (exe is null)
            {
                result.Failed.Add($"{bw.DisplayName}: 找不到浏览器程序");
                continue;
            }

            var name = !string.IsNullOrEmpty(bw.DisplayName) ? bw.DisplayName : bw.BrowserId;
            progress?.Report($"打开 {name} 的 {urls.Count} 个标签页 …");

            bool isFirefox = string.Equals(bw.BrowserId, "firefox", StringComparison.OrdinalIgnoreCase)
                             || exe.EndsWith("firefox.exe", StringComparison.OrdinalIgnoreCase);

            int index = 0;
            const int batchSize = 20;

            if (isFirefox)
            {
                // Firefox 只支持一次一个 URL
                foreach (var url in urls)
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exe,
                            Arguments = "-new-tab " + CommandLineUtil.Quote(url),
                            UseShellExecute = true,
                        });
                        result.TabsRestored++;
                    }
                    catch (Exception ex)
                    {
                        result.Failed.Add($"{name}: {ex.Message}");
                        break;
                    }
                    Sleep(180, ct);
                }
                result.BrowserWindowsRestored++;
            }
            else
            {
                while (index < urls.Count)
                {
                    if (ct.IsCancellationRequested) return;
                    var chunk = urls.Skip(index).Take(batchSize).ToList();
                    index += chunk.Count;

                    var args = "--new-window " + string.Join(' ', chunk.Select(CommandLineUtil.Quote));
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exe,
                            Arguments = args,
                            UseShellExecute = true,
                        });
                        result.TabsRestored += chunk.Count;
                        result.BrowserWindowsRestored++;
                    }
                    catch (Exception ex)
                    {
                        result.Failed.Add($"{name}: {ex.Message}");
                        break;
                    }
                    Sleep(400, ct);
                }
            }
        }
    }

    private static bool IsBlankPage(string url) =>
        url.Equals("chrome://newtab/", StringComparison.OrdinalIgnoreCase) ||
        url.Equals("about:newtab", StringComparison.OrdinalIgnoreCase) ||
        url.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ||
        url.Equals("edge://newtab/", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveBrowserExe(BrowserWindowEntry bw)
    {
        if (!string.IsNullOrEmpty(bw.ExePath) && File.Exists(bw.ExePath)) return bw.ExePath;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var candidates = (bw.BrowserId ?? "").ToLowerInvariant() switch
        {
            "chrome" => new[]
            {
                Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
            },
            "edge" => new[]
            {
                Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"),
            },
            "brave" => new[]
            {
                Path.Combine(pf, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                Path.Combine(pf86, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
            },
            "vivaldi" => new[]
            {
                Path.Combine(local, @"Vivaldi\Application\vivaldi.exe"),
                Path.Combine(pf, @"Vivaldi\Application\vivaldi.exe"),
            },
            "opera" => new[]
            {
                Path.Combine(local, @"Programs\Opera\opera.exe"),
                Path.Combine(appData, @"Opera Software\Opera Stable\opera.exe"),
            },
            "firefox" => new[]
            {
                Path.Combine(pf, @"Mozilla Firefox\firefox.exe"),
                Path.Combine(pf86, @"Mozilla Firefox\firefox.exe"),
            },
            _ => Array.Empty<string>(),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    // ---------------- 媒体 ----------------

    private void RestoreMedia(Snapshot snapshot, RestoreSelection selection, RestoreResult result,
        IProgress<string>? progress, CancellationToken ct)
    {
        foreach (var m in snapshot.Media ?? new List<MediaEntry>())
        {
            if (ct.IsCancellationRequested) return;
            if (!string.Equals(m.Kind, "local", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(m.Source)) continue;

            // 用户在界面上取消了勾选
            if (selection.ExcludedMedia.Contains(m.Source))
            {
                result.Skipped++;
                continue;
            }

            // 已经被"应用"那一轮带起来了，就不要再开一次
            bool alreadyHandled = (snapshot.Apps ?? new List<AppEntry>()).Any(a =>
                !string.IsNullOrEmpty(a.LaunchArgs) &&
                a.LaunchArgs.Contains(m.Source, StringComparison.OrdinalIgnoreCase));
            if (alreadyHandled)
            {
                result.MediaRestored++;
                continue;
            }

            if (!File.Exists(m.Source))
            {
                result.Failed.Add($"{m.Title}: 文件已不存在");
                continue;
            }

            progress?.Report($"继续播放 {m.Title} …");
            try
            {
                if (!string.IsNullOrEmpty(m.PlayerExe) && File.Exists(m.PlayerExe))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = m.PlayerExe,
                        Arguments = string.IsNullOrWhiteSpace(m.ResumeArgs)
                            ? CommandLineUtil.Quote(m.Source)
                            : m.ResumeArgs,
                        UseShellExecute = true,
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo { FileName = m.Source, UseShellExecute = true });
                }
                result.MediaRestored++;
            }
            catch (Exception ex)
            {
                result.Failed.Add($"{m.Title}: {ex.Message}");
            }
            Sleep(150, ct);
        }
    }

    // ---------------- 工具 ----------------

    private readonly record struct TopWindow(IntPtr Hwnd, uint Pid, string ProcessName, string ClassName, string Title);

    private static List<TopWindow> EnumerateTopWindows()
    {
        var list = new List<TopWindow>();
        try
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                    if (NativeMethods.IsCloaked(hwnd)) return true;
                    var title = NativeMethods.GetWindowText(hwnd);
                    if (string.IsNullOrWhiteSpace(title)) return true;
                    uint pid = NativeMethods.GetPid(hwnd);
                    if (pid == 0) return true;

                    string procName = "";
                    try { procName = Process.GetProcessById((int)pid).ProcessName; }
                    catch { }

                    if (string.IsNullOrEmpty(procName))
                    {
                        var p = NativeMethods.GetProcessImagePath((int)pid);
                        if (!string.IsNullOrEmpty(p)) procName = Path.GetFileNameWithoutExtension(p);
                    }

                    list.Add(new TopWindow(hwnd, pid, procName, NativeMethods.GetClassName(hwnd), title));
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Warn("枚举顶层窗口失败: " + ex.Message);
        }
        return list;
    }

    private static string BuildSummary(RestoreResult r)
    {
        var parts = new List<string>();
        if (r.AppsRestored > 0) parts.Add($"{r.AppsRestored} 个应用");
        if (r.BrowserWindowsRestored > 0) parts.Add($"{r.BrowserWindowsRestored} 个浏览器窗口 / {r.TabsRestored} 个标签页");
        if (r.MediaRestored > 0) parts.Add($"{r.MediaRestored} 个视频音频");

        var text = parts.Count > 0 ? "已复原 " + string.Join("、", parts) : "没有需要复原的项目";
        if (r.Skipped > 0) text += $"，跳过 {r.Skipped} 项";
        if (r.Failed.Count > 0) text += $"，失败 {r.Failed.Count} 项";
        return text;
    }

    private static void Sleep(int ms, CancellationToken ct)
    {
        try { Task.Delay(ms, ct).Wait(ct); }
        catch (OperationCanceledException) { }
        catch (AggregateException) { }
    }
}
