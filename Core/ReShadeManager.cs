using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LANoireLauncher.Core;

public sealed class ReShadeStatus
{
    public bool RuntimeCached { get; init; }
    public bool Installed { get; init; }          // our ReShade .asi is in plugins\
    public string Label { get; init; } = "";
    public string ShaderSummary { get; init; } = "";
}

public sealed class ImportResult
{
    public int Effects { get; set; }
    public int Textures { get; set; }
    public int Presets { get; set; }
    public int Total => Effects + Textures + Presets;
    public void Add(ImportResult o) { Effects += o.Effects; Textures += o.Textures; Presets += o.Presets; }
}

/// <summary>
/// ReShade (BSD 3-Clause, crosire) for the DirectX 11 game, with DXVK on or off.
///
/// How it loads: the ReShade dll is copied to plugins\ReShade32.asi (or ReShade64.asi) and loaded by the
/// ASI loader (dinput8.dll) that V-Patch already installs. That avoids every name ReShade normally uses as a
/// proxy (dxgi.dll / d3d11.dll are DXVK's, dinput8.dll is V-Patch's), so the same file works in both modes.
///
/// Nothing is bundled. The runtime comes from the ReShade setup exe you pick or from reshade.me, and is cached in
/// %AppData%\LANoireLauncher\reshade\runtime. Shaders, textures and presets live in %AppData%\LANoireLauncher\reshade
/// too; ReShade.ini in the game folder points at them with absolute paths.
/// </summary>
public static partial class ReShadeManager
{
    // names ReShade can be installed under; one of these that is really ReShade and not ours means a double install
    private static readonly string[] LoaderNames = { "dxgi.dll", "d3d11.dll", "d3d10.dll", "d3d9.dll", "dinput8.dll", "ddraw.dll" };

    // files ReShade itself writes next to the exe; "Remove" deletes the ones that were not there before we first enabled it
    private static readonly string[] Artifacts = { "ReShade.ini", "ReShade.log", "ReShadePreset.ini" };

    private sealed class Manifest
    {
        public List<string> Added { get; set; } = new();
        public bool BaselineTaken { get; set; }
        public List<string> Preexisting { get; set; } = new();   // Artifacts that already existed before the first enable
        public string? IniBackup { get; set; }                   // copy of a ReShade.ini that already existed
    }

    // ---------- paths ----------

    public static string Root => Path.Combine(LauncherConfig.Dir, "reshade");
    public static string RuntimeDir => Path.Combine(Root, "runtime");
    public static string ShadersDir => Path.Combine(Root, "Shaders");
    public static string TexturesDir => Path.Combine(Root, "Textures");
    public static string PresetsDir => Path.Combine(Root, "Presets");

    private static string DllName(GameInstall g) => g.Is64Bit ? "ReShade64.dll" : "ReShade32.dll";
    private static string CachedDll(GameInstall g) => Path.Combine(RuntimeDir, DllName(g));
    private static string AsiName(GameInstall g) => Path.ChangeExtension(DllName(g), ".asi");
    private static string AsiRel(GameInstall g) => "plugins/" + AsiName(g);
    public static string AsiPath(GameInstall g) => Path.Combine(g.PluginsDir, AsiName(g));
    public static string IniPath(GameInstall g) => Path.Combine(g.Root, "ReShade.ini");
    // ReShade uses the folder its dll sits in as its base path when it is not loaded as the exe's own proxy dll, so loaded from
    // plugins\ it can look for (and create) ReShade.ini, ReShade.log and relative paths there instead of next to the exe.
    public static string PluginsIniPath(GameInstall g) => Path.Combine(g.PluginsDir, "ReShade.ini");
    private static readonly string[] PluginsArtifacts = { "ReShade.ini", "ReShade.log", "ReShadePreset.ini" };

    private static string ManifestPath(GameInstall g)
    {
        var id = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(g.Root.ToLowerInvariant())))[..12];
        return Path.Combine(LauncherConfig.Dir, "manifests", "reshade-" + id + ".json");
    }

    private static Manifest? LoadManifest(GameInstall g)
    {
        var p = ManifestPath(g);
        return File.Exists(p) ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(p)) : null;
    }

    private static void Finish(GameInstall g, Manifest m)
    {
        var p = ManifestPath(g);
        if (m.Added.Count == 0 && !m.BaselineTaken) { if (File.Exists(p)) File.Delete(p); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, JsonSerializer.Serialize(m));
    }

    private static bool IsOurs(Manifest? m, GameInstall g) =>
        m != null && m.Added.Contains(AsiRel(g), StringComparer.OrdinalIgnoreCase);

    // ---------- status ----------

    public static ReShadeStatus GetStatus(GameInstall g)
    {
        var m = LoadManifest(g);
        bool ours = IsOurs(m, g) && File.Exists(AsiPath(g));
        bool cached = File.Exists(CachedDll(g));
        var foreign = FindForeign(g, m);
        var ver = RuntimeVersion();

        var parts = new List<string>
        {
            cached ? "ReShade " + (ver.Length > 0 ? ver + " " : "") + (RuntimeHasAddon() ? "(add-on support) " : "(NOT known to have add-on support: use Download latest) ") + "ready in the launcher" : "ReShade is not installed in the launcher yet",
            ours ? "enabled (plugins\\" + AsiName(g) + ")" : "not enabled in the game folder",
        };
        if (!File.Exists(g.LoaderDll)) parts.Add("V-Patch's ASI loader (dinput8.dll) is missing, so ReShade cannot load");
        if (foreign != null) parts.Add("conflict: " + foreign + " is a ReShade file this launcher did not install");
        return new ReShadeStatus { RuntimeCached = cached, Installed = ours, Label = string.Join("  —  ", parts), ShaderSummary = ShaderSummary() };
    }

    /// <summary>True if the cached runtime was installed from the build with add-on support.</summary>
    public static bool RuntimeHasAddon() => File.Exists(Path.Combine(RuntimeDir, "addon.txt"));

    private static string RuntimeVersion()
    {
        var p = Path.Combine(RuntimeDir, "version.txt");
        return File.Exists(p) ? File.ReadAllText(p).Trim() : "";
    }

    private static IEnumerable<string> FindShaders(string nameContains) =>
        Directory.Exists(ShadersDir)
            ? Directory.EnumerateFiles(ShadersDir, "*.fx", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f).Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            : Enumerable.Empty<string>();

    private static string ShaderSummary()
    {
        int fx = Directory.Exists(ShadersDir) ? Directory.EnumerateFiles(ShadersDir, "*.fx", SearchOption.AllDirectories).Count() : 0;
        int tex = Directory.Exists(TexturesDir) ? Directory.EnumerateFiles(TexturesDir, "*", SearchOption.AllDirectories).Count() : 0;
        var rtgi = FindShaders("rtgi").FirstOrDefault();
        return $"{fx} effects, {tex} textures  —  " + (rtgi != null ? "RTGI found: " + Path.GetFileName(rtgi) : "RTGI not found");
    }

    /// <summary>A ReShade copy in the game folder that this launcher did not put there (it would hook the game a second time).</summary>
    private static string? FindForeign(GameInstall g, Manifest? m)
    {
        foreach (var n in LoaderNames)
        {
            var p = Path.Combine(g.Root, n);
            if (!File.Exists(p)) continue;
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(p);
                if ($"{vi.ProductName}|{vi.FileDescription}|{vi.InternalName}".Contains("reshade", StringComparison.OrdinalIgnoreCase)) return n;
            }
            catch { /* unreadable: ignore */ }
        }
        if (File.Exists(AsiPath(g)) && !IsOurs(m, g)) return "plugins\\" + AsiName(g);
        return null;
    }

    // ---------- runtime: install from setup / download ----------

    private static HttpClient NewHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) LANoireLauncher/" + UpdateManager.CurrentText);
        return h;
    }

    /// <summary>Newest ReShade setup exe from reshade.me, always the build WITH add-on support (ReShade_Setup_x.y.z_Addon.exe). Returns the temp file and its version.</summary>
    public static async Task<(string file, string version)> DownloadAsync()
    {
        using var http = NewHttp();
        var html = await http.GetStringAsync("https://reshade.me/");
        var m = Regex.Match(html, @"downloads/ReShade_Setup_(\d+(?:\.\d+)+)_Addon\.exe", RegexOptions.IgnoreCase);
        if (!m.Success)
            throw new InvalidOperationException("Could not find the add-on build's download link on reshade.me. Download \"ReShade ... with full add-on support\" there yourself and use \"Install from setup…\".");
        var ver = m.Groups[1].Value;
        var tmp = Path.Combine(Path.GetTempPath(), "ReShade_Setup_" + ver + "_Addon_" + Guid.NewGuid().ToString("N") + ".exe");
        await File.WriteAllBytesAsync(tmp, await http.GetByteArrayAsync("https://reshade.me/downloads/ReShade_Setup_" + ver + "_Addon.exe"));
        return (tmp, ver);
    }

    /// <summary>
    /// A ReShade setup exe is a small stub with a zip appended. The zip's offsets are relative to the start of the zip data,
    /// which .NET does not adjust for, so the stub is cut off first.
    /// </summary>
    private static ZipArchive OpenZip(byte[] all)
    {
        int eocd = -1;
        for (int i = all.Length - 22; i >= Math.Max(0, all.Length - 22 - 65535); i--)
            if (all[i] == 0x50 && all[i + 1] == 0x4B && all[i + 2] == 5 && all[i + 3] == 6) { eocd = i; break; }
        if (eocd < 0) throw new InvalidDataException("No zip data found in this file.");
        long cdSize = BitConverter.ToUInt32(all, eocd + 12), cdOffset = BitConverter.ToUInt32(all, eocd + 16);
        long start = eocd - cdSize - cdOffset;
        if (start < 0 || start > all.Length) throw new InvalidDataException("The zip data in this file is damaged.");
        var ms = start == 0 ? new MemoryStream(all, false) : new MemoryStream(all, (int)start, all.Length - (int)start, false);
        return new ZipArchive(ms, ZipArchiveMode.Read);
    }

    /// <summary>Takes ReShade32.dll / ReShade64.dll out of a setup exe, a zip, or a bare dll and caches them. Returns the version.</summary>
    public static string InstallRuntime(string source, string? versionHint = null, bool? addon = null)
    {
        var want = new[] { "ReShade32.dll", "ReShade64.dll" };
        var found = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        if (source.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(source);
            if (!want.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("A bare dll must be named ReShade32.dll or ReShade64.dll. Otherwise pick the ReShade setup exe or a zip.");
            found[name] = File.ReadAllBytes(source);
        }
        else
        {
            using var zip = OpenZip(File.ReadAllBytes(source));
            foreach (var e in zip.Entries)
                if (want.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
                {
                    using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
                    found[e.Name] = ms.ToArray();
                }
        }
        if (found.Count == 0) throw new InvalidDataException("No ReShade32.dll / ReShade64.dll found. Is this the ReShade setup from reshade.me?");

        Directory.CreateDirectory(RuntimeDir);
        string? anyPath = null;
        foreach (var kv in found)
        {
            var canon = want.First(w => w.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
            anyPath = Path.Combine(RuntimeDir, canon);
            File.WriteAllBytes(anyPath, kv.Value);
        }
        var ver = versionHint;
        if (string.IsNullOrWhiteSpace(ver) && anyPath != null)
        {
            try { ver = FileVersionInfo.GetVersionInfo(anyPath).FileVersion; } catch { }
        }
        ver = string.IsNullOrWhiteSpace(ver) ? "" : ver!.Trim();
        File.WriteAllText(Path.Combine(RuntimeDir, "version.txt"), ver);
        // The dll itself does not say whether it is the add-on build, so remember it: from the caller (our own download), else from the
        // setup's file name (ReShade_Setup_x.y.z_Addon.exe). A bare dll or a renamed zip counts as "not known to have add-on support".
        bool isAddon = addon ?? Path.GetFileName(source).Contains("addon", StringComparison.OrdinalIgnoreCase);
        var marker = Path.Combine(RuntimeDir, "addon.txt");
        if (isAddon) File.WriteAllText(marker, "1"); else if (File.Exists(marker)) File.Delete(marker);
        return ver.Length > 0 ? ver : "(version unknown)";
    }

    // ---------- enable / disable in the game folder ----------

    /// <summary>
    /// Turns ReShade on or off for this game. On: copies the cached dll to plugins\ReShade32/64.asi and points ReShade.ini at the
    /// launcher's shader, texture and preset folders (and at <paramref name="preset"/>, if given). Off: removes only the .asi.
    /// ReShade.ini, shaders and presets are always kept. Throws InvalidOperationException with a readable message if it cannot proceed.
    /// </summary>
    public static void Apply(GameInstall g, bool enable, string? preset)
    {
        var m = LoadManifest(g) ?? new Manifest();
        var asi = AsiPath(g); var rel = AsiRel(g);
        bool ours = IsOurs(m, g);

        if (!enable)
        {
            if (ours && File.Exists(asi)) File.Delete(asi);
            m.Added.RemoveAll(x => x.Equals(rel, StringComparison.OrdinalIgnoreCase));
            Finish(g, m);
            return;
        }

        var cache = CachedDll(g);
        if (!File.Exists(cache))
            throw new InvalidOperationException("ReShade is not installed in the launcher yet. Use \"Install from setup…\" (the add-on version) or \"Download latest\" first.");
        if (!File.Exists(g.LoaderDll))
            throw new InvalidOperationException("ReShade is loaded through the ASI loader (dinput8.dll) that comes with V-Patch. Install V-Patch first.");
        var foreign = FindForeign(g, m);
        if (foreign != null)
            throw new InvalidOperationException($"{foreign} in the game folder is a ReShade file that this launcher did not install, and two copies of ReShade conflict. " +
                "Remove or rename that file, then apply again. Keep your ReShade.ini in place: the launcher reuses it (depth settings included) and only changes the shader, texture and preset paths. \"Import from game folder\" copies your shaders and presets.");
        if (Root.Contains(','))
            throw new InvalidOperationException("The launcher's data folder path contains a comma, which ReShade.ini cannot store in a path list.");

        if (!m.BaselineTaken)                                        // remember what was there, so Remove can put it back
        {
            foreach (var a in Artifacts) if (File.Exists(Path.Combine(g.Root, a))) m.Preexisting.Add(a);
            if (File.Exists(IniPath(g)))
            {
                var bd = Path.Combine(LauncherConfig.Dir, "backups", "reshade-original");
                Directory.CreateDirectory(bd);
                m.IniBackup = Path.Combine(bd, "ReShade.ini");
                File.Copy(IniPath(g), m.IniBackup, true);
            }
            m.BaselineTaken = true;
        }

        Directory.CreateDirectory(g.PluginsDir);
        if (!File.Exists(asi) || GameInstall.Sha256Of(asi) != GameInstall.Sha256Of(cache)) File.Copy(cache, asi, true);
        if (!ours) m.Added.Add(rel);
        Finish(g, m);

        WriteIni(g, preset);
    }

    /// <summary>
    /// Puts the game folder back to how it was: removes the .asi, deletes ReShade.ini / ReShade.log / ReShadePreset.ini unless they existed
    /// before (a pre-existing ReShade.ini is restored from its backup), and removes plugins\ if that leaves it empty.
    /// Shaders and presets in %AppData% are kept. Does nothing if this launcher never enabled ReShade.
    /// </summary>
    public static void Uninstall(GameInstall g)
    {
        var m = LoadManifest(g);
        if (m == null) return;
        var asi = AsiPath(g);
        if (IsOurs(m, g) && File.Exists(asi)) File.Delete(asi);
        foreach (var a in Artifacts)
        {
            var p = Path.Combine(g.Root, a);
            if (!m.Preexisting.Contains(a, StringComparer.OrdinalIgnoreCase)) { if (File.Exists(p)) File.Delete(p); }
            else if (a == "ReShade.ini" && m.IniBackup != null && File.Exists(m.IniBackup)) File.Copy(m.IniBackup, p, true);
        }
        foreach (var a in PluginsArtifacts)
        {
            var p = Path.Combine(g.PluginsDir, a);
            try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { }
        }
        if (Directory.Exists(g.PluginsDir) && !Directory.EnumerateFileSystemEntries(g.PluginsDir).Any()) Directory.Delete(g.PluginsDir);
        var mp = ManifestPath(g);
        if (File.Exists(mp)) File.Delete(mp);
    }

    private static void WriteIni(GameInstall g, string? preset)
    {
        Directory.CreateDirectory(ShadersDir); Directory.CreateDirectory(TexturesDir); Directory.CreateDirectory(PresetsDir);
        var path = IniPath(g);
        if (!File.Exists(path)) File.WriteAllText(path, "[GENERAL]\r\n", new UTF8Encoding(false));
        else LauncherConfig.Backup(path);

        var ini = IniDocument.Load(path, '=');
        ini.Set("EffectSearchPaths", ShadersDir + "\\**", "GENERAL");      // "\**" = this folder and all subfolders
        ini.Set("TextureSearchPaths", TexturesDir + "\\**", "GENERAL");
        if (!string.IsNullOrEmpty(preset)) ini.Set("PresetPath", Path.Combine(PresetsDir, preset), "GENERAL");
        ini.Save();

        // The game is also launched with RESHADE_BASE_PATH_OVERRIDE pointing at the game folder (so the ini above is the one used), but if
        // this ReShade build ignores that, it reads plugins\ReShade.ini instead. Keep that one pointing at the same folders.
        try
        {
            Directory.CreateDirectory(g.PluginsDir);
            var pi = PluginsIniPath(g);
            if (!File.Exists(pi)) File.Copy(path, pi);
            else
            {
                var pd = IniDocument.Load(pi, '=');
                pd.Set("EffectSearchPaths", ShadersDir + "\\**", "GENERAL");
                pd.Set("TextureSearchPaths", TexturesDir + "\\**", "GENERAL");
                if (!string.IsNullOrEmpty(preset)) pd.Set("PresetPath", Path.Combine(PresetsDir, preset), "GENERAL");
                pd.Save();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    /// <summary>File name of the preset ReShade.ini currently points at, if it is one of the launcher's presets.</summary>
    public static string? ActivePreset(GameInstall g)
    {
        try
        {
            var p = IniPath(g);
            if (!File.Exists(p)) return null;
            var v = IniDocument.Load(p, '=').Get("PresetPath", "GENERAL");
            if (string.IsNullOrWhiteSpace(v)) return null;
            var name = Path.GetFileName(v.Trim().Replace('/', '\\'));
            return File.Exists(Path.Combine(PresetsDir, name)) ? name : null;
        }
        catch { return null; }
    }

    // ---------- shaders, textures, presets ----------

    public static async Task<string> DownloadStandardShadersAsync()
    {
        using var http = NewHttp();
        var tmp = Path.Combine(Path.GetTempPath(), "reshade-shaders_" + Guid.NewGuid().ToString("N") + ".zip");
        await File.WriteAllBytesAsync(tmp, await http.GetByteArrayAsync("https://github.com/crosire/reshade-shaders/archive/refs/heads/slim.zip"));
        return tmp;
    }

    /// <summary>
    /// Adds a folder or a .zip to the launcher's store. Anything under a "Shaders" or "Textures" folder goes to the matching place
    /// (subfolders kept, so #include paths keep working); loose .fx/.fxh files go to Shaders; preset .ini files go to Presets.
    /// </summary>
    public static ImportResult ImportShaders(string source, bool keepExisting = false)
    {
        var r = new ImportResult();
        // keepExisting (used for downloaded packs): an effect or preset that is already in the store is left alone, so a second copy
        // never ends up in another subfolder (two copies of one technique show up as a red error in ReShade).
        HashSet<string>? have = null;
        if (keepExisting)
        {
            have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(ShadersDir))
                foreach (var f in Directory.EnumerateFiles(ShadersDir, "*.fx", SearchOption.AllDirectories)) have.Add(Path.GetFileName(f));
        }
        if (Directory.Exists(source))
        {
            var top = new DirectoryInfo(source).Name;
            bool keepTop = top.Equals("Shaders", StringComparison.OrdinalIgnoreCase) || top.Equals("Textures", StringComparison.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(source, f);
                if (keepTop) rel = Path.Combine(top, rel);
                Place(rel, () => File.OpenRead(f), r, have);
            }
        }
        else
        {
            using var zip = ZipFile.OpenRead(source);
            foreach (var e in zip.Entries)
                if (e.Name.Length > 0) Place(e.FullName, () => e.Open(), r, have);
        }
        return r;
    }

    private static void Place(string rel, Func<Stream> open, ImportResult r, HashSet<string>? have = null)
    {
        var parts = rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p.StartsWith('.'))) return;     // also blocks ".." and .git

        int idx = Array.FindIndex(parts, p => p.Equals("Shaders", StringComparison.OrdinalIgnoreCase) || p.Equals("Textures", StringComparison.OrdinalIgnoreCase));
        string baseDir; string[] sub;
        if (idx >= 0 && idx < parts.Length - 1)
        {
            baseDir = parts[idx].Equals("Shaders", StringComparison.OrdinalIgnoreCase) ? ShadersDir : TexturesDir;
            sub = parts[(idx + 1)..];
        }
        else
        {
            var ext = Path.GetExtension(parts[^1]).ToLowerInvariant();
            if (ext == ".ini")
            {
                string text;
                using (var s = open()) using (var sr = new StreamReader(s)) text = sr.ReadToEnd();
                if (LooksLikePreset(text))
                {
                    Directory.CreateDirectory(PresetsDir);
                    if (have != null && File.Exists(Path.Combine(PresetsDir, parts[^1]))) return;      // never replace a preset the user may have tuned
                    File.WriteAllText(Path.Combine(PresetsDir, parts[^1]), text, new UTF8Encoding(false));
                    r.Presets++;
                }
                return;
            }
            if (ext != ".fx" && ext != ".fxh" && ext != ".h") return;
            baseDir = ShadersDir; sub = parts;
        }

        if (have != null && baseDir == ShadersDir && parts[^1].EndsWith(".fx", StringComparison.OrdinalIgnoreCase) && have.Contains(parts[^1])) return;
        var dest = Path.GetFullPath(Path.Combine(baseDir, Path.Combine(sub)));
        var fullBase = Path.GetFullPath(baseDir).TrimEnd('\\') + "\\";
        if (!dest.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using (var src = open()) using (var dst = File.Create(dest)) src.CopyTo(dst);

        if (baseDir == ShadersDir) { if (dest.EndsWith(".fx", StringComparison.OrdinalIgnoreCase)) r.Effects++; }
        else r.Textures++;
    }

    private static bool LooksLikePreset(string text) =>
        Regex.IsMatch(text, @"^\s*Techniques\s*=", RegexOptions.Multiline | RegexOptions.IgnoreCase)
        && !Regex.IsMatch(text, @"^\s*EffectSearchPaths\s*=", RegexOptions.Multiline | RegexOptions.IgnoreCase);   // that one is ReShade.ini

    /// <summary>Copies a ReShade preset (.ini) into the store. Importing the same name again replaces it.</summary>
    public static string ImportPreset(string file)
    {
        var name = Path.GetFileName(file);
        if (!LooksLikePreset(File.ReadAllText(file)))
            throw new InvalidDataException(name + " does not look like a ReShade preset (it needs a Techniques= line; ReShade.ini itself is not a preset).");
        Directory.CreateDirectory(PresetsDir);
        File.Copy(file, Path.Combine(PresetsDir, name), true);
        return name;
    }

    /// <summary>Copies reshade-shaders, reshade-presets and loose presets from an earlier manual ReShade install in the game folder.</summary>
    public static ImportResult ImportFromGame(GameInstall g)
    {
        var r = new ImportResult();
        foreach (var d in new[] { "reshade-shaders", "reshade-presets" })
        {
            var p = Path.Combine(g.Root, d);
            if (Directory.Exists(p)) r.Add(ImportShaders(p));
        }
        foreach (var f in Directory.EnumerateFiles(g.Root, "*.ini"))
        {
            try { ImportPreset(f); r.Presets++; }
            catch (InvalidDataException) { /* settings.ini, ReShade.ini, ... */ }
        }
        return r;
    }

    public static IEnumerable<string> ListPresets() =>
        Directory.Exists(PresetsDir)
            ? Directory.EnumerateFiles(PresetsDir, "*.ini").Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();

    /// <summary>Effect files named by a preset's enabled techniques (Name@File.fx) that are not in the shader store. ReShade skips those silently.</summary>
    public static List<string> MissingEffects(string presetName)
    {
        var missing = new List<string>();
        var p = Path.Combine(PresetsDir, Path.GetFileName(presetName));
        if (!File.Exists(p)) return missing;
        var m = Regex.Match(File.ReadAllText(p), @"^\s*Techniques\s*=(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!m.Success) return missing;
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(ShadersDir))
            foreach (var f in Directory.EnumerateFiles(ShadersDir, "*.fx", SearchOption.AllDirectories)) have.Add(Path.GetFileName(f));
        foreach (var t in m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int at = t.LastIndexOf('@');
            if (at < 0) continue;
            var fx = t[(at + 1)..];
            if (!have.Contains(fx) && !missing.Contains(fx, StringComparer.OrdinalIgnoreCase)) missing.Add(fx);
        }
        return missing;
    }

    public static void RemovePreset(string name)
    {
        var p = Path.Combine(PresetsDir, Path.GetFileName(name));
        if (File.Exists(p)) File.Delete(p);
    }

    // ---------- generated presets ----------

    private static IEnumerable<string> Techniques(string fxFile) =>
        Regex.Matches(File.ReadAllText(fxFile), @"^\s*technique\s+([A-Za-z_]\w*)", RegexOptions.Multiline).Select(m => m.Groups[1].Value);

    private static (string name, bool created) WritePreset(string name, IEnumerable<string> techniques)
    {
        Directory.CreateDirectory(PresetsDir);
        var p = Path.Combine(PresetsDir, name);
        if (File.Exists(p)) return (name, false);                      // never overwrite a preset the user may have tuned
        var list = string.Join(",", techniques);
        File.WriteAllText(p, "Techniques=" + list + "\r\nTechniqueSorting=" + list + "\r\n", new UTF8Encoding(false));
        return (name, true);
    }

    /// <summary>A preset that switches on every RTGI technique found in the shader store (debug views left out), with ReShade's default settings.</summary>
    public static (string name, bool created) MakeRtgiPreset()
    {
        var techs = new List<string>();
        foreach (var f in FindShaders("rtgi"))
            foreach (var t in Techniques(f).Where(n => !n.Contains("debug", StringComparison.OrdinalIgnoreCase)))
                techs.Add(t + "@" + Path.GetFileName(f));
        if (techs.Count == 0)
            throw new InvalidOperationException("No RTGI shader found. Add your RTGI files first (\"Add from folder…\" or \"Add from zip…\").");
        return WritePreset("RTGI (launcher).ini", techs);
    }

    /// <summary>A preset with only ReShade's DisplayDepth effect, for checking that the game's depth buffer is read correctly.</summary>
    public static (string name, bool created) MakeDepthPreset()
    {
        var f = FindShaders("DisplayDepth").FirstOrDefault()
            ?? throw new InvalidOperationException("DisplayDepth.fx not found. Use \"Download standard effects\" first.");
        var tech = Techniques(f).FirstOrDefault() ?? "DisplayDepth";
        return WritePreset("Depth check (launcher).ini", new[] { tech + "@" + Path.GetFileName(f) });
    }
}
