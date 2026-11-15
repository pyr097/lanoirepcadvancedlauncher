using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using LANoireLauncher.Core;

namespace LANoireLauncher;

// Enhancements tab, HD textures: builds the texture pack from the user's own PS4 files and manages the D3D11 plugin.
public partial class MainWindow
{
    private CancellationTokenSource? _hdCts;
    private bool _hdBusy, _hdClosedHooked;

    private string HdPackDir => _game == null ? "" :
        string.IsNullOrWhiteSpace(_cfg.HdPackPath) ? HdTextureManager.DefaultPackDir(_game) : _cfg.HdPackPath!;

    private (string Key, CheckBox Box)[] HdArchiveBoxes() => new (string Key, CheckBox Box)[]
    {
        ("vehicles", HdArcVehicles), ("props", HdArcProps), ("props.high", HdArcPropsHigh),
        ("characters", HdArcCharacters), ("reftextures_characters", HdArcRefChars), ("reftextures_populated", HdArcRefPop),
    };

    private void HdLog(string line)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => HdLog(line))); return; }
        HdLogBox.AppendText(line + Environment.NewLine);
        if (HdLogBox.Text.Length > 300000) HdLogBox.Text = HdLogBox.Text[^100000..];
        HdLogBox.ScrollToEnd();
    }

    private void RefreshHd()
    {
        if (_game == null)
        {
            HdStatusText.Text = "Select the game folder first.";
            HdPluginText.Text = "";
            foreach (var b in new[] { HdBuildBtn, HdExplainBtn, HdDeleteBtn, HdInstallBtn, HdRemoveBtn, HdApplyBtn, HdOpenLogBtn }) b.IsEnabled = false;
            return;
        }
        var pack = HdTextureManager.GetPackStatus(HdPackDir);
        var plug = HdTextureManager.GetPluginStatus(_game);
        HdStatusText.Text = "Pack: " + pack.Label + "   ·   Plugin: " +
                            (!plug.Installed ? "not installed" : plug.Enabled ? "installed, on" : "installed, off");
        HdPs4Box.Text = string.IsNullOrEmpty(_cfg.Ps4Path) ? "(not selected)" : _cfg.Ps4Path;

        string main;
        if (_game.Is64Bit) main = "This game executable is 64-bit, so the 32-bit plugin cannot load into it.";
        else if (!plug.LoaderPresent) main = "The plugin is started by V-Patch's ASI loader (dinput8.dll). Install V-Patch first (V-Patch tab).";
        else if (plug.Installed && !plug.Valid)
            main = "⚠ plugins\\LanTexReplace.asi is NOT the HD texture plugin (a different .asi was copied under that name, which can make V-Patch fail). Remove it and install again.";
        else if (!plug.Installed && !HdTextureManager.HasEmbeddedPlugin)
            main = "This launcher build does not include the plugin. \"Install\" will ask for the LanTexReplace.asi you built (Plugin\\build_msvc.bat).";
        else main = plug.Installed ? "Installed in plugins\\LanTexReplace.asi." : "Not installed yet.";

        var scan = HdTextureManager.ScanAsi(_game);
        var extra = new List<string>
        {
            scan.Names.Count == 0 ? "No .asi plugins found." : "ASI plugins the loader starts: " + string.Join(", ", scan.Names) + ".",
        };
        foreach (var w in scan.Warnings) extra.Add("⚠ " + w);
        var logPath = Path.Combine(_game.PluginsDir, "LanTexReplace.log");
        if (plug.Installed && plug.Log)
            extra.Add(File.Exists(logPath)
                ? "Log: plugins\\LanTexReplace.log (last written " + File.GetLastWriteTime(logPath).ToString("g") + ")."
                : "Log: not written yet. It is created each time the game starts.");
        HdPluginText.Text = main + "\n" + string.Join("\n", extra);
        HdOpenLogBtn.IsEnabled = File.Exists(logPath);

        HdEnableBox.IsChecked = plug.Enabled;
        HdDiagBox.IsChecked = plug.Log;
        HdBudgetBox.Text = plug.MaxHdMb.ToString(CultureInfo.InvariantCulture);

        HdBuildBtn.IsEnabled = !_hdBusy;
        HdExplainBtn.IsEnabled = !_hdBusy;
        HdCancelBtn.IsEnabled = _hdBusy;
        HdDeleteBtn.IsEnabled = pack.Exists && !_hdBusy;
        HdInstallBtn.IsEnabled = !_hdBusy;
        HdRemoveBtn.IsEnabled = plug.Installed;
        HdApplyBtn.IsEnabled = plug.Installed;
        HdPs4BrowseBtn.IsEnabled = !_hdBusy;

        // archives that do not exist in the chosen PS4 folder are greyed out
        bool haveFolder = !string.IsNullOrEmpty(_cfg.Ps4Path) && Directory.Exists(_cfg.Ps4Path);
        foreach (var (key, box) in HdArchiveBoxes())
            box.IsEnabled = !_hdBusy && (!haveFolder || HdTextureManager.Ps4HasArchive(_cfg.Ps4Path!, key));
    }

    // ---------- step 1: PS4 folder
    private void HdBrowsePs4_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the folder with your PS4 files (the one holding the .big.ps4 files, or a parent of it)" };
        if (dlg.ShowDialog() != true) return;
        var dir = HdTextureManager.ResolvePs4Dir(dlg.FolderName);
        if (dir == null) { Status("No .big.ps4 files found there (also looked in \\ps4 and \\final\\ps4)."); return; }
        _cfg.Ps4Path = dir;
        _cfg.Save();
        RefreshHd();
        Status("PS4 folder set.");
    }

    // ---------- step 2: build the pack
    private void HdHookClose()
    {
        if (_hdClosedHooked) return;
        _hdClosedHooked = true;
        Closed += (_, _) => { try { _hdCts?.Cancel(); } catch { /* already finished */ } };
    }

    private void HdCancel_Click(object sender, RoutedEventArgs e)
    {
        try { _hdCts?.Cancel(); } catch { /* already finished */ }
    }

    private async void HdBuild_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame() || _hdBusy) return;
        var g = _game!;
        if (string.IsNullOrEmpty(_cfg.Ps4Path) || !Directory.Exists(_cfg.Ps4Path)) { Status("Choose your PS4 folder first (step 1)."); return; }
        if (!Directory.Exists(Path.Combine(g.Root, "final", "pc"))) { Status("final\\pc was not found in the game folder."); return; }

        var archives = new List<string>();
        if (HdArcAll.IsChecked != true)
        {
            foreach (var (key, box) in HdArchiveBoxes()) if (box.IsChecked == true && box.IsEnabled) archives.Add(key);
            if (archives.Count == 0) { Status("Tick at least one archive, or \"All archives\"."); return; }
        }
        if (!double.TryParse(HdMinCorrBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var corr) || corr < 0.5 || corr > 0.99)
        { Status("The match threshold must be a number between 0.5 and 0.99 (default 0.85, use a dot)."); return; }

        var packDir = HdPackDir;
        bool fresh = HdFreshBox.IsChecked == true;
        if (fresh && HdTextureManager.GetPackStatus(packDir).Exists)
        {
            if (MessageBox.Show("Delete the existing pack and start a new one?", "New pack", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try { HdTextureManager.DeletePack(packDir, g); }
            catch (Exception ex) { Status("HD textures: " + ex.Message); return; }
        }

        _hdBusy = true;
        _hdCts = new CancellationTokenSource();
        HdHookClose();
        HdLogBox.Clear();
        RefreshHd();
        var ct = _hdCts.Token;
        try
        {
            var py = await HdGetPythonAsync(ct);
            if (py == null) return;
            int code = await HdTextureManager.BuildAsync(py, g, _cfg.Ps4Path!, packDir, archives, corr, false, HdLog, ct);
            var st = HdTextureManager.GetPackStatus(packDir);
            if (code == 0)
                Status($"Pack ready: {st.Label}. " + (HdTextureManager.GetPluginStatus(g).Installed
                    ? "Make sure \"Enable HD textures\" is ticked (step 3), then start the game."
                    : "Next: install the plugin (step 3)."));
            else if (code == -2) Status("Build cancelled. What was written so far is kept; run it again to continue.");
            else Status($"Build failed (exit code {code}). See the log in the HD textures box.");
        }
        catch (Exception ex)
        {
            HdLog("Error: " + ex.Message);
            Status("HD textures: " + ex.Message);
        }
        finally
        {
            _hdBusy = false;
            _hdCts?.Dispose();
            _hdCts = null;
            RefreshHd();
        }
    }

    /// <summary>Finds 64-bit Python with numpy (offering to install numpy). Logs and reports problems; returns null on failure.</summary>
    private async Task<PythonInfo?> HdGetPythonAsync(CancellationToken ct)
    {
        HdLog("Looking for Python…");
        var chk = await HdTextureManager.FindPythonAsync();
        var py = chk.Info;
        if (py == null && chk.NeedsNumpy != null)
        {
            if (MessageBox.Show("The tool needs numpy for Python (a free package, about 20 MB). Install it now with pip?",
                    "numpy", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                HdLog("Installing numpy…");
                int pip = await HdTextureManager.InstallNumpyAsync(chk.NeedsNumpy, HdLog, ct);
                if (pip == 0) { chk = await HdTextureManager.FindPythonAsync(); py = chk.Info; }
                else HdLog("pip failed (exit code " + pip + ").");
            }
        }
        if (py == null)
        {
            var why = string.IsNullOrEmpty(chk.Problem) ? "Python with numpy is required." : chk.Problem;
            HdLog(why);
            Status("HD textures: " + why);
            return null;
        }
        HdLog($"Using Python {py.Version}.");
        return py;
    }

    private async void HdExplain_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame() || _hdBusy) return;
        var g = _game!;
        var logPath = Path.Combine(g.PluginsDir, "LanTexReplace.log");
        if (!File.Exists(logPath)) { Status("No plugin log yet. Turn on the diagnostic log, play a few minutes, close the game, then try again."); return; }
        string? ps4 = !string.IsNullOrEmpty(_cfg.Ps4Path) && Directory.Exists(_cfg.Ps4Path) ? _cfg.Ps4Path : null;
        if (ps4 == null) { Status("Choose your PS4 folder first (step 1): the analysis compares with the PS4 textures."); return; }
        var archives = new List<string>();
        if (HdArcAll.IsChecked != true)
            foreach (var (key, box) in HdArchiveBoxes()) if (box.IsChecked == true && box.IsEnabled) archives.Add(key);

        _hdBusy = true;
        _hdCts = new CancellationTokenSource();
        HdHookClose();
        HdLogBox.Clear();
        RefreshHd();
        var ct = _hdCts.Token;
        try
        {
            var py = await HdGetPythonAsync(ct);
            if (py == null) return;
            HdLog(archives.Count == 0 ? "Analysing against all archives (slow)…" : "Analysing against: " + string.Join(", ", archives));
            int code = await HdTextureManager.ExplainAsync(py, g, ps4, logPath, archives, HdLog, ct);
            Status(code == 0 ? "Coverage analysis finished. See the table in the log box." :
                   code == -2 ? "Analysis cancelled." : $"Analysis failed (exit code {code}). See the log box.");
        }
        catch (Exception ex) { HdLog("Error: " + ex.Message); Status("HD textures: " + ex.Message); }
        finally { _hdBusy = false; _hdCts?.Dispose(); _hdCts = null; RefreshHd(); }
    }

    private void HdDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame() || _hdBusy) return;
        var st = HdTextureManager.GetPackStatus(HdPackDir);
        if (MessageBox.Show($"Delete the HD texture pack ({st.Label})?\n\nYou can build it again from your PS4 files.", "Delete pack",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { HdTextureManager.DeletePack(HdPackDir, _game!); RefreshHd(); Status("Pack deleted."); }
        catch (Exception ex) { Status("HD textures: " + ex.Message); }
    }

    // ---------- step 3: plugin
    private bool HdWriteSettings(out string error)
    {
        error = "";
        if (!int.TryParse(HdBudgetBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) || mb < 64 || mb > 16384)
        { error = "The HD memory limit must be a whole number between 64 and 16384 (MB)."; return false; }
        HdTextureManager.WriteSettings(_game!, HdEnableBox.IsChecked == true, mb, HdDiagBox.IsChecked == true, HdPackDir);
        return true;
    }

    private void HdInstall_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame()) return;
        if (GameInstall.IsRunning()) { Status("Close L.A. Noire first."); return; }
        var g = _game!;
        if (g.Is64Bit) { Status("This game executable is 64-bit; the 32-bit HD texture plugin cannot load into it."); return; }
        if (!File.Exists(g.LoaderDll)) { Status("The plugin is started by V-Patch's ASI loader. Install V-Patch first (V-Patch tab)."); return; }
        string? src = null;
        if (!HdTextureManager.HasEmbeddedPlugin)
        {
            var dlg = new OpenFileDialog { Title = "Select the LanTexReplace.asi you built (32-bit)", Filter = "ASI plugin (*.asi)|*.asi" };
            if (dlg.ShowDialog() != true) return;
            src = dlg.FileName;
        }
        try
        {
            HdTextureManager.InstallPlugin(g, src);
            if (!HdWriteSettings(out var err)) { RefreshHd(); Status("Plugin installed. " + err); return; }
            RefreshHd();
            Status("HD texture plugin installed. Build a pack (step 2) if you have not yet, then start the game.");
        }
        catch (Exception ex) { Status("HD textures: " + ex.Message); }
    }

    private void HdRemove_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame()) return;
        if (GameInstall.IsRunning()) { Status("Close L.A. Noire first."); return; }
        if (MessageBox.Show("Remove the HD texture plugin from the game's plugins folder? Your pack is kept.", "Remove plugin",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { HdTextureManager.UninstallPlugin(_game!); RefreshHd(); Status("HD texture plugin removed."); }
        catch (Exception ex) { Status("HD textures: " + ex.Message); }
    }

    private void HdApply_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame()) return;
        if (GameInstall.IsRunning()) { Status("Close L.A. Noire first."); return; }
        try
        {
            if (!HdWriteSettings(out var err)) { Status(err); return; }
            RefreshHd();
            Status(HdEnableBox.IsChecked == true ? "Texture settings saved: HD textures are on." : "Texture settings saved: HD textures are off.");
        }
        catch (Exception ex) { Status("HD textures: " + ex.Message); }
    }

    /// <summary>Saves the texture settings as soon as the user changes them (no separate Apply needed).</summary>
    private void HdSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_game == null || !HdTextureManager.GetPluginStatus(_game).Installed) return;
        if (GameInstall.IsRunning()) { Status("Close L.A. Noire first: the texture settings were not saved."); return; }
        try
        {
            if (!HdWriteSettings(out var err)) { Status(err); return; }
            Status("Texture settings saved.");
        }
        catch (Exception ex) { Status("HD textures: " + ex.Message); }
    }

    /// <summary>Called from Apply / Apply and Launch so the texture settings are never left unsaved. Returns a short note or null.</summary>
    private string? HdSaveSettingsQuiet()
    {
        if (_game == null) return null;
        var st = HdTextureManager.GetPluginStatus(_game);
        if (!st.Installed || !st.Valid) return null;
        if (!HdWriteSettings(out var err)) return "(" + err + ")";
        return "HD textures: " + (HdEnableBox.IsChecked == true ? "on" : "off") + (HdDiagBox.IsChecked == true ? ", log on." : ".");
    }

    private void HdOpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (_game == null) return;
        var path = Path.Combine(_game.PluginsDir, "LanTexReplace.log");
        if (!File.Exists(path)) { Status("No log yet. Turn on the diagnostic log and start the game once."); return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Status("Could not open the log: " + ex.Message); }
    }
}
