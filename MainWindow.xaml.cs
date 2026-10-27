using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using LANoireLauncher.Core;
using LANoireLauncher.Overlay;

namespace LANoireLauncher;

public partial class MainWindow : Window
{
    private const string GfxSection = "LANSettings";
    private readonly LauncherConfig _cfg = LauncherConfig.Load();
    private GameInstall? _game;
    private readonly List<Field> _patch = new();
    private readonly List<Field> _gfx = new();
    private OverlayController? _overlay;
    private HwndSource? _hwndSource;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    private const int HotkeyId = 0x4C41;   // Ctrl+Shift+F toggles the overlay

    // dark title bar + noir caption colour (Windows 10 20H1+ / 11; silently ignored on older builds)
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        int on = 1, caption = 0x000E0D0D, text = 0x00D4E2E8;      // COLORREF = 0x00BBGGRR
        DwmSetWindowAttribute(h, 20, ref on, 4);
        DwmSetWindowAttribute(h, 35, ref caption, 4);
        DwmSetWindowAttribute(h, 36, ref text, 4);
        _hwndSource = HwndSource.FromHwnd(h);
        _hwndSource?.AddHook(WndProc);
        RegisterHotKey(h, HotkeyId, 0x0002 | 0x0004 | 0x4000, 0x46);  // MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 'F'
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == HotkeyId)
        {
            if (_overlay != null) _overlay.UserVisible = !_overlay.UserVisible;
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        var h = new WindowInteropHelper(this).Handle;
        UnregisterHotKey(h, HotkeyId);
        _hwndSource?.RemoveHook(WndProc);
        _overlay?.Dispose();
        base.OnClosed(e);
    }

    public MainWindow()
    {
        InitializeComponent();
        BuildFields();
        Loaded += async (_, _) => await InitAsync();
    }

    // ---------- field definitions ----------
    private static string? PosInt(string v) => int.TryParse(v, out var n) && n >= 0 ? null : "must be a whole number (0 or more)";
    private static string? PosFloat(string v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? null : "must be a number above 0 (use a dot, e.g. 1.2)";
    private static string? ResFmt(string v) => Regex.IsMatch(v, @"^\d+x\d+(x\d+)?$") ? null : "use WIDTHxHEIGHTxHZ, e.g. 1920x1080x60";

    private void BuildFields()
    {
        _patch.AddRange(new[]
        {
            new Field("fixes", "fps_unlock", "Unlock FPS cap and apply high-FPS fixes", FieldKind.Flag01, "Turn off if an FPS bug blocks progress."),
            new Field("fixes", "resolution_unlock", "Allow any resolution", FieldKind.Flag01, "Removes resolution filtering."),
            new Field("fixes", "aspect_correction", "Aspect ratio / FoV / UI correction", FieldKind.Flag01, "Removes black bars on non-16:9."),
            new Field("options", "fps_limit", "FPS limit (0 = none)", FieldKind.Text, "Needs FPS unlock enabled.", null, PosInt),
            new Field("options", "fov_multiplier", "FoV multiplier (1.0 = default)", FieldKind.Text, "Needs aspect correction enabled.", null, PosFloat),
            new Field("options", "borderless_window", "Borderless window", FieldKind.Flag01, "Off = regular window with a title bar. The launcher always runs the game windowed."),
            new Field("options", "skip_logos_and_legals", "Skip company logos and legal screens", FieldKind.Flag01, "Faster startup."),
        });

        // NOTE: dropdown contents are best guesses; the boxes are editable. Verify against the game's own options.
        var quality = new[] { "Quality", "Balanced", "Performance" };
        _gfx.AddRange(new[]
        {
            new Field(GfxSection, "Resolution", "Resolution (WxHxHz)", FieldKind.Text, "e.g. 1920x1080x60", null, ResFmt),
            new Field(GfxSection, "Antialiasing", "Antialiasing", FieldKind.Combo, "0 = off", new[] { "0", "2", "4", "8" }),
            new Field(GfxSection, "Anisotropic Filtering", "Anisotropic filtering", FieldKind.Combo, "", new[] { "0", "2", "4", "8", "16" }),
            new Field(GfxSection, "SSAO", "SSAO", FieldKind.FlagYesNo, ""),
            new Field(GfxSection, "Facial Quality", "Facial quality", FieldKind.Combo, "", quality),
            new Field(GfxSection, "Texture Detail", "Texture detail", FieldKind.Combo, "", quality),
            new Field(GfxSection, "Shadow Detail", "Shadow detail", FieldKind.Combo, "", quality),
            new Field(GfxSection, "Lighting Detail", "Lighting detail", FieldKind.Combo, "", quality),
            new Field(GfxSection, "Environment Map Detail", "Environment map detail", FieldKind.Combo, "", quality),
            new Field(GfxSection, "LOD Quality", "LOD quality", FieldKind.Combo, "", quality),
            new Field(GfxSection, "Interface Size", "Interface size", FieldKind.Combo, "", new[] { "Normal" }),
            new Field(GfxSection, "Threading", "Threading", FieldKind.Combo, "", new[] { "Multi", "Single" }),
            new Field(GfxSection, "Command Line", "Command line (game's own)", FieldKind.Text, "Stored in settings.ini by the game."),
        });

        foreach (var f in _patch) PatchPanel.Children.Add(f.Build());
        foreach (var f in _gfx) GfxPanel.Children.Add(f.Build());
    }

    // ---------- init / refresh ----------
    private async Task InitAsync()
    {
        var path = _cfg.GamePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(Path.Combine(path, "LaNoire.exe"))) path = GameInstall.AutoDetect();
        if (path != null) await OpenGameAsync(path);
        else { InfoText.Text = "No game selected. Use Browse… and pick LaNoire.exe."; Status("Select your L.A. Noire folder to begin."); }
        RefreshProfiles();
        OverlayBox.IsChecked = _cfg.OverlayEnabled;
    }

    private async Task OpenGameAsync(string path)
    {
        _game = GameInstall.Open(path, out var err);
        if (_game == null) { Status(err); return; }
        _cfg.GamePath = _game.Root;

        if (string.IsNullOrEmpty(_cfg.SettingsPath) || !File.Exists(_cfg.SettingsPath))
            _cfg.SettingsPath = _game.SettingsCandidates().FirstOrDefault(File.Exists);
        _cfg.Save();

        var sha = await Task.Run(() => GameInstall.Sha256Of(_game.ExePath));
        GamePathBox.Text = _game.Root;
        InfoText.Text = $"LaNoire.exe  version {_game.FileVersion}  (build {_game.Build})\n" +
                        $"SHA-256: {sha}\n" +
                        (_game.IsSupported ? "Build is supported by V-Patch 2.0." : "⚠ Build is NOT in V-Patch's supported list (2617 / 2663 / 2675). The patch may not work.");
        LoadFromDisk();
    }

    private void LoadFromDisk()
    {
        if (_game == null) return;
        var ps = PatchManager.GetStatus(_game);
        PatchStatusText.Text = ps.Label + (ps.Usable && !ps.IniPresent ? "  —  run the game once to generate lanvp.ini" : "");
        if (ps.IniPresent)
        {
            var ini = IniDocument.Load(_game.LanvpIni, '=');
            foreach (var f in _patch) f.SetValue(ini.Get(f.Key, f.Section));
        }
        RefreshDxvk();
        RefreshAtb();
        RefreshReShade();
        SettingsPathBox.Text = _cfg.SettingsPath ?? "(not found — use Browse…)";
        if (!string.IsNullOrEmpty(_cfg.SettingsPath) && File.Exists(_cfg.SettingsPath))
        {
            var ini = IniDocument.Load(_cfg.SettingsPath, ':');
            foreach (var f in _gfx) f.SetValue(ini.Get(f.Key, f.Section));
        }
    }

    private void Status(string s) => StatusText.Text = s;

    // ---------- browse ----------
    private async void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Select LaNoire.exe", Filter = "LaNoire.exe|LaNoire.exe" };
        if (dlg.ShowDialog() == true) await OpenGameAsync(dlg.FileName);
    }

    private async void AutoDetect_Click(object sender, RoutedEventArgs e)
    {
        var p = GameInstall.AutoDetect();
        if (p == null) Status("Could not find an install in the usual places. Use Browse…");
        else await OpenGameAsync(p);
    }

    private void BrowseSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Select the game's settings.ini", Filter = "settings.ini|settings.ini" };
        if (dlg.ShowDialog() != true) return;
        _cfg.SettingsPath = dlg.FileName; _cfg.Save(); LoadFromDisk();
    }

    // ---------- patch management ----------
    private bool NeedGame() { if (_game == null) { Status("Select the game folder first."); return false; } return true; }

    private void TryInstall(string zip)
    {
        try { PatchManager.Install(_game!, zip, false); }
        catch (LoaderConflictException)
        {
            var r = MessageBox.Show("A different dinput8.dll is already in the game folder (ReShade or another mod?).\n\n" +
                "Replace it? The original will be backed up and restored if you uninstall V-Patch.",
                "dinput8.dll conflict", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) { Status("Install cancelled."); return; }
            PatchManager.Install(_game!, zip, true);
        }
        LoadFromDisk(); Status("V-Patch installed. Run the game once to generate lanvp.ini if it is missing.");
    }

    private void InstallZip_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame() || GameInstall.IsRunning()) { if (_game != null) Status("Close L.A. Noire first."); return; }
        var dlg = new OpenFileDialog { Title = "Select the V-Patch release zip", Filter = "Zip files|*.zip" };
        if (dlg.ShowDialog() != true) return;
        try { TryInstall(dlg.FileName); } catch (Exception ex) { Status("Install failed: " + ex.Message); }
    }

    private async void DownloadPatch_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame() || GameInstall.IsRunning()) { if (_game != null) Status("Close L.A. Noire first."); return; }
        var ok = MessageBox.Show("Download the latest V-Patch release from github.com/VaanaCZ/LANVP and install it into your game folder?\n\n" +
            "V-Patch is © VaanaCZ, licensed CC BY-NC 4.0 (non-commercial use).", "Download V-Patch", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        try { Status("Downloading…"); var zip = await PatchManager.DownloadLatestAsync(); TryInstall(zip); File.Delete(zip); }
        catch (Exception ex) { Status("Download/install failed: " + ex.Message); }
    }

    private void UninstallPatch_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame() || GameInstall.IsRunning()) { if (_game != null) Status("Close L.A. Noire first."); return; }
        if (MessageBox.Show("Remove V-Patch files that this launcher installed?", "Uninstall", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { PatchManager.Uninstall(_game!); LoadFromDisk(); Status("V-Patch removed."); }
        catch (Exception ex) { Status("Uninstall failed: " + ex.Message); }
    }

    // ---------- DXVK / Vulkan ----------
    // DirectX 11 is always used; the box only decides whether DXVK (Vulkan) sits on top of it.
    private DxvkApi SelectedApi => DxvkEnableBox.IsChecked == true ? DxvkApi.D3D11 : DxvkApi.Off;

    private string DxvkVersionText => string.IsNullOrWhiteSpace(DxvkVersionBox.Text) ? "latest" : DxvkVersionBox.Text.Trim();

    private static string ComboText(System.Windows.Controls.ComboBox b) =>
        b.SelectedItem is System.Windows.Controls.ComboBoxItem i ? (i.Content?.ToString() ?? "") : b.Text;

    private void RefreshDxvk()
    {
        if (_game == null) return;
        var st = DxvkManager.GetStatus(_game);
        DxvkStatusText.Text = st.Label + (_game.Is64Bit ? "   [64-bit exe]" : "   [32-bit exe]");
        DxvkCacheText.Text = $"Pipeline cache: {DxvkManager.CacheBytes() / 1048576.0:F1} MB";
        DxvkEnableBox.IsChecked = st.D3D11Present;
    }

    private void DxvkInstall(string archive, string? version)
    {
        const DxvkApi api = DxvkApi.D3D11;
        try { DxvkManager.Apply(_game!, api, archive, version, false); }
        catch (LoaderConflictException ex)
        {
            var r = MessageBox.Show($"A different {ex.FileName} is already in the game folder (ReShade, SpecialK or another DXVK?).\n\n" +
                "Replace it? The original is backed up and restored when you uninstall DXVK.",
                "DLL conflict", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) { Status("Install cancelled."); return; }
            DxvkManager.Apply(_game!, api, archive, version, true);
        }
        RefreshDxvk(); Status("DXVK installed. Tick \"Render with Vulkan\" and press Play to use it.");
    }

    private void DxvkInstallArchive_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame() || GameInstall.IsRunning()) { if (_game != null) Status("Close L.A. Noire first."); return; }
        var dlg = new OpenFileDialog { Title = "Select a DXVK release archive", Filter = "DXVK archive|*.tar.gz;*.zip" };
        if (dlg.ShowDialog() != true) return;
        try { DxvkInstall(dlg.FileName, System.IO.Path.GetFileName(dlg.FileName)); }
        catch (Exception ex) { Status("DXVK install failed: " + ex.Message); }
    }

    private async void DxvkDownload_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame() || GameInstall.IsRunning()) { if (_game != null) Status("Close L.A. Noire first."); return; }
        if (MessageBox.Show($"Download DXVK ({DxvkVersionText}) from github.com/doitsujin/dxvk and install it into your game folder?\n\nDXVK is licensed under the zlib licence.",
                "Download DXVK", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            Status("Downloading DXVK…");
            var (file, tag) = await DxvkManager.DownloadAsync(DxvkVersionText);
            try { DxvkInstall(file, tag); } finally { File.Delete(file); }
        }
        catch (Exception ex) { Status("DXVK download/install failed: " + ex.Message); }
    }

    private void DxvkUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame() || GameInstall.IsRunning()) { if (_game != null) Status("Close L.A. Noire first."); return; }
        try { DxvkManager.Uninstall(_game!); DxvkEnableBox.IsChecked = false; RefreshDxvk(); Status("DXVK removed; the game uses native Direct3D again."); }
        catch (Exception ex) { Status("DXVK uninstall failed: " + ex.Message); }
    }

    private void DxvkClearCache_Click(object sender, RoutedEventArgs e)
    {
        try { DxvkManager.ClearCache(); RefreshDxvk(); Status("DXVK pipeline cache cleared. Expect stutter while it rebuilds."); }
        catch (Exception ex) { Status("Could not clear cache: " + ex.Message); }
    }

    // lanvp.ini is generated by V-Patch, so the name of its "enforce DirectX 11" key is looked up in the file
    // instead of being hard-coded. Returns null if no plausible key exists.
    private static string? FindDx11Key(IniDocument ini) =>
        ini.Keys().FirstOrDefault(k => Regex.IsMatch(k, @"(dx|d3d|directx).*11|11.*(dx|d3d|directx)", RegexOptions.IgnoreCase));

    /// <summary>DirectX 11 is always forced in V-Patch's lanvp.ini, with or without DXVK.</summary>
    private string? ForceDx11()
    {
        if (_game == null || !File.Exists(_game.LanvpIni)) return null;
        var ini = IniDocument.Load(_game.LanvpIni, '=');
        var key = FindDx11Key(ini);
        if (key == null) return "No DirectX 11 option found in lanvp.ini - check that the game really renders with DirectX 11.";
        if (ini.Get(key) == "1") return null;
        LauncherConfig.Backup(_game.LanvpIni);
        ini.Set(key, "1"); ini.Save();
        return $"Set '{key}' = 1 in lanvp.ini (DirectX 11).";
    }

    private Dictionary<string, string> DxvkEnvironment() =>
        SelectedApi == DxvkApi.Off ? new()
        : DxvkManager.BuildEnvironment(DxvkConfBox.Text, ComboText(DxvkLogBox));

    // ---------- apply / launch ----------
    private bool Apply()
    {
        if (!NeedGame()) return false;
        if (GameInstall.IsRunning()) { Status("Close L.A. Noire before applying settings."); return false; }

        foreach (var f in _patch.Concat(_gfx))
        {
            var err = f.Validate();
            if (err != null) { Status(err); return false; }
        }
        var rw = ResWBox.Text.Trim(); var rh = ResHBox.Text.Trim();
        if ((rw.Length > 0 || rh.Length > 0) && !(int.TryParse(rw, out var w) && int.TryParse(rh, out var h) && w > 0 && h > 0))
        { Status("Force resolution needs both width and height as whole numbers."); return false; }

        string Val(string key) => _patch.Concat(_gfx).First(f => f.Key == key).GetValue();
        var ps = PatchManager.GetStatus(_game!);

        if (double.TryParse(Val("fps_limit"), out var lim) && lim > 0 && Val("fps_unlock") == "0")
            MessageBox.Show("FPS limit has no effect while FPS unlock is off.", "Heads-up");
        if (Val("fov_multiplier") != "1.0" && Val("aspect_correction") == "0")
            MessageBox.Show("FoV multiplier has no effect while aspect correction is off.", "Heads-up");

        try
        {
            if (ps.IniPresent)
            {
                LauncherConfig.Backup(_game!.LanvpIni);
                var ini = IniDocument.Load(_game.LanvpIni, '=');
                foreach (var f in _patch) ini.Set(f.Key, f.GetValue(), f.Section);
                ini.Save();
            }
            if (!string.IsNullOrEmpty(_cfg.SettingsPath) && File.Exists(_cfg.SettingsPath))
            {
                LauncherConfig.SaveOriginal(_cfg.SettingsPath);
                LauncherConfig.Backup(_cfg.SettingsPath);
                var ini = IniDocument.Load(_cfg.SettingsPath, ':');
                foreach (var f in _gfx) ini.Set(f.Key, f.GetValue(), f.Section);
                if (ini.Get("Renderer", GfxSection) != null) ini.Set("Renderer", "DirectX 11", GfxSection);
                ini.Set("Fullscreen", "NO", GfxSection);                  // exclusive fullscreen is not offered: windowed or borderless only
                ini.Save();
            }
        }
        catch (Exception ex) { Status("Could not write settings: " + ex.Message); return false; }

        string note = "";
        try
        {
            var ds = DxvkManager.GetStatus(_game!);
            if (SelectedApi != DxvkApi.Off || ds.D3D11Present)
                DxvkManager.Apply(_game!, SelectedApi, null, null, false);   // switches the DLL set or turns DXVK off; never installs new files
            if (ForceDx11() is { } n) note = " " + n;
            RefreshDxvk();
        }
        catch (Exception ex) { Status("DXVK: " + ex.Message); return false; }

        try { if (ApplyReShade() is { } rn) note += " " + rn; }
        catch (Exception ex) { Status("ReShade: " + ex.Message); return false; }

        Status("Settings applied (backups are in %AppData%\\LANoireLauncher\\backups)." + note);
        return true;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        await PreflightReShadeAsync();      // offers to download anything ReShade or the preset is missing
        Apply();
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        await PreflightReShadeAsync();
        if (!Apply()) return;
        Process? game;
        var args = new List<string>();
        if (ResWBox.Text.Trim().Length > 0) args.Add($"-res {ResWBox.Text.Trim()} {ResHBox.Text.Trim()}");
        if (IgnoreFocusBox.IsChecked == true) args.Add("-ignorefocus");
        if (ExtraArgsBox.Text.Trim().Length > 0) args.Add(ExtraArgsBox.Text.Trim());
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _game!.ExePath,
                WorkingDirectory = _game.Root,
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
            };
            bool wantFps = OverlayBox.IsChecked == true, dxvk = SelectedApi != DxvkApi.Off;
            if (wantFps && dxvk) psi.Environment["DXVK_HUD"] = "fps";   // DXVK draws its own tiny counter
            else psi.Environment.Remove("DXVK_HUD");
            foreach (var kv in DxvkEnvironment()) psi.Environment[kv.Key] = kv.Value;
            // ReShade is loaded from plugins\ReShade64.asi; without this it treats plugins\ as its base folder
            if (ReShadeEnableBox.IsChecked == true) psi.Environment["RESHADE_BASE_PATH_OVERRIDE"] = _game.Root;
            game = Process.Start(psi);
            Status("Launched: LaNoire.exe " + string.Join(" ", args) + (SelectedApi != DxvkApi.Off ? "   [Vulkan via DXVK]" : "   [DirectX 11]"));
        }
        catch (Exception ex) { Status("Launch failed: " + ex.Message); return; }
        if (game != null && OverlayBox.IsChecked == true) StartOverlay(game, SelectedApi != DxvkApi.Off);
    }

    // ---------- FPS overlay ----------
    // DXVK on  -> DXVK's built-in HUD ("fps"), set as an environment variable at launch, plus a launcher-drawn "(DXVK)" label beside it.
    // DXVK off -> launcher-drawn "FPS: n" text in the same style, fed by Windows' DXGI present events.
    private void OverlayBox_Click(object sender, RoutedEventArgs e)
    {
        _cfg.OverlayEnabled = OverlayBox.IsChecked == true;
        _cfg.Save();
        if (!_cfg.OverlayEnabled) { _overlay?.Dispose(); _overlay = null; OverlayMsgText.Text = ""; }
    }

    private void StartOverlay(Process game, bool dxvk)
    {
        _overlay?.Dispose(); _overlay = null;
        FpsCounter? counter = null;
        if (!dxvk)
        {
            counter = FpsCounter.TryStart(game.Id, out var err);
            if (counter == null) { OverlayMsgText.Text = err ?? ""; return; }
        }
        var ctl = new OverlayController(game, counter, msg => OverlayMsgText.Text = msg);
        ctl.Finished += () => { if (_overlay == ctl) _overlay = null; };
        _overlay = ctl;
    }

    // ---------- profiles ----------
    // ---------- enhancements (attribute patches in root.atb.pc) ----------
    private bool _atbRefreshing;

    private void RefreshAtb()
    {
        if (_game == null)
        {
            AtbStatusText.Text = "Select the game folder first.";
            AtbApplyBtn.IsEnabled = false; AtbRevertBtn.IsEnabled = false;
            return;
        }
        var st = AtbPatcher.GetStatus(AtbPatcher.WadPath(_game));
        AtbStatusText.Text = st.Label;
        AtbApplyBtn.IsEnabled = st.CanApply;
        AtbRevertBtn.IsEnabled = st.CanRevert;
        _atbRefreshing = true;
        AtbFogBox.IsChecked = st.Sets.TryGetValue("fog", out var f) && f.Patched == f.Total && f.Total > 0;
        AtbLightBox.IsChecked = st.Sets.TryGetValue("lighting", out var l) && l.Patched == l.Total && l.Total > 0;
        AtbVehBox.IsChecked = st.Sets.TryGetValue("vehicles", out var v) && v.Patched == v.Total && v.Total > 0;
        _atbRefreshing = false;
    }

    private HashSet<string> AtbChosenSets()
    {
        var s = new HashSet<string>();
        if (AtbFogBox.IsChecked == true) s.Add("fog");
        if (AtbLightBox.IsChecked == true) s.Add("lighting");
        if (AtbVehBox.IsChecked == true) s.Add("vehicles");
        return s;
    }

    private async void AtbApply_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame()) return;
        if (GameInstall.IsRunning()) { Status("Close L.A. Noire first."); return; }
        var chosen = AtbChosenSets();
        if (chosen.Count == 0) { Status("Tick at least one set, or use \"Revert all to original\"."); return; }
        int total = chosen.Sum(x => AtbPatcher.SetSize(x));
        var r = MessageBox.Show(
            $"This makes {total} values in your game file out.wad.pc match the PS4 remaster ({string.Join(", ", chosen)}). " +
            "Sets you did not tick are set back to original.\n\n" +
            "Every value is checked first, a journal is kept so it can be reverted exactly, and the file size does not change. " +
            "A file-verification in the Rockstar launcher may flag it: revert before verifying.\n\nContinue?",
            "Apply attribute patches", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;

        var wad = AtbPatcher.WadPath(_game!);
        bool exact = AtbExactBox.IsChecked == true, backup = AtbBackupBox.IsChecked == true;
        try
        {
            if (backup) Status("Copying out.wad.pc (about 150 MB), please wait…");
            var n = await Task.Run(() => AtbPatcher.ApplySets(wad, chosen, exact, backup));
            RefreshAtb();
            Status(n == 0 ? "Nothing to change: this selection was already applied." : $"Enhancements applied: {n} values written.");
        }
        catch (Exception ex) { RefreshAtb(); Status("Enhancements: " + ex.Message); }
    }

    private void AtbRevert_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame()) return;
        if (GameInstall.IsRunning()) { Status("Close L.A. Noire first."); return; }
        try
        {
            var n = AtbPatcher.Revert(AtbPatcher.WadPath(_game!));
            AtbPatcher.RemoveBackupCopy(AtbPatcher.WadPath(_game!));
            RefreshAtb();
            Status(n == 0 ? "Nothing to revert: all values are already original." : $"Reverted: {n} values restored.");
        }
        catch (Exception ex) { RefreshAtb(); Status("Enhancements: " + ex.Message); }
    }

    private void RefreshProfiles()
    {
        ProfileBox.Items.Clear();
        foreach (var p in _cfg.Profiles) ProfileBox.Items.Add(p.Name);
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = ProfileBox.Text.Trim();
        if (name.Length == 0) { Status("Type a profile name first."); return; }
        _cfg.Profiles.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _cfg.Profiles.Add(new Profile
        {
            Name = name,
            Patch = _patch.ToDictionary(f => f.Key, f => f.GetValue()),
            Graphics = _gfx.ToDictionary(f => f.Key, f => f.GetValue()),
            ResW = ResWBox.Text, ResH = ResHBox.Text,
            IgnoreFocus = IgnoreFocusBox.IsChecked == true, ExtraArgs = ExtraArgsBox.Text,
            Dxvk = new()
            {
                ["Api"] = SelectedApi.ToString(),
                ["Log"] = ComboText(DxvkLogBox), ["Conf"] = DxvkConfBox.Text,
            },
            ReShade = new()
            {
                ["Enabled"] = ReShadeEnableBox.IsChecked == true ? "1" : "0",
                ["Preset"] = SelectedPreset,
            },
        });
        _cfg.Save(); RefreshProfiles(); ProfileBox.Text = name; Status($"Profile '{name}' saved.");
    }

    private void LoadProfile_Click(object sender, RoutedEventArgs e)
    {
        var p = _cfg.Profiles.FirstOrDefault(x => x.Name.Equals(ProfileBox.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        if (p == null) { Status("No such profile."); return; }
        foreach (var f in _patch) if (p.Patch.TryGetValue(f.Key, out var v)) f.SetValue(v);
        foreach (var f in _gfx) if (p.Graphics.TryGetValue(f.Key, out var v)) f.SetValue(v);
        DxvkEnableBox.IsChecked = p.Dxvk.TryGetValue("Api", out var api) && api == nameof(DxvkApi.D3D11);   // old D3D9 profiles load as "off"
        DxvkConfBox.Text = p.Dxvk.GetValueOrDefault("Conf", "");
        var lg = p.Dxvk.GetValueOrDefault("Log", "default");
        for (int i = 0; i < DxvkLogBox.Items.Count; i++)
            if (DxvkLogBox.Items[i] is System.Windows.Controls.ComboBoxItem it && (it.Content?.ToString() ?? "") == lg) DxvkLogBox.SelectedIndex = i;
        if (p.ReShade.TryGetValue("Enabled", out var rsOn)) ReShadeEnableBox.IsChecked = rsOn == "1";
        if (p.ReShade.TryGetValue("Preset", out var rsPreset)) SelectPreset(rsPreset);
        ResWBox.Text = p.ResW; ResHBox.Text = p.ResH; IgnoreFocusBox.IsChecked = p.IgnoreFocus; ExtraArgsBox.Text = p.ExtraArgs;
        Status($"Profile '{p.Name}' loaded into the form. Press Save Options to write it.");
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        var n = ProfileBox.Text.Trim();
        if (_cfg.Profiles.RemoveAll(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) > 0)
        { _cfg.Save(); RefreshProfiles(); ProfileBox.Text = ""; Status($"Profile '{n}' deleted."); }
    }
}
