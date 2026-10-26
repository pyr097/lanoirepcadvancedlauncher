using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using LANoireLauncher.Core;

namespace LANoireLauncher.Overlay;

/// <summary>
/// Shows the FPS badge only while the game window is in front, refreshes twice a second, and cleans up when the game exits.
/// It runs entirely in the launcher process.
/// </summary>
public sealed class OverlayController : IDisposable
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private readonly OverlayWindow _win = new();
    private readonly Process _game;
    private readonly FpsCounter _counter;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Action<string> _say;
    private long _lastFrames, _lastTick = Environment.TickCount64;
    private bool _disposed;

    public bool UserVisible { get; set; } = true;
    public event Action? Finished;

    public OverlayController(Process game, FpsCounter counter, Action<string> say)
    {
        _game = game; _counter = counter; _say = say;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _say("FPS overlay on - it appears once the game window is in front. Ctrl+Shift+F shows/hides it.");
    }

    private void Tick()
    {
        if (_disposed) return;
        bool exited;
        try { exited = _game.HasExited; } catch { exited = true; }
        if (exited) { Dispose(); Finished?.Invoke(); return; }

        long now = Environment.TickCount64, frames = _counter.Frames;
        double secs = Math.Max(0.001, (now - _lastTick) / 1000.0);
        double fps = (frames - _lastFrames) / secs;
        _lastTick = now; _lastFrames = frames;

        _game.Refresh();
        IntPtr hwnd = _game.MainWindowHandle;
        bool show = UserVisible && hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;
        if (!show) { if (_win.IsVisible) _win.Hide(); return; }

        _win.SetStats(fps >= 0.5 ? (fps, 1000.0 / fps) : null);
        if (!_win.IsVisible) _win.Show();
        _win.PlaceTopRight(hwnd);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        try { _win.Close(); } catch { }
        _counter.Dispose();
        _game.Dispose();
    }
}
