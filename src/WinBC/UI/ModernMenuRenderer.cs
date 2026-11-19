using System.Drawing.Drawing2D;

namespace WinBC.UI;

/// <summary>右键菜单的暗色调色板。</summary>
internal sealed class ModernColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Modern.Card;
    public override Color ImageMarginGradientBegin => Modern.Card;
    public override Color ImageMarginGradientMiddle => Modern.Card;
    public override Color ImageMarginGradientEnd => Modern.Card;
    public override Color MenuBorder => Modern.Border;
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => Modern.Hover;
    public override Color MenuItemSelectedGradientBegin => Modern.Hover;
    public override Color MenuItemSelectedGradientEnd => Modern.Hover;
    public override Color SeparatorDark => Modern.Border;
    public override Color SeparatorLight => Modern.Border;
}

/// <summary>
/// 自绘右键菜单：暗色底、圆角高亮块、柔和字体，跟主界面同一套视觉。
/// </summary>
internal sealed class ModernMenuRenderer : ToolStripProfessionalRenderer
{
    public ModernMenuRenderer() : base(new ModernColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Modern.Card);
        g.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, e.ToolStrip!.Width - 1f, e.ToolStrip!.Height - 1f);
        using var pen = new Pen(Modern.Border);
        using var path = Modern.RoundPath(r, e.ToolStrip!.Sf(8f));
        g.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected && !e.Item.Pressed) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float pad = e.ToolStrip!.S(4);
        var r = new RectangleF(pad, 1f, e.Item.Width - pad * 2, e.Item.Height - 2f);
        Modern.FillRound(g, r, e.ToolStrip!.Sf(6f), e.Item.Pressed ? Modern.Pressed : Modern.Hover);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Modern.Text : Modern.Faint;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var g = e.Graphics;
        float y = e.Item.Height / 2f;
        float pad = e.ToolStrip!.S(10);
        using var pen = new Pen(Modern.Border);
        g.DrawLine(pen, pad, y, e.ToolStrip!.Width - pad, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Modern.Sub;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // 勾选标记也换成细勾
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = e.ToolStrip!.S(16);
        var box = new RectangleF(e.ImageRectangle.Left + (e.ImageRectangle.Width - s) / 2f,
                                 e.ImageRectangle.Top + (e.ImageRectangle.Height - s) / 2f, s, s);
        using var pen = new Pen(Modern.Text, e.ToolStrip!.Sf(2f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        g.DrawLines(pen, new[]
        {
            new PointF(box.Left + s * 0.15f, box.Top + s * 0.52f),
            new PointF(box.Left + s * 0.40f, box.Top + s * 0.76f),
            new PointF(box.Left + s * 0.85f, box.Top + s * 0.24f),
        });
    }
}

/// <summary>把右键菜单统一打扮成主界面的风格。</summary>
internal static class ModernMenu
{
    public static void Style(ContextMenuStrip menu)
    {
        menu.Renderer = new ModernMenuRenderer();
        menu.BackColor = Modern.Card;
        menu.ForeColor = Modern.Text;
        menu.Font = Modern.Body;
        menu.ShowImageMargin = false;
        menu.Padding = new Padding(5, 6, 5, 6);
        menu.DropShadowEnabled = false;

        foreach (ToolStripItem item in menu.Items)
        {
            item.Font = Modern.Body;
            item.ForeColor = Modern.Text;
            item.Padding = new Padding(12, 5, 12, 5);
        }

        // 菜单每次弹出时：补圆角
        menu.Opened += (_, _) =>
        {
            try
            {
                if (menu.IsHandleCreated)
                    Native.NativeMethods.TryEnableRoundedCorners(menu.Handle, Native.NativeMethods.DWMWCP_ROUND);
            }
            catch { }

            // ToolStrip 会管自己的 Region，必须等这一轮布局铺完再裁，否则会被它覆盖回去
            try
            {
                menu.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (menu.Width <= 0 || menu.Height <= 0) return;
                        using var path = Modern.RoundPath(
                            new RectangleF(0, 0, menu.Width, menu.Height), menu.Sf(10f));
                        var old = menu.Region;
                        menu.Region = new Region(path);
                        old?.Dispose();
                    }
                    catch { }
                }));
            }
            catch { }
        };
    }
}
