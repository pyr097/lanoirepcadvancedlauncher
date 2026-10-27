using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace LANoireLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // the check window must not count as "the last window closed", and must not become the app's main window
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var carryOn = LANoireLauncher.Core.DependencyCheck.Run();
        MainWindow = null;
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        if (!carryOn) { Shutdown(); return; }

        if (!IsAdministrator() && AskToRestartAsAdmin())
        {
            Shutdown();
            return;
        }
        LANoireLauncher.Core.UpdateManager.CleanupAfterUpdate();      // removes LANoireLauncher.exe.old left by the last update
        base.OnStartup(e);
    }

    private static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Explains why, offers a UAC restart, and returns true only if the elevated copy was actually started.</summary>
    private static bool AskToRestartAsAdmin()
    {
        var answer = MessageBox.Show(
            "The FPS overlay needs administrator rights when DXVK is off, to read the game's frame data from Windows. " +
            "With DXVK on it works without them.\n\n" +
            "Restart as administrator now?",
            "Administrator rights", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return false;
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception) { return false; }       // UAC prompt declined: carry on without admin rights
    }
}
