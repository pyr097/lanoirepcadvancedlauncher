using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace LANoireLauncher.Overlay;

/// <summary>Small click-through FPS / frame-time badge pinned to the top-right corner of the game window.</summary>
public partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000;
    private const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public OverlayWindow() { InitializeComponent(); }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
    }

    public void SetStats((double fps, double ms)? s)
    {
        FpsText.Text = s == null ? "--" : Math.Round(s.Value.fps).ToString("0");
        MsText.Text = s == null ? "-- ms" : s.Value.ms.ToString("0.0") + " ms";
    }

    /// <summary>Pins the badge 12 px inside the top-right corner of the game's client area (works in windowed and borderless).</summary>
    public void PlaceTopRight(IntPtr gameHwnd)
    {
        if (!GetClientRect(gameHwnd, out var rc)) return;
        var topRight = new POINT { X = rc.Right, Y = rc.Top };
        if (!ClientToScreen(gameHwnd, ref topRight)) return;
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        int margin = (int)Math.Round(12 * dpi.DpiScaleX);
        var h = new WindowInteropHelper(this).Handle;
        SetWindowPos(h, HWND_TOPMOST, topRight.X - w - margin, topRight.Y + margin, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }
}
