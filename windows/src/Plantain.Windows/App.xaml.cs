using System.Windows;

namespace Plantain.Windows;

public partial class App : Application
{
    // Presence marker shared with Setup/Uninstall. Keep it alive until process exit;
    // multiple app windows/processes are still allowed.
    private static readonly Mutex installationGuard = new(false, @"Local\Plantain.Windows.97E4A1C8");
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        GC.KeepAlive(installationGuard);
        ThemeManager.Apply(Resources);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
