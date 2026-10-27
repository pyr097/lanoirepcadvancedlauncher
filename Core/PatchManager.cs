using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace LANoireLauncher.Core;

public sealed class LoaderConflictException : Exception
{
    public string FileName { get; }
    public LoaderConflictException() : this("dinput8.dll") { }
    public LoaderConflictException(string file) : base("A different " + file + " already exists in the game folder.") { FileName = file; }
}

public sealed class PatchStatus
{
    public bool LoaderPresent { get; init; }
    public bool AsiPresent { get; init; }
    public bool IniPresent { get; init; }
    public string Label { get; init; } = "";
    public bool Usable => LoaderPresent && AsiPresent;
}

/// <summary>
/// Installs/removes V-Patch (CC BY-NC 4.0, by VaanaCZ) into the user's game folder.
/// The patch is never bundled: it comes from a zip the user supplies or from GitHub at runtime.
/// </summary>
public static class PatchManager
{
    // SHA-256 of the files inside LaNoireVPatch 2.0 (used only to recognise the version).
    private const string Known20Loader = "29fee0aa46414864ad1f65b2689b470bfb2cf2f09a94e2afd271e68f2e16743b";
    private const string Known20Asi = "b6ed1ce1bf5bcb7acb71a0cc8f7b2170953fd1b2fe7f02c6ee8a7b381291d673";
    private const string Prefix = "L.A.Noire/";

    private sealed class Manifest
    {
        public List<string> Added { get; set; } = new();
        public string? LoaderBackup { get; set; }
    }

    private static string ManifestPath(GameInstall g)
    {
        var id = Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(g.Root.ToLowerInvariant())))[..12];
        return Path.Combine(LauncherConfig.Dir, "manifests", id + ".json");
    }

    private static Manifest? LoadManifest(GameInstall g)
    {
        var p = ManifestPath(g);
        return File.Exists(p) ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(p)) : null;
    }

    private static void SaveManifest(GameInstall g, Manifest m)
    {
        var p = ManifestPath(g);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, JsonSerializer.Serialize(m));
    }

    public static bool InstalledByLauncher(GameInstall g) => File.Exists(ManifestPath(g));

    public static PatchStatus GetStatus(GameInstall g)
    {
        bool loader = File.Exists(g.LoaderDll), asi = File.Exists(g.LanvpAsi), ini = File.Exists(g.LanvpIni);
        string label;
        if (loader && asi)
            label = GameInstall.Sha256Of(g.LoaderDll) == Known20Loader && GameInstall.Sha256Of(g.LanvpAsi) == Known20Asi
                ? "V-Patch 2.0 installed"
                : "V-Patch installed (unrecognised version or loader)";
        else if (loader) label = "dinput8.dll present, but V-Patch plugin missing (another mod?)";
        else label = "V-Patch not installed";
        return new PatchStatus { LoaderPresent = loader, AsiPresent = asi, IniPresent = ini, Label = label };
    }

    /// <summary>Fetches the newest release zip from GitHub. Returns the temp file path.</summary>
    public static async Task<string> DownloadLatestAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LANoireLauncher/" + UpdateManager.CurrentText);
        var json = await http.GetStringAsync("https://api.github.com/repos/VaanaCZ/LANVP/releases/latest");
        using var doc = JsonDocument.Parse(json);
        string? url = null;
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { url = a.GetProperty("browser_download_url").GetString(); break; }
        }
        if (url == null) throw new InvalidOperationException("No .zip asset found in the latest V-Patch release.");
        var tmp = Path.Combine(Path.GetTempPath(), "LANVP_" + Guid.NewGuid().ToString("N") + ".zip");
        await File.WriteAllBytesAsync(tmp, await http.GetByteArrayAsync(url));
        return tmp;
    }

    private static string HashEntry(ZipArchiveEntry e)
    {
        using var s = e.Open();
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>Extracts the "L.A.Noire" folder of the release zip into the game folder.</summary>
    public static void Install(GameInstall g, string zipPath, bool replaceForeignLoader)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries
            .Where(e => e.Name.Length > 0 && e.FullName.Replace('\\', '/').StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!entries.Any(e => e.Name.Equals("lanvp.asi", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("This zip does not look like a V-Patch release (no lanvp.asi).");

        var rootFull = Path.GetFullPath(g.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var manifest = LoadManifest(g) ?? new Manifest();

        // Plan first, write later, so a conflict leaves the folder untouched.
        var plan = new List<(ZipArchiveEntry e, string rel, string dest)>();
        foreach (var e in entries)
        {
            var rel = e.FullName.Replace('\\', '/')[Prefix.Length..];
            var dest = Path.GetFullPath(Path.Combine(g.Root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsafe path in zip: " + e.FullName);
            plan.Add((e, rel, dest));
        }

        foreach (var (e, rel, dest) in plan)
            if (rel.Equals("dinput8.dll", StringComparison.OrdinalIgnoreCase) && File.Exists(dest)
                && GameInstall.Sha256Of(dest) != HashEntry(e) && !replaceForeignLoader
                && !manifest.Added.Contains(rel))
                throw new LoaderConflictException();

        foreach (var (e, rel, dest) in plan)
        {
            bool exists = File.Exists(dest);
            if (rel.Equals("plugins/lanvp.ini", StringComparison.OrdinalIgnoreCase) && exists) continue; // keep user config
            if (exists && GameInstall.Sha256Of(dest) == HashEntry(e)) continue;                         // already identical

            if (rel.Equals("dinput8.dll", StringComparison.OrdinalIgnoreCase) && exists && !manifest.Added.Contains(rel))
            {
                var bdir = Path.Combine(LauncherConfig.Dir, "backups", "loader-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(bdir);
                manifest.LoaderBackup = Path.Combine(bdir, "dinput8.dll");
                File.Copy(dest, manifest.LoaderBackup, true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, true);
            if (!manifest.Added.Contains(rel)) manifest.Added.Add(rel);
        }
        SaveManifest(g, manifest);
    }

    /// <summary>Removes only what this launcher added, restoring a backed-up dinput8.dll if there was one.</summary>
    public static void Uninstall(GameInstall g)
    {
        var m = LoadManifest(g)
            ?? throw new InvalidOperationException("V-Patch was not installed by this launcher, so nothing was removed.");
        var files = m.Added;
        foreach (var rel in files)
        {
            var p = Path.Combine(g.Root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(p)) File.Delete(p);
        }
        if (m.LoaderBackup != null && File.Exists(m.LoaderBackup))
            File.Copy(m.LoaderBackup, g.LoaderDll, true);
        if (Directory.Exists(g.PluginsDir) && !Directory.EnumerateFileSystemEntries(g.PluginsDir).Any())
            Directory.Delete(g.PluginsDir);
        var mp = ManifestPath(g);
        if (File.Exists(mp)) File.Delete(mp);
    }
}
