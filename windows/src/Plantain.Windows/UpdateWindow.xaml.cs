using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Plantain.Core;

namespace Plantain.Windows;

public partial class UpdateWindow : Window
{
    private readonly WindowsUpdate update;
    private readonly Action<Uri> open;
    public UpdateWindow(string installed, WindowsUpdate update, Action<Uri>? open = null)
    {
        this.update = update;
        this.open = open ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        InitializeComponent();
        VersionLabel.Text = $"Установлена: {installed}\nНовая версия: {update.Version}";
        var close = new RoutedCommand();
        CommandBindings.Add(new CommandBinding(close, (_, _) => Close()));
        InputBindings.Add(new KeyBinding(close, new KeyGesture(Key.Escape)));
    }
    private void Later(object sender, RoutedEventArgs e) => Close();
    private void Download(object sender, RoutedEventArgs e)
    {
        try
        {
            if (update.ReleasePage.Scheme != "https" || update.ReleasePage.Host != "github.com" || update.ReleasePage.Port != 443 ||
                update.ReleasePage.UserInfo.Length > 0 || !update.ReleasePage.AbsolutePath.StartsWith("/sirdimitry/plantain/releases/tag/windows-v", StringComparison.Ordinal))
                throw new InvalidOperationException();
            open(update.ReleasePage);
            Close();
        }
        catch { MessageBox.Show(this, "Не удалось открыть страницу обновления GitHub. Попробуйте позже.", "Обновление", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
