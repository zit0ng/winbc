using System.Drawing.Drawing2D;

namespace WinBC.UI;

/// <summary>圆角扁平按钮：Primary（实心主色）/ Secondary（白底描边）/ Ghost（无底色）。</summary>
internal sealed class RoundButton : Control
{
    private bool _hover;
    private bool _pressed;

    public bool Primary { get; set; }
    public bool Ghost { get; set; }
    public int Radius { get; set; } = 10;
    public Color? AccentColor { get; set; }

    public RoundButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Font = Modern.Body;
        ForeColor = Modern.Text;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = false;
            Invalidate();
            if (ClientRectangle.Contains(e.Location)) OnClick(EventArgs.Empty);
        }
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var accent = AccentColor ?? Modern.Primary;
        float radius = this.Sf(Radius);
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);

        Color fill;
        Color foreground;
        bool stroke = false;

        if (Primary)
        {
            // 刻意不用品牌蓝填充：中性色按钮看起来更克制
            fill = _pressed ? Modern.ButtonSurfacePress
                 : _hover ? Modern.ButtonSurfaceHover
                 : Modern.ButtonSurface;
            foreground = Modern.ButtonText;
            stroke = true;
        }
        else if (Ghost)
        {
            fill = _pressed ? Modern.Pressed : _hover ? Modern.Hover : Color.Transparent;
            foreground = Enabled ? Modern.Text : Modern.Faint;
        }
        else
        {
            fill = _pressed ? Modern.Pressed : _hover ? Modern.Hover : Modern.Card;
            foreground = Enabled ? Modern.Text : Modern.Faint;
            stroke = true;
        }

        if (fill.A > 0) Modern.FillRound(g, rect, radius, fill);
        if (stroke) Modern.DrawRound(g, rect, radius, Primary ? Modern.ButtonBorder : Modern.Border);

        if (!string.IsNullOrEmpty(Text))
        {
            Modern.DrawText(g, Text, Font ?? Modern.Body,
                Enabled ? foreground : Modern.Faint,
                rect, ContentAlignment.MiddleCenter);
        }

        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(Color.FromArgb(90, Modern.Primary), this.Sf(2f)) { DashStyle = DashStyle.Dot };
            using var path = Modern.RoundPath(RectangleF.Inflate(rect, -this.Sf(3f), -this.Sf(3f)), radius);
            g.DrawPath(pen, path);
        }
    }
}
