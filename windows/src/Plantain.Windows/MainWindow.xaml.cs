using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;

namespace Plantain.Windows;

public partial class MainWindow : Window
{
    private readonly IWindowsServices services;
    private bool closing;
    private FailuresWindow? failureList;
    private string announcedStatus = "";
    public MainViewModel Model { get; }
    public MainWindow() : this(new WindowsServices()) { }
    private MainWindow(IWindowsServices services) : this(new MainViewModel(services: services), services) { }
    public MainWindow(MainViewModel model, IWindowsServices services)
    {
        this.services = services; Model = model;
        InitializeComponent(); DataContext = model;
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MaxHeight = SystemParameters.WorkArea.Height;
        Height = Math.Min(Height, MaxHeight);
        Loaded += (_, _) => LinkBox.Focus();
        Model.PropertyChanged += ModelChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += PreferenceChanged;
        Closed += (_, _) => { Model.PropertyChanged -= ModelChanged; Microsoft.Win32.SystemEvents.UserPreferenceChanged -= PreferenceChanged; };
        AddShortcut(Key.O, ModifierKeys.Control, () => OpenFolder(this, new()));
        AddShortcut(Key.E, ModifierKeys.Control, () => ShowFailures(this, new()));
        AddShortcut(Key.Escape, ModifierKeys.None, Model.Stop);
    }

    private void AddShortcut(Key key, ModifierKeys modifiers, Action action)
    {
        var command = new RoutedCommand();
        CommandBindings.Add(new CommandBinding(command, (_, _) => action()));
        InputBindings.Add(new KeyBinding(command, new KeyGesture(key, modifiers)));
    }

    private async void Start(object sender, RoutedEventArgs e) { await Model.StartAsync(); if (Model.Link.Length == 0) LinkBox.Focus(); }
    private void Stop(object sender, RoutedEventArgs e) => Model.Stop();
    private void ChooseDestination(object sender, RoutedEventArgs e)
    {
        try { if (services.ChooseFolder(this, Model.Destination) is { } folder) Model.SetDestination(folder); }
        catch { MessageBox.Show(this, "Не удалось выбрать папку без сохранения истории диалога.", "Выбор папки", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private void ResetDestination(object sender, RoutedEventArgs e) => Model.SetDestination(services.DefaultDestination);
    private void OpenFolder(object sender, RoutedEventArgs e)
    {
        try { services.OpenFolder(Model.FolderToOpen); }
        catch { MessageBox.Show(this, "Папка недоступна. Выберите место сохранения.", "Открыть папку", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private void ShowFailures(object sender, RoutedEventArgs e)
    {
        if (!Model.HasIssues) return;
        if (failureList is not null) { failureList.Activate(); return; }
        failureList = new FailuresWindow(Model.Failures) { Owner = this };
        try { failureList.ShowDialog(); }
        finally
        {
            failureList = null;
            _ = Dispatcher.InvokeAsync(() => IssuesButton.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
    }
    private void About(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing || !Model.IsRunning) return;
        e.Cancel = true;
        IsEnabled = false;
        await Model.StopAndWaitAsync();
        closing = true;
        Close();
    }
    private void OnActivated(object? sender, EventArgs e) => ThemeManager.Apply(Application.Current.Resources);
    private void PreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        => _ = Dispatcher.InvokeAsync(() => ThemeManager.Apply(Application.Current.Resources));
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Model.IsRunning && Model.Status != announcedStatus)
        {
            announcedStatus = Model.Status;
            UIElementAutomationPeer.FromElement(StatusBlock)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
