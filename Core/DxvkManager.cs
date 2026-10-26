using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace LANoireLauncher.Core;

public enum DxvkApi { Off, D3D9, D3D11 }

public sealed class DxvkStatus
{
    public bool D3D9Present { get; init; }
    public bool D3D11Present { get; init; }   // d3d11.dll + dxgi.dll
    public string Label { get; init; } = "";
}

/// <summary>
/// Installs/removes DXVK (zlib licence, github.com/doitsujin/dxvk) next to LaNoire.exe.
/// DXVK is never bundled: it comes from an archive you pick or from GitHub at runtime.
/// Only the DLLs for the chosen API are installed, and only 32-bit or 64-bit DLLs matching the exe.
/// Runtime options are passed as environment variables, so no config file is written into the game folder.
/// </summary>
public static class DxvkManager
{
    private static readonly string[] D3D9Dlls = { "d3d9.dll" };
    private static readonly string[] D3D11Dlls = { "d3d11.dll", "dxgi.dll" };

    private sealed class Manifest
    {
        public List<string> Added { get; set; } = new();
        public Dictionary<string, string> Backups { get; set; } = new();   // rel -> backup path
        public string? Version { get; set; }
    }

    private static string ManifestPath(GameInstall g)
    {
        var id = Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(g.Root.ToLowerInvariant())))[..12];
        return Path.Combine(LauncherConfig.Dir, "manifests", "dxvk-" + id + ".json");
    }

    private static Manifest? Load(GameInstall g)
    {
        var p = ManifestPath(g);
        return File.Exists(p) ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(p)) : null;
    }

    private static void Save(GameInstall g, Manifest m)
    {
        var p = ManifestPath(g);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, JsonSerializer.Serialize(m));
    }

    private static bool IsOurs(Manifest? m, string dll) => m != null && m.Added.Contains(dll, StringComparer.OrdinalIgnoreCase);

    public static string[] DllsFor(DxvkApi api) => api switch
    {
        DxvkApi.D3D9 => D3D9Dlls,
        DxvkApi.D3D11 => D3D11Dlls,
        _ => Array.Empty<string>(),
    };

    public static DxvkStatus GetStatus(GameInstall g)
    {
        var m = Load(g);
        bool d9 = IsOurs(m, "d3d9.dll") && File.Exists(Path.Combine(g.Root, "d3d9.dll"));
        bool d11 = IsOurs(m, "d3d11.dll") && IsOurs(m, "dxgi.dll")
                   && File.Exists(Path.Combine(g.Root, "d3d11.dll")) && File.Exists(Path.Combine(g.Root, "dxgi.dll"));
        string label = (d9, d11) switch
        {
            (true, true) => "DXVK installed for D3D9 and D3D11" + Ver(m),
            (true, false) => "DXVK installed for D3D9" + Ver(m),
            (false, true) => "DXVK installed for D3D11" + Ver(m),
            _ => "DXVK not installed (game uses Microsoft's Direct3D)",
        };
        // a d3d9/d3d11/dxgi.dll we did not install: ReShade, SpecialK, another DXVK...
        var foreign = new[] { "d3d9.dll", "d3d11.dll", "dxgi.dll" }
            .Where(f => File.Exists(Path.Combine(g.Root, f)) && !IsOurs(m, f)).ToList();
        if (foreign.Count > 0) label += $"  —  note: {string.Join(", ", foreign)} in the game folder was not installed by this launcher";
        return new DxvkStatus { D3D9Present = d9, D3D11Present = d11, Label = label };
    }

    private static string Ver(Manifest? m) => string.IsNullOrEmpty(m?.Version) ? "" : " (" + m!.Version + ")";

    // ---------- download ----------

    /// <summary>tag: "latest" or a release tag such as "v1.10.3" (needed for GPUs without Vulkan 1.3).</summary>
    public static async Task<(string file, string tag)> DownloadAsync(string tag)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LANoireLauncher/1.0");
        var api = tag.Equals("latest", StringComparison.OrdinalIgnoreCase)
            ? "https://api.github.com/repos/doitsujin/dxvk/releases/latest"
            : "https://api.github.com/repos/doitsujin/dxvk/releases/tags/" + Uri.EscapeDataString(tag);
        using var doc = JsonDocument.Parse(await http.GetStringAsync(api));
        var resolved = doc.RootElement.GetProperty("tag_name").GetString() ?? tag;
        string? url = null;
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            // plain release archive, not the -native Linux one
            if (name.StartsWith("dxvk-", StringComparison.OrdinalIgnoreCase) && !name.Contains("native", StringComparison.OrdinalIgnoreCase)
                && (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            { url = a.GetProperty("browser_download_url").GetString(); break; }
        }
        if (url == null) throw new InvalidOperationException("No DXVK archive found in release " + resolved + ".");
        var ext = url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".tar.gz";
        var tmp = Path.Combine(Path.GetTempPath(), "DXVK_" + Guid.NewGuid().ToString("N") + ext);
        await File.WriteAllBytesAsync(tmp, await http.GetByteArrayAsync(url));
        return (tmp, resolved);
    }

    // ---------- archive reading ----------

    /// <summary>Returns the wanted DLLs (by file name) from the x32 or x64 folder of a DXVK .tar.gz or .zip.</summary>
    private static Dictionary<string, byte[]> ReadDlls(string archive, string arch, IEnumerable<string> wanted)
    {
        var want = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        bool Matches(string fullName, out string file)
        {
            var parts = fullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            file = parts.Length > 0 ? parts[^1] : "";
            return parts.Length >= 2 && parts[^2].Equals(arch, StringComparison.OrdinalIgnoreCase) && want.Contains(file);
        }

        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var e in zip.Entries)
                if (Matches(e.FullName, out var f))
                { using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); found[f] = ms.ToArray(); }
        }
        else
        {
            using var fs = File.OpenRead(archive);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var tar = new TarReader(gz);
            TarEntry? te;
            while ((te = tar.GetNextEntry()) != null)
                if (te.DataStream != null && Matches(te.Name, out var f))
                { using var ms = new MemoryStream(); te.DataStream.CopyTo(ms); found[f] = ms.ToArray(); }
        }
        return found;
    }

    // ---------- install / uninstall ----------

    /// <summary>
    /// Installs the DLLs for <paramref name="api"/>. DLLs of the other API are removed if we installed them.
    /// A d3d9/d3d11/dxgi.dll that is not ours (ReShade etc.) is backed up first, and only if replaceForeign is true.
    /// </summary>
    public static void Apply(GameInstall g, DxvkApi api, string? archive, string? version, bool replaceForeign)
    {
        var m = Load(g) ?? new Manifest();

        // switching API or turning off: remove what we put there
        var keep = DllsFor(api);
        foreach (var rel in m.Added.ToList())
            if (!keep.Contains(rel, StringComparer.OrdinalIgnoreCase)) RemoveOne(g, m, rel);

        if (api == DxvkApi.Off) { Finish(g, m); return; }

        var dlls = DllsFor(api);
        bool allPresent = dlls.All(d => IsOurs(m, d) && File.Exists(Path.Combine(g.Root, d)));
        if (archive == null)
        {
            if (!allPresent) throw new InvalidOperationException("DXVK DLLs for " + api + " are not installed yet. Pick an archive or download one.");
            Finish(g, m); return;
        }

        var arch = g.Is64Bit ? "x64" : "x32";
        var data = ReadDlls(archive, arch, dlls);
        var missing = dlls.Where(d => !data.ContainsKey(d)).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"The archive has no {arch}/{string.Join(", ", missing)}. Is it a DXVK release?");

        // check for conflicts before touching anything
        foreach (var d in dlls)
        {
            var dest = Path.Combine(g.Root, d);
            if (File.Exists(dest) && !IsOurs(m, d) && !replaceForeign) throw new LoaderConflictException(d);
        }

        foreach (var d in dlls)
        {
            var dest = Path.Combine(g.Root, d);
            if (File.Exists(dest) && !IsOurs(m, d))
            {
                var bdir = Path.Combine(LauncherConfig.Dir, "backups", "dxvk-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(bdir);
                var bp = Path.Combine(bdir, d);
                File.Copy(dest, bp, true);
                m.Backups[d] = bp;
            }
            File.WriteAllBytes(dest, data[d]);
            if (!IsOurs(m, d)) m.Added.Add(d);
        }
        if (version != null) m.Version = version;
        Finish(g, m);
    }

    private static void RemoveOne(GameInstall g, Manifest m, string rel)
    {
        var p = Path.Combine(g.Root, rel);
        if (File.Exists(p)) File.Delete(p);
        if (m.Backups.TryGetValue(rel, out var bp) && File.Exists(bp)) { File.Copy(bp, p, true); }
        m.Backups.Remove(rel);
        m.Added.RemoveAll(x => x.Equals(rel, StringComparison.OrdinalIgnoreCase));
    }

    private static void Finish(GameInstall g, Manifest m)
    {
        if (m.Added.Count == 0) { var mp = ManifestPath(g); if (File.Exists(mp)) File.Delete(mp); }
        else Save(g, m);
    }

    public static void Uninstall(GameInstall g) => Apply(g, DxvkApi.Off, null, null, false);

    // ---------- runtime configuration ----------

    public static string ConfFile => Path.Combine(LauncherConfig.Dir, "dxvk.conf");
    public static string CacheDir => Path.Combine(LauncherConfig.Dir, "dxvk-cache");
    public static string LogDir => Path.Combine(LauncherConfig.Dir, "dxvk-logs");

    /// <summary>Environment variables to set on the game process. DXVK reads these itself; nothing is written to the game folder.</summary>
    public static Dictionary<string, string> BuildEnvironment(string hud, string frameRate, string confText, string logLevel)
    {
        var env = new Dictionary<string, string>();
        Directory.CreateDirectory(CacheDir);
        env["DXVK_STATE_CACHE_PATH"] = CacheDir;               // pipeline cache survives game updates / reinstalls
        if (!string.IsNullOrWhiteSpace(hud)) env["DXVK_HUD"] = hud.Trim();
        if (int.TryParse(frameRate, out var fps) && fps > 0) env["DXVK_FRAME_RATE"] = fps.ToString();
        if (!string.IsNullOrWhiteSpace(logLevel) && !logLevel.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(LogDir);
            env["DXVK_LOG_LEVEL"] = logLevel.Trim();
            env["DXVK_LOG_PATH"] = LogDir;
        }
        if (!string.IsNullOrWhiteSpace(confText))
        {
            Directory.CreateDirectory(LauncherConfig.Dir);
            File.WriteAllText(ConfFile, confText.Replace("\r\n", "\n"));
            env["DXVK_CONFIG_FILE"] = ConfFile;
        }
        return env;
    }

    public static void ClearCache()
    {
        if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, true);
    }

    public static long CacheBytes() =>
        Directory.Exists(CacheDir) ? new DirectoryInfo(CacheDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
}
