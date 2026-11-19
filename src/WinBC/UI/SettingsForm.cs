using System.Drawing.Drawing2D;
using WinBC.Native;

namespace WinBC.UI;

/// <summary>
/// 设置窗口：两列排布，一屏看完，不需要滚动。
/// 和主界面同一套暗色/浅色外观。
/// </summary>
public sealed class SettingsForm : ModernForm
{
    private readonly Settings _settings;

    private readonly RoundButton _closeButton = new();
    private readonly RoundButton _saveButton = new();
    private readonly RoundButton _cancelButton = new();

    private CheckRow _promptRow = null!;
    private CheckRow _restorePosRow = null!;
    private CheckRow _rollingRow = null!;
    private CheckRow _browserRow = null!;
    private CheckRow _uwpRow = null!;
    private CheckRow _darkRow = null!;
    private NumberRow _intervalRow = null!;
    private NumberRow _retentionRow = null!;
    private NumberRow _maxRow = null!;

    private int _leftY;
    private int _rightY;

    private const int HeaderH = 46;
    private const int Pad = 18;
    private const int RowH = 56;
    private const int ColW = 292;
    private const int Col1X = 18;
    private const int Col2X = 330;

    public SettingsForm(Settings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        BuildUi();
        LoadValues();
    }

    // ══════════════════════════════════════════════════════════════════

    private void BuildUi()
    {
        SuspendLayout();

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.None;   // 尺寸统一由 S() 缩放，避免和 WinForms 自动缩放叠加
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Modern.Card;
        ForeColor = Modern.Text;
        Text = "设置";
        StartPosition = FormStartPosition.CenterParent;
        DoubleBuffered = true;
        ShowInTaskbar = false;
        KeyPreview = true;
        try { Icon = Util.AppIcon.Instance; } catch { }

        _closeButton.Text = "✕";
        _closeButton.Ghost = true;
        _closeButton.Radius = 8;
        _closeButton.Font = new Font(Modern.Family, 10F, FontStyle.Regular, GraphicsUnit.Point);
        _closeButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        _saveButton.Primary = true;
        _saveButton.Text = "保存";
        _saveButton.Radius = 10;
        _saveButton.Font = new Font(Modern.Family, 10F, FontStyle.Bold, GraphicsUnit.Point);
        _saveButton.Click += (_, _) => SaveAndClose();

        _cancelButton.Text = "取消";
        _cancelButton.Radius = 10;
        _cancelButton.Font = new Font(Modern.Family, 10F, FontStyle.Regular, GraphicsUnit.Point);
        _cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.AddRange(new Control[] { _closeButton, _cancelButton, _saveButton });

        BuildContent();

        ResumeLayout(false);
        LayoutAll();
    }

    /// <summary>左列：恢复 + 记录；右列：存储 + 外观。</summary>
    private void BuildContent()
    {
        int top = this.S(HeaderH) + this.S(10);
        _leftY = top;
        _rightY = top;

        AddSection(true, "恢复");
        _promptRow = AddToggle(true, "开机后询问是否恢复", "启动时弹小卡片问一句",
            v => _settings.AutoPromptOnStartup = v);
        _restorePosRow = AddToggle(true, "复原时还原窗口位置", "摆回原来的位置和大小",
            v => _settings.RestoreWindowPositions = v);

        AddSection(true, "记录");
        _rollingRow = AddToggle(true, "定时自动记录", "关机事件的兜底备份",
            v => _settings.EnableRollingSnapshot = v);
        _intervalRow = AddNumber(true, "记录间隔", "多久自动记一次", 15, 3600, 15, " 秒",
            v => _settings.RollingIntervalSeconds = v);
        _browserRow = AddToggle(true, "记录浏览器标签页", "当前开着的网页",
            v => _settings.RestoreBrowserTabs = v);
        _uwpRow = AddToggle(true, "记录商店应用", "照片、计算器等",
            v => _settings.IncludeUwpApps = v);

        AddSection(false, "存储");
        _retentionRow = AddNumber(false, "保留天数", "超过就自动删除", 1, 3650, 1, " 天",
            v => _settings.RetentionDays = v);
        _maxRow = AddNumber(false, "最多保留", "数量上限", 5, 500, 5, " 份",
            v => _settings.MaxSnapshots = v);

        AddSection(false, "外观");
        _darkRow = AddToggle(false, "深色模式", "关掉就是浅色",
            v => _settings.DarkMode = v);
    }

    private void LoadValues()
    {
        _promptRow.Checked = _settings.AutoPromptOnStartup;
        _restorePosRow.Checked = _settings.RestoreWindowPositions;
        _rollingRow.Checked = _settings.EnableRollingSnapshot;
        _intervalRow.Value = _settings.RollingIntervalSeconds;
        _browserRow.Checked = _settings.RestoreBrowserTabs;
        _uwpRow.Checked = _settings.IncludeUwpApps;
        _retentionRow.Value = _settings.RetentionDays;
        _maxRow.Value = _settings.MaxSnapshots;
        _darkRow.Checked = _settings.DarkMode;
    }

    // ── 两列填充 ──────────────────────────────────────────────

    private void AddSection(bool left, string title)
    {
        ref int y = ref left ? ref _leftY : ref _rightY;
        int x = left ? Col1X : Col2X;

        var label = new Label
        {
            Text = title,
            Font = Modern.Small,
            ForeColor = Modern.Faint,
            AutoSize = false,
            BackColor = Color.Transparent,
            Bounds = new Rectangle(this.S(x + 4), y + this.S(8), this.S(200), this.S(18)),
        };
        Controls.Add(label);
        y += this.S(30);
    }

    private CheckRow AddToggle(bool left, string title, string subtitle, Action<bool> onChange)
    {
        var row = new CheckRow
        {
            Title = title,
            Subtitle = subtitle,
            Checked = true,
        };
        row.CheckedChanged += (_, _) => onChange(row.Checked);
        AddRow(left, row);
        return row;
    }

    private NumberRow AddNumber(bool left, string title, string subtitle,
        int min, int max, int step, string unit, Action<int> onChange)
    {
        var row = new NumberRow
        {
            Title = title,
            Subtitle = subtitle,
            Min = min,
            Max = max,
            Step = step,
            Unit = unit,
        };
        row.ValueChanged += (_, _) => onChange(row.Value);
        AddRow(left, row);
        return row;
    }

    private void AddRow(bool left, Control c)
    {
        ref int y = ref left ? ref _leftY : ref _rightY;
        int x = left ? Col1X : Col2X;

        c.Bounds = new Rectangle(this.S(x), y, this.S(ColW), this.S(RowH));
        Controls.Add(c);
        y += this.S(RowH) + this.S(1);
    }

    // ══════════════════════════════════════════════════════════════════

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyInitialSize(640, 548);
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

        int pad = this.S(Pad);
        int icon = this.S(28);
        int top = this.S(HeaderH);

        _closeButton.Bounds = new Rectangle(ClientSize.Width - pad - icon, (top - icon) / 2, icon, icon);

        int btnH = this.S(42);
        int btnW = this.S(96);
        int bottomY = ClientSize.Height - pad - btnH;
        _saveButton.Bounds = new Rectangle(ClientSize.Width - pad - btnW, bottomY, btnW, btnH);
        _cancelButton.Bounds = new Rectangle(_saveButton.Left - this.S(8) - btnW, bottomY, btnW, btnH);

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var bg = new SolidBrush(Modern.Card))
            g.FillRectangle(bg, ClientRectangle);

        int pad = this.S(Pad);
        int top = this.S(HeaderH);

        using (var pen = new Pen(Modern.Border, this.Sf(1f)))
            g.DrawLine(pen, 0, top, Width, top);

        float dot = this.S(8);
        Modern.FillPill(g, new RectangleF(pad, (top - dot) / 2f, dot, dot), Modern.Primary);
        Modern.DrawText(g, "设置", Modern.H2, Modern.Text,
            new RectangleF(pad + dot + this.S(8), 0, this.S(200), top), ContentAlignment.MiddleLeft);
    }

    // ══════════════════════════════════════════════════════════════════

    private void SaveAndClose()
    {
        try
        {
            _settings.Save();   // Save() 内部会 Normalize 一次
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            Util.Log.Error("保存设置失败", ex);
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
