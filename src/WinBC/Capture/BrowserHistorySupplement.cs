using System.Runtime.InteropServices;
using WinBC.Models;
using WinBC.Util;

namespace WinBC.Capture;

/// <summary>
/// Chromium 系浏览器运行时会独占当前 Session_ 文件（实测 FileShare 各种组合都拿不到），
/// 于是我们只能读到"上一份已落盘"的会话，标签页会滞后。
/// 这里用浏览器自己的 History 数据库（winsqlite3 只读打开）补上最近访问过的页面，
/// 只在"会话文件明显陈旧或没解析出标签"时才动手，避免把用户早已关掉的页面也带回来。
/// </summary>
internal static class BrowserHistorySupplement
{
    private const int SQLITE_OK = 0;
    private const int SQLITE_ROW = 100;
    private const int SQLITE_OPEN_READONLY = 1;
    /// <summary>Chrome 时间戳 epoch（1601-01-01）到 Unix epoch 的毫秒偏移，用于换算。</summary>
    private const long ChromeEpochOffsetSeconds = 11644473600L;

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern long sqlite3_column_int64(IntPtr stmt, int col);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr db);

    /// <summary>
    /// 给每份 BrowserWindowEntry 补充最近访问的标签页。
    /// 逻辑：读历史里最近 <paramref name="recentMinutes"/> 分钟内访问过的页面，
    /// 把还不在标签列表里的追加到末尾（最多 <paramref name="maxExtraTabs"/> 条）。
    /// 宁可多记一条让用户在界面上取消勾选，也不要漏掉他关机前真的开着的页面。
    /// </summary>
    public static void Supplement(List<BrowserWindowEntry> browsers, List<string> warnings,
        int recentMinutes = 30, int maxExtraTabs = 8)
    {
        if (browsers is null || browsers.Count == 0) return;
        if (!File.Exists(Path.Combine(Environment.SystemDirectory, "winsqlite3.dll"))) return;

        // 同一个 profile 可能被拆成多个窗口条目，但历史只有一份 —— 必须按 profile 去重，
        // 否则每个窗口各补一遍，标签会成倍重复。
        var groups = new Dictionary<string, List<BrowserWindowEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var bw in browsers)
        {
            var dir = ResolveProfileDir(bw);
            if (string.IsNullOrEmpty(dir)) continue;
            if (!groups.TryGetValue(dir, out var list))
            {
                list = new List<BrowserWindowEntry>();
                groups[dir] = list;
            }
            list.Add(bw);
        }

        foreach (var pair in groups)
        {
            var profileDir = pair.Key;
            var group = pair.Value;

            try
            {
                var historyPath = Path.Combine(profileDir, "History");
                if (!File.Exists(historyPath)) continue;

                // 该 profile 下所有窗口里已经有的 URL 都要算进去
                var existing = new HashSet<string>(
                    group.SelectMany(g => g.Tabs).Select(t => t.Url),
                    StringComparer.OrdinalIgnoreCase);

                var recent = ReadRecentUrls(historyPath, recentMinutes, maxExtraTabs + existing.Count + 10);
                if (recent.Count == 0) continue;

                // 只补到第一个窗口，避免同一批 URL 在多个窗口里各出现一次
                var target = group[0];
                int nextIndex = target.Tabs.Count == 0 ? 0 : target.Tabs.Max(t => t.Index) + 1;
                int added = 0;

                foreach (var (url, title) in recent)
                {
                    if (added >= maxExtraTabs) break;
                    if (existing.Contains(url)) continue;
                    if (IsNoise(url)) continue;

                    target.Tabs.Add(new BrowserTabEntry
                    {
                        Url = url,
                        Title = title,
                        Index = nextIndex++,
                        Selected = false,
                        Pinned = false,
                    });
                    existing.Add(url);
                    added++;
                }

                if (added > 0)
                {
                    warnings.Add($"[browser] {target.DisplayName}: 会话文件被浏览器独占，已从访问历史补充 {added} 个最近标签");
                    Log.Info($"浏览器历史补充 {target.DisplayName} +{added}（profile={Path.GetFileName(profileDir)}）");
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"[browser] {Path.GetFileName(profileDir)}: 历史补充失败 {ex.Message}");
            }
        }
    }

    /// <summary>把一条浏览器记录归到它的 profile 目录。</summary>
    private static string? ResolveProfileDir(BrowserWindowEntry bw)
    {
        if (!string.IsNullOrEmpty(bw.ProfileDir)) return bw.ProfileDir;
        if (!string.IsNullOrEmpty(bw.UserDataDir))
        {
            return string.IsNullOrEmpty(bw.ProfileName)
                ? bw.UserDataDir
                : Path.Combine(bw.UserDataDir, bw.ProfileName);
        }
        return null;
    }

    /// <summary>读 History 里最近访问的 URL（按访问时间倒序）。数据库会被拷到临时目录再读，避免锁冲突。</summary>
    private static List<(string Url, string Title)> ReadRecentUrls(string historyPath, int recentMinutes, int limit)
    {
        var result = new List<(string, string)>();
        string temp = Path.Combine(Path.GetTempPath(), $"winbc_hist_{Guid.NewGuid():N}.db");

        try
        {
            File.Copy(historyPath, temp, true);

            // Chrome 时间：自 1601-01-01 起的微秒数
            long threshold = (DateTime.UtcNow.ToFileTimeUtc() - recentMinutes * 60L * 10_000_000L) / 10L;
            string sql = "SELECT url, COALESCE(title,''), last_visit_time FROM urls " +
                         $"WHERE last_visit_time > {threshold} ORDER BY last_visit_time DESC LIMIT {Math.Max(1, limit)}";

            if (sqlite3_open_v2(Utf8Z(temp), out IntPtr db, SQLITE_OPEN_READONLY, IntPtr.Zero) != SQLITE_OK || db == IntPtr.Zero)
                return result;

            IntPtr stmt = IntPtr.Zero;
            try
            {
                if (sqlite3_prepare_v2(db, Utf8Z(sql), -1, out stmt, IntPtr.Zero) != SQLITE_OK || stmt == IntPtr.Zero)
                    return result;

                while (sqlite3_step(stmt) == SQLITE_ROW)
                {
                    var urlPtr = sqlite3_column_text(stmt, 0);
                    var titlePtr = sqlite3_column_text(stmt, 1);
                    if (urlPtr == IntPtr.Zero) continue;

                    var url = Marshal.PtrToStringUTF8(urlPtr) ?? "";
                    var title = titlePtr == IntPtr.Zero ? "" : (Marshal.PtrToStringUTF8(titlePtr) ?? "");
                    if (url.Length < 8) continue;
                    if (url.Contains(' ')) continue;

                    result.Add((url, title));
                }
            }
            finally
            {
                if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                sqlite3_close(db);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取浏览历史失败: " + ex.Message);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }

        return result;
    }

    private static byte[] Utf8Z(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        var withZero = new byte[bytes.Length + 1];
        Array.Copy(bytes, withZero, bytes.Length);
        return withZero;
    }

    private static bool IsNoise(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;

        // 浏览器内部页 / 扩展页 / 开发工具
        if (url.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.StartsWith("edge://", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return true;

        // 一眼就是统计/跳转/登录的地址
        string[] noiseHosts =
        {
            "googleads", "doubleclick", "googletagmanager", "google-analytics",
            "gstatic.com/generate_204", "/favicon", "/gen_204",
            "login.live.com/oauth", "accounts.google.com/o/oauth",
            "login.microsoftonline.com/common/oauth", "/oauth2/authorize",
            "passport.bilibili.com", "/cdn-cgi/", "chrome-proxy",
        };
        foreach (var n in noiseHosts)
            if (url.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
