using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using LANoireLauncher.Core;

namespace LANoireLauncher.Overlay;

/// <summary>
/// Shows the FPS badge only while the game window is in front, refreshes ten times a second, and cleans up when the game exits.
/// It runs entirely in the launcher process.
/// </summary>
public sealed class OverlayController : IDisposable
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private readonly OverlayWindow _win = new();
    private readonly Process _game;
    private readonly FpsCounter? _counter;   // null = DXVK mode (label only)
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Action<string> _say;
    private IntPtr _hwnd;
    private int _tickNo;
    private double _shownFps = -1;
    private bool _disposed;

    public bool UserVisible { get; set; } = true;
    public event Action? Finished;

    public OverlayController(Process game, FpsCounter? counter, Action<string> say)
    {
        _game = game; _counter = counter; _say = say;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        if (counter != null) _say("FPS overlay on - it appears once the game window is in front. Ctrl+Shift+F shows/hides it.");
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

        if (_counter != null)
        {
            var fps = _counter.CurrentFps();
            if (fps != null) _shownFps = fps.Value;       // keep the last value instead of flashing "--"
            if (_shownFps >= 0) _win.SetFps(_shownFps);
        }
        else _win.SetTagOnly();
        if (!_win.IsVisible) _win.Show();
        if (_counter != null) _win.PlaceTopLeft(hwnd, 8, 4); else _win.PlaceTopRight(hwnd, 8, 4);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        try { _win.Close(); } catch { }
        _counter?.Dispose();
        _game.Dispose();
    }
}
