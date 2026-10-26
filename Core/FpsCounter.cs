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
    private long _frames;

    /// <summary>Total frames presented by the game since the counter started.</summary>
    public long Frames => Interlocked.Read(ref _frames);

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
                    Interlocked.Increment(ref counter._frames);
            };
            session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue);
            Task.Run(() => { try { session.Source.Process(); } catch { } });
            return counter;
        }
        catch (UnauthorizedAccessException)
        {
            session?.Dispose();
            error = "FPS overlay with DXVK off needs the launcher to run as administrator (or your account in the \"Performance Log Users\" group). With DXVK on it works without.";
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
