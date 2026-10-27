using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace LANoireLauncher.Core;

/// <summary>
/// Counts the game's Present calls through Windows' own DXGI trace events (ETW). Nothing is injected into the game
/// and no frame is delayed. This is the FPS source for plain DirectX 11; with DXVK the launcher uses DXVK's built-in HUD instead.
/// Windows only allows trace sessions for administrators or members of "Performance Log Users".
/// </summary>
public sealed class FpsCounter : IDisposable
{
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");   // Microsoft-Windows-DXGI
    private const int PresentStart = 42;
    private const string SessionName = "LANoireLauncher-fps";

    private readonly TraceEventSession _session;
    private readonly object _lock = new();
    private readonly Queue<long> _stamps = new();     // event times (ticks) of recent presents
    private long _lastArrival;                         // when the last event reached us (Environment.TickCount64)
    private const long WindowTicks = TimeSpan.TicksPerSecond / 2;

    /// <summary>
    /// Current frame rate from the timestamps of the most recent presents (last ~0.5 s of game time).
    /// ETW hands events over in bursts, so counting per timer tick flickers; event timestamps do not.
    /// Returns null until the first frames arrive; 0 when nothing was presented for a while (loading screen, paused).
    /// </summary>
    public double? CurrentFps()
    {
        lock (_lock)
        {
            if (_stamps.Count == 0) return null;
            if (Environment.TickCount64 - _lastArrival > 2500) return 0;
            if (_stamps.Count < 2) return null;
            long first = _stamps.Peek(), last = 0;
            foreach (var t in _stamps) last = t;
            return last > first ? (_stamps.Count - 1) * (double)TimeSpan.TicksPerSecond / (last - first) : null;
        }
    }

    private void OnPresent(long ticks)
    {
        lock (_lock)
        {
            _stamps.Enqueue(ticks);
            _lastArrival = Environment.TickCount64;
            while (_stamps.Count > 2 && ticks - _stamps.Peek() > WindowTicks) _stamps.Dequeue();
        }
    }

    private FpsCounter(TraceEventSession session) => _session = session;

    public static FpsCounter? TryStart(int processId, out string? error)
    {
        error = null;
        TraceEventSession? session = null;
        try
        {
            TraceEventSession.GetActiveSession(SessionName)?.Stop();     // leftover from a crashed launcher
            session = new TraceEventSession(SessionName) { StopOnDispose = true };
            var counter = new FpsCounter(session);
            session.Source.AllEvents += e =>
            {
                if (e.ProcessID == processId && (int)e.ID == PresentStart && e.ProviderGuid == DxgiProvider)
                    counter.OnPresent(e.TimeStamp.Ticks);
            };
            session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue);
            Task.Run(() => { try { session.Source.Process(); } catch { } });
            return counter;
        }
        catch (UnauthorizedAccessException)
        {
            session?.Dispose();
            error = "With DXVK off, the FPS overlay needs administrator rights (or the \"Performance Log Users\" group). With DXVK on it works without.";
            return null;
        }
        catch (Exception ex)
        {
            session?.Dispose();
            error = "FPS overlay could not start: " + ex.Message;
            return null;
        }
    }

    public void Dispose()
    {
        try { _session.Dispose(); } catch { }
    }
}
