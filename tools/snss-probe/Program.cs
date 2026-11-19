using System.Diagnostics;
using System.Text;
using WinBC.Capture;
using WinBC.Models;

// 独立验证工具：调用 WinBC.Capture.BrowserSessionReader，把结果 dump 成文本供 Lead 复查。
Console.OutputEncoding = Encoding.UTF8;

string outPath = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "browser-session-dump.txt");

var warnings = new List<string>();
var sw = Stopwatch.StartNew();
List<BrowserWindowEntry> windows;
try
{
    windows = BrowserSessionReader.ReadAll(warnings);
}
catch (Exception ex)
{
    Console.Error.WriteLine("ReadAll 抛异常（契约要求绝不抛）: " + ex);
    return 2;
}
sw.Stop();

var sb = new StringBuilder();
sb.AppendLine($"== WinBC BrowserSessionReader 实测 == {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
sb.AppendLine($"耗时 {sw.ElapsedMilliseconds} ms | 窗口 {windows.Count} | 标签 {windows.Sum(w => w.Tabs.Count)} | warnings {warnings.Count}");
sb.AppendLine(new string('=', 100));

int n = 0;
foreach (var w in windows)
{
    sb.AppendLine();
    sb.AppendLine($"[{w.BrowserId}] {w.DisplayName} | profile={w.ProfileName} | windowIndex={w.WindowIndex} | tabs={w.Tabs.Count}");
    sb.AppendLine($"    hwnd={w.Hwnd ?? "-"}  exe={w.ExePath ?? "-"}");
    sb.AppendLine($"    profileDir={w.ProfileDir}");
    foreach (var t in w.Tabs)
    {
        n++;
        var mark = t.Selected ? "*SEL*" : "     ";
        var title = t.Title.Length > 70 ? t.Title[..70] + "…" : t.Title;
        sb.AppendLine($"    {mark} #{t.Index,-2} {(t.Pinned ? "PIN" : "   ")} {t.Url}");
        sb.AppendLine($"              title: {title}");
    }
}

sb.AppendLine();
sb.AppendLine(new string('=', 100));
sb.AppendLine($"-- warnings ({warnings.Count}) --");
if (warnings.Count == 0) sb.AppendLine("(无)");
foreach (var wmsg in warnings) sb.AppendLine("  " + wmsg);

// 契约自检
sb.AppendLine();
sb.AppendLine("-- 自检 --");
sb.AppendLine($"  返回类型 List<BrowserWindowEntry>: {windows.GetType().Name}");
sb.AppendLine($"  空 URL 标签数: {windows.Sum(w => w.Tabs.Count(t => string.IsNullOrEmpty(t.Url)))}");
sb.AppendLine($"  重复 (tab 内) URL: {windows.Sum(w => w.Tabs.Count - w.Tabs.Select(t => t.Url).Distinct(StringComparer.Ordinal).Count())}");
sb.AppendLine($"  总标签数 <= 100: {n <= 100}");

var text = sb.ToString();
Console.Write(text);
try
{
    File.WriteAllText(outPath, text, new UTF8Encoding(false));
    // 顺手落一份 JSON，供 verify.ps1 做断言 / 供 Lead 直接喂给后端
    var jsonPath = Path.ChangeExtension(outPath, ".json");
    var jsonOpt = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(windows, jsonOpt), new UTF8Encoding(false));
    Console.WriteLine();
    Console.WriteLine("dump 已写入: " + outPath);
    Console.WriteLine("json 已写入: " + jsonPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine("写 dump 失败: " + ex.Message);
    return 1;
}
return 0;
