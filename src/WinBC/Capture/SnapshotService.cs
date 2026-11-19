using System.Diagnostics;
using WinBC.Models;
using WinBC.Storage;
using WinBC.Util;

namespace WinBC.Capture;

/// <summary>把"当前桌面状态"打包成一份快照。</summary>
public sealed class SnapshotService
{
    private readonly Settings _settings;
    private readonly SnapshotStore _store;
    private readonly WindowScanner _scanner;
    private readonly MediaScanner _mediaScanner;
    private readonly object _gate = new();

    public SnapshotService(Settings settings, SnapshotStore store)
    {
        _settings = settings;
        _store = store;
        _scanner = new WindowScanner(settings);
        _mediaScanner = new MediaScanner();
    }

    public Snapshot? LastSnapshot { get; private set; }

    /// <summary>上一轮的桌面指纹，用来判断"桌面根本没变"，避免无谓的落盘。</summary>
    private string? _lastFingerprint;

    /// <summary>采集一份快照并落盘。reason: shutdown | logoff | manually | rolling。</summary>
    public Snapshot Capture(string reason)
    {
        lock (_gate)
        {
            var sw = Stopwatch.StartNew();
            var snap = new Snapshot
            {
                Reason = reason,
                CreatedAt = DateTimeOffset.Now,
                Id = "",
                OsVersion = Environment.OSVersion.VersionString,
            };

            try { snap.Monitors = _scanner.EnumerateMonitors(); }
            catch (Exception ex) { snap.Warnings.Add("枚举显示器失败: " + ex.Message); }

            try { snap.Apps = _scanner.Scan(); }
            catch (Exception ex)
            {
                snap.Warnings.Add("枚举窗口失败: " + ex.Message);
                Log.Error("枚举窗口失败", ex);
            }

            try
            {
                snap.Browsers = BrowserSessionReader.ReadAll(snap.Warnings);
                // Chromium 运行时独占当前会话文件，读到的可能是旧数据；用访问历史补最近的标签
                BrowserHistorySupplement.Supplement(snap.Browsers, snap.Warnings);
                AttachBrowserWindows(snap);
                DedupeBrowserTabs(snap);
            }
            catch (Exception ex)
            {
                snap.Warnings.Add("读取浏览器标签页失败: " + ex.Message);
                Log.Error("读取浏览器标签页失败", ex);
            }

            try
            {
                // 媒体依赖标签页（网页视频）和窗口命令行（本地播放器），所以放在两者之后
                snap.Media = _mediaScanner.Scan(snap.Apps, snap.Browsers);
                // 把媒体进度回填到对应的应用条目
                foreach (var m in snap.Media)
                {
                    var host = snap.Apps.FirstOrDefault(a => a.MediaPositionSec is null &&
                        (string.Equals(a.ProcessName + ".exe", m.PlayerExe, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(a.ProcessName, m.PlayerName, StringComparison.OrdinalIgnoreCase)));
                    if (host is not null) host.MediaPositionSec = m.PositionSec;
                }
            }
            catch (Exception ex) { snap.Warnings.Add("识别媒体失败: " + ex.Message); }

            snap.CaptureMs = sw.ElapsedMilliseconds;

            // 滚动快照：桌面跟上一轮一模一样就不重写文件（省一次磁盘 IO）
            bool unchanged = string.Equals(_lastFingerprint, ComputeFingerprint(snap), StringComparison.Ordinal);
            _lastFingerprint = ComputeFingerprint(snap);

            try
            {
                if (string.Equals(reason, "rolling", StringComparison.OrdinalIgnoreCase))
                {
                    if (!unchanged) _store.SaveRolling(snap);
                }
                else
                {
                    _store.Save(snap);
                }

                if (!unchanged || !string.Equals(reason, "rolling", StringComparison.OrdinalIgnoreCase))
                    _store.Prune(_settings.MaxSnapshots, _settings.RetentionDays);
            }
            catch (Exception ex)
            {
                snap.Warnings.Add("保存快照失败: " + ex.Message);
                Log.Error("保存快照失败", ex);
            }

            LastSnapshot = snap;
            Log.Info($"采集完成 reason={reason} 应用={snap.AppCount} 标签={snap.TabCount} 耗时={snap.CaptureMs}ms");
            return snap;
        }
    }

    /// <summary>
    /// 桌面指纹：进程/标题/窗口矩形/最大化状态 + 标签页数量。
    /// 只要这些不变，就认为桌面没变，不必再写一次快照文件。
    /// </summary>
    private static string ComputeFingerprint(Snapshot snap)
    {
        var sb = new System.Text.StringBuilder(256);
        foreach (var a in snap.Apps.OrderBy(a => a.Pid))
        {
            sb.Append(a.Pid).Append('|')
              .Append(a.Title).Append('|')
              .Append(a.Window.State).Append('|')
              .Append(a.Window.NormalX).Append(',').Append(a.Window.NormalY).Append(',')
              .Append(a.Window.NormalWidth).Append(',').Append(a.Window.NormalHeight)
              .Append('\n');
        }
        sb.Append("tabs=").Append(snap.TabCount);
        return sb.ToString();
    }

    /// <summary>
    /// 同一个 profile 可能被解析成多个窗口条目（历史会话里残留的旧窗口 id），
    /// 导致同一批标签在多个"窗口"里各出现一次。这里按 profile 做 URL 级去重，
    /// 保留每个 URL 第一次出现的位置，空的窗口条目直接丢掉。
    /// </summary>
    private static void DedupeBrowserTabs(Snapshot snap)
    {
        foreach (var group in snap.Browsers
                     .GroupBy(b => b.ProfileDir ?? b.UserDataDir ?? b.BrowserId, StringComparer.OrdinalIgnoreCase)
                     .ToList())
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bw in group)
            {
                int before = bw.Tabs.Count;
                bw.Tabs.RemoveAll(t => string.IsNullOrWhiteSpace(t.Url) || !seen.Add(t.Url));
                if (bw.Tabs.Count != before)
                    Log.Info($"浏览器标签去重 {bw.DisplayName}: {before} → {bw.Tabs.Count}");
            }
        }

        int removed = snap.Browsers.RemoveAll(b => b.Tabs.Count == 0);
        if (removed > 0) Log.Info($"移除了 {removed} 个没有有效标签的浏览器窗口条目");
    }

    /// <summary>把浏览器进程的窗口位置贴到对应 BrowserWindowEntry 上，便于复原时定位。</summary>
    private static void AttachBrowserWindows(Snapshot snap)
    {
        foreach (var browser in snap.Browsers)
        {
            if (browser.Window is not null) continue;
            var exeName = string.IsNullOrEmpty(browser.ExePath)
                ? browser.BrowserId
                : Path.GetFileNameWithoutExtension(browser.ExePath);

            var hosts = snap.Apps
                .Where(a => string.Equals(a.ProcessName, exeName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.ZOrder)
                .ToList();

            if (hosts.Count == 0) continue;
            var host = browser.WindowIndex < hosts.Count ? hosts[browser.WindowIndex] : hosts[0];
            browser.Window = host.Window;
        }
    }
}
