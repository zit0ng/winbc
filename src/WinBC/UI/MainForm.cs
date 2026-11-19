using System.Diagnostics;
using System.Drawing.Drawing2D;
using WinBC.Capture;
using WinBC.Models;
using WinBC.Native;
using WinBC.Restore;
using WinBC.Storage;
using WinBC.Util;

namespace WinBC.UI;

/// <summary>
/// 主窗口：只负责一件事 —— 把上次关机前的桌面摆回来。
/// 历史记录在单独的窗口里（HistoryForm），两者不混在一起。
/// </summary>
public sealed class MainForm : ModernForm
{
    private readonly Settings _settings;
    private readonly SnapshotStore _store;
    private readonly SnapshotService _snapshotService;
    private readonly RestoreService _restoreService;

    // ── 数据（只装 shutdown / logoff 这几份"关机前"的快照）──
    private List<Snapshot> _snapshots = new();
    private int _index;
    private Snapshot? _current;
    private readonly HashSet<string> _excludedApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excludedUrls = new(StringComparer.OrdinalIgnoreCase);
    private int _tab;

    // ── 控件 ──
    private readonly RoundButton _prevButton = new();
    private readonly RoundButton _nextButton = new();
    private readonly RoundButton _menuButton = new();
    private readonly RoundButton _minButton = new();
    private readonly RoundButton _closeButton = new();
    private readonly ChipBar _chips = new();
    private readonly StatStrip _stats = new();
    private readonly Panel _listHost = new();
    private readonly RoundButton _restoreButton = new();
    private readonly RoundButton _historyButton = new();
    private readonly ContextMenuStrip _menu = new();

    // ── 状态 ──
    private string _status = "";
    private Color _statusColor = Modern.Sub;
    private bool _busy;
    private bool _allowClose;
    private CancellationTokenSource? _cts;
    private HistoryForm? _historyForm;

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<string, string, bool>? BalloonHook { get; set; }

    private const int ContentPad = 18;
    private const int RowHeight = 46;

    public MainForm(WinBC.Settings settings, WinBC.Storage.SnapshotStore store,
                    WinBC.Capture.SnapshotService snapshotService, WinBC.Restore.RestoreService restoreService)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
        _restoreService = restoreService ?? throw new ArgumentNullException(nameof(restoreService));

        BuildUi();
        LoadSnapshots();
    }

    public void AllowClose() => _allowClose = true;

    // ══════════════════════════════════════════════════════════════════
    // 骨架（CreateParams / 圆角 / 边缘缩放 / 拖动都在 ModernForm 基类里）
    // ══════════════════════════════════════════════════════════════════

    private void BuildUi()
    {
        SuspendLayout();

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.None;   // 尺寸统一由 S() 缩放，避免和 WinForms 自动缩放叠加
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Modern.Card;
        ForeColor = Modern.Text;
        Text = "WinBC · 关机现场";
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        KeyPreview = true;
        ShowInTaskbar = true;
        try { Icon = Util.AppIcon.Instance; } catch { }

        SetupIconButton(_menuButton, "⋯", ShowMenu);
        SetupIconButton(_minButton, "－", () => WindowState = FormWindowState.Minimized);
        SetupIconButton(_closeButton, "✕", CloseOrHide);
        SetupIconButton(_prevButton, "‹", () => Step(-1));
        SetupIconButton(_nextButton, "›", () => Step(1));
        _prevButton.Font = new Font(Modern.Family, 15F, FontStyle.Bold, GraphicsUnit.Point);
        _nextButton.Font = new Font(Modern.Family, 15F, FontStyle.Bold, GraphicsUnit.Point);

        // 只有两档：应用 / 网页
        _chips.Items = new[] { "应用", "网页" };
        _chips.SelectedIndexChanged += (_, _) => { _tab = _chips.SelectedIndex; RefreshList(); };

        _listHost.AutoScroll = true;
        _listHost.BackColor = Modern.Card;
        _listHost.Resize += (_, _) => ResizeRows();

        _restoreButton.Primary = true;
        _restoreButton.Text = "复原";
        _restoreButton.Radius = 12;
        _restoreButton.Font = new Font(Modern.Family, 11F, FontStyle.Bold, GraphicsUnit.Point);
        _restoreButton.Click += (_, _) => StartRestore();

        _historyButton.Text = "历史记录";
        _historyButton.Radius = 12;
        _historyButton.Font = new Font(Modern.Family, 10.5F, FontStyle.Regular, GraphicsUnit.Point);
        _historyButton.Click += (_, _) => OpenHistory();

        _stats.Compact = true;

        BuildMenu();

        Controls.AddRange(new Control[]
        {
            _prevButton, _nextButton, _menuButton, _minButton, _closeButton,
            _stats, _chips, _listHost, _historyButton, _restoreButton,
        });

        ResumeLayout(false);
    }

    private void SetupIconButton(RoundButton b, string text, Action onClick)
    {
        b.Text = text;
        b.Ghost = true;
        b.Radius = 8;
        b.Font = new Font(Modern.Family, 10F, FontStyle.Regular, GraphicsUnit.Point);
        b.ForeColor = Modern.Sub;
        b.Click += (_, _) => onClick();
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add("历史记录…", null, (_, _) => OpenHistory());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("立即记录一次", null, (_, _) => CaptureNow());
        _menu.Items.Add("刷新", null, (_, _) => LoadSnapshots());
        _menu.Items.Add(new ToolStripSeparator());

        var themeItem = new ToolStripMenuItem("深色模式")
        {
            CheckOnClick = true,
            Checked = _settings.DarkMode,
        };
        themeItem.Click += (_, _) =>
        {
            _settings.DarkMode = themeItem.Checked;
            _settings.Save();
            ApplyTheme();
        };
        _menu.Items.Add(themeItem);

        _menu.Items.Add("打开存档目录", null, (_, _) => OpenArchiveFolder());
        _menu.Items.Add("设置…", null, (_, _) => OpenSettings());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => ExitApp());

        // 统一暗色圆角外观（放在最后，好让每一项都吃到字体设置）
        ModernMenu.Style(_menu);
    }

    private void ApplyTheme()
    {
        ThemeManager.Apply(_settings.DarkMode);
        BackColor = Modern.Card;
        ForeColor = Modern.Text;
        Invalidate(true);
    }

    // ══════════════════════════════════════════════════════════════════
    // 布局
    // ══════════════════════════════════════════════════════════════════

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutAll();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyInitialSize(420, 700, 360, 540);
        LayoutAll();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // 窗口要展示了，把优先级恢复正常，否则界面会一顿一顿的
        Util.ResourceTuning.ExitBackground();
    }

    private void LayoutAll()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

        int pad = this.S(ContentPad);
        int icon = this.S(30);
        int top = this.S(HeaderHeight);

        int by = (top - icon) / 2;
        _closeButton.Bounds = new Rectangle(ClientSize.Width - pad - icon, by, icon, icon);
        _minButton.Bounds = new Rectangle(_closeButton.Left - this.S(2) - icon, by, icon, icon);
        _menuButton.Bounds = new Rectangle(_minButton.Left - this.S(2) - icon, by, icon, icon);

        int y = top + this.S(12);
        int small = this.S(28);
        _prevButton.Bounds = new Rectangle(pad, y, small, small);
        _nextButton.Bounds = new Rectangle(pad + small + this.S(4), y, small, small);
        y += small + this.S(12);

        int contentWidth = ClientSize.Width - pad * 2;

        // 统计条压扁：数字一行、标签一行，总共 52
        _stats.Bounds = new Rectangle(pad, y, contentWidth, this.S(52));
        y += this.S(52) + this.S(12);

        _chips.Bounds = new Rectangle(pad, y, contentWidth, this.S(32));
        y += this.S(32) + this.S(8);

        // 底部：左边「历史记录」，右边「复原」
        int btnH = this.S(46);
        int gap = this.S(10);
        int bottomY = ClientSize.Height - pad - btnH;
        int halfW = (contentWidth - gap) / 2;
        _historyButton.Bounds = new Rectangle(pad, bottomY, halfW, btnH);
        _restoreButton.Bounds = new Rectangle(pad + halfW + gap, bottomY, contentWidth - halfW - gap, btnH);

        // 状态行在按钮上方
        int listBottom = bottomY - this.S(26);
        int unit = this.S(RowHeight) + this.S(3);
        int avail = Math.Max(this.S(60), listBottom - y);
        int rows = Math.Max(2, avail / unit);
        int listHeight = rows * unit - this.S(3);
        _listHost.Bounds = new Rectangle(pad, y, contentWidth, listHeight);

        ResizeRows();
        Invalidate();
    }

    /// <summary>
    /// 只改宽度。位置在放进去的那一刻就定好了 —— 再改 Top 会和 Panel 的 AutoScroll
    /// 互相覆盖，导致内容高度虚增、视口停在空白处。
    /// </summary>
    private void ResizeRows()
    {
        if (_listHost.IsDisposed) return;
        int width = Math.Max(this.S(200), _listHost.ClientSize.Width - this.S(4));
        foreach (Control c in _listHost.Controls) c.Width = width;
    }

    // ══════════════════════════════════════════════════════════════════
    // 绘制
    // ══════════════════════════════════════════════════════════════════

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var bg = new SolidBrush(Modern.Card))
            g.FillRectangle(bg, ClientRectangle);

        int pad = this.S(ContentPad);
        int top = this.S(HeaderHeight);

        using (var pen = new Pen(Modern.Border, this.Sf(1f)))
            g.DrawLine(pen, 0, top, Width, top);

        float dot = this.S(8);
        Modern.FillPill(g, new RectangleF(pad, (top - dot) / 2f, dot, dot), Modern.Primary);
        Modern.DrawText(g, "WinBC", Modern.H2, Modern.Text,
            new RectangleF(pad + dot + this.S(8), 0, this.S(200), top), ContentAlignment.MiddleLeft);

        if (_current is not null)
        {
            var when = Modern.FormatTime(_current.CreatedAt);
            var reason = Modern.ReasonText(_current.Reason);
            float left = _nextButton.Right + this.S(10);

            // 右边要给"1/2"这种计数留位置，否则会重叠
            float rightLimit = _menuButton.Left - this.S(10);
            if (_snapshots.Count > 1) rightLimit -= this.S(56);

            float width = Math.Max(this.S(60), rightLimit - left);
            var timeRect = new RectangleF(left, _prevButton.Top, width, _prevButton.Height);
            Modern.DrawText(g, when, Modern.BodyBold, Modern.Text, timeRect);

            // 原因跟在时间后面，位置按实测文字宽度算，不能再用同一个矩形（会叠在一起）
            float timeWidth = g.MeasureString(when, Modern.BodyBold).Width;
            var reasonRect = new RectangleF(left + timeWidth - this.S(6), _prevButton.Top,
                Math.Max(this.S(20), width - timeWidth + this.S(6)), _prevButton.Height);
            Modern.DrawText(g, "·" + reason, Modern.Small, Modern.Faint, reasonRect);

            if (_snapshots.Count > 1)
            {
                Modern.DrawText(g, $"{_index + 1}/{_snapshots.Count}", Modern.Small, Modern.Faint,
                    new RectangleF(_menuButton.Left - this.S(58), _prevButton.Top, this.S(50), _prevButton.Height),
                    ContentAlignment.MiddleRight);
            }
        }
        else
        {
            Modern.DrawText(g, "还没有关机记录",
                Modern.Small, Modern.Faint,
                new RectangleF(_nextButton.Right + this.S(10), _prevButton.Top,
                    _menuButton.Left - _nextButton.Right - this.S(20), _prevButton.Height));
        }

        var statusRect = new RectangleF(pad, _listHost.Bottom + this.S(1),
            ClientSize.Width - pad * 2, this.S(22));
        if (!string.IsNullOrEmpty(_status))
            Modern.DrawText(g, _status, Modern.Tiny, _statusColor, statusRect);
        else if (_current is not null)
            Modern.DrawText(g, SelectionSummary(), Modern.Tiny, Modern.Sub, statusRect);
    }

    private void SetStatus(string text, Color? color = null)
    {
        _status = text;
        _statusColor = color ?? Modern.Sub;
        Invalidate();
    }

    // ══════════════════════════════════════════════════════════════════
    // 数据
    // ══════════════════════════════════════════════════════════════════

    /// <summary>这里只装"关机前"的快照；其它（手动/自动）都属于历史记录窗口。</summary>
    private static bool IsPowerSnapshot(Snapshot s) =>
        string.Equals(s.Reason, "shutdown", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(s.Reason, "logoff", StringComparison.OrdinalIgnoreCase);

    private void LoadSnapshots()
    {
        try
        {
            var previousId = _current?.Id;

            _snapshots = _store.ListSnapshots().Where(IsPowerSnapshot).ToList();

            int idx = 0;
            if (previousId is not null)
            {
                int found = _snapshots.FindIndex(s => s.Id == previousId);
                if (found >= 0) idx = found;
            }
            _index = Math.Clamp(idx, 0, Math.Max(0, _snapshots.Count - 1));
            SelectCurrent();
        }
        catch (Exception ex)
        {
            SetStatus("读取快照失败：" + ex.Message, Modern.Danger);
        }
    }

    private void Step(int delta)
    {
        if (_snapshots.Count <= 1) return;
        _index = Math.Clamp(_index + delta, 0, _snapshots.Count - 1);
        SelectCurrent();
    }

    private void SelectCurrent()
    {
        _current = _snapshots.Count > 0 ? _snapshots[_index] : null;
        _excludedApps.Clear();
        _excludedUrls.Clear();
        _status = "";

        if (_current is not null)
        {
            var snap = _current;
            int appCount = snap.Apps.Count(a => !a.IsIgnored);
            _stats.Items = new[] { ("个应用", appCount.ToString()), ("个网页", snap.TabCount.ToString()) };
            _chips.Counts = new[] { appCount, snap.TabCount };
        }
        else
        {
            _stats.Items = new[] { ("个应用", "0"), ("个网页", "0") };
            _chips.Counts = new[] { 0, 0 };
        }

        _prevButton.Enabled = _index > 0;
        _nextButton.Enabled = _index < _snapshots.Count - 1;
        _restoreButton.Enabled = _current is not null && !_busy;

        RefreshList();
        Invalidate();
    }

    private void RefreshList()
    {
        _listHost.SuspendLayout();
        foreach (Control c in _listHost.Controls.Cast<Control>().ToList())
        {
            _listHost.Controls.Remove(c);
            c.Dispose();
        }
        // 重建前把滚动收回顶部，否则新行会落在旧的滚动偏移里
        try { _listHost.AutoScrollPosition = new Point(0, 0); } catch { }

        if (_current is null)
        {
            AddPlaceholder("这里只显示关机前的状态。想看待办历史，点右上角 ⋯ →「历史记录…」");
            _listHost.ResumeLayout();
            return;
        }

        int width = Math.Max(this.S(200), _listHost.ClientSize.Width - this.S(4));
        int rowH = this.S(RowHeight);
        int gap = this.S(4);
        int y = this.S(2);

        if (_tab == 0) BuildAppRows(width, rowH, gap, ref y);
        else BuildTabRows(width, rowH, gap, ref y);

        _listHost.ResumeLayout();
    }

    private void AddRow(Control c, int width, int rowH, int gap, ref int y)
    {
        c.Bounds = new Rectangle(0, y, width, rowH);
        _listHost.Controls.Add(c);
        y += rowH + gap;
    }

    private void AddPlaceholder(string text)
    {
        _listHost.Controls.Add(new Label
        {
            Text = text,
            Font = Modern.Body,
            ForeColor = Modern.Faint,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = false,
            BackColor = Color.Transparent,
            Bounds = new Rectangle(0, this.S(40),
                Math.Max(this.S(200), _listHost.ClientSize.Width - this.S(4)), this.S(60)),
        });
    }

    private void BuildAppRows(int width, int rowH, int gap, ref int y)
    {
        var apps = _current!.Apps;
        if (apps.Count == 0) { AddPlaceholder("这份快照里没有记录到应用程序"); return; }

        foreach (var app in apps.OrderBy(a => a.IsIgnored).ThenBy(a => a.ZOrder))
        {
            string key = RestoreSelection.AppKey(app);
            var row = new CheckRow
            {
                // 竖屏窄窗：只显示名字，不显示窗口标题/尺寸那些简介
                Title = app.ProcessName,
                Subtitle = "",
                Muted = app.IsIgnored,
                Badge = app.IsIgnored ? "跳过" : null,
                BadgeIsWarning = app.IsIgnored,
                Checked = !app.IsIgnored && !_excludedApps.Contains(key),
                Payload = app,
            };
            row.CheckedChanged += (s, _) =>
            {
                if (s is not CheckRow r) return;
                if (r.Checked) _excludedApps.Remove(key); else _excludedApps.Add(key);
                Invalidate();
            };
            AddRow(row, width, rowH, gap, ref y);
        }
    }

    private void BuildTabRows(int width, int rowH, int gap, ref int y)
    {
        var entries = new List<(BrowserWindowEntry Window, BrowserTabEntry Tab)>();
        foreach (var bw in _current!.Browsers)
            foreach (var t in bw.Tabs)
                entries.Add((bw, t));

        if (entries.Count == 0) { AddPlaceholder("没有记录到浏览器标签页"); return; }

        foreach (var (window, tab) in entries.OrderBy(e => e.Window.BrowserId).ThenBy(e => e.Tab.Index))
        {
            string url = tab.Url;
            var row = new CheckRow
            {
                Title = string.IsNullOrWhiteSpace(tab.Title) ? ShortHost(url) : tab.Title.Trim(),
                Subtitle = "",
                Badge = tab.Selected ? "当前" : null,
                Checked = !_excludedUrls.Contains(url) && !UiTheme.IsNewTabUrl(url),
                Payload = tab,
            };
            row.CheckedChanged += (s, _) =>
            {
                if (s is not CheckRow r) return;
                if (r.Checked) _excludedUrls.Remove(url); else _excludedUrls.Add(url);
                Invalidate();
            };
            AddRow(row, width, rowH, gap, ref y);
        }
    }

    private static string BrowserName(BrowserWindowEntry bw) =>
        string.IsNullOrWhiteSpace(bw.DisplayName) ? bw.BrowserId : bw.DisplayName;

    private static string ShortHost(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return uri.Host.Replace("www.", "");
        }
        catch { }
        return url.Length <= 40 ? url : url[..40] + "…";
    }

    private string SelectionSummary()
    {
        if (_current is null) return "";
        int apps = _current.Apps.Count(a => !a.IsIgnored && !_excludedApps.Contains(RestoreSelection.AppKey(a)));
        int urls = _current.Browsers.SelectMany(b => b.Tabs).Count(t => !_excludedUrls.Contains(t.Url));
        return $"已选 {apps} 个应用  ·  {urls} 个网页";
    }

    // ══════════════════════════════════════════════════════════════════
    // 动作
    // ══════════════════════════════════════════════════════════════════

    private void StartRestore()
    {
        if (_busy) return;
        if (_current is null)
        {
            SetStatus("没有可复原的关机记录", Modern.Danger);
            return;
        }

        var snapshot = _current;
        var selection = new RestoreSelection
        {
            Apps = true,
            Browsers = true,
            Media = false,   // 主界面不再管视频
            ExcludedAppKeys = new HashSet<string>(_excludedApps, StringComparer.OrdinalIgnoreCase),
            ExcludedUrls = new HashSet<string>(_excludedUrls, StringComparer.OrdinalIgnoreCase),
        };

        SetBusy(true);
        SetStatus("正在复原…", Modern.Primary);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var progress = new Progress<string>(msg =>
        {
            if (IsDisposed) return;
            SetStatus(msg, Modern.Primary);
        });

        Task.Run(() =>
        {
            try
            {
                var result = _restoreService.Restore(snapshot, selection, progress, token);
                SafeInvoke(() =>
                {
                    SetBusy(false);
                    SetStatus(result.Failed.Count > 0
                        ? $"{result.Summary}（失败 {result.Failed.Count} 项，详见日志）"
                        : result.Summary,
                        result.Failed.Count > 0 ? Modern.Warning : Modern.Text);
                    LoadSnapshots();
                });
            }
            catch (OperationCanceledException)
            {
                SafeInvoke(() => { SetBusy(false); SetStatus("已取消", Modern.Sub); });
            }
            catch (Exception ex)
            {
                Log.Error("复原失败", ex);
                SafeInvoke(() => { SetBusy(false); SetStatus("复原失败：" + ex.Message, Modern.Danger); });
            }
        }, token);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _restoreButton.Enabled = !busy && _current is not null;
        _restoreButton.Text = busy ? "复原中…" : "复原";
        _historyButton.Enabled = !busy;
        _restoreButton.Invalidate();
    }

    /// <summary>打开历史记录窗口（非模态 —— 开着它的时候主窗口照样能操作）。</summary>
    public void OpenHistory()
    {
        try
        {
            if (_historyForm is not null && !_historyForm.IsDisposed)
            {
                _historyForm.Activate();
                return;
            }

            _historyForm = new HistoryForm(_settings, _store, _restoreService);
            _historyForm.FormClosed += (_, _) => _historyForm = null;
            _historyForm.Show(this);
        }
        catch (Exception ex)
        {
            Log.Error("打开历史记录失败", ex);
            SetStatus("打开历史记录失败：" + ex.Message, Modern.Danger);
        }
    }

    private void CaptureNow()
    {
        if (_busy) { SetStatus("正在处理上一个任务", Modern.Warning); return; }
        SetBusy(true);
        SetStatus("正在记录当前桌面…", Modern.Primary);

        Task.Run(() =>
        {
            try
            {
                var snap = _snapshotService.Capture("manually");
                SafeInvoke(() =>
                {
                    SetBusy(false);
                    SetStatus($"已记录进历史：{snap.AppCount} 个应用 · {snap.TabCount} 个网页", Modern.Text);
                    BalloonHook?.Invoke("WinBC · 已记录", $"{snap.AppCount} 个应用、{snap.TabCount} 个标签页", false);
                    LoadSnapshots();
                });
            }
            catch (Exception ex)
            {
                Log.Error("手动记录失败", ex);
                SafeInvoke(() => { SetBusy(false); SetStatus("记录失败：" + ex.Message, Modern.Danger); });
            }
        });
    }

    private void OpenArchiveFolder()
    {
        try
        {
            Directory.CreateDirectory(_store.RootDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_store.RootDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("打开目录失败：" + ex.Message, Modern.Danger);
        }
    }

    private void OpenSettings()
    {
        try
        {
            using var dlg = new SettingsForm(_settings);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                SetStatus("设置已保存", Modern.Text);
                BalloonHook?.Invoke("WinBC", "设置已保存", false);
                // 保留天数/份数可能改过，重新载入并按新策略清理一次
                _store.Prune(_settings.MaxSnapshots, _settings.RetentionDays);
                LoadSnapshots();
            }
        }
        catch (Exception ex)
        {
            Log.Error("打开设置失败", ex);
        }
    }

    private void ExitApp()
    {
        _allowClose = true;
        try { Application.Exit(); } catch { }
    }

    private void ShowMenu()
    {
        _menu.Show(_menuButton, new Point(0, _menuButton.Height + this.S(4)));
    }

    private void SafeInvoke(Action action)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch { }
    }

    // ══════════════════════════════════════════════════════════════════
    // 窗口交互（拖动 / 边缘缩放由 ModernForm 处理）
    // ══════════════════════════════════════════════════════════════════

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 只要宿主没说"可以关了"，就一律收进托盘。
        // （不能只判断 CloseReason.UserClosing —— 程序化关闭、任务管理器关闭等
        //   CloseReason 都不是 UserClosing，那就会真的把窗口关掉。）
        if (!_allowClose)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        try { _cts?.Cancel(); } catch { }
        base.OnFormClosing(e);
    }

    /// <summary>收进托盘：切后台低优先级 + 让工作集回落。
    /// 注意不能 Dispose 这个窗体 —— WinForms 的消息循环在最后一个窗体消失时会直接退出进程，
    /// 表现就是"点关闭 = 程序没了"。</summary>
    private void HideToTray()
    {
        Hide();
        Util.ResourceTuning.EnterBackground();
        Util.ResourceTuning.TrimWorkingSet();
        BalloonHook?.Invoke("WinBC", "已最小化到托盘，右键图标可再次打开", false);
    }

    private void CloseOrHide()
    {
        if (_allowClose) Close();
        else HideToTray();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _cts?.Cancel(); _cts?.Dispose(); } catch { }
            try { _menu.Dispose(); } catch { }
            try { if (_historyForm is not null && !_historyForm.IsDisposed) _historyForm.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
