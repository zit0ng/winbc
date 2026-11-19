using System.Text;
using System.Text.Json;
using WinBC;
using WinBC.Capture;
using WinBC.Models;
using WinBC.Native;
using WinBC.Storage;

namespace CaptureTest;

/// <summary>采集层独立验证工具：不依赖 UI，直接把一份快照打出来。</summary>
internal static class TestMain
{
    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length > 0 && args[0].Equals("restore-test", StringComparison.OrdinalIgnoreCase))
        {
            RestoreSelfTest.Run(args.Length > 1 ? args[1] : null);
            return;
        }

        var settings = Settings.Load();
        var store = new SnapshotStore(settings);
        var service = new SnapshotService(settings, store);

        Console.WriteLine("========== 原始可见顶层窗口（未过滤） ==========");
        DumpRawWindows();

        Console.WriteLine();
        Console.WriteLine("========== 采集结果 ==========");
        var snap = service.Capture("manually");

        Console.WriteLine($"快照 ID     : {snap.Id}");
        Console.WriteLine($"时间        : {snap.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"耗时        : {snap.CaptureMs} ms");
        Console.WriteLine($"显示器      : {snap.Monitors.Count}");
        foreach (var m in snap.Monitors)
            Console.WriteLine($"   [{m.DeviceName}] {m.X},{m.Y} {m.Width}x{m.Height} primary={m.IsPrimary}");

        Console.WriteLine($"应用条目    : {snap.Apps.Count}（有效 {snap.AppCount}，忽略 {snap.Apps.Count(a => a.IsIgnored)}）");
        foreach (var a in snap.Apps)
        {
            var flags = new List<string>();
            if (a.IsIgnored) flags.Add("IGNORED:" + a.IgnoreReason);
            if (a.IsUwp) flags.Add("UWP");
            if (!a.CanRestart) flags.Add("NORESTART");
            if (a.MediaPositionSec is not null) flags.Add($"seek={a.MediaPositionSec:0}s");

            Console.WriteLine($"   {a.ZOrder,2}. {a.ProcessName,-22} [{a.Window.State,-9}] " +
                              $"{a.Window.NormalX},{a.Window.NormalY} {a.Window.NormalWidth}x{a.Window.NormalHeight} mon={a.Window.MonitorIndex} " +
                              $"{string.Join(" ", flags)}");
            Console.WriteLine($"       标题 : {Trunc(a.Title, 70)}");
            if (!string.IsNullOrEmpty(a.ExePath)) Console.WriteLine($"       EXE  : {a.ExePath}");
            if (!string.IsNullOrEmpty(a.CommandLine)) Console.WriteLine($"       CMD  : {Trunc(a.CommandLine, 120)}");
            if (!string.IsNullOrEmpty(a.LaunchArgs)) Console.WriteLine($"       ARGS : {Trunc(a.LaunchArgs, 100)}");
            if (!string.IsNullOrEmpty(a.UwpAppId)) Console.WriteLine($"       AUMID: {a.UwpAppId}");
        }

        Console.WriteLine($"浏览器窗口  : {snap.Browsers.Count}，标签页 {snap.TabCount}");
        foreach (var b in snap.Browsers)
        {
            Console.WriteLine($"   [{b.BrowserId}] {b.DisplayName} profile={b.ProfileName} idx={b.WindowIndex} " +
                              $"exe={b.ExePath} window={(b.Window is null ? "null" : $"{b.Window.NormalX},{b.Window.NormalY} {b.Window.NormalWidth}x{b.Window.NormalHeight}")}");
            foreach (var t in b.Tabs.Take(40))
                Console.WriteLine($"      {t.Index,2}{(t.Selected ? "*" : " ")} {Trunc(t.Title, 46),-46} | {Trunc(t.Url, 80)}");
            if (b.Tabs.Count > 40) Console.WriteLine($"      … 还有 {b.Tabs.Count - 40} 条");
        }

        Console.WriteLine($"媒体        : {snap.Media.Count}");
        foreach (var m in snap.Media)
            Console.WriteLine($"   {m.Kind} {m.PlayerName} pos={m.PositionSec} dur={m.DurationSec} src={m.Source}");

        if (snap.Warnings.Count > 0)
        {
            Console.WriteLine("警告        :");
            foreach (var w in snap.Warnings) Console.WriteLine("   ! " + w);
        }

        var outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "out");
        try
        {
            Directory.CreateDirectory(outDir);
            var file = Path.Combine(outDir, "snapshot.json");
            File.WriteAllText(file, JsonSerializer.Serialize(snap, Settings.JsonOptions));
            Console.WriteLine();
            Console.WriteLine("JSON 已写出 : " + Path.GetFullPath(file));
        }
        catch (Exception ex)
        {
            Console.WriteLine("写出 JSON 失败: " + ex.Message);
        }

        Console.WriteLine();
        Console.WriteLine("========== 命令行读取自检 ==========");
        int me = Environment.ProcessId;
        var cmd = ProcessCommandLine.TryGet(me);
        Console.WriteLine($"本进程 PID={me} 命令行={(cmd ?? "<读取失败>")}");
        Console.WriteLine($"本进程 EXE={(NativeMethods.GetProcessImagePath(me) ?? "<读取失败>")}");
    }

    private static void DumpRawWindows()
    {
        int i = 0;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                bool visible = NativeMethods.IsWindowVisible(hwnd);
                var title = NativeMethods.GetWindowText(hwnd);
                var cls = NativeMethods.GetClassName(hwnd);
                bool cloaked = NativeMethods.IsCloaked(hwnd);
                uint pid = NativeMethods.GetPid(hwnd);
                if (!visible && title.Length == 0) return true;

                string proc = "";
                try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }

                Console.WriteLine($"{i++,3}. vis={visible,-5} cloak={cloaked,-5} pid={pid,-6} {proc,-20} {cls,-38} {Trunc(title, 50)}");
            }
            catch { }
            return true;
        }, IntPtr.Zero);
    }

    private static string Trunc(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
