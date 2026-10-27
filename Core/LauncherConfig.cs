using System.Text.Json;

namespace LANoireLauncher.Core;

public sealed class Profile
{
    public string Name { get; set; } = "";
    public Dictionary<string, string> Patch { get; set; } = new();
    public Dictionary<string, string> Graphics { get; set; } = new();
    public string ResW { get; set; } = "";
    public string ResH { get; set; } = "";
    public Dictionary<string, string> Dxvk { get; set; } = new();
    public Dictionary<string, string> ReShade { get; set; } = new();
    public bool IgnoreFocus { get; set; }
    public string ExtraArgs { get; set; } = "";
}

/// <summary>Launcher-owned data. Lives in %AppData%, never inside the game folder.</summary>
public sealed class LauncherConfig
{
    public string? GamePath { get; set; }
    public string? SettingsPath { get; set; }
    public bool OverlayEnabled { get; set; }
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public bool IncludePrereleases { get; set; }
    public string? SkippedVersion { get; set; }
    public List<Profile> Profiles { get; set; } = new();

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LANoireLauncher");
    private static string FilePath => Path.Combine(Dir, "config.json");

    public static LauncherConfig Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(FilePath)) ?? new(); }
        catch { /* corrupt config -> start fresh */ }
        return new LauncherConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string OriginalPath(string file)
    {
        var id = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(file).ToLowerInvariant())))[..12];
        return Path.Combine(Dir, "original", id + "-" + Path.GetFileName(file));
    }

    /// <summary>Keeps the very first version of a file, once, so it can be put back exactly ("Restore to stock").</summary>
    public static void SaveOriginal(string file)
    {
        var d = OriginalPath(file);
        if (!File.Exists(file) || File.Exists(d)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(d)!);
        File.Copy(file, d);
    }

    public static bool RestoreOriginal(string file)
    {
        var s = OriginalPath(file);
        if (!File.Exists(s)) return false;
        File.Copy(s, file, true);
        return true;
    }

    /// <summary>Copies a file into a timestamped backup folder before the launcher modifies it.</summary>
    public static void Backup(string file)
    {
        if (!File.Exists(file)) return;
        var dir = Path.Combine(Dir, "backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), true);
    }
}
