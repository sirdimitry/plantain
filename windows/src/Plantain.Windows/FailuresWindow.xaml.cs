using System.Windows;

namespace Plantain.Windows;

public partial class FailuresWindow : Window
{
    public FailuresWindow(IEnumerable<FailureRow> failures)
    {
        InitializeComponent();
        var rows = failures.ToArray();
        DataContext = new { Rows = rows, Summary = $"Не завершено: {rows.Length}. Готовые файлы сохранены. Закройте список и нажмите «Продолжить», чтобы повторить загрузку." };
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MaxHeight = SystemParameters.WorkArea.Height;
        Loaded += (_, _) => FilesGrid.Focus();
    }
    private void CloseDialog(object sender, RoutedEventArgs e) => Close();
}
