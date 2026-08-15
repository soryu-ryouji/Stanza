using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Stanza.App.Services;

namespace Stanza.App;

/// <summary>
/// 无边框透明窗口的系统集成：squircle 裁剪、边缘缩放（WM_NCHITTEST）、
/// 最大化工作区处理（WM_GETMINMAXINFO）、最大化时去除阴影与圆角。
/// </summary>
public partial class MainWindow
{
    private const double ShadowMargin = 16;    // 窗口边缘的透明留白（供阴影渲染，也作缩放热区）
    // squircle 名义半径。连续曲率曲线贴边缓慢起步，45° 对角线处视觉半径 ≈ 0.14×r，
    // r=32 的观感 ≈ 普通圆弧 16pt（macOS 26 Tahoe 量级）；r=16 观感只相当于圆弧 8，与 Windows 11 原生一致
    private const double CornerRadius = 32;
    private const double ResizeInside = 4;     // 边框内侧的缩放响应宽度
    private const double ResizeOutside = 6;    // 边框外侧（阴影区）的缩放响应宽度

    private System.Windows.Media.Effects.DropShadowEffect? _shadowEffect;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _shadowEffect = ShadowShape.Effect as System.Windows.Media.Effects.DropShadowEffect;
        ((HwndSource)PresentationSource.FromVisual(this)!).AddHook(WndProc);
        StateChanged += Window_StateChanged;
        ApplySquircle();
    }

    private void WindowFrame_SizeChanged(object sender, SizeChangedEventArgs e) => ApplySquircle();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        // 最大化时贴满工作区：去掉阴影边距与圆角，回到直角（与原生窗口一致）
        ShadowHost.Margin = new Thickness(maximized ? 0 : ShadowMargin);
        ShadowShape.Effect = maximized ? null : _shadowEffect;
        WindowFrame.Clip = null;
        if (!maximized) ApplySquircle();
    }

    private void ApplySquircle()
    {
        if (WindowState == WindowState.Maximized || WindowFrame.ActualWidth <= 0) return;
        WindowFrame.Clip = SquircleGeometry.Build(
            WindowFrame.ActualWidth, WindowFrame.ActualHeight, CornerRadius);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_GETMINMAXINFO:
            {
                // 无边框窗口最大化时默认会遮挡任务栏，这里把最大边界约束到工作区
                var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                var info = new NativeMethods.MONITORINFO();
                NativeMethods.GetMonitorInfo(monitor, ref info);
                var mmi = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
                mmi.ptMaxPosition.x = info.rcWork.left - info.rcMonitor.left;
                mmi.ptMaxPosition.y = info.rcWork.top - info.rcMonitor.top;
                mmi.ptMaxSize.x = info.rcWork.right - info.rcWork.left;
                mmi.ptMaxSize.y = info.rcWork.bottom - info.rcWork.top;
                Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
                break;
            }
            case NativeMethods.WM_NCHITTEST:
            {
                if (WindowState == WindowState.Maximized) break;   // 最大化用默认处理
                handled = true;
                return new IntPtr(HitTestChrome(lParam));
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>把窗口边缘区域映射为缩放命中码；内部区域返回 HTCLIENT 交给 WPF。</summary>
    private int HitTestChrome(IntPtr lParam)
    {
        // lParam 是屏幕坐标（有符号拆包，兼容多显示器负坐标）
        var x = (short)(lParam.ToInt32() & 0xFFFF);
        var y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);
        var pt = PointFromScreen(new Point(x, y));

        var left = ShadowMargin;
        var top = ShadowMargin;
        var right = ActualWidth - ShadowMargin;
        var bottom = ActualHeight - ShadowMargin;

        var onLeft = pt.X >= left - ResizeOutside && pt.X < left + ResizeInside;
        var onRight = pt.X >= right - ResizeInside && pt.X < right + ResizeOutside;
        var onTop = pt.Y >= top - ResizeOutside && pt.Y < top + ResizeInside;
        var onBottom = pt.Y >= bottom - ResizeInside && pt.Y < bottom + ResizeOutside;

        if (onLeft && onTop) return NativeMethods.HTTOPLEFT;
        if (onRight && onTop) return NativeMethods.HTTOPRIGHT;
        if (onLeft && onBottom) return NativeMethods.HTBOTTOMLEFT;
        if (onRight && onBottom) return NativeMethods.HTBOTTOMRIGHT;
        if (onLeft) return NativeMethods.HTLEFT;
        if (onRight) return NativeMethods.HTRIGHT;
        if (onTop) return NativeMethods.HTTOP;
        if (onBottom) return NativeMethods.HTBOTTOM;
        return NativeMethods.HTCLIENT;
    }
}
