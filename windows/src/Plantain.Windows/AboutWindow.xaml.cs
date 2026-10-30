using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace Plantain.Windows;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = "Windows " + typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
    }
    private void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { MessageBox.Show(this, "Не удалось открыть браузер.", "Открыть ссылку"); }
    }
    private void GitHub(object sender, RoutedEventArgs e) => Open("https://github.com/sirdimitry");
    private void Profile(object sender, RoutedEventArgs e) => Open("https://x.com/sirdimitry");
    private void MacOS(object sender, RoutedEventArgs e) => Open("https://github.com/sirdimitry/plantain/blob/main/README.md");
    private void Dismiss(object sender, RoutedEventArgs e) => Close();
}
