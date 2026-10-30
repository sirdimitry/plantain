using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Reflection;
using Plantain.Core;

namespace Plantain.Windows;

public partial class MainWindow : Window
{
    private readonly IWindowsServices services;
    private bool closing;
    private FailuresWindow? failureList;
    private string announcedStatus = "";
    private readonly IUpdateChecker? updateChecker;
    private readonly string installedVersion;
    private readonly CancellationTokenSource updateCancellation = new();
    private WindowsUpdate? pendingUpdate;
    private bool updateChecked, windowClosed;
    public MainViewModel Model { get; }
    public MainWindow() : this(new WindowsServices()) { }
    private MainWindow(IWindowsServices services) : this(new MainViewModel(services: services), services, new GitHubWindowsUpdates()) { }
    public MainWindow(MainViewModel model, IWindowsServices services, IUpdateChecker? updateChecker = null, string? installedVersion = null)
    {
        this.services = services; Model = model;
        this.updateChecker = updateChecker;
        this.installedVersion = installedVersion ?? typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        InitializeComponent(); DataContext = model;
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MaxHeight = SystemParameters.WorkArea.Height;
        Height = Math.Min(Height, MaxHeight);
        Loaded += (_, _) => LinkBox.Focus();
        ContentRendered += CheckUpdatesOnStartup;
        Model.PropertyChanged += ModelChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += PreferenceChanged;
        Closed += (_, _) => { windowClosed = true; updateCancellation.Cancel(); updateCancellation.Dispose(); Model.PropertyChanged -= ModelChanged; Microsoft.Win32.SystemEvents.UserPreferenceChanged -= PreferenceChanged; };
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
    private void SkipCurrent(object sender, RoutedEventArgs e) => Model.SkipCurrent();
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
    private void OnActivated(object? sender, EventArgs e)
    {
        ThemeManager.Apply(Application.Current.Resources);
        _ = Dispatcher.InvokeAsync(ShowPendingUpdate, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }
    private void PreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        => _ = Dispatcher.InvokeAsync(() => ThemeManager.Apply(Application.Current.Resources));
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        ShowPendingUpdate();
        if (!Model.IsRunning && Model.Status != announcedStatus)
        {
            announcedStatus = Model.Status;
            UIElementAutomationPeer.FromElement(StatusBlock)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private async void CheckUpdatesOnStartup(object? sender, EventArgs e)
    {
        if (updateChecked || updateChecker is null) return;
        updateChecked = true;
        try
        {
            var result = await updateChecker.CheckAsync(installedVersion, updateCancellation.Token);
            if (!windowClosed && result.Status == UpdateCheckStatus.Available)
            { pendingUpdate = result.Update; ShowPendingUpdate(); }
        }
        catch (OperationCanceledException) { }
        catch { /* A failed update check must not prevent downloads or show startup errors. */ }
    }

    private void ShowPendingUpdate()
    {
        if (pendingUpdate is null || windowClosed || !IsLoaded || !IsActive || !IsEnabled || Model.IsRunning || OwnedWindows.Count > 0) return;
        var update = pendingUpdate;
        pendingUpdate = null;
        new UpdateWindow(installedVersion, update) { Owner = this }.Show();
    }
}
