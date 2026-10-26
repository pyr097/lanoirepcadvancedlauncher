using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LANoireLauncher.Core;

/// <summary>
/// Frame statistics from Intel's PresentMon (MIT licence, github.com/GameTechDev/PresentMon), which reads Windows' own
/// ETW present events. Nothing is injected into the game and no frame is delayed, so the cost is a few CPU cycles per frame.
/// It sees DirectX 11 and Vulkan (DXVK) presents alike, so it works with and without DXVK.
/// PresentMon is never bundled: it comes from a file you pick or from GitHub at runtime.
/// </summary>
public sealed class FrameMonitor : IDisposable
{
    private readonly string _csv = Path.Combine(Path.GetTempPath(), "lanoire_presentmon_" + Guid.NewGuid().ToString("N") + ".csv");
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Queue<double> _ms = new();
    private double _sum;
    private long _lastTick;
    private Process? _proc;
    private bool _elevated;

    public bool ReceivedFrames { get; private set; }
    public string? HeaderError { get; private set; }

    // ---------- tool management ----------

    public static string ToolDir => Path.Combine(LauncherConfig.Dir, "tools");
    public static string DefaultExe => Path.Combine(ToolDir, "PresentMon.exe");

    public static async Task<string> DownloadAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LANoireLauncher/1.0");
        using var doc = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/GameTechDev/PresentMon/releases/latest"));
        string? url = null;
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (Regex.IsMatch(name, @"^PresentMon.*x64.*\.exe$", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(name, "service|install|capture|application", RegexOptions.IgnoreCase))
            { url = a.GetProperty("browser_download_url").GetString(); break; }
        }
        if (url == null) throw new InvalidOperationException("No PresentMon console exe found in the latest release. Download it manually and use Browse…");
        Directory.CreateDirectory(ToolDir);
        await File.WriteAllBytesAsync(DefaultExe, await http.GetByteArrayAsync(url));
        return DefaultExe;
    }

    // ---------- start / stop ----------

    private ProcessStartInfo BuildStart(string exe, string processName, bool elevate)
    {
        var args = $"--process_name {processName} --output_file \"{_csv}\" --no_console_stats " +
                   "--terminate_on_proc_exit --stop_existing_session --timed 43200 --terminate_after_timed";
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        };
        if (elevate) psi.Verb = "runas";
        return psi;
    }

    /// <summary>
    /// Tries without elevation first (works if you are in the "Performance Log Users" group); if PresentMon refuses,
    /// asks Windows for administrator rights once (UAC prompt).
    /// </summary>
    public static async Task<(FrameMonitor? monitor, string? error)> StartAsync(string exe, string processName)
    {
        var m = new FrameMonitor();
        TryDeleteOldTempFiles();
        try
        {
            m._proc = Process.Start(m.BuildStart(exe, processName, false));
            await Task.Delay(2500);
            bool died = false;
            try { died = m._proc == null || m._proc.HasExited; } catch { }
            if (died)
            {
                m._elevated = true;
                m._proc = Process.Start(m.BuildStart(exe, processName, true));
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { m.Dispose(); return (null, "PresentMon needs administrator rights and the UAC prompt was declined."); }
        catch (Exception ex)
        { m.Dispose(); return (null, "Could not start PresentMon: " + ex.Message); }

        _ = Task.Run(() => m.TailAsync(m._cts.Token));
        return (m, null);
    }

    private static void TryDeleteOldTempFiles()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), "lanoire_presentmon_*.csv"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddHours(-12)) File.Delete(f);
        }
        catch { }
    }

    // ---------- reading the CSV PresentMon writes ----------

    private async Task TailAsync(CancellationToken ct)
    {
        try
        {
            var until = DateTime.UtcNow.AddSeconds(90);                 // leaves time for the UAC prompt
            while (!File.Exists(_csv) && DateTime.UtcNow < until) await Task.Delay(250, ct);
            if (!File.Exists(_csv)) return;

            using var fs = new FileStream(_csv, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[16384];
            var pending = "";
            int col = -1;
            while (!ct.IsCancellationRequested)
            {
                int n = await fs.ReadAsync(buf, 0, buf.Length, ct);
                if (n == 0) { await Task.Delay(100, ct); continue; }
                pending += Encoding.UTF8.GetString(buf, 0, n);
                var parts = pending.Split('\n');
                pending = parts[^1];                                    // possibly a half-written line
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    var line = parts[i].TrimEnd('\r');
                    if (line.Length == 0) continue;
                    var f = line.Split(',');
                    if (col < 0)
                    {
                        col = Array.FindIndex(f, c => c.Equals("msBetweenPresents", StringComparison.OrdinalIgnoreCase)
                                                     || c.Equals("FrameTime", StringComparison.OrdinalIgnoreCase));
                        if (col < 0) { HeaderError = "PresentMon's CSV has no frame-time column."; return; }
                        continue;
                    }
                    if (f.Length > col && double.TryParse(f[col], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms > 0 && ms < 5000)
                        Push(ms);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void Push(double ms)
    {
        lock (_gate)
        {
            _ms.Enqueue(ms); _sum += ms;
            while (_sum > 1500 && _ms.Count > 1) _sum -= _ms.Dequeue();   // keep ~1.5 s of frames
            _lastTick = Environment.TickCount64;
            ReceivedFrames = true;
        }
    }

    /// <summary>Average over the last ~1.5 s of frames, or null if no frame arrived for 2 s.</summary>
    public (double fps, double ms)? GetStats()
    {
        lock (_gate)
        {
            if (_ms.Count == 0 || Environment.TickCount64 - _lastTick > 2000) return null;
            var avg = _sum / _ms.Count;
            return (1000.0 / avg, avg);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        // An elevated PresentMon cannot be ended from here; it stops itself when the game exits (--terminate_on_proc_exit).
        if (!_elevated) { try { if (_proc is { HasExited: false }) _proc.Kill(); } catch { } }
        try { File.Delete(_csv); } catch { }
    }
}
