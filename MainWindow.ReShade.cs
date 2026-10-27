using System.Diagnostics;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using LANoireLauncher.Core;

namespace LANoireLauncher;

// ReShade tab. Works the same with DXVK on or off: ReShade is loaded through V-Patch's ASI loader (see ReShadeManager).
public partial class MainWindow
{
    private const string KeepPreset = "(leave as is)";

    /// <summary>File name of the chosen preset, or "" for "leave as is".</summary>
    private string SelectedPreset => ReShadePresetBox.SelectedIndex > 0 ? (ReShadePresetBox.SelectedItem?.ToString() ?? "") : "";

    private void FillPresets(string? select)
    {
        ReShadePresetBox.Items.Clear();
        ReShadePresetBox.Items.Add(KeepPreset);
        foreach (var p in ReShadeManager.ListPresets()) ReShadePresetBox.Items.Add(p);
        int i = string.IsNullOrEmpty(select) ? -1 : ReShadePresetBox.Items.IndexOf(select);
        ReShadePresetBox.SelectedIndex = i > 0 ? i : 0;
    }

    private void RefreshReShade(bool syncEnable = true)
    {
        if (_game == null)
        {
            ReShadeStatusText.Text = "Select the game folder first.";
            return;
        }
        var st = ReShadeManager.GetStatus(_game);
        ReShadeStatusText.Text = st.Label;
        ShaderSummaryText.Text = st.ShaderSummary;
        if (syncEnable) ReShadeEnableBox.IsChecked = st.Installed;     // imports must not undo a tick that is not applied yet
        var keep = SelectedPreset;                       // keep an unsaved choice; on first load take the one ReShade.ini has
        FillPresets(keep.Length > 0 ? keep : ReShadeManager.ActivePreset(_game));
    }

    private void SelectPreset(string name)
    {
        int i = string.IsNullOrEmpty(name) ? -1 : ReShadePresetBox.Items.IndexOf(name);
        ReShadePresetBox.SelectedIndex = i > 0 ? i : 0;
    }

    /// <summary>Called from Apply(). Returns a short note for the status bar, or null if nothing was done.</summary>
    private string? ApplyReShade()
    {
        bool on = ReShadeEnableBox.IsChecked == true;
        var st = ReShadeManager.GetStatus(_game!);
        if (!on && !st.Installed) return null;           // never used: leave the game folder alone
        ReShadeManager.Apply(_game!, on, SelectedPreset);
        RefreshReShade();
        return on ? "ReShade on (Home opens its overlay in the game)." + MissingNote(SelectedPreset) : "ReShade off.";
    }

    /// <summary>Warning text for effect files a preset needs but the shader store does not have; "" if none.</summary>
    private static string MissingNote(string preset)
    {
        if (string.IsNullOrEmpty(preset)) return "";
        var miss = ReShadeManager.MissingEffects(preset);
        if (miss.Count == 0) return "";
        return $" ⚠ '{preset}' needs {miss.Count} effect file(s) that are not in the shader store: {string.Join(", ", miss.Take(6))}{(miss.Count > 6 ? ", …" : "")}. Add them with Add from folder / zip.";
    }

    private async void ReShadePresetBox_DropDownClosed(object sender, EventArgs e)
    {
        var preset = SelectedPreset;
        if (preset.Length == 0) return;
        await EnsureReShadeFilesAsync(preset, ReShadeEnableBox.IsChecked == true, false);
        var note = MissingNote(preset);
        if (note.Length > 0) Status(note.Trim());
    }

    private async void ReShadeEnable_Click(object sender, RoutedEventArgs e)
    {
        if (ReShadeEnableBox.IsChecked == true) await EnsureReShadeFilesAsync(SelectedPreset, true, false);
    }

    private async void ReShadeCheck_Click(object sender, RoutedEventArgs e) =>
        await EnsureReShadeFilesAsync(SelectedPreset, true, true);

    /// <summary>Called before Save Options / PLAY when ReShade is ticked. Never blocks: Apply() still reports anything that stays missing.</summary>
    private async Task PreflightReShadeAsync()
    {
        if (_game == null || ReShadeEnableBox.IsChecked != true || GameInstall.IsRunning()) return;
        await EnsureReShadeFilesAsync(SelectedPreset, true, false);
    }

    // ---------- missing files: offer to download everything needed ----------
    private readonly HashSet<string> _declinedPlans = new();
    private bool _fetching;

    private static string FileList(List<string> files, int max = 6) =>
        string.Join(", ", files.Take(max)) + (files.Count > max ? $", … (+{files.Count - max} more)" : "");

    /// <summary>
    /// Looks at what ReShade and the preset need (runtime, V-Patch's ASI loader, every effect file and header the preset names) and,
    /// if anything is missing, offers to download all of it in one go. <paramref name="explicitAsk"/> = the person pressed the button, so
    /// the answer is always shown; otherwise a plan that was already declined this session is not offered again.
    /// Returns true when nothing is missing afterwards.
    /// </summary>
    private async Task<bool> EnsureReShadeFilesAsync(string preset, bool withRuntime, bool explicitAsk)
    {
        if (_game == null) { if (explicitAsk) Status("Select the game folder first."); return false; }
        if (_fetching) return false;

        var plan = ReShadeManager.PlanReadiness(_game, preset, withRuntime);
        if (plan.AllGood)
        {
            if (explicitAsk)
                Status(preset.Length > 0 ? $"Nothing is missing: ReShade and everything '{preset}' needs are in place."
                                         : "Nothing is missing for ReShade. Pick a preset to check its effect files too.");
            return true;
        }
        if (!explicitAsk && _declinedPlans.Contains(plan.Signature)) return false;

        var sb = new StringBuilder();
        if (plan.Downloads.Count > 0)
        {
            sb.AppendLine(preset.Length > 0 ? $"ReShade and the preset '{preset}' need files that are not on this PC yet:" : "ReShade needs files that are not on this PC yet:");
            sb.AppendLine();
            foreach (var n in plan.Downloads)
            {
                sb.AppendLine("•  " + n.Title);
                sb.AppendLine("    " + n.Detail);
                if (n.Files.Count > 0) sb.AppendLine("    needed for: " + FileList(n.Files));
            }
        }
        if (plan.Cannot.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("The launcher cannot download these:");
            foreach (var (reason, files) in plan.Cannot) sb.AppendLine($"•  {FileList(files)}: {reason}");
        }

        if (plan.Downloads.Count == 0)
        {
            _declinedPlans.Add(plan.Signature);       // nothing to offer: say it once
            MessageBox.Show(sb.ToString().TrimEnd(), "Missing ReShade files", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        sb.AppendLine();
        sb.AppendLine("Download and install everything that can be downloaded? Shader files go to the launcher's own folder in %AppData%; " +
                      "only V-Patch's loader (if listed) is written to the game folder. Each download comes straight from its author.");
        if (MessageBox.Show(sb.ToString().TrimEnd(), "Download missing ReShade files", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            _declinedPlans.Add(plan.Signature);
            Status("Skipped. ReShade may not start, or effects in the preset will be skipped, until these files are added (Check & download missing files).");
            return false;
        }
        return await DownloadReadinessAsync(plan, preset, withRuntime);
    }

    private async Task<bool> DownloadReadinessAsync(Readiness plan, string preset, bool withRuntime)
    {
        _fetching = true;
        var tried = new HashSet<string>();
        var errors = new List<string>();
        var added = new ImportResult();
        bool keepTick = ReShadeEnableBox.IsChecked == true;
        try
        {
            foreach (var n in plan.Downloads)
            {
                try
                {
                    switch (n.Kind)
                    {
                        case NeedKind.Loader:
                            if (GameInstall.IsRunning()) { errors.Add(n.Title + ": close L.A. Noire first."); break; }
                            Status("Downloading V-Patch…");
                            var zip = await PatchManager.DownloadLatestAsync();
                            try { PatchManager.Install(_game!, zip, false); }
                            catch (LoaderConflictException) { errors.Add(n.Title + ": a different dinput8.dll is in the game folder. Use Install V-Patch on the Patch tab, which asks before replacing it."); }
                            finally { try { File.Delete(zip); } catch { } }
                            var ps = PatchManager.GetStatus(_game!);
                            PatchStatusText.Text = ps.Label + (ps.Usable && !ps.IniPresent ? "  —  run the game once to generate lanvp.ini" : "");
                            break;
                        case NeedKind.Runtime:
                            Status("Downloading ReShade…");
                            await ReShadeManager.DownloadAndInstallRuntimeAsync();
                            break;
                        case NeedKind.Pack:
                            tried.Add(n.Pack!.Id);
                            added.Add(await ReShadeManager.InstallPackAsync(n.Pack!, Status));
                            break;
                    }
                }
                catch (Exception ex) { errors.Add($"{n.Title}: {ex.Message}"); }
            }
        }
        finally { _fetching = false; }

        RefreshReShade(false);
        ReShadeEnableBox.IsChecked = keepTick;       // downloading must not change a tick that is not applied yet

        // Second pass: whatever the downloads did not cover is reported, not asked for again.
        var after = ReShadeManager.PlanReadiness(_game!, preset, withRuntime, tried);
        var leftover = new List<string>(errors);
        foreach (var n in after.Downloads.Where(d => d.Kind != NeedKind.Pack || !errors.Any(e => e.StartsWith(d.Title + ":"))))
            leftover.Add(n.Title + " is still missing" + (n.Files.Count > 0 ? " (" + FileList(n.Files, 4) + ")" : "") + ".");
        foreach (var (reason, files) in after.Cannot) leftover.Add($"{FileList(files)}: {reason}.");

        if (leftover.Count == 0)
        {
            Status($"Everything needed is in place (added {added.Effects} effect files, {added.Textures} textures). Press Save Options or PLAY.");
            return true;
        }
        Status($"Downloaded what was available (added {added.Effects} effect files, {added.Textures} textures), but some files are still missing.");
        MessageBox.Show("Still missing:\n\n•  " + string.Join("\n•  ", leftover), "Missing ReShade files", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    // ---------- runtime ----------
    private void ReShadeInstall_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select the ReShade setup (.exe), a zip, or ReShade32.dll / ReShade64.dll",
            Filter = "ReShade setup, zip or dll|*.exe;*.zip;*.dll|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var v = ReShadeManager.InstallRuntime(dlg.FileName);
            RefreshReShade(false);
            Status($"ReShade {v} is ready. Tick \"Enable ReShade\" and press Save Options or PLAY.");
        }
        catch (Exception ex) { Status("ReShade install failed: " + ex.Message); }
    }

    private async void ReShadeDownload_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Download the latest ReShade setup from reshade.me and take the ReShade dlls out of it?\n\n" +
                "ReShade is © crosire, BSD 3-Clause licence. Nothing is installed into the game until you tick \"Enable ReShade\" and apply.",
                "Download ReShade", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            Status("Downloading ReShade…");
            var (file, ver) = await ReShadeManager.DownloadAsync();
            try { ReShadeManager.InstallRuntime(file, ver); } finally { File.Delete(file); }
            RefreshReShade(false);
            Status($"ReShade {ver} is ready. Tick \"Enable ReShade\" and press Save Options or PLAY.");
        }
        catch (Exception ex) { Status("ReShade download failed: " + ex.Message); }
    }

    private void ReShadeRemove_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame() || GameInstall.IsRunning()) { if (_game != null) Status("Close L.A. Noire first."); return; }
        try
        {
            ReShadeManager.Uninstall(_game!);
            RefreshReShade();
            Status("ReShade removed: the game folder is back as it was (a ReShade.ini you had before was restored). Your shaders and presets in %AppData% were kept.");
        }
        catch (Exception ex) { Status("ReShade: " + ex.Message); }
    }

    // ---------- shaders ----------
    private void ReportImport(ImportResult r, string what)
    {
        RefreshReShade(false);
        Status(r.Total == 0
            ? $"{what}: nothing usable found (expected Shaders / Textures folders, .fx files or preset .ini files)."
            : $"{what}: added {r.Effects} effect files, {r.Textures} textures and {r.Presets} presets.");
    }

    private async Task ImportAsync(string source, string what)
    {
        try
        {
            Status("Importing…");
            var r = await Task.Run(() => ReShadeManager.ImportShaders(source));
            ReportImport(r, what);
        }
        catch (Exception ex) { Status(what + " failed: " + ex.Message); }
    }

    private async void ShadersFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select a folder with shaders (Shaders / Textures, .fx files or presets)" };
        if (dlg.ShowDialog() == true) await ImportAsync(dlg.FolderName, "Add from folder");
    }

    private async void ShadersZip_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Select a shader pack zip", Filter = "Zip files|*.zip" };
        if (dlg.ShowDialog() == true) await ImportAsync(dlg.FileName, "Add from zip");
    }

    private async void ShadersStandard_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Download ReShade's standard effects (github.com/crosire/reshade-shaders, 'slim' branch) into the launcher's shader folder?\n\n" +
                "Each shader keeps its own licence; see the files.", "Download standard effects",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            Status("Downloading standard effects…");
            var zip = await ReShadeManager.DownloadStandardShadersAsync();
            try { await ImportAsync(zip, "Standard effects"); } finally { File.Delete(zip); }
        }
        catch (Exception ex) { Status("Download failed: " + ex.Message); }
    }

    private async void ShadersFromGame_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame()) return;
        try
        {
            Status("Importing from the game folder…");
            var g = _game!;
            var r = await Task.Run(() => ReShadeManager.ImportFromGame(g));
            ReportImport(r, "Import from game folder");
        }
        catch (Exception ex) { Status("Import failed: " + ex.Message); }
    }

    private static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
    }

    private void ShadersOpen_Click(object sender, RoutedEventArgs e) => OpenFolder(ReShadeManager.Root);

    // ---------- presets ----------
    private async void PresetImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Select ReShade preset(s)", Filter = "ReShade presets (*.ini)|*.ini", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        int ok = 0; string? last = null; var bad = new List<string>();
        foreach (var f in dlg.FileNames)
        {
            try { last = ReShadeManager.ImportPreset(f); ok++; }
            catch (Exception ex) { bad.Add(ex.Message); }
        }
        FillPresets(last ?? SelectedPreset);
        Status((ok > 0 ? $"Imported {ok} preset(s)." : "No preset imported.") + (bad.Count > 0 ? " " + string.Join(" ", bad) : "") + MissingNote(last ?? ""));
        if (last != null) await EnsureReShadeFilesAsync(last, ReShadeEnableBox.IsChecked == true, false);
    }

    private void PresetRemove_Click(object sender, RoutedEventArgs e)
    {
        var name = SelectedPreset;
        if (name.Length == 0) { Status("Pick a preset to remove first."); return; }
        if (MessageBox.Show($"Delete the preset '{name}' from the launcher's preset folder?", "Remove preset", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { ReShadeManager.RemovePreset(name); FillPresets(null); Status($"Preset '{name}' removed."); }
        catch (Exception ex) { Status("Could not remove preset: " + ex.Message); }
    }

    private void PresetOpen_Click(object sender, RoutedEventArgs e) => OpenFolder(ReShadeManager.PresetsDir);

    private void PresetRtgi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (name, created) = ReShadeManager.MakeRtgiPreset();
            FillPresets(name);
            Status(created ? $"Created '{name}' (RTGI with ReShade's default settings) and selected it. Press Save Options, then tune it in the game with Home."
                           : $"'{name}' already exists, so it was kept and selected.");
        }
        catch (Exception ex) { Status(ex.Message); }
    }

    private void PresetDepth_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (name, created) = ReShadeManager.MakeDepthPreset();
            FillPresets(name);
            Status(created ? $"Created '{name}' and selected it. Press Save Options, start the game and look at the depth view."
                           : $"'{name}' already exists, so it was kept and selected.");
        }
        catch (Exception ex) { Status(ex.Message); }
    }
}
