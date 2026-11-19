using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinBC;

/// <summary>全局设置，持久化在 %LOCALAPPDATA%\WinBC\settings.json。</summary>
public sealed class Settings
{
    public static readonly string DefaultAppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinBC");

    /// <summary>是否周期性滚动记录（保证即使来不及关机也能恢复到最后一次）。</summary>
    public bool EnableRollingSnapshot { get; set; } = true;
    /// <summary>滚动记录间隔（秒）。后台常驻程序，默认 3 分钟一次就够了。</summary>
    public int RollingIntervalSeconds { get; set; } = 180;
    /// <summary>是否监听系统注销/关机事件。</summary>
    public bool WatchSessionEnding { get; set; } = true;
    /// <summary>开机后是否自动询问"是否恢复上次状态"。</summary>
    public bool AutoPromptOnStartup { get; set; } = true;
    /// <summary>是否随登录自动启动常驻托盘。</summary>
    public bool LaunchAtLogon { get; set; } = true;
    /// <summary>最多保留多少个快照（数量兜底，防止一天内爆量）。</summary>
    public int MaxSnapshots { get; set; } = 40;
    /// <summary>历史记录保留天数，超过就自动删除。</summary>
    public int RetentionDays { get; set; } = 30;
    /// <summary>复原时是否把窗口挪回原来的位置和大小。</summary>
    public bool RestoreWindowPositions { get; set; } = true;
    /// <summary>复原时是否重开浏览器标签页。</summary>
    public bool RestoreBrowserTabs { get; set; } = true;
    /// <summary>是否记录/复原 UWP（商店）应用。</summary>
    public bool IncludeUwpApps { get; set; } = true;
    /// <summary>深色模式（默认开）。</summary>
    public bool DarkMode { get; set; } = true;
    /// <summary>额外忽略的进程名（不含 .exe）。</summary>
    /// <remarks>注意：不要在这里放 explorer / ApplicationFrameHost，
    /// 用户的文件夹窗口和商店应用是值得恢复的，系统壳窗口已按窗口类名过滤。</remarks>
    public List<string> IgnoredProcesses { get; set; } = new()
    {
        "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost",
        "TextInputHost", "RuntimeBroker", "LockApp", "SecurityHealthSystray",
        "MoUsoCoreWorker", "NVIDIA Overlay", "SystemSettings",
    };
    /// <summary>额外忽略的窗口类名。</summary>
    public List<string> ExtraIgnoredClasses { get; set; } = new();

    [JsonIgnore] public string AppDataDir => DefaultAppDataDir;
    [JsonIgnore] public string SettingsPath => Path.Combine(AppDataDir, "settings.json");
    [JsonIgnore] public string SnapshotDir => Path.Combine(AppDataDir, "snapshots");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>读取设置；文件缺失或损坏时返回默认值（绝不抛异常）。</summary>
    public static Settings Load()
    {
        try
        {
            Settings s;
            var path = Path.Combine(DefaultAppDataDir, "settings.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                s = JsonSerializer.Deserialize<Settings>(json, Options) ?? new Settings();
            }
            else
            {
                s = new Settings();
            }
            s.Normalize();
            return s;
        }
        catch (Exception ex)
        {
            Util.Log.Error("读取设置失败，使用默认值", ex);
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            Normalize();
            Directory.CreateDirectory(AppDataDir);
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, SettingsPath, true);
        }
        catch (Exception ex)
        {
            Util.Log.Error("保存设置失败", ex);
        }
    }

    private void Normalize()
    {
        if (RollingIntervalSeconds < 15) RollingIntervalSeconds = 15;
        if (RollingIntervalSeconds > 3600) RollingIntervalSeconds = 3600;
        if (MaxSnapshots < 5) MaxSnapshots = 5;
        if (MaxSnapshots > 500) MaxSnapshots = 500;
        if (RetentionDays < 1) RetentionDays = 1;
        if (RetentionDays > 3650) RetentionDays = 3650;
        IgnoredProcesses ??= new List<string>();
        ExtraIgnoredClasses ??= new List<string>();
        if (!IgnoredProcesses.Any(p => string.Equals(p, "WinBC", StringComparison.OrdinalIgnoreCase)))
            IgnoredProcesses.Add("WinBC");
    }

    /// <summary>保存/读取设置时共用的 JSON 选项。</summary>
    public static JsonSerializerOptions JsonOptions => Options;

    public bool IsProcessIgnored(string processName) =>
        IgnoredProcesses.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));
}
