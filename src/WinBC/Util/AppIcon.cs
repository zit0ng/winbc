using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WinBC.Util;

/// <summary>
/// 应用图标：黑色实心圆 + 白色双箭头循环（"复原"的意象）。
/// 纯代码绘制，不依赖外部 ico 文件；托盘、窗口、任务栏共用同一份。
/// </summary>
internal static class AppIcon
{
    private static Icon? _instance;

    /// <summary>标准尺寸（32px，由系统缩放到托盘/标题栏需要的大小，保证高 DPI 下也清晰）。</summary>
    public static Icon Instance => _instance ??= Create(32);

    public static Icon Create(int size)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            Draw(g, size, Color.FromArgb(20, 20, 24), Color.White);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();   // Clone 之后就不再依赖这个 HICON 了
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>把图标画到任意 Graphics 上（生成 .ico 时复用同一套画法）。</summary>
    public static void Draw(Graphics g, float size, Color background, Color foreground)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 黑色圆底
        float pad = size * 0.02f;
        var circle = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
        using (var brush = new SolidBrush(background))
            g.FillEllipse(brush, circle);

        // 两段圆弧，留出两个箭头的位置
        float stroke = Math.Max(1.1f, size * 0.088f);
        float inset = size * 0.28f;
        var arc = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);

        using (var pen = new Pen(foreground, stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        })
        {
            g.DrawArc(pen, arc, 26f, 118f);    // 26° → 144°
            g.DrawArc(pen, arc, 206f, 118f);   // 206° → 324°
        }

        DrawArrowHead(g, arc, 148f, size, foreground);
        DrawArrowHead(g, arc, 328f, size, foreground);
    }

    private static void DrawArrowHead(Graphics g, RectangleF arc, float angleDegrees, float size, Color color)
    {
        float cx = arc.Left + arc.Width / 2f;
        float cy = arc.Top + arc.Height / 2f;
        float r = arc.Width / 2f;

        double angle = angleDegrees * Math.PI / 180.0;
        float px = cx + (float)(r * Math.Cos(angle));
        float py = cy + (float)(r * Math.Sin(angle));

        // 顺时针切线方向 = 箭头指向
        double tangent = angle + Math.PI / 2.0;
        float tx = (float)Math.Cos(tangent);
        float ty = (float)Math.Sin(tangent);

        // 圆弧的法线方向 = 箭头底边方向
        float nx = (float)Math.Cos(angle);
        float ny = (float)Math.Sin(angle);

        float length = size * 0.155f;
        float half = size * 0.093f;

        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, new[]
        {
            new PointF(px + tx * length, py + ty * length),
            new PointF(px - nx * half, py - ny * half),
            new PointF(px + nx * half, py + ny * half),
        });
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
