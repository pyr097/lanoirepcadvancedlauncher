using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace LANoireLauncher.Overlay;

/// <summary>
/// DXVK draws the FPS counter itself (DXVK_HUD=fps, set at launch). This only places a small "DXVK" label in the top-right
/// corner of the game window while the game is in front, and cleans up when the game exits.
/// It runs entirely in the launcher process and needs no special rights.
/// </summary>
public sealed class OverlayController : IDisposable
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private readonly OverlayWindow _win = new();
    private readonly Process _game;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private IntPtr _hwnd;
    private int _tickNo;
    private bool _disposed;

    public bool UserVisible { get; set; } = true;
    public event Action? Finished;

    public OverlayController(Process game)
    {
        _game = game;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        if (_disposed) return;
        bool exited;
        try { exited = _game.HasExited; } catch { exited = true; }
        if (exited) { Dispose(); Finished?.Invoke(); return; }

        // window lookup is comparatively slow, so only redo it about twice a second
        if (_tickNo++ % 5 == 0)
        {
            _game.Refresh();
            _hwnd = _game.MainWindowHandle;
        }
        IntPtr hwnd = _hwnd;
        bool show = UserVisible && hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;
        if (!show) { if (_win.IsVisible) _win.Hide(); return; }

        if (!_win.IsVisible) _win.Show();
        _win.PlaceTopRight(hwnd, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        try { _win.Close(); } catch { }
        _game.Dispose();
    }
}
