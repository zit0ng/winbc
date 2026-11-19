using System.Drawing.Drawing2D;
using WinBC.Models;

namespace WinBC.UI;

/// <summary>
/// 开机后的小提示卡：只问一句"要不要恢复"，只有两个选项。
/// 刻意做得轻——小尺寸、95% 不透明、没有多余按钮和装饰。
/// </summary>
public sealed class RestorePromptForm : ModernForm
{
    private const int Pad = 18;
    private const int BtnH = 34;

    /// <summary>点「复原」。</summary>
    public bool UserChoseRestore { get; private set; }

    /// <summary>点「稍后再看」。与 <see cref="UserChoseRestore"/> 互斥。</summary>
    public bool UserChoseViewMain { get; private set; }

    private readonly RoundButton _restoreButton = new();
    private readonly RoundButton _laterButton = new();

    /// <summary>标题字体做成静态的，别在 OnPaint 里 new Font（每次重绘都会泄漏一个 GDI 对象）。</summary>
    private static readonly Font HeadlineFont =
        new(Modern.Family, 12F, FontStyle.Bold, GraphicsUnit.Point);

    private readonly string _headline;
    private readonly string _detail;

    public RestorePromptForm(Snapshot snapshot, string reasonText)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));

        string reason = string.IsNullOrWhiteSpace(reasonText) ? "上次关机前" : reasonText.Trim();
        _headline = $"恢复{reason}的状态";
        _detail = $"{Modern.FormatTime(snapshot.CreatedAt)}  ·  " +
                  $"{snapshot.Apps.Count(a => !a.IsIgnored)} 个应用  ·  {snapshot.TabCount} 个网页";

        BuildUi();
    }

    private void BuildUi()
    {
        SuspendLayout();

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.None;   // 尺寸统一由 S() 缩放，避免和 WinForms 自动缩放叠加
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Modern.Card;
        ForeColor = Modern.Text;
        Text = "WinBC";
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        try { Icon = Util.AppIcon.Instance; } catch { }

        // 轻微透明，看起来更"轻"，不糊脸
        try { Opacity = 0.95; } catch { }

        _restoreButton.Primary = true;
        _restoreButton.Text = "复原";
        _restoreButton.Radius = 9;
        _restoreButton.Font = new Font(Modern.Family, 10F, FontStyle.Bold, GraphicsUnit.Point);
        _restoreButton.Click += (_, _) => { UserChoseRestore = true; Close(); };

        _laterButton.Ghost = true;
        _laterButton.Text = "稍后再看";
        _laterButton.Radius = 9;
        _laterButton.Font = new Font(Modern.Family, 10F, FontStyle.Regular, GraphicsUnit.Point);
        _laterButton.Click += (_, _) => Close();   // 只关掉，什么都不做

        Controls.AddRange(new Control[] { _laterButton, _restoreButton });

        ResumeLayout(false);
        LayoutAll();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyInitialSize(400, 152);
        LayoutAll();
        ApplyRoundRegion();
    }

    /// <summary>
    /// 设了 Opacity 之后窗口会变成分层窗口（WS_EX_LAYERED），
    /// DWM 的原生圆角就失效了，所以这里自己裁一个圆角出来。
    /// </summary>
    private void ApplyRoundRegion()
    {
        try
        {
            if (Width <= 0 || Height <= 0) return;
            using var path = Modern.RoundPath(
                new RectangleF(0, 0, Width, Height), this.Sf(13f));
            var old = Region;
            Region = new Region(path);
            old?.Dispose();
        }
        catch { }
    }

    private void LayoutAll()
    {
        if (ClientSize.Width <= 0) return;

        int pad = this.S(Pad);
        int h = this.S(BtnH);
        int y = ClientSize.Height - pad - h;

        int wRestore = this.S(84);
        int wLater = this.S(96);
        int gap = this.S(8);

        _restoreButton.Bounds = new Rectangle(ClientSize.Width - pad - wRestore, y, wRestore, h);
        _laterButton.Bounds = new Rectangle(_restoreButton.Left - gap - wLater, y, wLater, h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var bg = new SolidBrush(Modern.Card))
            g.FillRectangle(bg, ClientRectangle);

        int pad = this.S(Pad);

        Modern.DrawText(g, _headline, HeadlineFont, Modern.Text,
            new RectangleF(pad, this.S(18), Width - pad * 2, this.S(26)));

        Modern.DrawText(g, _detail, Modern.Small, Modern.Sub,
            new RectangleF(pad, this.S(46), Width - pad * 2, this.S(20)));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();          // 等同"稍后再看"
            return true;
        }
        if (keyData == Keys.Enter)
        {
            UserChoseRestore = true;
            Close();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
