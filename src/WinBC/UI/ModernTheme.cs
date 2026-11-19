using System.Drawing.Drawing2D;

namespace WinBC.UI;

/// <summary>
/// 现代扁平风格的颜色/字体/圆角绘制辅助。
/// 支持深色（默认）与浅色两套调色板，运行时可切换。
/// </summary>
internal static class Modern
{
    public static bool IsDark { get; private set; } = true;

    // ── 颜色（随主题变化）──────────────────────────────
    public static Color Bg;
    public static Color Card;
    public static Color CardAlt;
    public static Color Hover;
    public static Color Pressed;
    public static Color Border;
    public static Color Text;
    public static Color Sub;
    public static Color Faint;
    public static Color Primary;
    public static Color PrimarySoft;
    public static Color Danger;
    public static Color Warning;

    /// <summary>主按钮：刻意用中性色而不是品牌蓝，避免"广告感"。</summary>
    public static Color ButtonSurface;
    public static Color ButtonSurfaceHover;
    public static Color ButtonSurfacePress;
    public static Color ButtonBorder;
    public static Color ButtonText;

    static Modern() => Apply(true);

    public static void Apply(bool dark)
    {
        IsDark = dark;

        if (dark)
        {
            Bg = Color.FromArgb(27, 27, 31);
            Card = Color.FromArgb(32, 32, 36);
            CardAlt = Color.FromArgb(42, 42, 47);
            Hover = Color.FromArgb(46, 46, 52);
            Pressed = Color.FromArgb(56, 56, 64);
            Border = Color.FromArgb(51, 51, 56);
            Text = Color.FromArgb(245, 245, 247);
            Sub = Color.FromArgb(161, 161, 170);
            Faint = Color.FromArgb(113, 113, 122);
            Primary = Color.FromArgb(123, 167, 245);
            PrimarySoft = Color.FromArgb(43, 58, 85);
            Danger = Color.FromArgb(248, 113, 113);
            Warning = Color.FromArgb(251, 191, 36);

            ButtonSurface = Color.FromArgb(239, 239, 242);
            ButtonSurfaceHover = Color.FromArgb(255, 255, 255);
            ButtonSurfacePress = Color.FromArgb(216, 216, 222);
            ButtonBorder = Color.FromArgb(58, 58, 65);
            ButtonText = Color.FromArgb(27, 27, 31);
        }
        else
        {
            Bg = Color.FromArgb(245, 245, 247);
            Card = Color.FromArgb(255, 255, 255);
            CardAlt = Color.FromArgb(244, 244, 246);
            Hover = Color.FromArgb(240, 240, 243);
            Pressed = Color.FromArgb(232, 232, 236);
            Border = Color.FromArgb(228, 228, 232);
            Text = Color.FromArgb(24, 24, 27);
            Sub = Color.FromArgb(113, 113, 122);
            Faint = Color.FromArgb(161, 161, 170);
            Primary = Color.FromArgb(59, 111, 224);
            PrimarySoft = Color.FromArgb(235, 241, 254);
            Danger = Color.FromArgb(220, 38, 38);
            Warning = Color.FromArgb(217, 119, 6);

            ButtonSurface = Color.FromArgb(255, 255, 255);
            ButtonSurfaceHover = Color.FromArgb(247, 247, 249);
            ButtonSurfacePress = Color.FromArgb(239, 239, 242);
            ButtonBorder = Color.FromArgb(222, 222, 227);
            ButtonText = Color.FromArgb(24, 24, 27);
        }
    }

    // ── 字体（用 pt，DPI 自适应）─────────────────────────
    public const string Family = "Microsoft YaHei UI";

    public static readonly Font H1 = new(Family, 17F, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font H2 = new(Family, 11.5F, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font Body = new(Family, 9.5F, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font BodyBold = new(Family, 9.5F, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font Small = new(Family, 8.5F, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font Tiny = new(Family, 8F, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font Mono = new("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);

    /// <summary>把 96 DPI 下的像素尺寸换算到当前控件所在的缩放。</summary>
    public static int S(this Control c, int px) => (int)Math.Round(px * c.DeviceDpi / 96f);

    /// <summary>把 96 DPI 下的像素尺寸换算到当前缩放（浮点）。</summary>
    public static float Sf(this Control c, float px) => px * c.DeviceDpi / 96f;

    // ── 圆角 ──────────────────────────────────────────────

    public static GraphicsPath RoundPath(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        float d = radius * 2f;
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRound(Graphics g, RectangleF r, float radius, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundPath(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void DrawRound(Graphics g, RectangleF r, float radius, Color color, float width = 1f)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundPath(r, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    /// <summary>画一个圆形/胶囊形的实心块。</summary>
    public static void FillPill(Graphics g, RectangleF r, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundPath(r, r.Height / 2f);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    // ── 文字 ──────────────────────────────────────────────

    public static void DrawText(Graphics g, string text, Font font, Color color, RectangleF rect,
        ContentAlignment align = ContentAlignment.MiddleLeft, StringFormat? format = null)
    {
        var sf = format ?? new StringFormat
        {
            Alignment = align switch
            {
                ContentAlignment.MiddleCenter or ContentAlignment.TopCenter or ContentAlignment.BottomCenter
                    => StringAlignment.Center,
                ContentAlignment.MiddleRight or ContentAlignment.TopRight or ContentAlignment.BottomRight
                    => StringAlignment.Far,
                _ => StringAlignment.Near,
            },
            LineAlignment = align switch
            {
                ContentAlignment.TopLeft or ContentAlignment.TopCenter or ContentAlignment.TopRight
                    => StringAlignment.Near,
                ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight
                    => StringAlignment.Far,
                _ => StringAlignment.Center,
            },
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        using var brush = new SolidBrush(color);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.DrawString(text, font, brush, rect, sf);

        if (format is null) sf.Dispose();
    }

    // ── 杂项 ──────────────────────────────────────────────

    /// <summary>把 WinForms 自动滚动条也刷成深色（深色主题下系统默认那条白杠很扎眼）。</summary>
    public static void UseDarkScrollbar(Control control)
    {
        try
        {
            if (control is null || !control.IsHandleCreated) return;
            SetWindowTheme(control.Handle, IsDark ? "DarkMode_Explorer" : "Explorer", null);

            // 光设主题不重绘是不生效的，得让非客户区重算一次
            Native.NativeMethods.SetWindowPos(control.Handle, IntPtr.Zero, 0, 0, 0, 0,
                Native.NativeMethods.SWP_NOMOVE | Native.NativeMethods.SWP_NOSIZE |
                Native.NativeMethods.SWP_NOZORDER | Native.NativeMethods.SWP_NOACTIVATE |
                Native.NativeMethods.SWP_FRAMECHANGED);
        }
        catch
        {
            // 老系统上没有 DarkMode_Explorer，忽略
        }
    }

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string? pszSubIdList);

    public static string FormatTime(DateTimeOffset t)
    {
        var local = t.LocalDateTime;
        var today = DateTime.Today;
        if (local.Date == today) return $"今天 {local:HH:mm}";
        if (local.Date == today.AddDays(-1)) return $"昨天 {local:HH:mm}";
        if (local.Year == today.Year) return local.ToString("M月d日 HH:mm");
        return local.ToString("yyyy年M月d日 HH:mm");
    }

    public static string ReasonText(string reason) => reason switch
    {
        "shutdown" => "关机",
        "logoff" => "注销",
        "manually" => "手动",
        "rolling" => "自动",
        _ => reason,
    };
}

/// <summary>主题切换：改调色板并让所有已打开窗口重绘。</summary>
internal static class ThemeManager
{
    public static void Apply(bool dark)
    {
        var oldCard = Modern.Card;
        Modern.Apply(dark);
        var newCard = Modern.Card;

        foreach (Form form in Application.OpenForms)
        {
            try
            {
                // 容器面板的 BackColor 是值拷贝，得手动跟着换，否则切主题后列表区还是旧底色
                Recolor(form, oldCard, newCard);
                form.BackColor = newCard;
                form.ForeColor = Modern.Text;
                form.Invalidate(true);
            }
            catch { }
        }
    }

    private static void Recolor(Control parent, Color oldCard, Color newCard)
    {
        foreach (Control child in parent.Controls)
        {
            if (child.BackColor == oldCard) child.BackColor = newCard;
            if (child.Controls.Count > 0) Recolor(child, oldCard, newCard);
        }
    }
}
