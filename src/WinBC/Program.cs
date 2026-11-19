using System.Diagnostics;
using WinBC.Capture;
using WinBC.Models;
using WinBC.Restore;
using WinBC.Storage;
using WinBC.Triggers;
using WinBC.UI;
using WinBC.Util;

namespace WinBC;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            // 必须先设 DPI 模式再碰任何窗口：--snapshot 模式不建窗口，但同样需要拿准坐标
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }

            var settings = Settings.Load();
            Log.Cleanup();

            var store = new SnapshotStore(settings);
            var snapshotService = new SnapshotService(settings, store);
            var restoreService = new RestoreService(settings);

            string mode = NormalizeMode(args);
            string reasonArg = args.Length > 1 ? args[1] : "";

            Log.Info($"启动 mode={mode} args={string.Join(' ', args)}");

            switch (mode)
            {
                case "snapshot":
                    {
                        var reason = string.IsNullOrWhiteSpace(reasonArg) ? "shutdown" : reasonArg;
                        var snap = snapshotService.Capture(reason);
                        return snap is null ? 1 : 0;
                    }

                case "restore-last":
                    return RestoreLatest(store, restoreService);

                case "prompt":
                    return RunUi(settings, store, snapshotService, restoreService,
                        autoPrompt: true, showMain: false);

                case "history":
                    return RunUi(settings, store, snapshotService, restoreService,
                        autoPrompt: false, showMain: false, showHistory: true);

                case "settings":
                    return RunUi(settings, store, snapshotService, restoreService,
                        autoPrompt: false, showMain: false, showSettings: true);

                case "tray":
                    return RunUi(settings, store, snapshotService, restoreService,
                        autoPrompt: true, showMain: false);

                default: // "show" / 无参数
                    return RunUi(settings, store, snapshotService, restoreService,
                        autoPrompt: false, showMain: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("程序启动失败", ex);
            try
            {
                MessageBox.Show("WinBC 启动失败：\r\n" + ex.Message, "WinBC",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
            return 1;
        }
    }

    private static string NormalizeMode(string[] args)
    {
        if (args is null || args.Length == 0) return "show";
        var a = args[0].Trim().TrimStart('-', '/').ToLowerInvariant();
        return a switch
        {
            "snapshot" or "capture" or "s" => "snapshot",
            "restore-last" or "restore" or "r" => "restore-last",
            "prompt" or "p" => "prompt",
            "history" or "h" => "history",
            "settings" or "prefs" => "settings",
            "tray" or "background" or "t" or "quiet" => "tray",
            "show" or "ui" or "" => "show",
            _ => "show",
        };
    }

    private static int RestoreLatest(SnapshotStore store, RestoreService restoreService)
    {
        var snap = store.GetLatestRestorable();
        if (snap is null)
        {
            Log.Warn("没有可复原的快照");
            return 2;
        }
        var result = restoreService.Restore(snap, new RestoreSelection());
        Log.Info("命令行复原：" + result.Summary);
        return result.Failed.Count > 0 && result.Started.Count == 0 ? 3 : 0;
    }

    private static int RunUi(Settings settings, SnapshotStore store, SnapshotService snapshotService,
        RestoreService restoreService, bool autoPrompt, bool showMain, bool showHistory = false,
        bool showSettings = false)
    {
        using var instance = new SingleInstance(@"Local\WinBC.Ui");
        if (!instance.IsOwner)
        {
            // 已经有一个在跑：把它叫出来就行
            instance.SignalExisting();
            return 0;
        }

        // DPI 模式必须在创建任何窗口之前设定
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }

        // 主题也要在任何窗口创建之前定好（默认深色）
        Modern.Apply(settings.DarkMode);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) =>
        {
            Log.Error("UI 线程未处理异常", e.Exception);
            try
            {
                MessageBox.Show("发生错误：\r\n" + e.Exception.Message, "WinBC",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("未处理异常", e.ExceptionObject as Exception);

        using var context = new WinBcApplicationContext(
            settings, store, snapshotService, restoreService, autoPrompt, showMain, showHistory, showSettings);

        instance.ListenForShowRequest(() => context.ShowMainWindow());

        Application.Run(context);
        return 0;
    }

    /// <summary>常驻托盘的应用上下文：管主窗口、托盘图标、关机监听、开机提示。</summary>
    private sealed class WinBcApplicationContext : ApplicationContext
    {
        private readonly Settings _settings;
        private readonly SnapshotStore _store;
        private readonly SnapshotService _snapshotService;
        private readonly RestoreService _restoreService;
        private readonly ShutdownWatcher _watcher;
        private readonly TrayController _tray;
        private readonly Control _marshaller = new();
        private MainForm? _mainForm;
        private bool _prompted;
        private bool _disposed;

        public WinBcApplicationContext(Settings settings, SnapshotStore store,
            SnapshotService snapshotService, RestoreService restoreService,
            bool autoPrompt, bool showMain, bool showHistory = false, bool showSettings = false)
        {
            _settings = settings;
            _store = store;
            _snapshotService = snapshotService;
            _restoreService = restoreService;

            _tray = new TrayController(settings, store, snapshotService, restoreService,
                requestShowMain: ShowMainWindow,
                requestShowHistory: ShowHistoryWindow,
                requestExit: ExitApplication);

            // 必须在 UI 线程上先把 marshal 控件的窗口句柄创建出来。
            // 否则线程池线程第一次 BeginInvoke 时才创建句柄，那个线程没有消息循环，
            // 请求会被投递到一个永远不会被处理的消息队列里 —— 表现就是"点了托盘/--show 没反应"。
            _ = _marshaller.Handle;

            _watcher = new ShutdownWatcher(settings, snapshotService);
            _watcher.Start();

            if (showMain) ShowMainWindow();
            // 历史记录是模态窗口，必须等消息循环起来之后再弹，否则会卡在 Application.Run 之前
            if (showHistory) _marshaller.BeginInvoke(new Action(ShowHistoryWindow));
            if (showSettings) _marshaller.BeginInvoke(new Action(ShowSettingsWindow));
            if (autoPrompt) ScheduleStartupPrompt();

            // 全程没有窗口：直接进后台低优先级模式，等用户点托盘再恢复
            if (!showMain && !showHistory)
            {
                ResourceTuning.EnterBackground();
                ResourceTuning.TrimWorkingSet();
            }
        }

        // ---------- 窗口 ----------

        public void ShowMainWindow()
        {
            if (_marshaller.InvokeRequired)
            {
                try { _marshaller.BeginInvoke(new Action(ShowMainWindow)); } catch { }
                return;
            }

            try
            {
                if (_mainForm is null || _mainForm.IsDisposed)
                {
                    _mainForm = new MainForm(_settings, _store, _snapshotService, _restoreService);
                    // 关闭按钮隐藏到托盘时，通过托盘气泡提示用户
                    _mainForm.BalloonHook = _tray.ShowBalloon;
                }

                if (!_mainForm.Visible) _mainForm.Show();
                if (_mainForm.WindowState == FormWindowState.Minimized)
                    _mainForm.WindowState = FormWindowState.Normal;

                _mainForm.Activate();
                _mainForm.BringToFront();
            }
            catch (Exception ex)
            {
                Log.Error("显示主窗口失败", ex);
                try { _tray.ShowBalloon("WinBC", "打开主窗口失败：" + ex.Message, true); } catch { }
            }
        }

        /// <summary>设置窗口（模态，改完会即时生效）。</summary>
        public void ShowSettingsWindow()
        {
            if (_marshaller.InvokeRequired)
            {
                try { _marshaller.BeginInvoke(new Action(ShowSettingsWindow)); } catch { }
                return;
            }

            try
            {
                using var dlg = new SettingsForm(_settings);
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    Log.Info("设置已更新");
                    _tray.ShowBalloon("WinBC", "设置已保存。");
                }
            }
            catch (Exception ex)
            {
                Log.Error("打开设置失败", ex);
                try { _tray.ShowBalloon("WinBC", "打开设置失败：" + ex.Message, true); } catch { }
            }
        }

        /// <summary>历史记录窗口：所有记录过的时间点，点一条就能重新打开那一刻的状态。
        /// 复用主窗口持有的那个实例（非模态，两个窗口可以同时操作）。</summary>
        public void ShowHistoryWindow()
        {
            if (_marshaller.InvokeRequired)
            {
                try { _marshaller.BeginInvoke(new Action(ShowHistoryWindow)); } catch { }
                return;
            }

            try
            {
                ShowMainWindow();
                _mainForm?.OpenHistory();
            }
            catch (Exception ex)
            {
                Log.Error("打开历史记录失败", ex);
                try { _tray.ShowBalloon("WinBC", "打开历史记录失败：" + ex.Message, true); } catch { }
            }
        }

        // ---------- 开机提示 ----------
        private void ScheduleStartupPrompt()
        {
            if (!_settings.AutoPromptOnStartup) return;

            var snap = _store.GetLatestRestorable();
            if (snap is null) return;

            if (DateTimeOffset.Now - snap.CreatedAt > TimeSpan.FromDays(3)) return;
            if (snap.AppCount + snap.TabCount == 0) return;

            // 等托盘和桌面都就绪一点再弹
            var timer = new System.Windows.Forms.Timer { Interval = 2000 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                PromptRestore(snap);
            };
            timer.Start();
        }

        private void PromptRestore(Snapshot snap)
        {
            if (_prompted) return;
            _prompted = true;

            try
            {
                // 弹窗期间恢复正常优先级，关掉后立刻回到后台把资源让出去
                ResourceTuning.ExitBackground();

                // 这里只传"是什么"，提示框自己会拼成「发现{reason}的状态记录（…）」
                using var dlg = new RestorePromptForm(snap, "上次关机前");
                var result = dlg.ShowDialog();

                if (dlg.UserChoseRestore)
                {
                    RestoreAsync(snap, new RestoreSelection());
                }
                else if (dlg.UserChoseViewMain)
                {
                    ShowMainWindow();
                }
            }
            catch (Exception ex)
            {
                Log.Error("开机提示失败", ex);
            }
            finally
            {
                if (_mainForm is null || !_mainForm.Visible)
                {
                    ResourceTuning.EnterBackground();
                    ResourceTuning.TrimWorkingSet();
                }
            }
        }

        // ---------- 复原 ----------

        private void RestoreAsync(Snapshot snap, RestoreSelection selection)
        {
            _tray.ShowBalloon("正在复原", "正在按快照恢复程序、标签页和窗口位置…");

            Task.Run(() =>
            {
                try
                {
                    var r = _restoreService.Restore(snap, selection);
                    PostToUi(() => _tray.ShowBalloon("复原完成", r.Summary));
                }
                catch (Exception ex)
                {
                    Log.Error("复原失败", ex);
                    PostToUi(() => _tray.ShowBalloon("复原失败", ex.Message, true));
                }
            });
        }

        private void PostToUi(Action action)
        {
            try
            {
                if (_marshaller.IsDisposed) return;
                _marshaller.BeginInvoke(action);
            }
            catch
            {
                // 程序正在退出，忽略
            }
        }

        // ---------- 退出 ----------

        private void ExitApplication()
        {
            if (_marshaller.InvokeRequired)
            {
                try { _marshaller.BeginInvoke(new Action(ExitApplication)); } catch { }
                return;
            }

            try
            {
                _watcher.Dispose();
                _tray.Dispose();
                _mainForm?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("退出时清理失败", ex);
            }

            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                try { _watcher.Dispose(); } catch { }
                try { _tray.Dispose(); } catch { }
                try { _marshaller.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
