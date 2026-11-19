using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WinBC.Capture;

using WinBC.Models;

/// <summary>
/// 浏览器会话读取器：直接解析浏览器自己持久化的会话文件，拿到"关机前打开着"的标签页。
/// <para>Chromium 系（Chrome / Edge / Brave / Vivaldi / Opera）读 SNSS 格式的
/// <c>Sessions\Last Session</c> 或 <c>Sessions\Session_*</c>；Firefox 读
/// <c>sessionstore-backups\recovery.jsonlz4</c>（mozLz4）。</para>
/// <para>设计约束：绝不抛异常；所有问题以文本追加进 <c>warnings</c>。</para>
/// </summary>
public static class BrowserSessionReader
{
    // 单个窗口 / 单个 profile 的硬上限，防止畸形文件把快照撑爆。
    private const int MaxWindowsPerProfile = 32;
    private const int MaxTabsPerProfile = 512;

    /// <summary>扫描本机所有 Chromium 系 + Firefox 浏览器，返回按窗口分组的标签页清单。
    /// 任何异常都要吞掉并写入 warnings，绝不抛出。</summary>
    public static List<BrowserWindowEntry> ReadAll(List<string> warnings)
    {
        var result = new List<BrowserWindowEntry>();
        if (warnings is null) warnings = new List<string>();

        void Warn(string msg)
        {
            try { warnings.Add(msg); } catch { /* 忽略 */ }
        }

        try { ScanChromium(result, Warn); }
        catch (Exception ex) { Warn("[browser] Chromium 扫描异常: " + ex.Message); }

        try { ScanFirefox(result, Warn); }
        catch (Exception ex) { Warn("[browser] Firefox 扫描异常: " + ex.Message); }

        try { MatchHwnds(result, Warn); }
        catch (Exception ex) { Warn("[browser] 窗口句柄匹配异常: " + ex.Message); }

        return result;
    }

    // ════════════════════════════════════════════════════════════════════
    // Chromium：SNSS 会话文件
    // ════════════════════════════════════════════════════════════════════
    //
    // 文件布局（实测 Chrome/Edge/Opera 都是 version 3）：
    //   [0..3]  magic "SNSS"
    //   [4..7]  int32 version
    //   之后重复：[uint16 size][size 字节 payload]，payload[0] = command id
    //
    // 用到的 command（payload 布局全部在真文件上逐字节核对过）：
    //   0  SetTabWindow              [int32 windowId][int32 tabId]
    //   2  SetTabIndexInWindow       [int32 tabId][int32 index]
    //   6  UpdateTabNavigation       [int32 后续长度][int32 tabId][int32 navIndex]
    //                                [int32 urlBytes][url utf8][补齐到 4]
    //                                [int32 titleChars][title utf16le][补齐到 4]
    //   7  SetSelectedNavigationIdx  [int32 tabId][int32 navIndex]
    //   8  SetSelectedTabInIndex     [int32 windowId][int32 tabIndex]
    //   1  UpdateTabNavigation       （Tabs_ 文件，布局与 6 相同）
    //
    // 三个互相印证的证据（Chrome 自带 Session_ 208KB / 138 条 command，正好走到文件尾）：
    //  · 每条 id=6 的 [1..4] 都精确等于 size-5（4585→4580、2309→2304、3485→3480），
    //    所以它是"后续字节数"，不是 tabId；真正的 tabId 在 [5..8]。
    //  · titleLen 是"字符数"：'搜索 - Microsoft 必应' T=17 而数据 34 字节；
    //    '哔哩哔哩 (゜-゜)つロ 干杯~-bilibili' T=25 而数据 50 字节 → 按字节读会得到一堆 U+FFFD。
    //  · URL 后面确实有 4 字节对齐补齐：urlLen=25 时后面 3 个 0x00，urlLen=16 时一个都没有。
    //  · 分组/选中：id=0 第二字段的取值集合 {0x4c6541,0x4c65e5,0x4c65ec,0x4c65f0,0x4c65f3,0x4c65f7}
    //    与 id=2 第一字段、id=6 的 tabId 集合完全一致；id=0 第一字段恒为 0x4c6540，
    //    与 id=8/id=14 的窗口字段相同；id=8 最后一次是 index 2，而按 id=2 最后索引，
    //    index 2 = 那个 bilibili 标签，与当时窗口标题一致。

    private sealed class ChromiumSpec
    {
        public string Id = "";
        public string Name = "";
        /// <summary>user data 根目录（%LOCALAPPDATA%\...\User Data 这一类）。</summary>
        public string? Root;
        public string[] ExeHints = Array.Empty<string>();
    }

    private static List<ChromiumSpec> BuildChromiumSpecs()
    {
        string L(string env) => Environment.GetEnvironmentVariable(env) ?? "";
        var local = L("LOCALAPPDATA");
        var roaming = L("APPDATA");
        var pf = L("ProgramFiles");
        var pf86 = L("ProgramFiles(x86)");

        return new List<ChromiumSpec>
        {
            new()
            {
                Id = "chrome", Name = "Google Chrome",
                Root = Combine(local, "Google", "Chrome", "User Data"),
                ExeHints = new[]
                {
                    Combine(pf, "Google", "Chrome", "Application", "chrome.exe") ?? "",
                    Combine(pf86, "Google", "Chrome", "Application", "chrome.exe") ?? "",
                    Combine(local, "Google", "Chrome", "Application", "chrome.exe") ?? "",
                },
            },
            new()
            {
                Id = "edge", Name = "Microsoft Edge",
                Root = Combine(local, "Microsoft", "Edge", "User Data"),
                ExeHints = new[]
                {
                    Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe") ?? "",
                    Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe") ?? "",
                    Combine(local, "Microsoft", "Edge", "Application", "msedge.exe") ?? "",
                },
            },
            new()
            {
                Id = "brave", Name = "Brave",
                Root = Combine(local, "BraveSoftware", "Brave-Browser", "User Data"),
                ExeHints = new[]
                {
                    Combine(pf, "BraveSoftware", "Brave-Browser", "Application", "brave.exe") ?? "",
                    Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe") ?? "",
                },
            },
            new()
            {
                Id = "vivaldi", Name = "Vivaldi",
                Root = Combine(local, "Vivaldi", "User Data"),
                ExeHints = new[]
                {
                    Combine(local, "Vivaldi", "Application", "vivaldi.exe") ?? "",
                    Combine(pf, "Vivaldi", "Application", "Vivaldi.exe") ?? "",
                },
            },
            new()
            {
                Id = "opera", Name = "Opera",
                Root = Combine(roaming, "Opera Software", "Opera Stable"),
                // 实测：Opera Stable\Default\Sessions（和 Chrome 一样）。老布局的根级 Sessions 由枚举兜底。
                ExeHints = new[]
                {
                    Combine(local, "Programs", "Opera", "opera.exe") ?? "",
                    Combine(pf, "Opera", "opera.exe") ?? "",
                    Combine(pf86, "Opera", "opera.exe") ?? "",
                },
            },
        };
    }

    private static void ScanChromium(List<BrowserWindowEntry> sink, Action<string> warn)
    {
        var live = SnapshotLiveBrowsers();

        foreach (var spec in BuildChromiumSpecs())
        {
            try
            {
                if (string.IsNullOrEmpty(spec.Root) || !Directory.Exists(spec.Root)) continue;

                // 只记"这次开机后真正在跑的"浏览器：历史会话文件会一直留在磁盘上，
                // 把已退出的 Edge/Opera 也记下来，一键复原时会凭空多开两个用户没开过的浏览器。
                if (!live.IsRunning(spec.Id))
                {
                    warn($"[browser] {spec.Name}: 当前未运行，跳过历史会话");
                    continue;
                }

                var profileNames = ReadLocalStateProfileNames(spec.Root);
                var exePath = live.ExePath.TryGetValue(spec.Id, out var livePath) && !string.IsNullOrEmpty(livePath)
                    ? livePath
                    : ProbeFirstExisting(spec.ExeHints);

                var profiles = EnumerateChromiumProfiles(spec);
                if (profiles.Count == 0)
                {
                    warn($"[browser] {spec.Name}: 没找到 Default/Profile * 目录");
                    continue;
                }

                foreach (var profileDir in profiles)
                {
                    try
                    {
                        ScanChromiumProfile(spec, profileDir, profileNames, exePath, sink, warn);
                    }
                    catch (Exception ex)
                    {
                        warn($"[browser] {spec.Name} {Path.GetFileName(profileDir)} 解析失败: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                warn($"[browser] {spec.Name} 根目录扫描失败: {ex.Message}");
            }
        }
    }

    private static List<string> EnumerateChromiumProfiles(ChromiumSpec spec)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(spec.Root)) return list;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(spec.Root))
            {
                var name = Path.GetFileName(dir);
                bool wanted = name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                              || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase);
                if (!wanted) continue;
                if (!Directory.Exists(Path.Combine(dir, "Sessions"))) continue;   // 没 Session 目录的直接跳过
                list.Add(dir);
            }
        }
        catch (Exception)
        {
            // 枚举失败就当作没有
        }

        // 少数布局（老版本 Opera / 定制版）把 Sessions 直接放根下：实测本机 Opera Stable 是
        // Opera Stable\Default\Sessions，但保留这条兜底。
        try
        {
            if (Directory.Exists(Path.Combine(spec.Root, "Sessions")) && !list.Contains(spec.Root))
                list.Add(spec.Root);
        }
        catch (Exception) { }

        // Default 优先，其余按名字排
        list.Sort((a, b) =>
        {
            int Rank(string p)
            {
                var n = Path.GetFileName(p);
                if (n.Equals("Default", StringComparison.OrdinalIgnoreCase)) return -1;
                var digits = new string(n.Where(char.IsDigit).ToArray());
                return int.TryParse(digits, out var v) ? v : 999;
            }
            return Rank(a).CompareTo(Rank(b));
        });
        return list;
    }

    private static void ScanChromiumProfile(
        ChromiumSpec spec,
        string profileDir,
        Dictionary<string, string> profileNames,
        string? exePath,
        List<BrowserWindowEntry> sink,
        Action<string> warn)
    {
        var sessionsDir = Path.Combine(profileDir, "Sessions");
        if (!Directory.Exists(sessionsDir)) return;

        var dirName = Path.GetFileName(profileDir);
        var profileName = profileNames.TryGetValue(dirName, out var pn) && !string.IsNullOrWhiteSpace(pn) ? pn : dirName;

        var candidates = CollectSessionFiles(sessionsDir);
        if (candidates.Count == 0)
        {
            warn($"[browser] {spec.Name}/{profileName}: Sessions 目录里没有可用的会话文件（全是 0 字节？）");
            return;
        }

        List<BrowserWindowEntry>? picked = null;
        bool warnedLocked = false;
        foreach (var (file, _) in candidates)
        {
            ParsedSession parsed;
            try { parsed = ParseSnss(file); }
            catch (Exception ex)
            {
                warn($"[browser] {spec.Name}/{profileName}: {Path.GetFileName(file)} 解析异常 {ex.Message}");
                continue;
            }

            // 浏览器运行时会用独占句柄锁住"当前"会话文件（实测 Chrome 连 FileShare.Read|Write 都打不开），
            // 只能退到上一份已落盘的文件；这里如实说明，免得 Lead 以为数据是实时的。
            if (parsed.ReadError is not null)
            {
                if (!warnedLocked)
                {
                    warn($"[browser] {spec.Name}/{profileName}: 当前会话文件正被浏览器独占（{Path.GetFileName(file)}），改用上一份已落盘的会话文件，标签可能略有滞后");
                    warnedLocked = true;
                }
                continue;
            }

            // 浏览器运行中会不断新建/截断会话文件，偶尔读到 0 字节是正常的：静默换下一个候选。
            if (parsed.Commands == 0) continue;

            if (parsed.Truncated)
                warn($"[browser] {spec.Name}/{profileName}: {Path.GetFileName(file)} 尾部不完整，已按已解析出的 {parsed.Commands} 条命令处理");

            var entries = BuildEntries(parsed, spec, profileDir, profileName, exePath, Path.GetFileName(file), warn);
            if (entries.Count == 0) continue;

            picked = entries;
            break;
        }

        if (picked is null)
        {
            warn($"[browser] {spec.Name}/{profileName}: 会话文件里没有解析出标签页");
            return;
        }

        foreach (var e in picked) sink.Add(e);
    }

    /// <summary>
    /// 选文件：优先 <c>Last Session</c>（浏览器已退出时才写），否则取 mtime 最新且 &gt;0 字节的 <c>Session_*</c>。
    /// 再不行则降级到 <c>Last Tabs</c> / <c>Tabs_*</c>（里面有 URL 但没有窗口归属）。
    /// 0 字节文件必须跳过（Chrome 运行时确实会留 0 字节的 Session_/Tabs_/Apps_）。
    /// </summary>
    private static List<(string File, bool IsTabsFile)> CollectSessionFiles(string sessionsDir)
    {
        var result = new List<(string File, bool IsTabsFile)>();

        void Add(string path, bool isTabs)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 0) result.Add((fi.FullName, isTabs));
            }
            catch (Exception) { }
        }

        var lastSession = Path.Combine(sessionsDir, "Last Session");
        Add(lastSession, false);

        var sessions = new List<FileInfo>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(sessionsDir, "Session_*"))
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length > 0) sessions.Add(fi);
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        sessions.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
        foreach (var fi in sessions) result.Add((fi.FullName, false));

        // 只有真的没有 Session 文件时才考虑 Tabs_（Tabs_ 里混着已关闭标签的历史）
        if (result.Count == 0)
        {
            Add(Path.Combine(sessionsDir, "Last Tabs"), true);
            var tabs = new List<FileInfo>();
            try
            {
                foreach (var f in Directory.EnumerateFiles(sessionsDir, "Tabs_*"))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.Length > 0) tabs.Add(fi);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            tabs.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            foreach (var fi in tabs) result.Add((fi.FullName, true));
        }

        // Last Session 排第一，但若它比最新的 Session_ 还旧，说明浏览器在跑、Last Session 是上一轮的残渣，
        // 这时把最新的 Session_ 提到前面（任务约定的"优先 Last Session"只在它确实最新时成立）。
        if (result.Count >= 2 && !result[0].Item2 && !result[1].Item2)
        {
            try
            {
                var a = new FileInfo(result[0].File).LastWriteTimeUtc;
                var b = new FileInfo(result[1].File).LastWriteTimeUtc;
                if (b > a) result.Reverse(0, 2);
            }
            catch (Exception) { }
        }

        return result;
    }

    private sealed class NavEntry
    {
        public string Url = "";
        public string Title = "";
        public int Seq;
    }

    private sealed class ParsedSession
    {
        /// <summary>tabId → 窗口 id（command 0）。</summary>
        public readonly Dictionary<int, int> TabWindow = new();
        /// <summary>tabId → (窗口内序号, 该条命令的序号)；序号用于"后写覆盖"。</summary>
        public readonly Dictionary<int, (int Index, int Seq)> TabIndex = new();
        /// <summary>tabId → 当前选中的导航项下标（command 7）。</summary>
        public readonly Dictionary<int, int> TabSelectedNav = new();
        /// <summary>窗口 id → 当前选中标签下标（command 8）。</summary>
        public readonly Dictionary<int, int> WindowSelectedIndex = new();
        /// <summary>(tabId, navIndex) → 导航项；同一键后写覆盖。</summary>
        public readonly Dictionary<long, NavEntry> Nav = new();
        /// <summary>tabId → 首次出现顺序（稳定排序用）。</summary>
        public readonly Dictionary<int, int> TabFirstSeq = new();
        /// <summary>tabId → 最后出现顺序。</summary>
        public readonly Dictionary<int, int> TabLastSeq = new();
        /// <summary>窗口 id → 首次出现顺序。</summary>
        public readonly Dictionary<int, int> WindowFirstSeq = new();

        public int Seq;
        public int Commands;
        public bool Truncated;
        /// <summary>文件压根打不开（浏览器用独占句柄锁着当前会话文件）；非 null 表示读失败。</summary>
        public string? ReadError;
    }

    private static ParsedSession ParseSnss(string path)
    {
        var s = new ParsedSession();
        byte[] bytes;
        try { bytes = ReadAllBytesShared(path); }
        catch (Exception ex)
        {
            s.ReadError = ex.Message;
            s.Truncated = true;
            return s;
        }

        if (bytes.Length < 8) { s.Truncated = true; return s; }
        if (bytes[0] != (byte)'S' || bytes[1] != (byte)'N' || bytes[2] != (byte)'S' || bytes[3] != (byte)'S')
        {
            s.Truncated = true;
            return s;
        }
        // version 在 [4..7]，实测 3；不校验，未来版本尽力解析。
        int pos = 8;
        while (pos + 2 <= bytes.Length)
        {
            int size = BinaryPrimitives_UInt16(bytes, pos);
            pos += 2;
            if (size <= 0 || pos + size > bytes.Length) { s.Truncated = true; break; }

            s.Commands++;
            s.Seq++;
            int id = bytes[pos];
            int payloadEnd = pos + size;

            switch (id)
            {
                case 0:   // SetTabWindow(windowId, tabId)
                    if (size >= 9)
                    {
                        int win = BinaryPrimitives_Int32(bytes, pos + 1);
                        int tab = BinaryPrimitives_Int32(bytes, pos + 5);
                        s.TabWindow[tab] = win;
                        TouchTab(s, tab);
                        TouchWindow(s, win);
                    }
                    break;

                case 2:   // SetTabIndexInWindow(tabId, index)
                    if (size >= 9)
                    {
                        int tab = BinaryPrimitives_Int32(bytes, pos + 1);
                        int idx = BinaryPrimitives_Int32(bytes, pos + 5);
                        s.TabIndex[tab] = (idx, s.Seq);
                        TouchTab(s, tab);
                    }
                    break;

                case 7:   // SetSelectedNavigationIndex(tabId, navIndex)
                    if (size >= 9)
                    {
                        int tab = BinaryPrimitives_Int32(bytes, pos + 1);
                        int navIdx = BinaryPrimitives_Int32(bytes, pos + 5);
                        s.TabSelectedNav[tab] = navIdx;
                        TouchTab(s, tab);
                    }
                    break;

                case 8:   // SetSelectedTabInIndex(windowId, tabIndex)
                    if (size >= 9)
                    {
                        int win = BinaryPrimitives_Int32(bytes, pos + 1);
                        int idx = BinaryPrimitives_Int32(bytes, pos + 5);
                        s.WindowSelectedIndex[win] = idx;
                        TouchWindow(s, win);
                    }
                    break;

                case 6:   // UpdateTabNavigation（Session_ 文件）
                case 1:   // UpdateTabNavigation（Tabs_ 文件，布局相同）
                    ParseNavigation(bytes, pos, payloadEnd, size, s);
                    break;

                default:
                    break;
            }

            pos += size;
        }
        return s;
    }

    /// <summary>
    /// 导航命令（id=6 / id=1）的 payload 实测布局：
    /// <code>
    /// [0]      uint8  = 6 或 1
    /// [1..4]   int32  紧随其后的字节数 = size - 5（只有带字符串的 command 有这个长度前缀）
    /// [5..8]   int32  tabId
    /// [9..12]  int32  navIndex（该 tab 历史项下标 0,1,2…）
    /// [13..16] int32  url 的字节数 L
    /// [17..]   L 字节 UTF-8 url，之后按 4 字节对齐补齐
    /// 之后     int32  T = title 的"字符数"（不是字节数）
    ///          T*2 字节 UTF-16LE title，之后按 4 字节对齐补齐
    /// </code>
    /// 三处互相印证（Chrome 真实文件）：
    /// · 每条 id=6 的 [1..4] 都精确等于 size-5（4585→4580、2309→2304、3485→3480）；
    /// · "搜索 - Microsoft 必应" T=17、数据恰好 34 字节；"哔哩哔哩 (゜-゜)つロ 干杯~-bilibili" T=25、50 字节；
    /// · url 长度 25 时 URL 后面真有两个 0x00 加一个补齐字节，长度 16 时一个都没有。
    /// </summary>
    private static void ParseNavigation(byte[] bytes, int pos, int payloadEnd, int size, ParsedSession s)
    {
        if (size < 17) return;

        // 布局 A：有长度前缀（实测版本）；布局 B：无前缀，[1..4] 直接是 tabId（旧版本兜底）。
        int base0 = BinaryPrimitives_Int32(bytes, pos + 1) == size - 5 ? 5 : 1;
        if (size < base0 + 16) return;

        int tab = BinaryPrimitives_Int32(bytes, pos + base0);
        int navIdx = BinaryPrimitives_Int32(bytes, pos + base0 + 4);
        int urlLen = BinaryPrimitives_Int32(bytes, pos + base0 + 8);
        int urlPos = pos + base0 + 12;
        if (urlLen < 0 || urlPos + urlLen > payloadEnd) return;

        string url;
        try { url = Encoding.UTF8.GetString(bytes, urlPos, urlLen); }
        catch (Exception) { return; }
        if (IsGarbageUrl(url)) return;

        // title 长度是"字符数"；先按 4 字节对齐找，找不到再退回无补齐的写法。
        string title = "";
        int padNeeded = (4 - (urlLen % 4)) % 4;
        foreach (var pad in padNeeded == 0 ? new[] { 0 } : new[] { padNeeded, 0 })
        {
            int tlPos = urlPos + urlLen + pad;
            if (tlPos + 4 > payloadEnd) continue;
            int chars = BinaryPrimitives_Int32(bytes, tlPos);
            if (chars < 0 || chars > 8192) continue;
            int tPos = tlPos + 4;
            if (tPos + chars * 2 > payloadEnd) continue;
            try
            {
                title = Encoding.Unicode.GetString(bytes, tPos, chars * 2);
                break;
            }
            catch (Exception) { title = ""; }
        }

        long key = NavKey(tab, navIdx);
        if (s.Nav.TryGetValue(key, out var exist) && !string.IsNullOrEmpty(exist.Url) && string.IsNullOrEmpty(url))
        {
            // 不让空 URL 覆盖已有值
        }
        else
        {
            s.Nav[key] = new NavEntry { Url = url, Title = title, Seq = s.Seq };
        }

        TouchTab(s, tab);
    }

    private static long NavKey(int tab, int navIdx) => ((long)(uint)tab << 32) | (uint)navIdx;

    /// <summary>明显不是 URL 的字节（含控制字符）说明布局/偏移错了，直接丢弃。</summary>
    private static bool IsGarbageUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return true;
        for (int i = 0; i < url.Length; i++)
        {
            char c = url[i];
            if (c < 0x20 || c == 0x7F) return true;
        }
        return false;
    }

    private static void TouchTab(ParsedSession s, int tab)
    {
        if (!s.TabFirstSeq.ContainsKey(tab)) s.TabFirstSeq[tab] = s.Seq;
        s.TabLastSeq[tab] = s.Seq;
    }

    private static void TouchWindow(ParsedSession s, int win)
    {
        if (!s.WindowFirstSeq.ContainsKey(win)) s.WindowFirstSeq[win] = s.Seq;
    }

    /// <summary>把解析结果整理成 BrowserWindowEntry 列表（一个 entry = 一个窗口）。</summary>
    private static List<BrowserWindowEntry> BuildEntries(
        ParsedSession parsed,
        ChromiumSpec spec,
        string profileDir,
        string profileName,
        string? exePath,
        string fileName,
        Action<string> warn)
    {
        var result = new List<BrowserWindowEntry>();

        // 1) 窗口内序号 → tabId，同 (窗口,序号) 冲突时后写覆盖。
        //    这一步天然丢掉"已关闭但索引残留"的旧标签：Chromium 关闭标签后会把后面的标签重新编号，
        //    同一序号最后写的一定是当前还活着的那个。
        var windowTabs = new Dictionary<int, Dictionary<int, int>>();
        var ordered = parsed.TabIndex
            .Select(kv => (Tab: kv.Key, kv.Value.Index, kv.Value.Seq))
            .OrderBy(x => x.Seq)
            .ToList();

        // 文件里出现过的窗口：tab 的 SetTabWindow 绑定 + "选中标签下标"命令里的窗口。
        var knownWindows = new HashSet<int>(parsed.TabWindow.Values);
        foreach (var winId in parsed.WindowSelectedIndex.Keys) knownWindows.Add(winId);

        // 没有窗口绑定的 tab 归到哪：只有一个窗口时就归它；一个窗口都不知道时退成 0（降级单窗口）；
        // 有多个窗口时不敢猜，宁可丢掉也不能凭空造一个第 N 个窗口出来。
        int fallbackWindow = knownWindows.Count switch
        {
            0 => 0,
            1 => knownWindows.First(),
            _ => int.MinValue,
        };
        int unboundDropped = 0;

        foreach (var item in ordered)
        {
            if (item.Index < 0 || item.Index > MaxTabsPerProfile) continue;
            int win;
            if (!parsed.TabWindow.TryGetValue(item.Tab, out win))
            {
                if (fallbackWindow == int.MinValue) { unboundDropped++; continue; }
                win = fallbackWindow;
            }
            if (!windowTabs.TryGetValue(win, out var map))
            {
                map = new Dictionary<int, int>();
                windowTabs[win] = map;
            }
            map[item.Index] = item.Tab;
        }

        if (unboundDropped > 0)
            warn($"[browser] {spec.Name}/{profileName}: 忽略 {unboundDropped} 个没有窗口归属的标签（会话文件有多窗口且缺绑定记录）");

        // 2) 有 tab 但完全没有索引信息（只可能是 Tabs_ 降级或文件截断）：归入单窗口，按出现顺序。
        var withoutIndex = parsed.TabFirstSeq.Keys
            .Where(t => !parsed.TabIndex.ContainsKey(t))
            .OrderBy(t => parsed.TabFirstSeq[t])
            .ToList();

        if (windowTabs.Count == 0 && withoutIndex.Count > 0)
        {
            var map = new Dictionary<int, int>();
            int running = 0;
            foreach (var tab in withoutIndex)
            {
                var last = parsed.TabLastSeq.TryGetValue(tab, out var ls) ? ls : 0;
                if (last < parsed.Commands / 2) continue;   // 早期出现过的历史标签丢弃
                map[running++] = tab;
            }
            if (map.Count > 0)
            {
                windowTabs[0] = map;
                warn($"[browser] {spec.Name}/{profileName}: {fileName} 没有窗口归属信息（Tabs_ 格式），已降级为单窗口");
            }
        }
        else if (withoutIndex.Count > 0)
        {
            warn($"[browser] {spec.Name}/{profileName}: 忽略 {withoutIndex.Count} 个无索引标签（多为已关闭/历史）");
        }

        if (windowTabs.Count == 0) return result;

        // 3) 窗口顺序：按窗口首次出现顺序；没有窗口记录的（单窗口降级）排前面。
        var windowIds = windowTabs.Keys
            .OrderBy(w => parsed.WindowFirstSeq.TryGetValue(w, out var f) ? f : -1)
            .ToList();

        int entryIndex = 0;
        foreach (var win in windowIds)
        {
            if (entryIndex >= MaxWindowsPerProfile) break;
            var map = windowTabs[win];

            bool hasSelected = parsed.WindowSelectedIndex.TryGetValue(win, out var selIdx);
            var tabs = new List<BrowserTabEntry>();
            foreach (var idx in map.Keys.OrderBy(x => x))
            {
                var tab = map[idx];
                var (url, title) = ResolveCurrentNavigation(parsed, tab);
                if (string.IsNullOrEmpty(url)) continue;

                tabs.Add(new BrowserTabEntry
                {
                    Url = url,
                    Title = title,
                    Index = tabs.Count,      // 归并成连续序号，还原时按这个顺序开
                    Selected = hasSelected && idx == selIdx,
                    Pinned = false,          // SNSS 里没有可靠的 pinned 命令（实测没有），不瞎猜
                    MediaPositionSec = null,
                });
            }

            if (tabs.Count == 0) continue;

            result.Add(new BrowserWindowEntry
            {
                BrowserId = spec.Id,
                DisplayName = spec.Name,
                ExePath = exePath,
                UserDataDir = spec.Root,
                ProfileDir = profileDir,
                ProfileName = profileName,
                WindowIndex = entryIndex,
                Hwnd = null,          // 稍后由 MatchHwnds 用实时窗口标题回填
                Window = null,        // 几何信息由 Lead 的 AppEntry 采集负责，这里不抢
                Tabs = tabs,
            });
            entryIndex++;
        }


        return result;
    }

    /// <summary>当前显示的 URL/标题：优先 selected nav index，取不到就取最大 nav index。</summary>
    private static (string Url, string Title) ResolveCurrentNavigation(ParsedSession parsed, int tab)
    {
        if (parsed.TabSelectedNav.TryGetValue(tab, out var selNav)
            && parsed.Nav.TryGetValue(NavKey(tab, selNav), out var exact)
            && !string.IsNullOrEmpty(exact.Url))
        {
            return (exact.Url, exact.Title);
        }

        NavEntry? best = null;
        int bestIdx = int.MinValue;
        // Nav 的键空间可能很大，这里只扫该 tab 的键（tab 数量有限）
        foreach (var kv in parsed.Nav)
        {
            int t = (int)(kv.Key >> 32);
            if (t != tab) continue;
            int ni = (int)(kv.Key & 0xFFFFFFFF);
            if (ni > bestIdx) { bestIdx = ni; best = kv.Value; }
        }

        return best is null ? ("", "") : (best.Url, best.Title);
    }

    /// <summary>读 Chromium Local State 里的 profile.info_cache.&lt;dir&gt;.name。</summary>
    private static Dictionary<string, string> ReadLocalStateProfileNames(string root)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(root, "Local State");
            if (!File.Exists(path)) return dict;
            var bytes = ReadAllBytesShared(path);
            if (bytes.Length == 0) return dict;

            var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 };
            using var doc = JsonDocument.Parse(bytes, options);
            if (!doc.RootElement.TryGetProperty("profile", out var profile)) return dict;
            if (!profile.TryGetProperty("info_cache", out var cache)) return dict;
            foreach (var prop in cache.EnumerateObject())
            {
                try
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object
                        && prop.Value.TryGetProperty("name", out var nameEl)
                        && nameEl.ValueKind == JsonValueKind.String)
                    {
                        var n = nameEl.GetString();
                        if (!string.IsNullOrWhiteSpace(n)) dict[prop.Name] = n!;
                    }
                }
                catch (Exception) { }
            }
        }
        catch (Exception)
        {
            // Local State 坏了就用目录名，不算错误
        }
        return dict;
    }

    // ════════════════════════════════════════════════════════════════════
    // Firefox：sessionstore-backups/recovery.jsonlz4（mozLz4）
    // ════════════════════════════════════════════════════════════════════

    private static void ScanFirefox(List<BrowserWindowEntry> sink, Action<string> warn)
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA") ?? "";
        var root = Combine(appData, "Mozilla", "Firefox");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;

        var live = SnapshotLiveBrowsers();
        // 同 Chromium：已退出的 Firefox 不留历史会话（否则复原时会凭空拉起一个没开过的浏览器）
        if (!live.IsRunning("firefox"))
        {
            warn("[browser] Firefox: 当前未运行，跳过历史会话");
            return;
        }

        var profiles = new List<(string Dir, string Name)>();
        try
        {
            var profilesDir = Path.Combine(root, "Profiles");
            if (Directory.Exists(profilesDir))
            {
                foreach (var d in Directory.EnumerateDirectories(profilesDir))
                    profiles.Add((d, Path.GetFileName(d)));
            }
        }
        catch (Exception) { }

        if (profiles.Count == 0)
        {
            warn("[browser] Firefox: 没有找到 profile 目录");
            return;
        }

        var exeHints = new[]
        {
            Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? "", "Mozilla Firefox", "firefox.exe") ?? "",
            Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "", "Mozilla Firefox", "firefox.exe") ?? "",
        };

        var exePath = live.ExePath.TryGetValue("firefox", out var livePath) && !string.IsNullOrEmpty(livePath)
            ? livePath
            : ProbeFirstExisting(exeHints);

        var iniNames = ReadFirefoxProfileNames(root);

        foreach (var (dir, dirName) in profiles)
        {
            try
            {
                var backupDir = Path.Combine(dir, "sessionstore-backups");
                byte[]? json = null;

                foreach (var cand in new[]
                         {
                             Path.Combine(backupDir, "recovery.jsonlz4"),
                             Path.Combine(dir, "sessionstore.jsonlz4"),
                             Path.Combine(backupDir, "recovery.baklz4"),
                             Path.Combine(backupDir, "previous.jsonlz4"),
                         })
                {
                    if (!File.Exists(cand)) continue;
                    var raw = ReadAllBytesShared(cand);
                    var plain = TryDecompressMozLz4(raw);
                    if (plain is { Length: > 2 }) { json = plain; break; }
                }

                if (json is null)
                {
                    warn($"[browser] Firefox {dirName}: 没有可用的 sessionstore 备份");
                    continue;
                }

                var name = iniNames.TryGetValue(dirName, out var n) ? n : dirName;
                var entries = ParseFirefoxSession(json, exePath, root, dir, name, warn);
                foreach (var e in entries) sink.Add(e);
            }
            catch (Exception ex)
            {
                warn($"[browser] Firefox {dirName} 解析失败: {ex.Message}");
            }
        }
    }

    private static List<BrowserWindowEntry> ParseFirefoxSession(
        byte[] json, string? exePath, string userDataDir, string profileDir, string profileName, Action<string> warn)
    {
        var list = new List<BrowserWindowEntry>();
        try
        {
            var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 128 };
            using var doc = JsonDocument.Parse(json, options);
            if (!doc.RootElement.TryGetProperty("windows", out var windows) || windows.ValueKind != JsonValueKind.Array)
            {
                warn($"[browser] Firefox {profileName}: sessionstore 里没有 windows");
                return list;
            }

            int winIdx = 0;
            foreach (var win in windows.EnumerateArray())
            {
                int selectedTab = -1;
                if (win.TryGetProperty("selected", out var selEl) && selEl.ValueKind == JsonValueKind.Number)
                    selectedTab = selEl.GetInt32();   // Firefox 1-based

                var tabs = new List<BrowserTabEntry>();
                if (win.TryGetProperty("tabs", out var tabsEl) && tabsEl.ValueKind == JsonValueKind.Array)
                {
                    int pos = 1;
                    foreach (var tab in tabsEl.EnumerateArray())
                    {
                        try
                        {
                            bool pinned = tab.TryGetProperty("pinned", out var pinEl) && pinEl.ValueKind == JsonValueKind.True;
                            string url = "", title = "";
                            int cur = -1;
                            if (tab.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number)
                                cur = idxEl.GetInt32();

                            if (tab.TryGetProperty("entries", out var entriesEl) && entriesEl.ValueKind == JsonValueKind.Array)
                            {
                                int n = 0, want = cur > 0 ? cur : -1;
                                JsonElement pick = default;
                                bool hasPick = false;
                                foreach (var e in entriesEl.EnumerateArray())
                                {
                                    n++;
                                    if (want == n) { pick = e.Clone(); hasPick = true; }
                                }
                                if (!hasPick && entriesEl.GetArrayLength() > 0)
                                {
                                    pick = entriesEl[entriesEl.GetArrayLength() - 1];
                                    hasPick = true;
                                }
                                if (hasPick)
                                {
                                    if (pick.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String) url = u.GetString() ?? "";
                                    if (pick.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) title = t.GetString() ?? "";
                                    _ = n;
                                }
                            }

                            if (string.IsNullOrEmpty(url)) { pos++; continue; }

                            tabs.Add(new BrowserTabEntry
                            {
                                Url = url,
                                Title = title,
                                Index = tabs.Count,
                                Selected = selectedTab == pos,
                                Pinned = pinned,
                                MediaPositionSec = null,
                            });
                        }
                        catch (Exception) { }
                        pos++;
                    }
                }

                if (tabs.Count == 0) continue;

                list.Add(new BrowserWindowEntry
                {
                    BrowserId = "firefox",
                    DisplayName = "Firefox",
                    ExePath = exePath,
                    UserDataDir = userDataDir,
                    ProfileDir = profileDir,
                    ProfileName = profileName,
                    WindowIndex = winIdx++,
                    Hwnd = null,
                    Window = null,
                    Tabs = tabs,
                });

                if (winIdx >= MaxWindowsPerProfile) break;
            }
        }
        catch (Exception ex)
        {
            warn($"[browser] Firefox {profileName}: sessionstore JSON 解析失败 {ex.Message}");
        }
        return list;
    }

    private static Dictionary<string, string> ReadFirefoxProfileNames(string root)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var ini = Path.Combine(root, "profiles.ini");
            if (!File.Exists(ini)) return dict;

            string? curPath = null;
            bool isRelative = true;
            string? curName = null;

            void Flush()
            {
                if (string.IsNullOrEmpty(curPath)) return;
                var dir = isRelative ? Path.GetFileName(curPath.TrimEnd('/', '\\')) : Path.GetFileName(curPath.TrimEnd('/', '\\'));
                if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(curName)) dict[dir!] = curName!;
                curPath = null; curName = null; isRelative = true;
            }

            foreach (var line in File.ReadAllLines(ini))
            {
                var l = line.Trim();
                if (l.StartsWith("[", StringComparison.Ordinal))
                {
                    Flush();
                    continue;
                }
                int eq = l.IndexOf('=');
                if (eq <= 0) continue;
                var k = l[..eq].Trim();
                var v = l[(eq + 1)..].Trim();
                switch (k.ToLowerInvariant())
                {
                    case "path": curPath = v; break;
                    case "name": curName = v; break;
                    case "isrelative": isRelative = v == "1"; break;
                }
            }
            Flush();
        }
        catch (Exception) { }
        return dict;
    }

    /// <summary>moFile = "mozLz40\0" + int32 未压缩长度 + LZ4 block。</summary>
    private static byte[]? TryDecompressMozLz4(byte[] data)
    {
        if (data is null || data.Length <= 12) return null;
        if (data[0] != (byte)'m' || data[1] != (byte)'o' || data[2] != (byte)'z' || data[3] != (byte)'L'
            || data[4] != (byte)'z' || data[5] != (byte)'4' || data[6] != (byte)'0' || data[7] != 0)
            return null;

        int outLen = BinaryPrimitives_Int32(data, 8);
        if (outLen <= 0 || outLen > 256 * 1024 * 1024) return null;
        var dst = new byte[outLen];

        int sp = 12, dp = 0;
        try
        {
            while (sp < data.Length)
            {
                int token = data[sp++];
                int litLen = token >> 4;
                if (litLen == 15)
                {
                    int b;
                    do { if (sp >= data.Length) return null; b = data[sp++]; litLen += b; } while (b == 255);
                }
                if (litLen > 0)
                {
                    if (sp + litLen > data.Length || dp + litLen > outLen) return null;
                    Buffer.BlockCopy(data, sp, dst, dp, litLen);
                    sp += litLen; dp += litLen;
                }
                if (sp >= data.Length) break;      // 结尾（最后一组只有字面量）
                if (sp + 2 > data.Length) return null;
                int offset = data[sp] | (data[sp + 1] << 8);
                sp += 2;
                if (offset <= 0 || offset > dp) return null;
                int matchLen = token & 0x0F;
                if (matchLen == 15)
                {
                    int b;
                    do { if (sp >= data.Length) return null; b = data[sp++]; matchLen += b; } while (b == 255);
                }
                matchLen += 4;
                if (dp + matchLen > outLen) return null;
                for (int i = 0; i < matchLen; i++) { dst[dp] = dst[dp - offset]; dp++; }
            }
        }
        catch (Exception) { return null; }

        if (dp != outLen) return null;
        return dst;
    }

    // ════════════════════════════════════════════════════════════════════
    // HWND 回填：SNSS 里的窗口 id 是 Chromium 内部的 SessionWindow id（实测 0x034C6540），
    // 不是 HWND（实时 HWND 是 0x10518=66840）。所以用"选中标签标题 == 实时窗口标题前缀"来认领。
    // 只是诊断字段，认不准就留空，不影响分组。
    // ════════════════════════════════════════════════════════════════════

    private static void MatchHwnds(List<BrowserWindowEntry> entries, Action<string> warn)
    {
        if (entries.Count == 0) return;

        var windows = EnumerateTopLevelWindows();
        if (windows.Count == 0) return;

        // pid → 进程名（小写，无 .exe）
        var pidName = new Dictionary<int, string>();
        foreach (var w in windows)
        {
            if (pidName.ContainsKey(w.Pid)) continue;
            try { pidName[w.Pid] = Process.GetProcessById(w.Pid).ProcessName.ToLowerInvariant(); }
            catch (Exception) { pidName[w.Pid] = ""; }
        }

        int matched = 0, missed = 0;
        var used = new HashSet<IntPtr>();

        foreach (var e in entries)
        {
            try
            {
                // 窗口标题 = "当前选中标签的标题 + 浏览器后缀"（如 "… - Google Chrome"）。
                // 会话文件可能比实时状态旧几十分钟，所以用"窗口里任意一个标签的标题"去认领，
                // 而不是只认选中的那个。
                var titles = e.Tabs
                    .Select(t => t.Title)
                    .Where(t => !string.IsNullOrWhiteSpace(t) && t!.Length >= 4)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (titles.Count == 0) { missed++; continue; }

                var cands = new List<WindowHit>();
                foreach (var w in windows)
                {
                    if (used.Contains(w.Hwnd)) continue;
                    if (!pidName.TryGetValue(w.Pid, out var pname)) continue;
                    if (!ProcessNameMatchesBrowser(pname, e.BrowserId)) continue;
                    foreach (var t in titles)
                    {
                        if (w.Title.StartsWith(t!, StringComparison.OrdinalIgnoreCase)
                            || t!.StartsWith(w.Title, StringComparison.OrdinalIgnoreCase))
                        {
                            cands.Add(w);
                            break;
                        }
                    }
                }

                if (cands.Count == 1)
                {
                    e.Hwnd = cands[0].Hwnd.ToInt64().ToString();
                    used.Add(cands[0].Hwnd);
                    matched++;
                }
                else
                {
                    missed++;
                }
            }
            catch (Exception) { missed++; }
        }

        if (matched == 0 && entries.Count > 0)
            warn("[browser] 未能把会话窗口关联到实时 HWND（浏览器可能已退出），Hwnd 留空");
    }

    private static bool ProcessNameMatchesBrowser(string processName, string browserId)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        if (processName.Equals(browserId, StringComparison.OrdinalIgnoreCase)) return true;
        return browserId switch
        {
            "edge" => processName.Equals("msedge", StringComparison.OrdinalIgnoreCase),
            "brave" => processName.Equals("brave", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private sealed class WindowHit
    {
        public IntPtr Hwnd;
        public int Pid;
        public string Title = "";
    }

    private static List<WindowHit> EnumerateTopLevelWindows()
    {
        var list = new List<WindowHit>();
        try
        {
            EnumWindows((h, l) =>
            {
                try
                {
                    if (!IsWindowVisible(h)) return true;
                    var sb = new StringBuilder(512);
                    if (GetWindowTextW(h, sb, sb.Capacity) <= 0) return true;
                    GetWindowThreadProcessId(h, out uint pid);
                    if (pid == 0) return true;
                    list.Add(new WindowHit { Hwnd = h, Pid = (int)pid, Title = sb.ToString() });
                }
                catch (Exception) { }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception) { }
        return list;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ════════════════════════════════════════════════════════════════════
    // 小工具
    // ════════════════════════════════════════════════════════════════════

    /// <summary>本机正在运行的浏览器（browserId → exe 路径），同时用于"是否在跑"的判断。</summary>
    private sealed class LiveBrowsers
    {
        public readonly HashSet<string> Running = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> ExePath = new(StringComparer.OrdinalIgnoreCase);

        public bool IsRunning(string browserId) => Running.Contains(browserId);
    }

    /// <summary>
    /// 枚举进程表，按 browserId 归类（msedge→edge）。"在跑"和"exe 路径"分开记：
    /// 有的进程拿不到 MainModule，但它在跑这个事实仍然有效。
    /// </summary>
    private static LiveBrowsers SnapshotLiveBrowsers()
    {
        var live = new LiveBrowsers();
        string[] wanted = { "chrome", "msedge", "brave", "vivaldi", "opera", "firefox" };
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var name = p.ProcessName;
                    if (!wanted.Contains(name, StringComparer.OrdinalIgnoreCase)) { p.Dispose(); continue; }

                    var browserId = name.Equals("msedge", StringComparison.OrdinalIgnoreCase) ? "edge" : name.ToLowerInvariant();
                    live.Running.Add(browserId);

                    if (!live.ExePath.ContainsKey(browserId))
                    {
                        string? path = null;
                        try { path = p.MainModule?.FileName; } catch (Exception) { path = null; }
                        if (!string.IsNullOrEmpty(path)) live.ExePath[browserId] = path!;
                    }
                    p.Dispose();
                }
                catch (Exception) { try { p.Dispose(); } catch { } }
            }
        }
        catch (Exception) { }
        return live;
    }

    private static string? ProbeFirstExisting(IEnumerable<string> hints)
    {
        foreach (var h in hints)
        {
            if (string.IsNullOrWhiteSpace(h)) continue;
            try { if (File.Exists(h)) return h; } catch (Exception) { }
        }
        return null;
    }

    private static string? Combine(params string[] parts)
    {
        if (parts is null || parts.Length == 0) return null;
        var clean = parts.Where(p => !string.IsNullOrEmpty(p)).ToArray();
        if (clean.Length == 0) return null;
        try { return Path.Combine(clean); } catch (Exception) { return null; }
    }

    /// <summary>允许别的进程正在写（Chrome 运行时会占用会话文件），只读打开。</summary>
    private static byte[] ReadAllBytesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long len = fs.Length;
        if (len <= 0) return Array.Empty<byte>();
        if (len > 64L * 1024 * 1024) len = 64L * 1024 * 1024;   // 会话文件不该这么大
        var buf = new byte[(int)len];
        int read = 0;
        while (read < buf.Length)
        {
            int n = fs.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }
        if (read == buf.Length) return buf;
        var trimmed = new byte[read];
        Buffer.BlockCopy(buf, 0, trimmed, 0, read);
        return trimmed;
    }

    private static int BinaryPrimitives_Int32(byte[] b, int offset)
        => b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | (b[offset + 3] << 24);

    private static int BinaryPrimitives_UInt16(byte[] b, int offset)
        => b[offset] | (b[offset + 1] << 8);
}
