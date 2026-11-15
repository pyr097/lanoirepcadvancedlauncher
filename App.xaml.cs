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

        LANoireLauncher.Core.UpdateManager.CleanupAfterUpdate();      // removes LANoireLauncher.exe.old left by the last update
        base.OnStartup(e);
    }
}
