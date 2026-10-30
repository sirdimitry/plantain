using System.Windows;

namespace Plantain.Windows;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.Apply(Resources);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
