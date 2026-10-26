using System.Diagnostics;
using System.Security.Cryptography;

namespace LANoireLauncher.Core;

/// <summary>Describes the user's own L.A. Noire installation. Nothing here copies or modifies game files.</summary>
public sealed class GameInstall
{
    // Builds V-Patch 2.0 supports (per its README).
    public static readonly int[] SupportedBuilds = { 2617, 2663, 2675 };

    public string Root { get; }
    public int Build { get; private set; }
    public string FileVersion { get; private set; } = "";
    public bool IsSupported => SupportedBuilds.Contains(Build);
    /// <summary>Read from the exe's PE header, so DXVK gets the matching x32/x64 DLLs instead of an assumption.</summary>
    public bool Is64Bit { get; private set; }

    public string ExePath => Path.Combine(Root, "LaNoire.exe");
    public string PluginsDir => Path.Combine(Root, "plugins");
    public string LanvpIni => Path.Combine(PluginsDir, "lanvp.ini");
    public string LanvpAsi => Path.Combine(PluginsDir, "lanvp.asi");
    public string LoaderDll => Path.Combine(Root, "dinput8.dll");

    private GameInstall(string root) { Root = root; }

    public static GameInstall? Open(string path, out string error)
    {
        error = "";
        if (File.Exists(path)) path = Path.GetDirectoryName(path)!;
        var exe = Path.Combine(path, "LaNoire.exe");
        if (!File.Exists(exe)) { error = "LaNoire.exe was not found in that folder."; return null; }
        var info = FileVersionInfo.GetVersionInfo(exe);
        return new GameInstall(path) { Build = info.FileMajorPart, FileVersion = info.FileVersion ?? "", Is64Bit = IsPe64(exe) };
    }

    private static bool IsPe64(string exe)
    {
        try
        {
            using var fs = File.OpenRead(exe);
            using var br = new BinaryReader(fs);
            fs.Position = 0x3C;
            fs.Position = br.ReadInt32();
            if (br.ReadUInt32() != 0x00004550) return false;   // "PE\0\0"
            return br.ReadUInt16() == 0x8664;                  // IMAGE_FILE_MACHINE_AMD64
        }
        catch { return false; }
    }

    public static string Sha256Of(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    public static bool IsRunning() => Process.GetProcessesByName("LaNoire").Length > 0;

    /// <summary>Best-effort guess at common install locations; the user can always browse.</summary>
    public static string? AutoDetect()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = new[]
        {
            Path.Combine(pf, "Rockstar Games", "L.A. Noire"),
            Path.Combine(pf86, "Rockstar Games", "L.A. Noire"),
            Path.Combine(pf86, "Steam", "steamapps", "common", "L.A. Noire"),
            Path.Combine(pf, "Steam", "steamapps", "common", "L.A. Noire"),
        };
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "LaNoire.exe")));
    }

    /// <summary>Where the game's own settings.ini might live. Verify on your machine.</summary>
    public IEnumerable<string> SettingsCandidates()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(Root, "settings.ini");
        yield return Path.Combine(docs, "Rockstar Games", "L.A. Noire", "settings.ini");
        yield return Path.Combine(local, "Rockstar Games", "L.A. Noire", "settings.ini");
        yield return Path.Combine(roam, "Rockstar Games", "L.A. Noire", "settings.ini");
    }
}
