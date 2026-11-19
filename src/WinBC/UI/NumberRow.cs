using System.Drawing.Drawing2D;

namespace WinBC.UI;

/// <summary>
/// 设置项：左边标题+说明，右边一个"−  数值  +"的调节器。用于秒数、天数、份数这类数值。
/// </summary>
internal sealed class NumberRow : Control
{
    private bool _hoverMinus;
    private bool _hoverPlus;
    private int _value;

    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public int Min { get; set; } = 1;
    public int Max { get; set; } = 100;
    public int Step { get; set; } = 1;
    public string Unit { get; set; } = "";

    public event EventHandler? ValueChanged;

    public int Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, Min, Max);
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public NumberRow()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Font = Modern.Body;
        Height = 56;
    }

    // ── 命中区域 ──
    private (RectangleF Minus, RectangleF Plus) Regions()
    {
        int pad = this.S(10);
        float h = this.S(28);
        float top = (Height - h) / 2f;
        float w = this.S(28);
        float right = Width - pad;
        var plus = new RectangleF(right - w, top, w, h);
        var minus = new RectangleF(plus.Left - this.S(64) - w, top, w, h);
        return (minus, plus);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var (minus, plus) = Regions();
        bool hm = minus.Contains(e.Location);
        bool hp = plus.Contains(e.Location);
        if (hm != _hoverMinus || hp != _hoverPlus)
        {
            _hoverMinus = hm;
            _hoverPlus = hp;
            Cursor = (hm || hp) ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hoverMinus || _hoverPlus)
        {
            _hoverMinus = _hoverPlus = false;
            Invalidate();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }

        var (minus, plus) = Regions();
        if (minus.Contains(e.Location)) Value = _value - Step;
        else if (plus.Contains(e.Location)) Value = _value + Step;
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Value = _value + (e.Delta > 0 ? Step : -Step);
        base.OnMouseWheel(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        int pad = this.S(10);
        var (minus, plus) = Regions();

        // 左边文字
        float textWidth = Math.Max(this.S(60), minus.Left - pad - this.S(12));
        if (!string.IsNullOrEmpty(Subtitle))
        {
            Modern.DrawText(g, Title, Modern.BodyBold, Modern.Text,
                new RectangleF(pad, Height / 2f - this.S(19), textWidth, this.S(20)));
            Modern.DrawText(g, Subtitle, Modern.Small, Modern.Sub,
                new RectangleF(pad, Height / 2f + this.S(1), textWidth, this.S(18)));
        }
        else
        {
            Modern.DrawText(g, Title, Modern.Body, Modern.Text,
                new RectangleF(pad, 0, textWidth, Height));
        }

        // 右边调节器：底槽
        float slotLeft = minus.Left - this.S(6);
        float slotRight = plus.Right + this.S(6);
        var slot = new RectangleF(slotLeft, minus.Top - this.S(2),
            slotRight - slotLeft, minus.Height + this.S(4));
        Modern.FillRound(g, slot, this.Sf(9f), Modern.CardAlt);

        // − / +
        DrawStepButton(g, minus, "−", _hoverMinus);
        DrawStepButton(g, plus, "+", _hoverPlus);

        // 中间数值
        var valueRect = new RectangleF(minus.Right, minus.Top, plus.Left - minus.Right, minus.Height);
        var text = string.IsNullOrEmpty(Unit) ? _value.ToString() : $"{_value}{Unit}";
        Modern.DrawText(g, text, Modern.BodyBold, Modern.Text, valueRect, ContentAlignment.MiddleCenter);

        // 到边界时把对应按钮画淡
        if (_value <= Min)
            Modern.DrawText(g, "−", Modern.BodyBold, Modern.Faint,
                new RectangleF(minus.X, minus.Y, minus.Width, minus.Height), ContentAlignment.MiddleCenter);
        if (_value >= Max)
            Modern.DrawText(g, "+", Modern.BodyBold, Modern.Faint,
                new RectangleF(plus.X, plus.Y, plus.Width, plus.Height), ContentAlignment.MiddleCenter);
    }

    private void DrawStepButton(Graphics g, RectangleF r, string glyph, bool hover)
    {
        if (hover) Modern.FillRound(g, r, this.Sf(8f), Modern.Hover);
        bool atLimit = glyph == "−" ? _value <= Min : _value >= Max;
        Modern.DrawText(g, glyph, Modern.BodyBold,
            atLimit ? Modern.Faint : Modern.Text, r, ContentAlignment.MiddleCenter);
    }
}
