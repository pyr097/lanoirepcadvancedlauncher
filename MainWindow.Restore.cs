using System.Windows;
using LANoireLauncher.Core;

namespace LANoireLauncher;

// "Restore everything to stock": undoes every change the launcher made to the game folder.
public partial class MainWindow
{
    private void RestoreStock_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame()) return;
        if (GameInstall.IsRunning()) { Status("Close L.A. Noire first."); return; }
        if (MessageBox.Show(
                "This puts the game folder back to how it was before this launcher:\n\n" +
                "•  ReShade removed (a ReShade.ini you had before is restored)\n" +
                "•  DXVK removed (a d3d11.dll / dxgi.dll you had before is restored)\n" +
                "•  Enhancements reverted and the out.wad.pc.orig copy deleted\n" +
                "•  V-Patch removed (a dinput8.dll you had before is restored)\n" +
                "•  settings.ini restored to its first version\n\n" +
                "Only files this launcher installed are touched. Your shaders, presets, profiles and backups in %AppData% are kept.\n\nContinue?",
                "Restore to stock", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var g = _game!;
        var failed = new List<string>();
        void Step(string name, Action a)
        {
            try { a(); }
            catch (Exception ex) { failed.Add(name + ": " + ex.Message); }
        }

        Step("ReShade", () => ReShadeManager.Uninstall(g));                       // first: it loads through V-Patch's loader
        Step("DXVK", () => DxvkManager.Uninstall(g));
        Step("Enhancements", () =>
        {
            var wad = AtbPatcher.WadPath(g);
            if (!File.Exists(wad)) return;
            AtbPatcher.Revert(wad);
            AtbPatcher.RemoveBackupCopy(wad);
        });
        Step("V-Patch", () => { if (PatchManager.InstalledByLauncher(g)) PatchManager.Uninstall(g); });
        Step("settings.ini", () =>
        {
            if (!string.IsNullOrEmpty(_cfg.SettingsPath) && File.Exists(_cfg.SettingsPath))
                LauncherConfig.RestoreOriginal(_cfg.SettingsPath);
        });

        DxvkEnableBox.IsChecked = false;
        ReShadeEnableBox.IsChecked = false;
        LoadFromDisk();
        Status(failed.Count == 0
            ? "Game folder restored to stock."
            : "Restored, except: " + string.Join("  |  ", failed));
    }
}
