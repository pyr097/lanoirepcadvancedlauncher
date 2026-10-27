using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using LANoireLauncher.Core;

namespace LANoireLauncher;

// Launcher self-update (GitHub Releases). See Core/UpdateManager.cs.
public partial class MainWindow
{
    private UpdateInfo? _update;
    private bool _updateBusy;

    private async Task StartupUpdateCheckAsync()
    {
        UpdateVersionText.Text = "Installed version: v" + UpdateManager.CurrentText;
        Title = "L.A. Noire Advanced Launcher  v" + UpdateManager.CurrentText;
        UpdateAutoBox.IsChecked = _cfg.CheckUpdatesOnStartup;
        UpdatePreBox.IsChecked = _cfg.IncludePrereleases;
        if (Environment.GetCommandLineArgs().Contains("--updated")) Status("Launcher updated to v" + UpdateManager.CurrentText + ".");
        if (_cfg.CheckUpdatesOnStartup) await CheckForUpdateAsync(manual: false);
    }

    private async Task CheckForUpdateAsync(bool manual)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        UpdateCheckBtn.IsEnabled = false;
        if (manual) UpdateStatusText.Text = "Checking GitHub…";
        try
        {
            var info = await UpdateManager.CheckAsync(_cfg.IncludePrereleases);
            if (info == null)
            {
                SetUpdate(null);
                if (manual) UpdateStatusText.Text = "No releases have been published yet.";
            }
            else if (info.Version <= UpdateManager.Current)
            {
                SetUpdate(null);
                if (manual) UpdateStatusText.Text = "You are up to date (v" + UpdateManager.CurrentText + ").";
            }
            else if (!manual && info.Tag == _cfg.SkippedVersion) SetUpdate(null);       // skipped: only a manual check shows it again
            else SetUpdate(info);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            if (manual) UpdateStatusText.Text = "Could not check for updates: " + ex.Message;   // silent at startup (offline etc.)
        }
        finally
        {
            _updateBusy = false;
            UpdateCheckBtn.IsEnabled = true;
        }
    }

    /// <summary>Shows or hides everything that belongs to a pending update.</summary>
    private void SetUpdate(UpdateInfo? info)
    {
        _update = info;
        var show = info != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateBtn.Visibility = show;
        UpdateInstallBtn.Visibility = show;
        UpdateNotesBtn.Visibility = show;
        UpdateSkipBtn.Visibility = show;
        UpdateNotesBox.Visibility = info != null && !string.IsNullOrWhiteSpace(info.Notes) ? Visibility.Visible : Visibility.Collapsed;
        if (info == null) return;

        var v = UpdateManager.Format(info.Version);
        UpdateBtn.Content = "Update available: v" + v;
        UpdateStatusText.Text = $"Version {v} is available{(info.Prerelease ? " (pre-release)" : "")}. You have v{UpdateManager.CurrentText}.";
        UpdateNotesText.Text = info.Notes.Replace("\r\n", "\n").Trim();
    }

    private void UpdateBtn_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedItem = AboutTab;
    private async void UpdateCheck_Click(object sender, RoutedEventArgs e) => await CheckForUpdateAsync(manual: true);

    private void UpdateOptions_Click(object sender, RoutedEventArgs e)
    {
        _cfg.CheckUpdatesOnStartup = UpdateAutoBox.IsChecked == true;
        var pre = UpdatePreBox.IsChecked == true;
        var changed = pre != _cfg.IncludePrereleases;
        _cfg.IncludePrereleases = pre;
        _cfg.Save();
        if (changed) _ = CheckForUpdateAsync(manual: true);
    }

    private void UpdateSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_update == null) return;
        _cfg.SkippedVersion = _update.Tag;
        _cfg.Save();
        SetUpdate(null);
        UpdateStatusText.Text = "Skipped. A manual check will show it again.";
    }

    private void UpdateRelease_Click(object sender, RoutedEventArgs e)
    {
        if (_update != null) OpenUrl(_update.PageUrl);
    }

    private void OpenRepo_Click(object sender, RoutedEventArgs e) => OpenUrl(UpdateManager.RepoUrl);

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LauncherConfig.Dir);
        Process.Start(new ProcessStartInfo(LauncherConfig.Dir) { UseShellExecute = true });
    }

    private static void OpenUrl(string url)
    {
        if (!string.IsNullOrEmpty(url)) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void UpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        var info = _update;
        if (info == null || _updateBusy) return;

        if (!UpdateManager.CanReplaceSelf())
        {
            if (MessageBox.Show("The launcher is in a folder it cannot write to, so it cannot replace itself here.\n\n" +
                                "Open the release page to download the new version by hand?",
                    "Update", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes) OpenUrl(info.PageUrl);
            return;
        }

        var mb = info.AssetSize > 0 ? $" ({info.AssetSize / 1048576.0:0.#} MB)" : "";
        var running = GameInstall.IsRunning() ? "\n\nL.A. Noire is running. It keeps running, but the FPS counter goes away while the launcher restarts." : "";
        if (MessageBox.Show($"Download version {UpdateManager.Format(info.Version)}{mb} and restart the launcher?\n\n" +
                            "Your settings, profiles and backups are kept." + running,
                "Update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _updateBusy = true;
        UpdateInstallBtn.IsEnabled = UpdateCheckBtn.IsEnabled = UpdateSkipBtn.IsEnabled = false;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateStatusText.Text = "Downloading…";
        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdateProgress.Value = p;
                UpdateStatusText.Text = $"Downloading… {p:P0}";
            });
            var (file, verified) = await UpdateManager.DownloadAsync(info, progress);
            UpdateStatusText.Text = verified ? "Checksum verified. Restarting…" : "Installing (no published checksum to verify)…";
            UpdateManager.ApplyAndRestart(file);
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            UpdateStatusText.Text = "Update failed, the launcher was not changed: " + ex.Message;
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateInstallBtn.IsEnabled = UpdateCheckBtn.IsEnabled = UpdateSkipBtn.IsEnabled = true;
            _updateBusy = false;
        }
    }
}
