using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using LANoireLauncher.Core;

namespace LANoireLauncher.Overlay;

/// <summary>
/// Shows the overlay only while L.A. Noire's window is in front, refreshes twice a second, and cleans up when the game exits.
/// All of it runs in the launcher process, not in the game.
/// </summary>
public sealed class OverlayController : IDisposable
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private readonly OverlayWindow _win = new();
    private readonly FrameMonitor _mon;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Action<string> _say;
    private readonly DateTime _started = DateTime.UtcNow;
    private DateTime? _seenAt;
    private bool _warned, _disposed;

    public bool UserVisible { get; set; } = true;
    public event Action? Finished;

    public OverlayController(FrameMonitor mon, Action<string> say)
    {
        _mon = mon; _say = say;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _say("Overlay armed - it appears once the game window is in front. Toggle with Ctrl+Shift+F.");
    }

    private void Tick()
    {
        if (_disposed) return;
        var procs = Process.GetProcessesByName("LaNoire");
        var p = procs.FirstOrDefault();
        IntPtr hwnd = p?.MainWindowHandle ?? IntPtr.Zero;
        foreach (var x in procs) x.Dispose();

        if (p == null)
        {
            if (_seenAt != null || (DateTime.UtcNow - _started).TotalSeconds > 120)
            { _say("Overlay stopped (game is not running)."); Dispose(); Finished?.Invoke(); }
            return;
        }
        _seenAt ??= DateTime.UtcNow;

        if (!_warned && !_mon.ReceivedFrames && (DateTime.UtcNow - _seenAt.Value).TotalSeconds > 15)
        {
            _warned = true;
            _say(_mon.HeaderError ?? "Overlay: no frame data from PresentMon yet. If you declined the UAC prompt, or this PresentMon build uses other options, the overlay stays at --.");
        }

        bool show = UserVisible && hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;
        if (!show) { if (_win.IsVisible) _win.Hide(); return; }
        _win.SetStats(_mon.GetStats());
        if (!_win.IsVisible) _win.Show();
        _win.PlaceTopRight(hwnd);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        try { _win.Close(); } catch { }
        _mon.Dispose();
    }
}
