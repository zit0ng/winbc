namespace WinBC.Restore;

/// <summary>用户在界面上勾选的恢复范围。</summary>
public sealed class RestoreSelection
{
    public bool Apps { get; set; } = true;
    public bool Browsers { get; set; } = true;
    public bool Media { get; set; } = true;
    /// <summary>被排除的应用，键为 AppKey(app)：processName|title。</summary>
    public HashSet<string> ExcludedAppKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>被排除的标签页 URL。</summary>
    public HashSet<string> ExcludedUrls { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>被排除的媒体条目（按 Source 路径/URL 匹配）。</summary>
    public HashSet<string> ExcludedMedia { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string AppKey(WinBC.Models.AppEntry app) => $"{app.ProcessName}|{app.Title}";
    public static string AppKey(string processName, string title) => $"{processName}|{title}";
}

/// <summary>一次恢复操作的结果。</summary>
public sealed class RestoreResult
{
    public List<string> Started { get; } = new();
    public List<string> Failed { get; } = new();
    public int Skipped { get; set; }
    public int AppsRestored { get; set; }
    public int BrowserWindowsRestored { get; set; }
    public int TabsRestored { get; set; }
    public int MediaRestored { get; set; }
    public string Summary { get; set; } = "";

    public override string ToString() => Summary;
}
