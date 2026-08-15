using System.Runtime.InteropServices;

namespace Stanza.App.Services;

/// <summary>窗口相关的 Win32 消息与结构。</summary>
internal static class NativeMethods
{
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_GETMINMAXINFO = 0x0024;
    public const int MONITOR_DEFAULTTONEAREST = 2;

    public const int HTCLIENT = 1;
    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    /// <summary>系统光标闪烁间隔（毫秒）；0 或 0xFFFFFFFF 表示不闪烁。</summary>
    [DllImport("user32.dll")]
    public static extern uint GetCaretBlinkTime();

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public class MONITORINFO
    {
        public int cbSize = Marshal.SizeOf<MONITORINFO>();
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
