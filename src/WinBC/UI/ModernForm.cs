using WinBC.Native;

namespace WinBC.UI;

/// <summary>
/// 无边框窗口的公共底座：原生圆角、四周不画任何系统边框、边缘可拖拽缩放、标题栏可拖动。
/// </summary>
/// <remarks>
/// 关键点：**不要**往 CreateParams 里加 WS_THICKFRAME。
/// 加了虽然能白拿系统 resize 和阴影，但系统会顺带给窗口四周画一圈非客户区边框，
/// 深色窗口上就是一条扎眼的浅色描边（用户看到的那条"白边"）。
/// 这里改成自己实现 WM_NCHITTEST 来提供边缘缩放，既没有边框也不丢 resize。
/// </remarks>
public class ModernForm : Form
{
    protected const int HeaderHeight = 46;
    private const int GripSize = 6;
    private const int HTCLIENT = 1;
    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCACTIVATE = 0x0086;
    private const int WM_NCPAINT = 0x0085;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // 关键：不加 WS_THICKFRAME。
            // 加了确实能白拿系统 resize 和阴影，但 DWM 会给窗口描一圈 1px 边框，
            // 而且非客户区还会留下标题栏空间 —— 深色窗口上非常明显。
            // 这里改成纯无边框窗口，边缘缩放自己用 WM_NCHITTEST 实现。
            cp.ClassStyle |= 0x00020000;  // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.TryEnableRoundedCorners(Handle, NativeMethods.DWMWCP_ROUND);
    }

    protected override void WndProc(ref Message m)
    {
        // 非客户区一律算成 0：客户区直接铺满整个窗口，没有任何标题栏/边框
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            m.Result = IntPtr.Zero;
            return;
        }

        // 窗口激活状态变化时（Alt+Tab、被别的窗口抢焦点，例如微信截图），
        // DefWindowProc 会按"标准窗口"重画一遍标题栏。这里直接吞掉，
        // 否则就会冒出那条浅蓝色的系统标题栏。
        if (m.Msg == WM_NCACTIVATE)
        {
            m.Result = (IntPtr)1;
            return;
        }

        // 同理：不让系统绘制任何非客户区
        if (m.Msg == WM_NCPAINT)
        {
            m.Result = IntPtr.Zero;
            return;
        }

        if (m.Msg == NativeMethods.WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if (m.Result.ToInt32() == HTCLIENT)
            {
                try
                {
                    long lp = m.LParam.ToInt64();
                    int sx = unchecked((short)(lp & 0xFFFF));
                    int sy = unchecked((short)((lp >> 16) & 0xFFFF));
                    var p = PointToClient(new Point(sx, sy));
                    int grip = this.S(GripSize);

                    bool left = p.X <= grip;
                    bool right = p.X >= ClientSize.Width - grip;
                    bool top = p.Y <= grip;
                    bool bottom = p.Y >= ClientSize.Height - grip;

                    if (top && left) m.Result = (IntPtr)NativeMethods.HTTOPLEFT;
                    else if (top && right) m.Result = (IntPtr)NativeMethods.HTTOPRIGHT;
                    else if (bottom && left) m.Result = (IntPtr)NativeMethods.HTBOTTOMLEFT;
                    else if (bottom && right) m.Result = (IntPtr)NativeMethods.HTBOTTOMRIGHT;
                    else if (left) m.Result = (IntPtr)NativeMethods.HTLEFT;
                    else if (right) m.Result = (IntPtr)NativeMethods.HTRIGHT;
                    else if (top) m.Result = (IntPtr)NativeMethods.HTTOP;
                    else if (bottom) m.Result = (IntPtr)NativeMethods.HTBOTTOM;
                }
                catch
                {
                    // 坐标解析失败就按客户区处理
                }
            }
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // 再清一次边框颜色：窗口真正显示后 DWM 才认这个属性
        NativeMethods.TryEnableRoundedCorners(Handle, NativeMethods.DWMWCP_ROUND);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && e.Y < this.S(HeaderHeight))
            NativeMethods.BeginDragMove(Handle);
    }

    private bool _initialSizeApplied;

    /// <summary>
    /// 按 DPI 设定窗口尺寸。
    /// 必须在 OnLoad 里调 —— 构造函数执行时 DeviceDpi 还是 96，算出来的尺寸会不准。
    /// 同时这里也是唯一的缩放来源：窗体是 AutoScaleMode.None，其它地方全部走 S()。
    /// </summary>
    protected void ApplyInitialSize(int logicalWidth, int logicalHeight, int minWidth = 0, int minHeight = 0)
    {
        if (_initialSizeApplied) return;
        _initialSizeApplied = true;

        try
        {
            ClientSize = new Size(this.S(logicalWidth), this.S(logicalHeight));
            if (minWidth > 0 && minHeight > 0)
                MinimumSize = new Size(this.S(minWidth), this.S(minHeight));

            // 屏幕装不下就往里收，别让窗口跑到屏幕外
            var wa = Screen.FromControl(this).WorkingArea;
            if (Width > wa.Width) Width = wa.Width - this.S(24);
            if (Height > wa.Height) Height = wa.Height - this.S(24);
            if (Left < wa.Left) Left = wa.Left + this.S(16);
            if (Top < wa.Top) Top = wa.Top + this.S(16);
        }
        catch
        {
            // 尺寸调整失败不影响功能
        }
    }
}
