using System.Drawing.Drawing2D;

namespace WinBC.UI;

/// <summary>胶囊分段切换（应用 / 网页 / 视频）。</summary>
internal sealed class ChipBar : Control
{
    private string[] _items = Array.Empty<string>();
    private int[] _counts = Array.Empty<int>();
    private int _selected;
    private int _hover = -1;
    private RectangleF[] _rects = Array.Empty<RectangleF>();

    public event EventHandler? SelectedIndexChanged;

    public ChipBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Height = 36;
    }

    public string[] Items
    {
        get => _items;
        set { _items = value ?? Array.Empty<string>(); _hover = -1; Invalidate(); }
    }

    /// <summary>每个 chip 后面的数字（显示成 "应用 8"）。可空。</summary>
    public int[] Counts
    {
        get => _counts;
        set { _counts = value ?? Array.Empty<int>(); Invalidate(); }
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            var v = Math.Clamp(value, 0, Math.Max(0, _items.Length - 1));
            if (v == _selected) return;
            _selected = v;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private string TextOf(int i) =>
        i < _counts.Length && _counts[i] >= 0 ? $"{_items[i]} {_counts[i]}" : _items[i];

    private void Recalc(Graphics g)
    {
        var rects = new RectangleF[_items.Length];
        float x = 0;
        float h = this.S(32);
        float top = (Height - h) / 2f;

        for (int i = 0; i < _items.Length; i++)
        {
            var size = g.MeasureString(TextOf(i), Modern.Body);
            float w = size.Width + this.S(26);
            rects[i] = new RectangleF(x, top, w, h);
            x += w + this.S(8);
        }
        _rects = rects;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hit = HitTest(e.Location);
        if (hit != _hover)
        {
            _hover = hit;
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != -1)
        {
            _hover = -1;
            Invalidate();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            int hit = HitTest(e.Location);
            if (hit >= 0) SelectedIndex = hit;
        }
        base.OnMouseDown(e);
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _rects.Length; i++)
            if (_rects[i].Contains(p)) return i;
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        Recalc(g);

        for (int i = 0; i < _items.Length; i++)
        {
            var r = _rects[i];
            bool active = i == _selected;
            bool hovered = i == _hover;

            if (active) Modern.FillPill(g, r, Modern.PrimarySoft);
            else if (hovered) Modern.FillPill(g, r, Modern.Hover);

            Modern.DrawText(g, TextOf(i), active ? Modern.BodyBold : Modern.Body,
                active ? Modern.Primary : Modern.Sub, r, ContentAlignment.MiddleCenter);
        }
    }
}

/// <summary>数字统计块（浅灰圆角底）。Compact 模式下压扁，适合竖屏窄窗口。</summary>
internal sealed class StatStrip : Control
{
    private (string Label, string Value)[] _items = Array.Empty<(string, string)>();

    public StatStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Height = 76;
    }

    /// <summary>压扁显示（更小的字号与更紧的间距）。</summary>
    public bool Compact { get; set; }

    public (string Label, string Value)[] Items
    {
        get => _items;
        set { _items = value ?? Array.Empty<(string, string)>(); Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        Modern.FillRound(g, bounds, this.Sf(Compact ? 10f : 14f), Modern.CardAlt);

        if (_items.Length == 0) return;

        bool compact = Compact;
        var valueFont = compact ? Modern.BodyBold : Modern.H2;
        float valueTop = compact ? this.S(6) : this.S(12);
        float valueH = compact ? this.S(22) : this.S(30);
        float labelTop = compact ? this.S(26) : this.S(42);
        float labelH = compact ? this.S(16) : this.S(20);
        float linePad = compact ? this.S(10) : this.S(18);

        float cell = Width / _items.Length;
        for (int i = 0; i < _items.Length; i++)
        {
            float left = i * cell;

            if (i > 0)
            {
                using var pen = new Pen(Color.FromArgb(226, 229, 234), this.Sf(1f));
                float x = left;
                g.DrawLine(pen, x, linePad, x, Height - linePad);
            }

            Modern.DrawText(g, _items[i].Value, valueFont, Modern.Text,
                new RectangleF(left, valueTop, cell, valueH), ContentAlignment.MiddleCenter);
            Modern.DrawText(g, _items[i].Label, Modern.Small, Modern.Sub,
                new RectangleF(left, labelTop, cell, labelH), ContentAlignment.MiddleCenter);
        }
    }
}
