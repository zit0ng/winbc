using System.Text.RegularExpressions;
using WinBC.Models;
using WinBC.Util;

namespace WinBC.Capture;

/// <summary>从应用窗口里提取"正在播放的视频/音频"，包括播放进度。</summary>
public sealed class MediaScanner
{
    public static readonly string[] VideoExtensions =
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".ts", ".m2ts", ".mts",
        ".mpg", ".mpeg", ".rmvb", ".rm", ".m4v", ".3gp", ".vob", ".ogv", ".divx", ".asf",
    };

    public static readonly string[] AudioExtensions =
    {
        ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".ape", ".alac", ".dsf",
    };

    /// <summary>已知播放器：进程名 -> 恢复时定位进度的参数模板。</summary>
    private static readonly Dictionary<string, (string display, Func<double, string> seekArg)> Players =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["vlc"] = ("VLC", s => $"--start-time={s:0.###}"),
            ["potplayermini64"] = ("PotPlayer", s => $"/seek={s:0.###}"),
            ["potplayermini"] = ("PotPlayer", s => $"/seek={s:0.###}"),
            ["mpv"] = ("mpv", s => $"--start={s:0.###}"),
            ["mpc-hc64"] = ("MPC-HC", s => $"/start {s:0.###}"),
            ["mpc-hc"] = ("MPC-HC", s => $"/start {s:0.###}"),
            ["mpc-be64"] = ("MPC-BE", s => $"/start {s:0.###}"),
            ["mpvnet"] = ("mpv.net", s => $"--start={s:0.###}"),
            ["kodi"] = ("Kodi", _ => ""),
            ["wmplayer"] = ("Windows Media Player", _ => ""),
            ["musicbee"] = ("MusicBee", _ => ""),
        };

    private static readonly Regex TimePairRegex = new(
        @"(\d{1,2}:\d{2}(?::\d{2})?)\s*/\s*(\d{1,2}:\d{2}(?::\d{2})?)",
        RegexOptions.Compiled);

    private static readonly Regex TimeSingleRegex = new(
        @"(?:^|[\s\(\[-])(\d{1,2}:\d{2}:\d{2})(?:[\s\)\]-]|$)",
        RegexOptions.Compiled);

    /// <summary>常见视频/音乐站：域名后缀 + 友好名。</summary>
    private static readonly (string Host, string Label)[] VideoHosts =
    {
        ("bilibili.com", "哔哩哔哩"), ("youtube.com", "YouTube"), ("youtu.be", "YouTube"),
        ("v.qq.com", "腾讯视频"), ("iqiyi.com", "爱奇艺"), ("youku.com", "优酷"),
        ("mgtv.com", "芒果TV"), ("douyin.com", "抖音"), ("vimeo.com", "Vimeo"),
        ("netflix.com", "Netflix"), ("twitch.tv", "Twitch"), ("disneyplus.com", "Disney+"),
        ("music.163.com", "网易云音乐"), ("y.qq.com", "QQ音乐"), ("kuwo.cn", "酷我音乐"),
        ("music.migu.cn", "咪咕音乐"),
    };

    /// <summary>扫描本地播放器 + 浏览器里的网页视频。browsers 可为 null。</summary>
    public List<MediaEntry> Scan(List<AppEntry> apps, List<BrowserWindowEntry>? browsers = null)
    {
        var list = new List<MediaEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in apps ?? new List<AppEntry>())
        {
            if (app.IsIgnored) continue;
            try
            {
                var media = ExtractFromApp(app);
                if (media is not null && seen.Add(media.Source))
                    list.Add(media);
            }
            catch (Exception ex)
            {
                Log.Warn($"提取媒体信息失败（{app.ProcessName}）: {ex.Message}");
            }
        }

        // 网页视频：跟着标签页走，这里只做"可见化"，恢复仍由标签页逻辑负责
        foreach (var bw in browsers ?? new List<BrowserWindowEntry>())
        {
            foreach (var tab in bw.Tabs)
            {
                try
                {
                    var site = DetectVideoSite(tab.Url);
                    if (site is null) continue;
                    if (!seen.Add(tab.Url)) continue;

                    list.Add(new MediaEntry
                    {
                        Kind = "web",
                        Source = tab.Url,
                        PlayerName = site,
                        Title = string.IsNullOrWhiteSpace(tab.Title) ? tab.Url : tab.Title,
                        PositionSec = tab.MediaPositionSec,
                        ResumeArgs = tab.Url,
                    });
                }
                catch (Exception ex)
                {
                    Log.Warn("识别网页视频失败: " + ex.Message);
                }
            }
        }

        return list;
    }

    /// <summary>判断一个 URL 是不是具体某条视频/音乐播放页（不是站点首页）。</summary>
    public static string? DetectVideoSite(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();

        foreach (var (h, label) in VideoHosts)
        {
            if (!host.EndsWith(h, StringComparison.Ordinal)) continue;

            bool looksLikePlayback =
                path.Contains("/video") || path.Contains("/watch") || path.Contains("/play") ||
                path.Contains("/bangumi") || path.StartsWith("/b/") || path.Contains("/song") ||
                path.Contains("/mv/") || path.Contains("/live/");
            if (looksLikePlayback) return label;
        }
        return null;
    }

    private static MediaEntry? ExtractFromApp(AppEntry app)
    {
        var file = FindLocalMediaFile(app);
        if (file is null) return null;

        bool isVideo = VideoExtensions.Any(e => file.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        var (pos, dur) = ParsePlaybackFromTitle(app.Title);

        string playerExe = app.ExePath ?? app.ProcessName + ".exe";
        string playerName = app.ProcessName;
        string resumeArgs;

        if (Players.TryGetValue(app.ProcessName, out var meta))
        {
            playerName = meta.display;
            var seek = pos.HasValue ? meta.seekArg(pos.Value) : "";
            resumeArgs = string.IsNullOrEmpty(seek)
                ? CommandLineUtil.Quote(file)
                : $"{seek} {CommandLineUtil.Quote(file)}";
        }
        else
        {
            resumeArgs = CommandLineUtil.Quote(file);
        }

        return new MediaEntry
        {
            Kind = isVideo ? "local" : "audio",
            Source = file,
            PlayerExe = playerExe,
            PlayerName = playerName,
            Title = System.IO.Path.GetFileName(file),
            PositionSec = pos,
            DurationSec = dur,
            ResumeArgs = resumeArgs,
        };
    }

    /// <summary>从命令行参数里找真实存在的媒体文件；其次是标题里带扩展名的文件名。</summary>
    private static string? FindLocalMediaFile(AppEntry app)
    {
        if (!string.IsNullOrWhiteSpace(app.LaunchArgs))
        {
            foreach (var token in CommandLineUtil.Split(app.LaunchArgs))
            {
                var candidate = token.Trim().Trim('"');
                if (candidate.Length < 4) continue;
                bool extOk = VideoExtensions.Concat(AudioExtensions)
                    .Any(e => candidate.EndsWith(e, StringComparison.OrdinalIgnoreCase));
                if (!extOk) continue;

                try
                {
                    if (System.IO.File.Exists(candidate)) return candidate;
                }
                catch { }

                // 文件可能已被移动，但路径形态明确时也记录（恢复时会失败并提示）
                if (candidate.Contains(":\\") || candidate.StartsWith(@"\\"))
                    return candidate;
            }
        }

        // 兜底：标题形如 "影片名.mp4 - VLC media player"
        var titlePart = app.Title.Split(" - ", 2, StringSplitOptions.None)[0].Trim();
        foreach (var ext in VideoExtensions.Concat(AudioExtensions))
        {
            if (titlePart.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // 标题只有文件名，尝试在常见目录里定位
                    var found = TryLocateByFileName(titlePart);
                    if (found is not null) return found;
                }
                catch { }
            }
        }

        return null;
    }

    private static string? TryLocateByFileName(string fileName)
    {
        string[] dirs =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop"),
        };
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            try
            {
                var hit = Directory.EnumerateFiles(dir, fileName, SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (hit is not null) return hit;
            }
            catch { }
        }
        return null;
    }

    /// <summary>从窗口标题解析 "00:12:34 / 01:30:00" 或单段时间戳。</summary>
    public static (double? position, double? duration) ParsePlaybackFromTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return (null, null);

        var pair = TimePairRegex.Match(title);
        if (pair.Success)
        {
            double? p = ParseTime(pair.Groups[1].Value);
            double? d = ParseTime(pair.Groups[2].Value);
            if (p.HasValue && d.HasValue && d > 0)
                return (p, d);
        }

        var single = TimeSingleRegex.Match(title);
        if (single.Success)
        {
            double? p = ParseTime(single.Groups[1].Value);
            if (p.HasValue && p > 0) return (p, null);
        }

        return (null, null);
    }

    private static double? ParseTime(string text)
    {
        var parts = text.Split(':');
        try
        {
            return parts.Length switch
            {
                2 => int.Parse(parts[0]) * 60 + int.Parse(parts[1]),
                3 => int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60 + int.Parse(parts[2]),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }
}
