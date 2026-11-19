using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WinBC.Models;
using WinBC.Restore;

namespace WinBC.UI;

/// <summary>
/// 托盘控制器：提供通知区图标、右键菜单（打开主窗口 / 立即记录 / 复原上一次 / 设置 / 退出）与气泡提示。
/// 注意：主窗口由 Program 统一创建和持有，这里只通过 <c>requestShowMain</c> 回调请求显示，绝不自己 new MainForm。
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly WinBC.Settings _settings;
    private readonly WinBC.Storage.SnapshotStore _store;
    private readonly WinBC.Capture.SnapshotService _snapshotService;
    private readonly WinBC.Restore.RestoreService _restoreService;
    private readonly Action? _requestShowMain;
    private readonly Action? _requestShowHistory;
    private readonly Action? _requestExit;

    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly Icon _icon;
    /// <summary>构造时的 UI 同步上下文，保证后台任务的回调能切回 UI 线程。</summary>
    private readonly SynchronizationContext? _sync;

    private bool _disposed;
    private int _busy; // 0 = 空闲，1 = 正在采集/恢复

    public TrayController(WinBC.Settings settings, WinBC.Storage.SnapshotStore store,
                          WinBC.Capture.SnapshotService snapshotService, WinBC.Restore.RestoreService restoreService,
                          Action? requestShowMain = null, Action? requestShowHistory = null,
                          Action? requestExit = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
        _restoreService = restoreService ?? throw new ArgumentNullException(nameof(restoreService));
        _requestShowMain = requestShowMain;
        _requestShowHistory = requestShowHistory;
        _requestExit = requestExit;

        _sync = SynchronizationContext.Current;

        _icon = CreateTrayIcon();

        _statusItem = new ToolStripMenuItem("WinBC") { Enabled = false, Font = UiTheme.BaseFont };
        _menu = BuildMenu();

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "WinBC · 状态快照",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _tray.DoubleClick += (_, _) => RequestShowMain();
    }

    // ══════════════════════════════════════════════════════════════════
    // 菜单
    // ══════════════════════════════════════════════════════════════════

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip
        {
            Font = UiTheme.BaseFont,
            ShowImageMargin = false,
            RenderMode = ToolStripRenderMode.System,
        };

        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateItem("打开主窗口", RequestShowMain));
        menu.Items.Add(CreateItem("历史记录…", RequestShowHistory));
        menu.Items.Add(CreateItem("立即记录一次", () => _ = CaptureOnceAsync()));
        menu.Items.Add(CreateItem("复原上一次", () => _ = RestoreLastAsync()));
        menu.Items.Add(CreateItem("设置…", OpenSettings));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateItem("退出", RequestExit));

        menu.Opening += (_, _) => UpdateStatusItem();
        UpdateStatusItem();

        // 跟主界面同一套暗色圆角菜单
        ModernMenu.Style(menu);

        return menu;
    }

    private static ToolStripMenuItem CreateItem(string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text) { Font = UiTheme.BaseFont };
        item.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex) { Debug.WriteLine("[WinBC] 托盘菜单执行失败：" + ex.Message); }
        };
        return item;
    }

    private void UpdateStatusItem()
    {
        try
        {
            _statusItem.Text = $"WinBC · 共 {_store.Count} 个快照";
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[WinBC] 读取快照数量失败：" + ex.Message);
            _statusItem.Text = "WinBC";
        }
    }

    private void RequestShowMain()
    {
        try
        {
            if (_requestShowMain is null)
            {
                Debug.WriteLine("[WinBC] 未提供 requestShowMain 回调，无法打开主窗口。");
                return;
            }

            _requestShowMain();
        }
        catch (Exception ex)
        {
            LogWarn("打开主窗口失败", ex);
            ShowBalloon("WinBC", "打开主窗口失败：" + ex.Message, warning: true);
        }
    }

    private void RequestShowHistory()
    {
        try
        {
            if (_requestShowHistory is null)
            {
                RequestShowMain();
                return;
            }
            _requestShowHistory();
        }
        catch (Exception ex)
        {
            LogWarn("打开历史记录失败", ex);
            ShowBalloon("WinBC", "打开历史记录失败：" + ex.Message, warning: true);
        }
    }

    private void OpenSettings()
    {
        try
        {
            using var dialog = new SettingsForm(_settings);
            if (dialog.ShowDialog() == DialogResult.OK) ShowBalloon("WinBC", "设置已保存。");
        }
        catch (Exception ex)
        {
            LogWarn("打开设置失败", ex);
            ShowBalloon("WinBC · 打开设置失败", ex.Message, warning: true);
        }
    }

    private void RequestExit()
    {
        try
        {
            try { _tray.Visible = false; } catch { /* 图标已释放 */ }

            if (_requestExit is not null)
            {
                _requestExit(); // 由 ApplicationContext 负责真正退出
                return;
            }

            Application.Exit();
        }
        catch (Exception ex)
        {
            LogWarn("托盘退出失败", ex);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // 立即记录 / 复原上一次
    // ══════════════════════════════════════════════════════════════════

    private async Task CaptureOnceAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            ShowBalloon("WinBC", "正在处理上一个任务，请稍候。");
            return;
        }

        try
        {
            Snapshot snapshot = await Task.Run(() => _snapshotService.Capture("manually"));
            ShowBalloon("WinBC · 已记录", $"记录下 {snapshot.AppCount} 个应用、{snapshot.BrowserWindowCount} 个窗口 {snapshot.TabCount} 个标签页。");
        }
        catch (Exception ex)
        {
            LogWarn("托盘记录失败", ex);
            ShowBalloon("WinBC · 记录失败", ex.Message, warning: true);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task RestoreLastAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            ShowBalloon("WinBC", "正在处理上一个任务，请稍候。");
            return;
        }

        try
        {
            Snapshot? latest = await Task.Run(() => _store.ListSnapshots().FirstOrDefault());
            if (latest is null)
            {
                ShowBalloon("WinBC", "还没有可恢复的快照。");
                return;
            }

            RestoreSelection selection = BuildFullSelection(latest);
            ShowBalloon("WinBC", $"正在恢复 {UiTheme.FormatShortTime(latest.CreatedAt)} 的快照…");

            RestoreResult result = await Task.Run(() => _restoreService.Restore(latest, selection));

            ShowBalloon("WinBC · 复原完成", result.Summary);
            if (result.Failed.Count > 0)
            {
                string detail = string.Join("；", result.Failed.Take(3));
                ShowBalloon("WinBC · 部分条目失败", detail, warning: true);
            }
        }
        catch (Exception ex)
        {
            LogWarn("托盘复原失败", ex);
            ShowBalloon("WinBC · 复原失败", ex.Message, warning: true);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>托盘"复原上一次"= 全量恢复，但跳过系统壳噪音窗口与空白新标签页。</summary>
    private static RestoreSelection BuildFullSelection(Snapshot snapshot)
    {
        var selection = new RestoreSelection
        {
            Apps = snapshot.Apps.Any(app => !app.IsIgnored),
            Browsers = snapshot.Browsers.Any(window => window.Tabs.Any(tab => !UiTheme.IsNewTabUrl(tab.Url))),
            Media = snapshot.Media.Count > 0,
            ExcludedAppKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ExcludedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };

        foreach (AppEntry app in snapshot.Apps)
        {
            if (app.IsIgnored) selection.ExcludedAppKeys.Add(RestoreSelection.AppKey(app));
        }

        foreach (BrowserWindowEntry window in snapshot.Browsers)
        {
            foreach (BrowserTabEntry tab in window.Tabs)
            {
                if (UiTheme.IsNewTabUrl(tab.Url)) selection.ExcludedUrls.Add(tab.Url);
            }
        }

        return selection;
    }

    // ══════════════════════════════════════════════════════════════════
    // 气泡与图标
    // ══════════════════════════════════════════════════════════════════

    /// <summary>显示托盘气泡（可从任意线程调用）。</summary>
    public void ShowBalloon(string title, string text, bool warning = false)
    {
        if (_disposed) return;

        void ShowCore()
        {
            if (_disposed) return;
            try
            {
                _tray.BalloonTipTitle = string.IsNullOrEmpty(title) ? "WinBC" : title;
                _tray.BalloonTipText = text ?? "";
                _tray.BalloonTipIcon = warning ? ToolTipIcon.Warning : ToolTipIcon.Info;
                _tray.ShowBalloonTip(4000);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WinBC] 显示气泡失败：" + ex.Message);
            }
        }

        if (_sync is null || SynchronizationContext.Current == _sync)
        {
            ShowCore();
            return;
        }

        try { _sync.Post(_ => ShowCore(), null); }
        catch (Exception ex)
        {
            Debug.WriteLine("[WinBC] 切回 UI 线程失败：" + ex.Message);
            ShowCore();
        }
    }

    /// <summary>黑底白箭头的圆形图标，见 <see cref="Util.AppIcon"/>。</summary>
    private static Icon CreateTrayIcon()
    {
        try
        {
            return Util.AppIcon.Create(32);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[WinBC] 生成托盘图标失败，回退系统图标：" + ex.Message);
            return (Icon)SystemIcons.Shield.Clone();
        }
    }

    // ══════════════════════════════════════════════════════════════════

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _tray.Visible = false; } catch { /* 已隐藏 */ }
        try { _tray.Dispose(); } catch { /* 已释放 */ }
        try { _menu.Dispose(); } catch { /* 已释放 */ }
        try { _icon.Dispose(); } catch { /* 已释放 */ }
    }

    /// <summary>写一条诊断日志；日志组件异常不影响托盘功能。</summary>
    private static void LogWarn(string message, Exception ex)
    {
        Debug.WriteLine($"[WinBC] {message}：{ex}");
        try { WinBC.Util.Log.Warn($"{message}：{ex}"); }
        catch { /* 日志组件不可用时忽略 */ }
    }
}
