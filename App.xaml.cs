using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace LANoireLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (!IsAdministrator() && AskToRestartAsAdmin())
        {
            Shutdown();
            return;
        }
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
            "The FPS overlay needs administrator rights to read the game's frame data when DXVK is off. " +
            "Without them it will not work (with DXVK on it still does).\n\n" +
            "Restart the launcher as administrator now?",
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
