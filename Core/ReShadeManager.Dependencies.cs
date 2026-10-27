using System.Net;
using System.Text.RegularExpressions;

namespace LANoireLauncher.Core;

/// <summary>A shader pack the launcher knows how to download straight from its author's repository.</summary>
public sealed class ShaderPack
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Source { get; init; } = "";      // shown in the dialog
    public string Licence { get; init; } = "";
    /// <summary>Gets a lower-case file name or include path; true if this pack is where it normally comes from.</summary>
    public Func<string, bool> Claims { get; init; } = _ => false;
}

public enum NeedKind { Loader, Runtime, Pack }

/// <summary>One thing that has to be downloaded, and which files asked for it.</summary>
public sealed class Need
{
    public NeedKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public ShaderPack? Pack { get; init; }
    public List<string> Files { get; } = new();
}

/// <summary>What is missing for ReShade (and a preset) to work, split into what can be downloaded and what cannot.</summary>
public sealed class Readiness
{
    public List<Need> Downloads { get; } = new();
    public List<(string Reason, List<string> Files)> Cannot { get; } = new();
    public bool AllGood => Downloads.Count == 0 && Cannot.Count == 0;
    /// <summary>Identifies this exact set of problems, so a declined offer is not repeated on every click.</summary>
    public string Signature =>
        string.Join("|", Downloads.Select(d => d.Title + ":" + string.Join(",", d.Files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)))) + "#" +
        string.Join("|", Cannot.Select(c => c.Reason + ":" + string.Join(",", c.Files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))));
}

// Which files ReShade, a preset and the game folder need, and where to get what is missing.
// Nothing here is bundled: every pack is fetched from its author's own repository (GitHub's "archive/HEAD.zip" is the
// default branch, so a renamed branch does not break it) and goes through the same ImportShaders() as a manual zip.
public static partial class ReShadeManager
{
    public static readonly ShaderPack Standard = new()
    {
        Id = "standard",
        Title = "ReShade standard effects",
        Url = "https://github.com/crosire/reshade-shaders/archive/refs/heads/slim.zip",
        Source = "github.com/crosire/reshade-shaders (slim)",
        Licence = "each shader keeps its own licence",
        Claims = _ => true,
    };

    // FXAA, Levels, LiftGammaGain, LumaSharpen, SMAA, Vibrance, Curves, Tonemap... are CeeJay.dk's SweetFX package.
    // ReShade's installer lists it separately from "Standard effects", and the crosire/reshade-shaders slim zip does not contain them.
    private static readonly HashSet<string> SweetFxFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "FXAA.fx", "Levels.fx", "LiftGammaGain.fx", "LumaSharpen.fx", "SMAA.fx", "Vibrance.fx", "Curves.fx", "Technicolor.fx",
        "Technicolor2.fx", "Tonemap.fx", "Vignette.fx", "FakeHDR.fx", "DPX.fx", "Cartoon.fx", "ColorMatrix.fx", "Monochrome.fx",
        "Nostalgia.fx", "Sepia.fx", "Border.fx", "CAS.fx",
    };

    public static readonly ShaderPack SweetFx = new()
    {
        Id = "sweetfx",
        Title = "SweetFX effects (CeeJay.dk)",
        Url = "https://github.com/CeeJayDK/SweetFX/archive/HEAD.zip",
        Source = "github.com/CeeJayDK/SweetFX",
        Licence = "© CeeJay.dk, see the repository",
        Claims = n => SweetFxFiles.Contains(Path.GetFileName(n)),
    };

    // The slim branch leaves out some shared headers (Fxaa3_11.h, ...), so it is the last resort for anything still missing.
    public static readonly ShaderPack StandardFull = new()
    {
        Id = "standard-full",
        Title = "ReShade standard effects (full branch)",
        Url = "https://github.com/crosire/reshade-shaders/archive/refs/heads/master.zip",
        Source = "github.com/crosire/reshade-shaders (master)",
        Licence = "each shader keeps its own licence",
        Claims = _ => false,
    };

    public static readonly ShaderPack[] Packs =
    {
        Standard,
        SweetFx,
        StandardFull,
        new()
        {
            Id = "immerse",
            Title = "iMMERSE (MartysMods) shaders",
            Url = "https://github.com/martymcmodding/iMMERSE/archive/HEAD.zip",
            Source = "github.com/martymcmodding/iMMERSE",
            Licence = "© Pascal Gilcher, see the repository",
            Claims = n => n.Contains("martysmods"),
        },
        new()
        {
            Id = "lumenite",
            Title = "LumeniteFX shaders",
            Url = "https://github.com/umar-afzaal/LumeniteFX/archive/HEAD.zip",
            Source = "github.com/umar-afzaal/LumeniteFX",
            Licence = "© Umar Afzaal, see the repository",
            Claims = n => n.Contains("lumenite"),
        },
        new()
        {
            Id = "quint",
            Title = "qUINT shaders",
            Url = "https://github.com/martymcmodding/qUINT/archive/HEAD.zip",
            Source = "github.com/martymcmodding/qUINT",
            Licence = "© Pascal Gilcher, see the repository",
            Claims = n => n.Contains("quint_") || n.Contains("quint/"),
        },
    };

    private const string RtgiReason =
        "RTGI is a paid shader (from its author's Patreon), so the launcher cannot download it. Add the files you have with Add from folder / zip";

    /// <summary>
    /// Where a file or include path normally comes from, most likely source first; null for shaders that are not freely downloadable (RTGI).
    /// A file that is not in the first source is looked for in the next one, so a wrong guess costs one extra download, not a dead end.
    /// </summary>
    private static List<ShaderPack>? PacksFor(string pathOrName)
    {
        var n = pathOrName.Replace('\\', '/').ToLowerInvariant();
        if (n.Contains("rtgi")) return null;
        foreach (var p in Packs)
            if (p.Id != Standard.Id && p.Id != SweetFx.Id && p.Id != StandardFull.Id && p.Claims(n)) return new List<ShaderPack> { p };
        bool fxaaHeader = n.EndsWith(".h") || n.Contains("fxaa");
        return SweetFx.Claims(n) || fxaaHeader
            ? new List<ShaderPack> { SweetFx, Standard, StandardFull }
            : new List<ShaderPack> { Standard, SweetFx, StandardFull };
    }

    // The one shader file that ships inside the launcher: NVIDIA's FXAA 3.11 header (Timothy Lottes), which FXAA.fx includes.
    // ReShade's slim branch leaves it out, so after packs are imported it is written next to FXAA.fx if no copy exists anywhere in the store.
    private const string BundledFxaaHeader = "Fxaa3_11.h";

    /// <summary>Writes the bundled Fxaa3_11.h beside FXAA.fx if the store has FXAA.fx but no Fxaa3_11.h. An existing copy is never replaced. True if it wrote the file.</summary>
    public static bool EnsureBundledHeaders()
    {
        try
        {
            if (!Directory.Exists(ShadersDir)) return false;
            var fxaa = Directory.EnumerateFiles(ShadersDir, "FXAA.fx", SearchOption.AllDirectories).FirstOrDefault();
            if (fxaa == null) return false;
            if (Directory.EnumerateFiles(ShadersDir, BundledFxaaHeader, SearchOption.AllDirectories).Any()) return false;
            using var res = typeof(ReShadeManager).Assembly.GetManifestResourceStream(BundledFxaaHeader);
            if (res == null) return false;
            using var dst = File.Create(Path.Combine(Path.GetDirectoryName(fxaa)!, BundledFxaaHeader));
            res.CopyTo(dst);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }      // best effort; MissingIncludes then reports it
    }

    private static bool HasAnyEffect() =>
        Directory.Exists(ShadersDir) && Directory.EnumerateFiles(ShadersDir, "*.fx", SearchOption.AllDirectories).Any();

    private static IEnumerable<string> PresetEffectNames(string presetName)
    {
        var p = Path.Combine(PresetsDir, Path.GetFileName(presetName));
        if (!File.Exists(p)) yield break;
        var m = Regex.Match(File.ReadAllText(p), @"^\s*Techniques\s*=(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!m.Success) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int at = t.LastIndexOf('@');
            if (at < 0) continue;
            var fx = t[(at + 1)..];
            if (seen.Add(fx)) yield return fx;
        }
    }

    /// <summary>
    /// #include "..." files that the preset's effects (and the headers those include) need but the shader store does not have.
    /// Effects that are themselves missing are not looked at here; MissingEffects() reports those.
    /// </summary>
    public static List<string> MissingIncludes(string presetName)
    {
        var missing = new List<string>();
        if (!Directory.Exists(ShadersDir)) return missing;

        EnsureBundledHeaders();      // a header we ship ourselves is never reported as missing

        var files = Directory.EnumerateFiles(ShadersDir, "*", SearchOption.AllDirectories).ToList();
        var relToFull = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files) relToFull[Path.GetRelativePath(ShadersDir, f).Replace('\\', '/')] = f;

        string? Resolve(string fromDir, string inc)
        {
            inc = inc.Replace('\\', '/');
            try
            {
                var local = Path.GetFullPath(Path.Combine(fromDir, inc));
                if (File.Exists(local)) return local;
            }
            catch { /* odd characters in the include: fall through to the store lookup */ }
            if (relToFull.TryGetValue(inc, out var exact)) return exact;
            foreach (var kv in relToFull)
                if (kv.Key.EndsWith("/" + inc, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        var wanted = new HashSet<string>(PresetEffectNames(presetName), StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(files.Where(f => wanted.Contains(Path.GetFileName(f))));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rx = new Regex("^[ \\t]*#[ \\t]*include[ \\t]+\"([^\"]+)\"", RegexOptions.Multiline);

        while (queue.Count > 0)
        {
            var file = queue.Dequeue();
            if (!visited.Add(file)) continue;
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            foreach (Match m in rx.Matches(text))
            {
                var inc = m.Groups[1].Value.Replace('\\', '/');
                var found = Resolve(Path.GetDirectoryName(file)!, inc);
                if (found == null)
                {
                    if (!missing.Contains(inc, StringComparer.OrdinalIgnoreCase)) missing.Add(inc);
                }
                else
                {
                    var ext = Path.GetExtension(found).ToLowerInvariant();
                    if (ext == ".fxh" || ext == ".fx" || ext == ".h") queue.Enqueue(found);
                }
            }
        }
        return missing;
    }

    /// <summary>
    /// Works out everything that has to be downloaded so ReShade and <paramref name="preset"/> work: the ReShade runtime and V-Patch's
    /// ASI loader (only when <paramref name="withRuntime"/>), and the shader packs for every effect file and header the preset needs.
    /// <paramref name="tried"/> holds pack ids that were already downloaded this round; a file still missing from one of those is
    /// reported as not downloadable instead of being asked for again.
    /// </summary>
    public static Readiness PlanReadiness(GameInstall g, string? preset, bool withRuntime, ISet<string>? tried = null)
    {
        var r = new Readiness();
        tried ??= new HashSet<string>();

        if (withRuntime)
        {
            if (!File.Exists(g.LoaderDll))
                r.Downloads.Add(new Need
                {
                    Kind = NeedKind.Loader,
                    Title = "V-Patch ASI loader (dinput8.dll)",
                    Detail = "github.com/VaanaCZ/LANVP, © VaanaCZ, CC BY-NC 4.0. Installs V-Patch into the game folder; ReShade loads through it.",
                });
            if (!File.Exists(CachedDll(g)))
                r.Downloads.Add(new Need { Kind = NeedKind.Runtime, Title = "ReShade runtime (with add-on support)", Detail = "reshade.me, © crosire, BSD 3-Clause" });
            else if (!RuntimeHasAddon())
                r.Downloads.Add(new Need
                {
                    Kind = NeedKind.Runtime,
                    Title = "ReShade runtime (with add-on support)",
                    Detail = "reshade.me, © crosire, BSD 3-Clause. Replaces the cached ReShade, which is not known to be the add-on build.",
                });
        }

        var packNeeds = new Dictionary<string, Need>();
        var cannot = new Dictionary<string, List<string>>();

        void Cannot(string reason, string file)
        {
            if (!cannot.TryGetValue(reason, out var l)) cannot[reason] = l = new List<string>();
            if (!l.Contains(file, StringComparer.OrdinalIgnoreCase)) l.Add(file);
        }

        void Want(string file)
        {
            var chain = PacksFor(file);
            if (chain == null) { Cannot(RtgiReason, file); return; }
            var pack = chain.FirstOrDefault(c => !tried.Contains(c.Id));
            if (pack == null)
            {
                Cannot($"not found in {string.Join(" or ", chain.Select(c => c.Title))} (renamed or removed upstream, or part of a paid pack). Add it with Add from folder / zip", file);
                return;
            }
            if (!packNeeds.TryGetValue(pack.Id, out var n))
                packNeeds[pack.Id] = n = new Need { Kind = NeedKind.Pack, Title = pack.Title, Detail = pack.Source + "; " + pack.Licence, Pack = pack };
            if (!n.Files.Contains(file, StringComparer.OrdinalIgnoreCase)) n.Files.Add(file);
        }

        if (!string.IsNullOrEmpty(preset))
        {
            foreach (var fx in MissingEffects(preset)) Want(fx);
            foreach (var inc in MissingIncludes(preset)) Want(inc);
        }
        else if (withRuntime && !HasAnyEffect())
        {
            Want("ReShade.fxh");      // an empty shader store: start with ReShade's standard effects
        }

        foreach (var p in Packs)
            if (packNeeds.TryGetValue(p.Id, out var n)) r.Downloads.Add(n);
        foreach (var kv in cannot) r.Cannot.Add((kv.Key, kv.Value));
        return r;
    }

    // ---------- downloads ----------

    /// <summary>Downloads a pack's zip and adds it to the shader store. Effects that already exist anywhere in the store are left alone.</summary>
    public static async Task<ImportResult> InstallPackAsync(ShaderPack pack, Action<string>? progress = null)
    {
        using var http = NewHttp();
        var tmp = Path.Combine(Path.GetTempPath(), "reshade-pack_" + pack.Id + "_" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            progress?.Invoke($"Downloading {pack.Title}…");
            byte[]? data = null;
            Exception? last = null;
            for (int attempt = 1; attempt <= 3 && data == null; attempt++)
            {
                try { data = await http.GetByteArrayAsync(pack.Url); }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { last = ex; break; }
                catch (Exception ex) { last = ex; if (attempt < 3) await Task.Delay(1500 * attempt); }
            }
            if (data == null)
                throw new InvalidOperationException($"Could not download {pack.Title} from {pack.Source}: {last?.Message}");

            await File.WriteAllBytesAsync(tmp, data);
            progress?.Invoke($"Installing {pack.Title}…");
            return await Task.Run(() => ImportShaders(tmp, keepExisting: true));
        }
        finally { try { File.Delete(tmp); } catch { /* temp file, the OS cleans it up eventually */ } }
    }

    /// <summary>Downloads the newest ReShade setup with add-on support, takes the dlls out of it and caches them. Returns the version.</summary>
    public static async Task<string> DownloadAndInstallRuntimeAsync()
    {
        var (file, ver) = await DownloadAsync();
        try { return InstallRuntime(file, ver, addon: true); }
        finally { try { File.Delete(file); } catch { } }
    }
}
