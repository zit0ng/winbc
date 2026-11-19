using System.Text;

namespace WinBC.Util;

/// <summary>极简文件日志，线程安全，按天切分。</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string LogDir = Path.Combine(Settings.DefaultAppDataDir, "logs");

    public static string CurrentLogPath =>
        Path.Combine(LogDir, $"winbc-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERR ", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(CurrentLogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败绝不影响主流程
        }
    }

    /// <summary>清理 7 天前的日志。</summary>
    public static void Cleanup(int keepDays = 7)
    {
        try
        {
            if (!Directory.Exists(LogDir)) return;
            var cutoff = DateTime.Now.AddDays(-keepDays);
            foreach (var f in Directory.GetFiles(LogDir, "winbc-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff)
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }
}
