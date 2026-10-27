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
                "This undoes everything the launcher changed in the game folder:\n\n" +
                "•  ReShade, DXVK and V-Patch removed\n" +
                "•  Enhancements reverted\n" +
                "•  settings.ini restored to its first version\n\n" +
                "Files you had before (such as dinput8.dll) are put back. Your shaders, presets, profiles and backups are kept.\n\nContinue?",
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
