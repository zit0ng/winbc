using System;
using System.Drawing;
using System.Windows.Forms;
using WinBC.Models;

namespace WinBC.UI;

/// <summary>
/// 集中定义 WinBC 界面的配色、字体与通用控件工厂，保证主窗口 / 询问框 / 设置页风格一致。
/// 设计取舍：现代扁平、浅色、Microsoft YaHei UI 9pt、强调色 #2F6FED、列表行高 24、不做自绘圆角。
/// </summary>
internal static class UiTheme
{
    // ── 配色 ──────────────────────────────────────────────────────────
    /// <summary>强调色（主按钮、链接、选中态）。</summary>
    public static readonly Color Accent = ColorTranslator.FromHtml("#2F6FED");
    /// <summary>强调色悬停/按下。</summary>
    public static readonly Color AccentHover = ColorTranslator.FromHtml("#2560D8");
    /// <summary>强调色浅底。</summary>
    public static readonly Color AccentSoft = ColorTranslator.FromHtml("#EAF1FE");
    /// <summary>窗口底色。</summary>
    public static readonly Color Background = ColorTranslator.FromHtml("#F5F7FB");
    /// <summary>卡片/面板底色。</summary>
    public static readonly Color Surface = Color.White;
    /// <summary>分隔线。</summary>
    public static readonly Color Border = ColorTranslator.FromHtml("#DFE5F0");
    /// <summary>主要文字。</summary>
    public static readonly Color TextPrimary = ColorTranslator.FromHtml("#1B2333");
    /// <summary>次要说明文字。</summary>
    public static readonly Color TextMuted = ColorTranslator.FromHtml("#6B7686");
    /// <summary>禁用/已忽略条目的文字。</summary>
    public static readonly Color TextDisabled = ColorTranslator.FromHtml("#A0A9B8");
    /// <summary>日志框底色。</summary>
    public static readonly Color LogBackground = ColorTranslator.FromHtml("#FBFCFE");

    /// <summary>列表行高（像素）。</summary>
    public const int RowHeight = 24;

    // ── 字体 ──────────────────────────────────────────────────────────
    public static readonly Font BaseFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font TitleFont = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font SectionFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font LogFont = new Font("Microsoft YaHei UI", 8.25F, FontStyle.Regular, GraphicsUnit.Point);

    // ── 控件工厂 ──────────────────────────────────────────────────────

    /// <summary>创建扁平按钮。<paramref name="primary"/> 为 true 时使用强调色实心样式。</summary>
    public static Button CreateButton(string text, bool primary = false, int width = 96, int height = 32)
    {
        var button = new Button
        {
            Text = text,
            Width = width,
            Height = height,
            Font = BaseFont,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand,
            TabStop = true,
        };
        button.FlatAppearance.BorderSize = 1;
        if (primary)
        {
            button.BackColor = Accent;
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderColor = Accent;
            button.FlatAppearance.MouseOverBackColor = AccentHover;
            button.FlatAppearance.MouseDownBackColor = AccentHover;
        }
        else
        {
            button.BackColor = Surface;
            button.ForeColor = TextPrimary;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = AccentSoft;
            button.FlatAppearance.MouseDownBackColor = Border;
        }

        return button;
    }

    /// <summary>创建无边框的"链接"式小按钮（用于全选 / 全不选）。</summary>
    public static Button CreateLinkButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Font = BaseFont,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            BackColor = Surface,
            ForeColor = Accent,
            Cursor = Cursors.Hand,
            Padding = new Padding(6, 2, 6, 2),
            Margin = new Padding(4, 0, 0, 0),
            TabStop = false,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = AccentSoft;
        button.FlatAppearance.MouseDownBackColor = AccentSoft;
        return button;
    }

    // ── 列表辅助 ──────────────────────────────────────────────────────

    /// <summary>
    /// 通过一个 1px 宽的透明小图标撑高 ListView 行高（WinForms 没有直接的行高属性）。
    /// 失败时忽略——只是行高退回默认值，不影响功能。
    /// </summary>
    public static void ApplyRowHeight(ListView list, int height = RowHeight)
    {
        try
        {
            var images = new ImageList
            {
                ImageSize = new Size(1, height),
                ColorDepth = ColorDepth.Depth32Bit,
            };
            images.Images.Add(new Bitmap(1, height)); // 完全透明，仅用于撑高行
            list.SmallImageList = images;
        }
        catch
        {
            // 行高设置失败不影响功能
        }
    }

    /// <summary>让最后一列自动吃掉剩余宽度，避免窗口拉伸后右侧留白。</summary>
    public static void StretchLastColumn(ListView list, int minWidth = 120)
    {
        void Fit()
        {
            try
            {
                if (list.Columns.Count == 0) return;
                int used = 0;
                for (int i = 0; i < list.Columns.Count - 1; i++) used += list.Columns[i].Width;
                int rest = list.ClientSize.Width - used - 4;
                list.Columns[list.Columns.Count - 1].Width = Math.Max(minWidth, rest);
            }
            catch
            {
                // 布局微调失败不影响功能
            }
        }

        list.Resize += (_, _) => Fit();
        Fit();
    }

    // ── 文案 ──────────────────────────────────────────────────────────

    /// <summary>把快照 Reason 翻译成中文。</summary>
    public static string ReasonText(string? reason) => reason switch
    {
        "shutdown" => "关机",
        "logoff" => "注销",
        "manually" => "手动",
        "rolling" => "滚动",
        null or "" => "未知",
        _ => reason,
    };

    /// <summary>快照时间统一显示为本地时间 yyyy-MM-dd HH:mm:ss。</summary>
    public static string FormatTime(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>短时间格式（询问框用）。</summary>
    public static string FormatShortTime(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>截断过长文本，避免列表里被横向撑爆。</summary>
    public static string Shorten(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>是否是"新标签页"这类不需要恢复的空白页。</summary>
    public static bool IsNewTabUrl(string? url)
        => !string.IsNullOrEmpty(url) && url.Contains("://newtab", StringComparison.OrdinalIgnoreCase);

    /// <summary>媒体条目是否来自网页。</summary>
    public static bool IsWebMedia(MediaEntry media) => string.Equals(media.Kind, "web", StringComparison.OrdinalIgnoreCase);
}

/// <summary>双缓冲 ListView：滚动与勾选时不闪。</summary>
internal sealed class BufferedListView : ListView
{
    public BufferedListView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
}

/// <summary>双缓冲 TreeView：展开/勾选时不闪。</summary>
internal sealed class BufferedTreeView : TreeView
{
    public BufferedTreeView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
}
