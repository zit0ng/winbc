using System.Drawing.Drawing2D;
using WinBC.Models;
using WinBC.Native;
using WinBC.Restore;
using WinBC.Storage;
using WinBC.Util;

namespace WinBC.UI;

/// <summary>
/// 历史记录窗口（和"关机前复原"分开的独立界面，非模态，可以和主窗口同时操作）。
/// 左边挑一个时间点，右边预览那一刻开着什么，逐条勾选后恢复。
/// </summary>
public sealed class HistoryForm : ModernForm
{
    private readonly Settings _settings;
    private readonly SnapshotStore _store;
    private readonly RestoreService _restoreService;

    private readonly RoundButton _closeButton = new();
    private readonly RoundButton _restoreButton = new();
    private readonly RoundButton _clearButton = new();
    private readonly ChipBar _chips = new();
    private readonly Panel _listHost = new();
    private readonly Panel _previewHost = new();
    private readonly ContextMenuStrip _rowMenu = new();
    private readonly ToolStripMenuItem _pinItem = new();
    private int _menuIndex = -1;

    private List<Snapshot> _items = new();
    private int _selected = -1;
    private int _previewTab;
    private readonly HashSet<string> _excludedApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excludedUrls = new(StringComparer.OrdinalIgnoreCase);

    private string _status = "";
    private Color _statusColor = Modern.Sub;
    private bool _busy;

    private const int ContentPad = 18;
    private const int LeftWidth = 200;
    private const int RowHeight = 52;

    public HistoryForm(Settings settings, SnapshotStore store, RestoreService restoreService)
    {
        _settings = settings;
        _store = store;
        _restoreService = restoreService;

        BuildUi();
        LoadItems();
    }

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
        Text = "WinBC · 历史记录";
        StartPosition = FormStartPosition.CenterParent;
        DoubleBuffered = true;
        ShowInTaskbar = false;
        try { Icon = Util.AppIcon.Instance; } catch { }

        _closeButton.Text = "✕";
        _closeButton.Ghost = true;
        _closeButton.Radius = 8;
        _closeButton.Font = new Font(Modern.Family, 10F, FontStyle.Regular, GraphicsUnit.Point);
        _closeButton.Click += (_, _) => Close();

        _restoreButton.Primary = true;
        _restoreButton.Text = "恢复";
        _restoreButton.Radius = 11;
        _restoreButton.Font = new Font(Modern.Family, 10.5F, FontStyle.Bold, GraphicsUnit.Point);
        _restoreButton.Enabled = false;
        _restoreButton.Click += (_, _) => RestoreSelected();

        _clearButton.Ghost = true;
        _clearButton.Text = "清除全部";
        _clearButton.Radius = 10;
        _clearButton.Font = new Font(Modern.Family, 9.5F, FontStyle.Regular, GraphicsUnit.Point);
        _clearButton.ForeColor = Modern.Sub;
        _clearButton.Click += (_, _) => ClearAll();

        // 记录行右键菜单
        _pinItem.Text = "置顶";
        _pinItem.Click += (_, _) => TogglePin();
        _rowMenu.Items.Add(_pinItem);
        _rowMenu.Items.Add(new ToolStripSeparator());
        _rowMenu.Items.Add("删除这条记录", null, (_, _) => DeleteRow());
        ModernMenu.Style(_rowMenu);

        _chips.Items = new[] { "应用", "网页" };
        _chips.SelectedIndexChanged += (_, _) => { _previewTab = _chips.SelectedIndex; RefreshPreview(); };

        _listHost.AutoScroll = true;
        _listHost.BackColor = Modern.Card;
        _listHost.Resize += (_, _) => ResizeRows(_listHost);

        _previewHost.AutoScroll = true;
        _previewHost.BackColor = Modern.Card;
        _previewHost.Resize += (_, _) => ResizeRows(_previewHost);

        Controls.AddRange(new Control[] { _closeButton, _listHost, _previewHost, _chips, _clearButton, _restoreButton });

        ResumeLayout(false);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyInitialSize(760, 640, 620, 480);
        LayoutAll();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutAll();
    }

    private void LayoutAll()
    {
        if (ClientSize.Width <= 0) return;

        int pad = this.S(ContentPad);
        int top = this.S(HeaderHeight);
        int icon = this.S(30);

        _closeButton.Bounds = new Rectangle(ClientSize.Width - pad - icon, (top - icon) / 2, icon, icon);

        int btnH = this.S(44);
        int btnW = this.S(132);
        int bottomY = ClientSize.Height - pad - btnH;
        _restoreButton.Bounds = new Rectangle(ClientSize.Width - pad - btnW, bottomY, btnW, btnH);

        int clearW = this.S(84);
        _clearButton.Bounds = new Rectangle(pad, bottomY + (btnH - this.S(32)) / 2, clearW, this.S(32));

        int listTop = top + this.S(14);
        int listBottom = bottomY - this.S(12);
        int leftW = Math.Min(this.S(LeftWidth), ClientSize.Width / 2 - pad);
        int rightX = pad + leftW + this.S(14);
        int rightW = ClientSize.Width - pad - rightX;

        _listHost.Bounds = new Rectangle(pad, listTop, leftW, Math.Max(this.S(60), listBottom - listTop));

        _chips.Bounds = new Rectangle(rightX, listTop, rightW, this.S(32));
        int previewTop = listTop + this.S(32) + this.S(8);
        _previewHost.Bounds = new Rectangle(rightX, previewTop, rightW,
            Math.Max(this.S(60), listBottom - previewTop));

        ResizeRows(_listHost);
        ResizeRows(_previewHost);
        Invalidate();
    }

    /// <summary>只改宽度，不动位置 —— 位置一旦放下就别再设，否则会和 AutoScroll 打架。</summary>
    private void ResizeRows(Panel host)
    {
        if (host.IsDisposed) return;
        int width = Math.Max(this.S(120), host.ClientSize.Width - this.S(4));
        foreach (Control c in host.Controls) c.Width = width;
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
        Modern.DrawText(g, "历史记录", Modern.H2, Modern.Text,
            new RectangleF(pad + dot + this.S(8), 0, this.S(240), top), ContentAlignment.MiddleLeft);

        // 底部：清除（左）、状态（中）、恢复（右）
        var statusRect = new RectangleF(_clearButton.Right + this.S(14), _restoreButton.Top + this.S(4),
            _restoreButton.Left - _clearButton.Right - this.S(28), this.S(36));
        var text = !string.IsNullOrEmpty(_status) ? _status
                 : _selected >= 0 && _selected < _items.Count ? SelectionSummary()
                 : "从左边选一条记录";
        Modern.DrawText(g, text, Modern.Small,
            !string.IsNullOrEmpty(_status) ? _statusColor : Modern.Sub, statusRect);
    }

    // ══════════════════════════════════════════════════════════════════
    // 左侧：记录列表
    // ══════════════════════════════════════════════════════════════════

    private void LoadItems()
    {
        try
        {
            _items = _store.ListSnapshots().ToList();
        }
        catch (Exception ex)
        {
            Log.Error("读取历史记录失败", ex);
            _items = new List<Snapshot>();
        }

        RebuildList();
        _selected = _items.Count > 0 ? 0 : -1;
        ApplyListSelection();
        RefreshPreview();
        Invalidate();
    }

    private void RebuildList()
    {
        _listHost.SuspendLayout();
        Clear(_listHost);
        try { _listHost.AutoScrollPosition = new Point(0, 0); } catch { }

        if (_items.Count == 0)
        {
            _listHost.Controls.Add(new Label
            {
                Text = "还没有记录",
                Font = Modern.Body,
                ForeColor = Modern.Faint,
                TextAlign = ContentAlignment.MiddleCenter,
                AutoSize = false,
                BackColor = Color.Transparent,
                Bounds = new Rectangle(0, this.S(30),
                    Math.Max(this.S(120), _listHost.ClientSize.Width - this.S(4)), this.S(40)),
            });
            _listHost.ResumeLayout();
            return;
        }

        int width = Math.Max(this.S(120), _listHost.ClientSize.Width - this.S(4));
        int rowH = this.S(RowHeight);
        int gap = this.S(3);
        int y = this.S(2);

        // 一次性读出置顶集合：之前每行都调一次 IsPinned，40 行就是 40 次文件读取
        var pinnedIds = _store.GetPinnedIds();

        for (int i = 0; i < _items.Count; i++)
        {
            var snap = _items[i];
            int index = i;
            bool pinned = pinnedIds.Contains(snap.Id);

            var row = new CheckRow
            {
                ShowCheck = false,
                Title = Modern.FormatTime(snap.CreatedAt),
                Subtitle = $"{snap.Apps.Count(a => !a.IsIgnored)} 应用 · {snap.TabCount} 网页",
                Badge = pinned ? "★ " + Modern.ReasonText(snap.Reason) : Modern.ReasonText(snap.Reason),
                Checked = true,
                Bounds = new Rectangle(0, y, width, rowH),
            };
            row.MouseClick += (_, _) =>
            {
                _selected = index;
                ApplyListSelection();
                RefreshPreview();
            };
            row.MouseUp += (_, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                _menuIndex = index;
                _selected = index;
                ApplyListSelection();
                RefreshPreview();
                _pinItem.Text = _store.IsPinned(snap.Id) ? "取消置顶" : "置顶";
                _rowMenu.Show(row, e.Location);
            };
            _listHost.Controls.Add(row);
            y += rowH + gap;
        }

        _listHost.ResumeLayout();
    }

    private void ApplyListSelection()
    {
        int i = 0;
        foreach (Control c in _listHost.Controls)
        {
            if (c is CheckRow row)
            {
                row.Selected = i == _selected;
                i++;
            }
        }
        _restoreButton.Enabled = _selected >= 0 && !_busy;
    }

    // ══════════════════════════════════════════════════════════════════
    // 右侧：预览
    // ══════════════════════════════════════════════════════════════════

    private void RefreshPreview()
    {
        _excludedApps.Clear();
        _excludedUrls.Clear();
        _status = "";

        _previewHost.SuspendLayout();
        Clear(_previewHost);
        try { _previewHost.AutoScrollPosition = new Point(0, 0); } catch { }

        if (_selected < 0 || _selected >= _items.Count)
        {
            _chips.Counts = new[] { 0, 0 };
            _previewHost.ResumeLayout();
            Invalidate();
            return;
        }

        var snap = _items[_selected];
        int appCount = snap.Apps.Count(a => !a.IsIgnored);
        _chips.Counts = new[] { appCount, snap.TabCount };

        int width = Math.Max(this.S(120), _previewHost.ClientSize.Width - this.S(4));
        int rowH = this.S(40);
        int gap = this.S(2);
        int y = this.S(2);

        if (_previewTab == 0) BuildAppRows(snap, width, rowH, gap, ref y);
        else BuildTabRows(snap, width, rowH, gap, ref y);

        _previewHost.ResumeLayout();
        Invalidate();
    }

    private static void Clear(Panel host)
    {
        foreach (Control c in host.Controls.Cast<Control>().ToList())
        {
            host.Controls.Remove(c);
            c.Dispose();
        }
    }

    private void AddPreviewEmpty(string text)
    {
        _previewHost.Controls.Add(new Label
        {
            Text = text,
            Font = Modern.Body,
            ForeColor = Modern.Faint,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = false,
            BackColor = Color.Transparent,
            Bounds = new Rectangle(0, this.S(30),
                Math.Max(this.S(120), _previewHost.ClientSize.Width - this.S(4)), this.S(40)),
        });
    }

    private void BuildAppRows(Snapshot snap, int width, int rowH, int gap, ref int y)
    {
        var apps = snap.Apps.Where(a => !a.IsIgnored).OrderBy(a => a.ZOrder).ToList();
        if (apps.Count == 0) { AddPreviewEmpty("这一刻没有记录到应用程序"); return; }

        foreach (var app in apps)
        {
            string key = RestoreSelection.AppKey(app);
            var row = new CheckRow
            {
                Title = app.ProcessName,
                Subtitle = "",
                Checked = true,
                Badge = null,
                Bounds = new Rectangle(0, y, width, rowH),
            };
            row.CheckedChanged += (s, _) =>
            {
                if (s is not CheckRow r) return;
                if (r.Checked) _excludedApps.Remove(key); else _excludedApps.Add(key);
                Invalidate();
            };
            _previewHost.Controls.Add(row);
            y += rowH + gap;
        }
    }

    private void BuildTabRows(Snapshot snap, int width, int rowH, int gap, ref int y)
    {
        var entries = snap.Browsers.SelectMany(b => b.Tabs).ToList();
        if (entries.Count == 0) { AddPreviewEmpty("这一刻没有记录到网页"); return; }

        foreach (var tab in entries.OrderBy(t => t.Index))
        {
            string url = tab.Url;
            bool blank = UiTheme.IsNewTabUrl(url);
            var row = new CheckRow
            {
                Title = string.IsNullOrWhiteSpace(tab.Title) ? ShortHost(url) : tab.Title.Trim(),
                Subtitle = "",
                Checked = !blank,
                Badge = tab.Selected ? "当前" : null,
                Bounds = new Rectangle(0, y, width, rowH),
            };
            if (blank) _excludedUrls.Add(url);

            row.CheckedChanged += (s, _) =>
            {
                if (s is not CheckRow r) return;
                if (r.Checked) _excludedUrls.Remove(url); else _excludedUrls.Add(url);
                Invalidate();
            };
            _previewHost.Controls.Add(row);
            y += rowH + gap;
        }
    }

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
        if (_selected < 0 || _selected >= _items.Count) return "";
        var snap = _items[_selected];
        int apps = snap.Apps.Count(a => !a.IsIgnored && !_excludedApps.Contains(RestoreSelection.AppKey(a)));
        int urls = snap.Browsers.SelectMany(b => b.Tabs).Count(t => !_excludedUrls.Contains(t.Url));
        return $"将恢复 {apps} 个应用 · {urls} 个网页";
    }

    // ══════════════════════════════════════════════════════════════════
    // 记录管理：置顶 / 删除 / 清空
    // ══════════════════════════════════════════════════════════════════

    private void TogglePin()
    {
        if (_menuIndex < 0 || _menuIndex >= _items.Count) return;
        var snap = _items[_menuIndex];
        bool now = !_store.IsPinned(snap.Id);
        _store.SetPinned(snap.Id, now);

        // 记住当前查看的那一条，重排后还选中它
        string keepId = snap.Id;
        LoadItems();
        int found = _items.FindIndex(s => s.Id == keepId);
        if (found >= 0)
        {
            _selected = found;
            ApplyListSelection();
            RefreshPreview();
        }

        _status = now ? "已置顶" : "已取消置顶";
        _statusColor = Modern.Sub;
        Invalidate();
    }

    private void DeleteRow()
    {
        if (_menuIndex < 0 || _menuIndex >= _items.Count) return;
        var snap = _items[_menuIndex];

        var confirm = MessageBox.Show(this,
            $"删除「{Modern.FormatTime(snap.CreatedAt)}」这条记录？\r\n\r\n只是删掉这份快照文件，不会影响你当前的桌面。",
            "WinBC · 删除记录", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;

        _store.Delete(snap.Id);
        _store.SetPinned(snap.Id, false);
        LoadItems();
        _status = "已删除一条记录";
        _statusColor = Modern.Sub;
        Invalidate();
    }

    private void ClearAll()
    {
        if (_items.Count == 0) return;

        var confirm = MessageBox.Show(this,
            $"清除全部 {_items.Count} 条历史记录？\r\n\r\n快照文件会被删除，无法恢复。",
            "WinBC · 清除历史记录", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK) return;

        _store.DeleteAll();
        _items = new List<Snapshot>();
        RebuildList();
        _selected = -1;
        _excludedApps.Clear();
        _excludedUrls.Clear();
        ApplyListSelection();   // 必须在 _selected 改成 -1 之后调用，否则「恢复」按钮还是亮的
        RefreshPreview();
        _status = "已清除全部历史记录";
        _statusColor = Modern.Sub;
        Invalidate();
    }

    // ══════════════════════════════════════════════════════════════════
    // 恢复
    // ══════════════════════════════════════════════════════════════════

    private void RestoreSelected()
    {
        if (_busy) return;
        if (_selected < 0 || _selected >= _items.Count) return;

        var snap = _items[_selected];
        var selection = new RestoreSelection
        {
            Apps = true,
            Browsers = true,
            Media = false,
            ExcludedAppKeys = new HashSet<string>(_excludedApps, StringComparer.OrdinalIgnoreCase),
            ExcludedUrls = new HashSet<string>(_excludedUrls, StringComparer.OrdinalIgnoreCase),
        };

        SetBusy(true);
        _status = "正在恢复…";
        _statusColor = Modern.Primary;
        Invalidate();

        var progress = new Progress<string>(msg =>
        {
            if (IsDisposed) return;
            _status = msg;
            _statusColor = Modern.Primary;
            Invalidate();
        });

        Task.Run(() =>
        {
            try
            {
                var result = _restoreService.Restore(snap, selection, progress);
                SafeInvoke(() =>
                {
                    SetBusy(false);
                    _status = result.Failed.Count > 0
                        ? $"{result.Summary}（{result.Failed.Count} 项失败）"
                        : result.Summary;
                    _statusColor = result.Failed.Count > 0 ? Modern.Warning : Modern.Text;
                    Invalidate();
                });
            }
            catch (Exception ex)
            {
                Log.Error("恢复历史状态失败", ex);
                SafeInvoke(() =>
                {
                    SetBusy(false);
                    _status = "恢复失败：" + ex.Message;
                    _statusColor = Modern.Danger;
                    Invalidate();
                });
            }
        });
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _restoreButton.Enabled = !busy && _selected >= 0;
        _restoreButton.Text = busy ? "恢复中…" : "恢复";
        _clearButton.Enabled = !busy;      // 恢复过程中不允许把记录清空
        _restoreButton.Invalidate();
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
}
