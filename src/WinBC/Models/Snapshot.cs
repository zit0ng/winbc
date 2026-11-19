using System.Text.Json.Serialization;

namespace WinBC.Models;

/// <summary>整份"关机前状态"快照。序列化为 JSON 存盘。</summary>
public sealed class Snapshot
{
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    /// <summary>shutdown | logoff | manually | rolling</summary>
    public string Reason { get; set; } = "manually";
    public string MachineName { get; set; } = Environment.MachineName;
    public string UserName { get; set; } = Environment.UserName;
    public string? OsVersion { get; set; }

    public List<MonitorInfo> Monitors { get; set; } = new();
    public List<AppEntry> Apps { get; set; } = new();
    public List<BrowserWindowEntry> Browsers { get; set; } = new();
    public List<MediaEntry> Media { get; set; } = new();

    /// <summary>采集耗时（毫秒），用于诊断。</summary>
    public long CaptureMs { get; set; }
    /// <summary>采集过程中遇到的问题（不阻塞快照生成）。</summary>
    public List<string> Warnings { get; set; } = new();

    [JsonIgnore]
    public int AppCount => Apps.Count(a => !a.IsIgnored);
    [JsonIgnore]
    public int TabCount => Browsers.Sum(b => b.Tabs.Count);
    [JsonIgnore]
    public int BrowserWindowCount => Browsers.Count;
}

/// <summary>一台显示器。</summary>
public sealed class MonitorInfo
{
    public string DeviceName { get; set; } = "";
    /// <summary>虚拟桌面坐标下的工作区。</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsPrimary { get; set; }
    public string? FriendlyName { get; set; }
}

/// <summary>窗口位置/状态，使用 WINDOWPLACEMENT 语义以便精确还原最大化/最小化。</summary>
public sealed class WindowBox
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>normal | maximized | minimized</summary>
    public string State { get; set; } = "normal";
    /// <summary>还原后的正常位置（最大化时也能还原到原来的大小）。</summary>
    public int NormalX { get; set; }
    public int NormalY { get; set; }
    public int NormalWidth { get; set; }
    public int NormalHeight { get; set; }
    public int MonitorIndex { get; set; }

    [JsonIgnore]
    public bool HasGeometry => NormalWidth > 0 && NormalHeight > 0;
}

/// <summary>一个正在运行的桌面应用窗口。</summary>
public sealed class AppEntry
{
    public string ProcessName { get; set; } = "";
    public string Title { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string? ExePath { get; set; }
    public string? CommandLine { get; set; }
    public string? WorkingDirectory { get; set; }
    public int Pid { get; set; }
    /// <summary>UWP / ApplicationFrameHost 托管的商店应用。</summary>
    public bool IsUwp { get; set; }
    /// <summary>UWP 应用的 AppUserModelID，如 Microsoft.WindowsCalculator_8wekyb3d8bbwe!App。</summary>
    public string? UwpAppId { get; set; }
    /// <summary>该窗口是否属于"不该恢复"的噪音（系统壳、输入法、驱动浮层等）。</summary>
    public bool IsIgnored { get; set; }
    public string? IgnoreReason { get; set; }
    public WindowBox Window { get; set; } = new();
    /// <summary>能否用 EXE 直接重启（系统壳类不能）。</summary>
    public bool CanRestart { get; set; } = true;
    public int ZOrder { get; set; }
    /// <summary>从命令行/标题推断出的打开文档或媒体文件。</summary>
    public string? OpenDocument { get; set; }
    /// <summary>媒体播放进度（秒），用于视频类恢复。</summary>
    public double? MediaPositionSec { get; set; }
    /// <summary>用于重启的参数（已剔除 exe 自身路径）。</summary>
    public string? LaunchArgs { get; set; }
    public DateTimeOffset StartTime { get; set; }

    [JsonIgnore]
    public string Display => IsIgnored ? $"[跳过] {ProcessName}" : ProcessName;
}

/// <summary>一个浏览器窗口（含其全部标签页）。</summary>
public sealed class BrowserWindowEntry
{
    /// <summary>chrome | edge | brave | vivaldi | opera | firefox</summary>
    public string BrowserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? ExePath { get; set; }
    public string? UserDataDir { get; set; }
    public string? ProfileDir { get; set; }
    public string? ProfileName { get; set; }
    /// <summary>窗口在快照中的序号。</summary>
    public int WindowIndex { get; set; }
    /// <summary>该窗口的宿主 HWND（十进制字符串，仅诊断用）。</summary>
    public string? Hwnd { get; set; }
    public WindowBox? Window { get; set; }
    public List<BrowserTabEntry> Tabs { get; set; } = new();
}

public sealed class BrowserTabEntry
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public int Index { get; set; }
    public bool Selected { get; set; }
    public bool Pinned { get; set; }
    /// <summary>网页内视频的播放进度（秒），能推断出来时才有值。</summary>
    public double? MediaPositionSec { get; set; }

    [JsonIgnore]
    public string ShortUrl => Url.Length <= 90 ? Url : Url[..90] + "…";
}

/// <summary>播放器里的本地媒体，或网页媒体。</summary>
public sealed class MediaEntry
{
    /// <summary>local | web</summary>
    public string Kind { get; set; } = "local";
    /// <summary>本地文件完整路径，或网页 URL。</summary>
    public string Source { get; set; } = "";
    public string? PlayerExe { get; set; }
    public string? PlayerName { get; set; }
    public string Title { get; set; } = "";
    public double? PositionSec { get; set; }
    public double? DurationSec { get; set; }
    /// <summary>用于恢复时定位到播放进度的命令行模板结果。</summary>
    public string? ResumeArgs { get; set; }

    [JsonIgnore]
    public string PositionText => PositionSec is null ? "" : TimeSpan.FromSeconds(PositionSec.Value).ToString(@"hh\:mm\:ss");
}
