using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace LANoireLauncher.Core;

/// <summary>A newer launcher release found on GitHub.</summary>
public sealed record UpdateInfo(
    Version Version, string Tag, string Notes, string PageUrl,
    string AssetName, string AssetUrl, long AssetSize, string? Sha256, string? Sha256Url, bool Prerelease);

/// <summary>
/// Self-update from GitHub Releases. A release needs a tag like v1.2.3 and a .exe asset; a SHA-256 is taken from GitHub's
/// own asset digest or from a "&lt;asset&gt;.sha256" asset and checked before anything is replaced.
/// A running exe can be renamed but not overwritten on Windows, so the swap is: current -> .old, new -> current, start, exit.
/// </summary>
public static class UpdateManager
{
    public const string Repo = "pyr097/lanoirepcadvancedlauncher";
    public static string RepoUrl => "https://github.com/" + Repo;

    public static Version Current { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));
    public static string CurrentText => Format(Current);
    public static string Format(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

    public static string ExePath => Environment.ProcessPath ?? "";
    private static string OldPath => ExePath + ".old";
    private static string UpdateDir => Path.Combine(LauncherConfig.Dir, "update");

    /// <summary>1.2 / 1.2.3 / 1.2.3.0 all compare as major.minor.build (a missing part is -1, which would sort below 0).</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>"v1.2.3", "1.2.3-beta.1" and "v1.2" all parse; anything else is not a release tag.</summary>
    public static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var t = tag.Trim().TrimStart('v', 'V');
        var cut = t.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) t = t[..cut];
        if (!t.Contains('.')) t += ".0";
        if (!Version.TryParse(t, out var v)) return false;
        version = Normalize(v);
        return true;
    }

    private static HttpClient NewHttp(TimeSpan timeout)
    {
        var h = new HttpClient { Timeout = timeout };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("LANoireLauncher/" + CurrentText);
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    /// <summary>The newest published release (null when the repository has none yet). Drafts are never returned.</summary>
    public static async Task<UpdateInfo?> CheckAsync(bool includePrereleases, CancellationToken ct = default)
    {
        using var http = NewHttp(TimeSpan.FromSeconds(20));
        var url = $"https://api.github.com/repos/{Repo}/releases" + (includePrereleases ? "?per_page=15" : "/latest");
        using var resp = await http.GetAsync(url, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;       // no releases yet
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        UpdateInfo? best = null;
        IEnumerable<JsonElement> releases = includePrereleases ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
        foreach (var rel in releases)
        {
            if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            var info = Parse(rel);
            if (info != null && (best == null || info.Version > best.Version)) best = info;
        }
        return best;
    }

    private static UpdateInfo? Parse(JsonElement rel)
    {
        var tag = rel.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseTag(tag, out var ver)) return null;

        string? name = null, assetUrl = null, sha = null, shaUrl = null;
        long size = 0;
        if (rel.TryGetProperty("assets", out var assets))
        {
            var list = assets.EnumerateArray().ToList();
            // the launcher exe: prefer a name that looks like ours, else the first .exe
            var exe = list.FirstOrDefault(a => IsExe(a) && Str(a, "name").Contains("LANoire", StringComparison.OrdinalIgnoreCase));
            if (exe.ValueKind == JsonValueKind.Undefined) exe = list.FirstOrDefault(IsExe);
            if (exe.ValueKind != JsonValueKind.Undefined)
            {
                name = Str(exe, "name");
                assetUrl = Str(exe, "browser_download_url");
                size = exe.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                var digest = Str(exe, "digest");                                         // "sha256:<hex>" on current GitHub
                if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = digest[7..].ToLowerInvariant();
                var side = list.FirstOrDefault(a => Str(a, "name").Equals(name + ".sha256", StringComparison.OrdinalIgnoreCase));
                if (side.ValueKind != JsonValueKind.Undefined) shaUrl = Str(side, "browser_download_url");
            }
        }
        if (name == null || string.IsNullOrEmpty(assetUrl)) return null;                  // a release without an exe is not installable

        return new UpdateInfo(ver, tag, Str(rel, "body"), Str(rel, "html_url"), name, assetUrl, size, sha, shaUrl,
            rel.TryGetProperty("prerelease", out var p) && p.GetBoolean());

        static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static bool IsExe(JsonElement a) => Str(a, "name").EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Downloads to %AppData%\LANoireLauncher\update, checks size, "MZ" header and SHA-256, and returns the file path.</summary>
    public static async Task<(string Path, bool HashVerified)> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(UpdateDir);
        var target = Path.Combine(UpdateDir, $"LANoireLauncher-{info.Tag}.exe");
        var part = target + ".part";

        using var http = NewHttp(TimeSpan.FromMinutes(10));
        var expected = info.Sha256;
        if (expected == null && info.Sha256Url != null)
        {
            var text = await http.GetStringAsync(info.Sha256Url, ct);
            expected = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total, done = 0;
        using (var resp = await http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            total = resp.Content.Headers.ContentLength ?? info.AssetSize;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buf = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                hash.AppendData(buf, 0, n);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        try
        {
            if (total > 0 && done != total) throw new InvalidDataException($"Download incomplete ({done} of {total} bytes).");
            if (done < 1_000_000 || !HasMzHeader(part)) throw new InvalidDataException("The downloaded file is not a Windows program.");
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (expected != null && !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Checksum mismatch: the download does not match the published SHA-256. Nothing was changed.");
            File.Move(part, target, true);
            return (target, expected != null);
        }
        catch
        {
            try { File.Delete(part); } catch (IOException) { }
            throw;
        }
    }

    private static bool HasMzHeader(string file)
    {
        using var f = File.OpenRead(file);
        return f.ReadByte() == 'M' && f.ReadByte() == 'Z';
    }

    /// <summary>False when the launcher sits somewhere it cannot write (e.g. Program Files without admin rights).</summary>
    public static bool CanReplaceSelf()
    {
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(ExePath)!, ".update-probe-" + Environment.ProcessId);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Swaps the exe and starts the new one. Rolls back if anything fails. The caller must shut the app down when this returns.
    /// </summary>
    public static void ApplyAndRestart(string newExe)
    {
        var cur = ExePath;
        var old = OldPath;
        if (File.Exists(old)) File.Delete(old);
        File.Move(cur, old);                                    // a running exe can be renamed
        try { File.Copy(newExe, cur, true); }
        catch
        {
            try { if (File.Exists(cur)) File.Delete(cur); } catch (IOException) { }
            File.Move(old, cur);                                // roll back: the old launcher stays in place
            throw;
        }
        try { File.Delete(newExe); } catch (IOException) { }

        var psi = new ProcessStartInfo(cur, "--updated") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(cur)! };
        if (IsAdministrator()) psi.Verb = "runas";              // an elevated launcher comes back elevated (FPS overlay, no extra prompt text)
        try { Process.Start(psi); }
        catch (Win32Exception) when (psi.Verb == "runas")       // UAC declined: start it normally
        {
            psi.Verb = "";
            Process.Start(psi);
        }
    }

    /// <summary>Deletes the previous exe and downloaded files after a restart (retries while the old process is still exiting).</summary>
    public static void CleanupAfterUpdate()
    {
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 6; i++)
            {
                try
                {
                    if (File.Exists(OldPath)) File.Delete(OldPath);
                    if (Directory.Exists(UpdateDir)) Directory.Delete(UpdateDir, true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(2000); }
            }
        });
    }

    private static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
