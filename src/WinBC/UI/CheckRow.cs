using System.Drawing.Drawing2D;

namespace WinBC.UI;

/// <summary>
/// 一行可勾选的内容：左边圆形复选框，中间标题 + 说明，右边可选标签。
/// 整行可点，鼠标移上去有圆角高亮。
/// </summary>
internal sealed class CheckRow : Control
{
    private bool _hover;
    private bool _checked = true;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>是否画左边的圆形勾选框（历史记录里用不到勾选）。</summary>
    public bool ShowCheck { get; set; } = true;

    private bool _selected;

    /// <summary>整行被选中（历史记录用）。</summary>
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Invalidate();
        }
    }

    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    /// <summary>右侧小胶囊文字，例如"跳过""14 个标签页"。</summary>
    public string? Badge { get; set; }
    public bool BadgeIsWarning { get; set; }
    /// <summary>灰显（例如系统壳窗口，默认不勾）。</summary>
    public bool Muted { get; set; }
    public object? Payload { get; set; }

    public event EventHandler? CheckedChanged;

    public CheckRow()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Font = Modern.Body;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && ShowCheck)
        {
            Checked = !Checked;
            base.OnMouseDown(e);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        int pad = this.S(10);
        int boxSize = this.S(18);
        int gap = this.S(12);
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);

        if (_selected) Modern.FillRound(g, bounds, this.Sf(8f), Modern.PrimarySoft);
        else if (_hover) Modern.FillRound(g, bounds, this.Sf(8f), Modern.Hover);

        float textLeft = pad;

        // ── 复选框：选中时只画一个勾，不铺底色 ──
        if (ShowCheck)
        {
            var box = new RectangleF(pad, (Height - boxSize) / 2f, boxSize, boxSize);

            if (Checked)
            {
                using var pen = new Pen(Muted ? Modern.Faint : Modern.Text, this.Sf(2.2f))
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round,
                };
                g.DrawLines(pen, new[]
                {
                    new PointF(box.Left + boxSize * 0.16f, box.Top + boxSize * 0.52f),
                    new PointF(box.Left + boxSize * 0.42f, box.Top + boxSize * 0.76f),
                    new PointF(box.Left + boxSize * 0.86f, box.Top + boxSize * 0.24f),
                });
            }
            else
            {
                using var pen = new Pen(Modern.Border, this.Sf(1.6f));
                g.DrawEllipse(pen, box);
            }

            textLeft = box.Right + gap;
        }

        // ── 右侧标签 ──
        float rightEdge = Width - pad;
        if (!string.IsNullOrEmpty(Badge))
        {
            var badgeFont = Modern.Tiny;
            var size = g.MeasureString(Badge, badgeFont);
            float bw = size.Width + this.S(14);
            float bh = this.S(20);
            var badgeRect = new RectangleF(rightEdge - bw, (Height - bh) / 2f, bw, bh);
            Modern.FillPill(g, badgeRect, BadgeIsWarning ? Color.FromArgb(254, 243, 199) : Modern.CardAlt);
            Modern.DrawText(g, Badge!, badgeFont,
                BadgeIsWarning ? Color.FromArgb(180, 120, 10) : Modern.Sub,
                badgeRect, ContentAlignment.MiddleCenter);
            rightEdge = badgeRect.Left - this.S(10);
        }

        // ── 文本 ──
        float textWidth = Math.Max(0, rightEdge - textLeft);
        bool hasSub = !string.IsNullOrEmpty(Subtitle);

        if (hasSub)
        {
            var titleRect = new RectangleF(textLeft, Height / 2f - this.S(19), textWidth, this.S(20));
            var subRect = new RectangleF(textLeft, Height / 2f + this.S(1), textWidth, this.S(18));
            Modern.DrawText(g, Title, Modern.BodyBold, Muted ? Modern.Sub : Modern.Text, titleRect);
            Modern.DrawText(g, Subtitle, Modern.Small, Modern.Sub, subRect);
        }
        else
        {
            var titleRect = new RectangleF(textLeft, 0, textWidth, Height);
            Modern.DrawText(g, Title, Modern.Body, Muted ? Modern.Sub : Modern.Text, titleRect);
        }
    }
}
