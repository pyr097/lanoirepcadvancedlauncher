using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LANoireLauncher.Core;

public sealed class HdPackStatus
{
    public bool Exists { get; init; }
    public int Entries { get; init; }
    public long Bytes { get; init; }
    public string Label { get; init; } = "";
}

public sealed class HdPluginStatus
{
    public bool Installed { get; init; }
    public bool LoaderPresent { get; init; }
    public bool Valid { get; init; } = true;        // false when plugins\LanTexReplace.asi is not really the HD texture plugin
    public bool Enabled { get; init; } = true;
    public bool Log { get; init; }
    public int MaxHdMb { get; init; } = 1024;
}

public sealed record PythonInfo(string Exe, string[] Prefix, string Version);

public sealed class PythonCheck
{
    public PythonInfo? Info { get; init; }          // a usable 64-bit Python with numpy
    public PythonInfo? NeedsNumpy { get; init; }    // a usable 64-bit Python that lacks numpy
    public string Problem { get; init; } = "";
}

/// <summary>
/// HD textures from the PS4 remaster. Two parts:
///  1. a texture PACK, built on this PC from the user's own PS4 files by lan_tex_tools.py (run as a background process);
///  2. a small D3D11 plugin (LanTexReplace.asi, loaded through V-Patch's ASI loader) that swaps matching textures
///     while the game loads them, so no game archive is ever modified.
/// Nothing derived from the PS4 files leaves the user's PC.
/// </summary>
public static class HdTextureManager
{
    public const string AsiName = "LanTexReplace.asi";
    public const string IniName = "LanTexReplace.ini";
    private const string Section = "LanTexReplace";
    private const string ToolResource = "lan_tex_tools.py";
    private const string AsiResource = "LanTexReplace.asi";

    /// <summary>Archives where the PS4 textures are clearly larger, with the file name used by the tool.</summary>
    public static readonly (string Key, string Label)[] RecommendedArchives =
    {
        ("vehicles", "Vehicles"),
        ("props", "Props"),
        ("props.high", "Props (high detail)"),
        ("characters", "Characters"),
        ("reftextures_characters", "Character textures"),
        ("reftextures_populated", "World textures"),
    };

    public static string PluginAsi(GameInstall g) => Path.Combine(g.PluginsDir, AsiName);
    public static string PluginIni(GameInstall g) => Path.Combine(g.PluginsDir, IniName);
    public static string DefaultPackDir(GameInstall g) => Path.Combine(g.Root, "hdpack");
    private static string ToolsDir => Path.Combine(LauncherConfig.Dir, "tools");

    public static bool HasEmbeddedPlugin
    {
        get
        {
            using var s = typeof(HdTextureManager).Assembly.GetManifestResourceStream(AsiResource);
            return s != null;
        }
    }

    // ------------------------------------------------------------------ status

    public static HdPackStatus GetPackStatus(string packDir)
    {
        var manifest = Path.Combine(packDir, "manifest.json");
        if (!File.Exists(manifest)) return new HdPackStatus { Label = "no pack yet" };
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            int n = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
            long bytes = 0;
            var tex = Path.Combine(packDir, "textures");
            if (Directory.Exists(tex))
                foreach (var f in new DirectoryInfo(tex).EnumerateFiles("*.dds")) bytes += f.Length;
            return new HdPackStatus { Exists = true, Entries = n, Bytes = bytes, Label = $"{n:N0} textures, {bytes / 1048576.0:N0} MB" };
        }
        catch (Exception ex) { return new HdPackStatus { Label = "pack unreadable (" + ex.Message + ")" }; }
    }

    public static HdPluginStatus GetPluginStatus(GameInstall g)
    {
        bool installed = File.Exists(PluginAsi(g));
        bool loader = File.Exists(g.LoaderDll);
        bool enabled = true, log = false;
        int mb = 1024;
        var ini = PluginIni(g);
        if (File.Exists(ini))
        {
            try
            {
                var d = IniDocument.Load(ini, '=');
                enabled = (d.Get("Replace", Section) ?? "1") != "0";
                log = (d.Get("Log", Section) ?? "0") == "1";
                if (int.TryParse(d.Get("MaxHdMB", Section), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m > 0) mb = m;
            }
            catch { /* unreadable ini -> show defaults */ }
        }
        return new HdPluginStatus { Installed = installed, LoaderPresent = loader, Valid = !installed || IsHdPlugin(PluginAsi(g)),
                                    Enabled = enabled, Log = log, MaxHdMb = mb };
    }

    // ------------------------------------------------------------------ plugin file checks

    /// <summary>True when the file is a 32-bit PE that contains the HD texture plugin's own marker string.
    /// Prevents installing some other .asi (for example V-Patch's) under the plugin's name.</summary>
    public static bool IsHdPlugin(string path)
    {
        try
        {
            var b = File.ReadAllBytes(path);
            if (b.Length < 0x1000 || b[0] != (byte)'M' || b[1] != (byte)'Z') return false;
            int pe = BitConverter.ToInt32(b, 0x3C);
            if (pe < 0 || pe + 6 > b.Length || b[pe] != (byte)'P' || b[pe + 1] != (byte)'E') return false;
            if (BitConverter.ToUInt16(b, pe + 4) != 0x14C) return false;                    // 32-bit x86 only
            return b.AsSpan().IndexOf(Encoding.ASCII.GetBytes("LanTexReplace.ini")) >= 0;
        }
        catch { return false; }
    }

    public sealed class AsiScan
    {
        public List<string> Names { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    /// <summary>Lists the .asi files the ASI loader would start (game folder, scripts, plugins) and flags identical copies.</summary>
    public static AsiScan ScanAsi(GameInstall g)
    {
        var scan = new AsiScan();
        var byHash = new Dictionary<string, string>();
        foreach (var dir in new[] { g.Root, Path.Combine(g.Root, "scripts"), g.PluginsDir })
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.asi").ToList(); } catch { continue; }
            foreach (var f in files)
            {
                var rel = Path.GetRelativePath(g.Root, f);
                scan.Names.Add(rel);
                try
                {
                    var h = GameInstall.Sha256Of(f);
                    if (byHash.TryGetValue(h, out var other))
                        scan.Warnings.Add($"{rel} is an identical copy of {other}, so the same plugin would be loaded twice.");
                    else byHash[h] = rel;
                }
                catch { /* unreadable file: skip */ }
            }
        }
        return scan;
    }

    // ------------------------------------------------------------------ plugin

    /// <summary>Copies the plugin into the game's plugins folder: the embedded one, or a file the user built.</summary>
    public static void InstallPlugin(GameInstall g, string? sourceAsi)
    {
        Directory.CreateDirectory(g.PluginsDir);
        var dest = PluginAsi(g);
        LauncherConfig.Backup(dest);
        if (sourceAsi != null)
        {
            if (!IsHdPlugin(sourceAsi))
                throw new InvalidOperationException("That file is not the HD texture plugin. It must be the 32-bit LanTexReplace.asi built from the Plugin folder. Nothing was installed.");
            File.Copy(sourceAsi, dest, true);
        }
        else
        {
            using var res = typeof(HdTextureManager).Assembly.GetManifestResourceStream(AsiResource)
                ?? throw new InvalidOperationException("This launcher build does not contain the plugin. Choose the LanTexReplace.asi you built.");
            using (var fs = File.Create(dest)) res.CopyTo(fs);
            if (!IsHdPlugin(dest))
            {
                File.Delete(dest);
                throw new InvalidOperationException("The plugin embedded in this launcher build is not valid. Nothing was installed.");
            }
        }
    }

    public static void UninstallPlugin(GameInstall g)
    {
        foreach (var f in new[] { PluginAsi(g), PluginIni(g), Path.Combine(g.PluginsDir, "LanTexReplace.log") })
            if (File.Exists(f)) File.Delete(f);
    }

    /// <summary>Writes the plugin's settings file (created when missing).</summary>
    public static void WriteSettings(GameInstall g, bool enabled, int maxHdMb, bool diagnosticLog, string packDir)
    {
        Directory.CreateDirectory(g.PluginsDir);
        var ini = PluginIni(g);
        if (!File.Exists(ini))
            File.WriteAllText(ini, "[LanTexReplace]\r\nReplace=1\r\nLog=0\r\nLogUpdates=0\r\nPack=hdpack\r\nMaxHdMB=1024\r\n");
        var d = IniDocument.Load(ini, '=');
        d.Set("Replace", enabled ? "1" : "0", Section);
        d.Set("Log", diagnosticLog ? "1" : "0", Section);
        d.Set("LogUpdates", diagnosticLog ? "1" : "0", Section);
        d.Set("MaxHdMB", maxHdMb.ToString(CultureInfo.InvariantCulture), Section);
        d.Set("Pack", packDir, Section);
        d.Save();
    }

    // ------------------------------------------------------------------ pack

    /// <summary>Accepts the PS4 folder itself, its parent, or its "final" parent. Returns null when no .big.ps4 files are found.</summary>
    public static string? ResolvePs4Dir(string chosen)
    {
        foreach (var d in new[] { chosen, Path.Combine(chosen, "ps4"), Path.Combine(chosen, "final", "ps4") })
        {
            try { if (Directory.Exists(d) && Directory.EnumerateFiles(d, "*.big.ps4").Any()) return d; }
            catch { /* unreadable folder */ }
        }
        return null;
    }

    public static bool Ps4HasArchive(string ps4Dir, string key) => File.Exists(Path.Combine(ps4Dir, key + ".big.ps4"));

    /// <summary>Deletes a pack folder, but only if it really looks like one.</summary>
    public static void DeletePack(string packDir, GameInstall g)
    {
        var full = Path.GetFullPath(packDir).TrimEnd('\\', '/');
        var root = Path.GetFullPath(g.Root).TrimEnd('\\', '/');
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete the game folder.");
        if (!File.Exists(Path.Combine(full, "manifest.json")) && !Directory.Exists(Path.Combine(full, "textures")))
            throw new InvalidOperationException("That folder does not look like a texture pack; nothing was deleted.");
        Directory.Delete(full, true);
    }

    // ------------------------------------------------------------------ Python + the build tool

    private static async Task<(int Code, string Output)> RunQuickAsync(string exe, IEnumerable<string> args, int timeoutMs = 20000)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return (-1, "");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-1, ""); }
            return (p.ExitCode, (await stdout) + (await stderr));
        }
        catch { return (-1, ""); }      // executable not found, blocked, ...
    }

    /// <summary>Finds a 64-bit Python 3 and checks for numpy (needed by lan_tex_tools.py).</summary>
    public static async Task<PythonCheck> FindPythonAsync()
    {
        var candidates = new (string Exe, string[] Prefix)[]
        {
            ("py", new[] { "-3" }), ("python", Array.Empty<string>()), ("python3", Array.Empty<string>()),
        };
        string problem = "Python 3 was not found. Install 64-bit Python 3.8 or newer from python.org (tick \"Add python.exe to PATH\"), then try again.";
        foreach (var (exe, prefix) in candidates)
        {
            var probe = prefix.Concat(new[] { "-c", "import struct,sys;print(struct.calcsize('P')*8, sys.version.split()[0])" });
            var (code, output) = await RunQuickAsync(exe, probe);
            if (code != 0) continue;
            var parts = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (parts[0] != "64")
            {
                problem = "Only 32-bit Python was found. The archives are several GB, so 64-bit Python 3 is required.";
                continue;
            }
            var info = new PythonInfo(exe, prefix, parts[1]);
            var (nc, _) = await RunQuickAsync(exe, prefix.Concat(new[] { "-c", "import numpy" }));
            return nc == 0 ? new PythonCheck { Info = info } : new PythonCheck { NeedsNumpy = info, Problem = "numpy is missing." };
        }
        return new PythonCheck { Problem = problem };
    }

    private static async Task<int> RunStreamingAsync(string exe, IEnumerable<string> args, Action<string> onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        using var reg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } });
        await proc.WaitForExitAsync();
        proc.WaitForExit();                 // lets the redirected output finish flushing
        return ct.IsCancellationRequested ? -2 : proc.ExitCode;
    }

    /// <summary>
    /// Analyses a diagnostic log from the plugin: for every texture the game loaded it reports which archive it lives in and
    /// whether the PS4 version has a larger counterpart. Reads the archives only; writes nothing.
    /// </summary>
    public static Task<int> ExplainAsync(PythonInfo py, GameInstall g, string? ps4Dir, string logPath, IReadOnlyList<string> archives,
                                         Action<string> onLine, CancellationToken ct)
    {
        var script = ExtractTool();
        var args = new List<string>(py.Prefix) { "-u", script, "explain", "--pc", Path.Combine(g.Root, "final", "pc"), "--log", logPath };
        if (!string.IsNullOrEmpty(ps4Dir)) { args.Add("--ps4"); args.Add(ps4Dir); }
        if (archives.Count > 0) { args.Add("--archives"); args.Add(string.Join(",", archives)); }
        return RunStreamingAsync(py.Exe, args, onLine, ct);
    }

    public static Task<int> InstallNumpyAsync(PythonInfo py, Action<string> onLine, CancellationToken ct) =>
        RunStreamingAsync(py.Exe, py.Prefix.Concat(new[] { "-m", "pip", "install", "numpy" }), onLine, ct);

    private static string ExtractTool()
    {
        Directory.CreateDirectory(ToolsDir);
        var dest = Path.Combine(ToolsDir, "lan_tex_tools.py");
        using var res = typeof(HdTextureManager).Assembly.GetManifestResourceStream(ToolResource)
            ?? throw new InvalidOperationException("The texture tool is missing from this launcher build.");
        using var ms = new MemoryStream();
        res.CopyTo(ms);
        var bytes = ms.ToArray();
        if (!File.Exists(dest) || !File.ReadAllBytes(dest).AsSpan().SequenceEqual(bytes)) File.WriteAllBytes(dest, bytes);
        return dest;
    }

    /// <summary>
    /// Builds or extends the pack. Returns the tool's exit code (0 = ok, -2 = cancelled).
    /// Runs "lan_tex_tools.py build": it reads the PC and PS4 archives and only writes into packDir.
    /// </summary>
    public static Task<int> BuildAsync(PythonInfo py, GameInstall g, string ps4Dir, string packDir, IReadOnlyList<string> archives,
                                       double minCorr, bool fresh, Action<string> onLine, CancellationToken ct)
    {
        var script = ExtractTool();
        var args = new List<string>(py.Prefix) { "-u", script, "build",
            "--pc", Path.Combine(g.Root, "final", "pc"), "--ps4", ps4Dir, "--out", packDir,
            "--min-corr", minCorr.ToString(CultureInfo.InvariantCulture) };
        if (archives.Count > 0) { args.Add("--archives"); args.Add(string.Join(",", archives)); }
        if (fresh) args.Add("--fresh");
        return RunStreamingAsync(py.Exe, args, onLine, ct);
    }
}
