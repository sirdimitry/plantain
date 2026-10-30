using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Plantain.Core;
using Plantain.Windows;

namespace Plantain.Windows.Tests;

internal static class Program
{
    private static Application app = null!;
    private static string artifacts = "";
    private static int failed;
    private static readonly List<object> results = [];

    [STAThread]
    private static int Main(string[] args)
    {
        artifacts = Path.GetFullPath(args.Length > 0 && args[0] != "--preview" ? args[0] : "artifacts/ui");
        Directory.CreateDirectory(artifacts);
        app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Plantain.Windows;component/Theme.xaml", UriKind.Relative) });
        ThemeManager.Apply(app.Resources, false);
        if (args.Contains("--preview"))
        {
            var services = new FakeServices(artifacts);
            var model = new MainViewModel(new PreviewRunner(), services) { Link = "https://disk.yandex.ru/d/synthetic-preview" };
            var window = new MainWindow(model, services) { Title = "Подорожник — проверка интерфейса" };
            window.Closed += (_, _) => app.Shutdown();
            window.Show();
            app.Run();
            return 0;
        }
        app.Dispatcher.InvokeAsync(async () =>
        {
            try { await RunTests(); }
            catch (Exception e) { failed++; Console.WriteLine(e); }
            File.WriteAllText(Path.Combine(artifacts, "ui-test-results.json"), JsonSerializer.Serialize(new
            {
                version = typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion,
                utc = DateTimeOffset.UtcNow, total = results.Count, failed, results
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"UI RESULT {results.Count - failed}/{results.Count} passed");
            app.Shutdown(failed == 0 ? 0 : 1);
        });
        return app.Run();
    }

    private static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    private static async Task Case(string name, Func<Task> run)
    {
        try { await run(); results.Add(new { name, passed = true, error = (string?)null }); Console.WriteLine("PASS UI " + name); }
        catch (Exception e) { failed++; results.Add(new { name, passed = false, error = e.ToString() }); Console.WriteLine("FAIL UI " + name + ": " + e.Message); }
    }
    private static Task Flush() => app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static MainViewModel Model(IDownloadRunner runner, out FakeServices services)
    {
        services = new FakeServices(artifacts);
        return new MainViewModel(runner, services) { Link = "https://disk.yandex.ru/d/synthetic" };
    }
    private static readonly DownloadResult Success = new(1, 1, 0, 6, 0, []);
    private static IDownloadRunner Successful() => new TestRunner((report, _) =>
    {
        report?.Invoke(new FileDiscovered("/", "movie.mp4", 6)); report?.Invoke(new InventoryFound(1, 6));
        report?.Invoke(new FileCompleted("/")); return Task.FromResult(Success);
    });

    private static async Task RunTests()
    {
        await Case("empty input disables start; known folder unavailable requires choice", () =>
        {
            var services = new FakeServices(""); var model = new MainViewModel(Successful(), services);
            Check(!model.CanStart); model.Link = "https://disk.yandex.ru/d/synthetic"; Check(!model.CanStart);
            model.SetDestination(artifacts); Check(model.CanStart); return Task.CompletedTask;
        });
        await Case("successful download clears bound field and disables start", async () =>
        {
            var model = Model(Successful(), out var services); var window = new MainWindow(model, services); window.Show();
            try
            {
                await model.StartAsync(); await Flush();
                Check(model.Link == "" && !model.CanStart && model.StartText == "Скачать" && !model.HasIssues);
                Check(((TextBox)window.FindName("LinkBox")).Text == "" && !((Button)window.FindName("StartButton")).IsEnabled);
                Check(model.TotalFraction == 1 && services.Released == 1);
            }
            finally { window.Close(); }
        });
        await Case("partial result lists filename with extension and keeps link", async () =>
        {
            var model = Model(new PreviewRunner(), out var services); await model.StartAsync();
            Check(model.HasIssues && model.Failures.Count == 2 && model.Link.Length > 0 && model.StartText == "Продолжить");
            Check(model.Failures.Select(x => x.Name).SequenceEqual(["Интервью.mp4", "Обложка.png"]));
            Check(model.Failures.All(x => x.Reason.Length > 0));
        });
        await Case("root single-file failure resolves slash to original filename", async () =>
        {
            var model = Model(new TestRunner((report, _) =>
            {
                report?.Invoke(new FileDiscovered("/", "archive.tar.gz", 6));
                return Task.FromResult(new DownloadResult(1, 0, 0, 0, 0, [new("/", "HTTP 403")]));
            }), out _);
            await model.StartAsync(); Check(model.Failures.Single().Name == "archive.tar.gz");
        });
        await Case("duplicate filenames retain distinct relative paths", async () =>
        {
            var model = Model(new TestRunner((report, _) =>
            {
                report?.Invoke(new FileDiscovered("/A/photo.jpg", "photo.jpg", 6));
                report?.Invoke(new FileDiscovered("/B/photo.jpg", "photo.jpg", 6));
                return Task.FromResult(new DownloadResult(2, 0, 0, 0, 0, [new("/A/photo.jpg", "first"), new("/B/photo.jpg", "second")]));
            }), out _);
            await model.StartAsync(); Check(model.Failures.Count == 2 && model.Failures.Select(x => x.RelativePath).Distinct().Count() == 2);
        });
        await Case("unlisted directory is labelled as folder, not invented filenames", async () =>
        {
            var model = Model(new TestRunner((_, _) => Task.FromResult(new DownloadResult(0, 0, 0, 0, 0, [new("/CON", "Недопустимое имя", true)]))), out _);
            await model.StartAsync(); Check(model.Failures.Single().IsDirectory && model.Failures.Single().Name.Contains("CON"));
        });
        await Case("cancel lists incomplete known files but excludes completed ones", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var model = Model(new TestRunner(async (report, token) =>
            {
                report?.Invoke(new FileDiscovered("/done.txt", "done.txt", 6)); report?.Invoke(new FileCompleted("/done.txt"));
                report?.Invoke(new FileDiscovered("/pending.mp4", "pending.mp4", 6)); entered.SetResult();
                await Task.Delay(Timeout.Infinite, token); return Success;
            }), out var services);
            var task = model.StartAsync(); await entered.Task; model.Stop(); await task;
            Check(model.Failures.Single().Name == "pending.mp4" && model.Link.Length > 0 && services.Released == 1);
        });
        await Case("fatal error without inventory opens explanatory list", async () =>
        {
            var model = Model(new TestRunner((_, _) => throw new FatalApiException("Ссылка не публичная")), out var services);
            await model.StartAsync(); Check(model.HasIssues && model.Error == "Ссылка не публичная" && services.Released == 1);
        });
        await Case("red issue button is accessible and opens actual modal grid", async () =>
        {
            var model = Model(new PreviewRunner(), out var services); await model.StartAsync();
            var window = new MainWindow(model, services); window.Show(); await Flush();
            var opened = false;
            _ = app.Dispatcher.InvokeAsync(() =>
            {
                var dialog = app.Windows.OfType<FailuresWindow>().Single();
                try
                {
                    var grid = (DataGrid)dialog.FindName("FilesGrid");
                    Check(grid.Items.Count == 2 && ((FailureRow)grid.Items[0]).Name.EndsWith(".mp4"));
                    ((Button)window.FindName("IssuesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(app.Windows.OfType<FailuresWindow>().Count() == 1, "Duplicate error dialogs opened");
                    opened = true;
                }
                finally { dialog.Close(); }
            }, DispatcherPriority.Background);
            try
            {
                var button = (Button)window.FindName("IssuesButton");
                Check(button.IsVisible && AutomationProperties.GetName(button).Length > 0);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(opened);
                await Flush(); Check(button.IsKeyboardFocused, "Focus did not return to issue button");
            }
            finally { window.Close(); }
        });
        await Case("close cancels active transfer and waits for cleanup", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var model = Model(new TestRunner(async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return Success; }), out var services);
            var window = new MainWindow(model, services); window.Show(); var task = model.StartAsync(); await entered.Task;
            window.Close(); await task; await Flush();
            Check(!model.IsRunning && services.Released == 1 && !window.IsVisible);
        });
        await Case("new success clears previous issue list", async () =>
        {
            var calls = 0; var model = Model(new TestRunner((report, _) =>
            {
                report?.Invoke(new FileDiscovered("/", "movie.mp4", 6));
                if (++calls == 1) return Task.FromResult(new DownloadResult(1, 0, 0, 0, 0, [new("/", "network")]));
                report?.Invoke(new FileCompleted("/")); return Task.FromResult(Success);
            }), out _);
            await model.StartAsync(); Check(model.HasIssues); await model.StartAsync(); Check(!model.HasIssues && model.Link == "");
        });
        await Case("changing destination clears old result and issues, retaining input", async () =>
        {
            var model = Model(new PreviewRunner(), out _); await model.StartAsync(); Check(model.HasIssues);
            var source = model.Link; model.SetDestination(Path.Combine(artifacts, "different-destination"));
            Check(!model.HasIssues && model.TotalFraction == 0 && model.DoneText == "0 из 0" && model.Link == source && model.StartText == "Скачать");
        });
        await Case("real engine events reach UI and successful sibling is excluded", async () =>
        {
            var folder = Path.Combine(artifacts, "engine-" + Guid.NewGuid().ToString("N"));
            var target = Path.Combine(folder, "Folder copy"); Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "bad.mp4"), "keepme");
            try
            {
                var model = new MainViewModel(new EngineRunner(), new FakeServices(folder)) { Link = "https://disk.yandex.ru/d/synthetic" };
                await model.StartAsync();
                Check(model.Failures.Single().Name == "bad.mp4" && File.ReadAllText(Path.Combine(target, "good.txt")) == "abcdef");
                Check(File.ReadAllText(Path.Combine(target, "bad.mp4")) == "keepme");
            }
            finally { Directory.Delete(folder, true); }
        });
        await Case("verification bytes do not count towards total or network speed", () =>
        {
            var model = Model(Successful(), out _);
            model.Receive(new InventoryFound(1, 6)); model.Receive(new FileStarted("test", 6, true)); model.Receive(new TransferProgress(3, 0));
            Check(model.TotalFraction == 0 && model.FileFraction == 0.5 && model.SpeedText == "—"); return Task.CompletedTask;
        });
        await Case("byte and ETA formatting", () =>
        {
            Check(MainViewModel.Bytes(1024) == "1,0 КиБ" && MainViewModel.Duration(3660) == "1 ч 1 мин"); return Task.CompletedTask;
        });
        await Case("system Downloads lookup and best-effort own dialog cleanup", () =>
        {
            var services = new WindowsServices(); var path = services.DefaultDestination;
            Check(path.Length == 0 || Directory.Exists(path)); WindowsServices.ClearDialogHistory(); return Task.CompletedTask;
        });
        await Case("sleep lease releases on owner dispatcher", () =>
        {
            using var awake = new WindowsServices().KeepAwake(); return Task.CompletedTask;
        });
        await Case("startup shows one blue update prompt and Later dismisses it", async () =>
        {
            var model = Model(Successful(), out var services);
            var checker = new FakeUpdates(new(UpdateCheckStatus.Available, new("0.1.0-dev.7", new Uri("https://github.com/sirdimitry/plantain/releases/tag/windows-v0.1.0-dev.7"))));
            var window = new MainWindow(model, services, checker, "0.1.0-dev.6"); window.Show(); window.Activate();
            try
            {
                await Until(() => window.OwnedWindows.OfType<UpdateWindow>().Any());
                var popup = window.OwnedWindows.OfType<UpdateWindow>().Single();
                Check(((TextBlock)popup.FindName("VersionLabel")).Text.Contains("0.1.0-dev.7"));
                Save(popup, "update-notification.png", 1);
                ((Button)popup.FindName("LaterButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Activate(); await Flush(); Check(checker.Calls == 1 && !window.OwnedWindows.OfType<UpdateWindow>().Any());
            }
            finally { window.Close(); }
        });
        await Case("unavailable update service stays quiet and download form works", async () =>
        {
            var model = Model(Successful(), out var services); var checker = new FakeUpdates(new(UpdateCheckStatus.Unavailable));
            var window = new MainWindow(model, services, checker, "0.1.0-dev.6"); window.Show(); window.Activate();
            try { await Until(() => checker.Calls == 1); await Flush(); Check(model.CanStart && !window.OwnedWindows.OfType<UpdateWindow>().Any()); }
            finally { window.Close(); }
        });
        await Case("update button opens exact Windows release after consent", async () =>
        {
            Uri? opened = null; var release = new Uri("https://github.com/sirdimitry/plantain/releases/tag/windows-v0.1.0-dev.7");
            var popup = new UpdateWindow("0.1.0-dev.6", new("0.1.0-dev.7", release), uri => opened = uri);
            popup.Show(); await Flush(); Check(opened is null);
            ((Button)popup.FindName("DownloadUpdateButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(opened == release && !popup.IsVisible);
        });
        await Case("closing main cancels pending startup update check", async () =>
        {
            var model = Model(Successful(), out var services); var checker = new CancellableUpdates();
            var window = new MainWindow(model, services, checker, "0.1.0-dev.6"); window.Show();
            await Until(() => checker.Started); window.Close(); await Until(() => checker.Cancelled);
            Check(!app.Windows.OfType<UpdateWindow>().Any());
        });
        await Case("new update notification waits until active download finishes", async () =>
        {
            var finish = new TaskCompletionSource<DownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var model = Model(new TestRunner((_, _) => finish.Task), out var services); var checker = new PendingUpdates();
            var window = new MainWindow(model, services, checker, "0.1.0-dev.6"); window.Show(); window.Activate();
            try
            {
                await Until(() => checker.Calls == 1); var run = model.StartAsync();
                checker.Result.SetResult(new(UpdateCheckStatus.Available, new("0.1.0-dev.7", new Uri("https://github.com/sirdimitry/plantain/releases/tag/windows-v0.1.0-dev.7"))));
                await Task.Delay(50); await Flush(); Check(!window.OwnedWindows.OfType<UpdateWindow>().Any());
                finish.SetResult(Success); await run; await Until(() => window.OwnedWindows.OfType<UpdateWindow>().Any());
                window.OwnedWindows.OfType<UpdateWindow>().Single().Close();
            }
            finally { finish.TrySetResult(Success); window.Close(); }
        });
        foreach (var dark in new[] { false, true })
            await Case((dark ? "dark" : "light") + " theme renders main and error windows at 100 and 200 percent", async () =>
            {
                var model = Model(new PreviewRunner(), out var services); await model.StartAsync();
                var window = new MainWindow(model, services); window.Show(); ThemeManager.Apply(app.Resources, dark); await Flush();
                try
                {
                    Check(((Button)window.FindName("StartButton")).ActualWidth > 0 && ((Button)window.FindName("IssuesButton")).IsVisible);
                    foreach (var scale in new[] { 1d, 2d }) Save(window, $"main-{(dark ? "dark" : "light")}-{scale:0}x.png", scale);
                    var dialog = new FailuresWindow(model.Failures) { Owner = window }; dialog.Show(); await Flush();
                    try { Save(dialog, $"issues-{(dark ? "dark" : "light")}.png", 1); }
                    finally { dialog.Close(); }
                }
                finally { window.Close(); }
            });
        await Case("compact main window keeps primary controls available", async () =>
        {
            var model = Model(Successful(), out var services); var window = new MainWindow(model, services) { Width = 540, Height = 560 };
            window.Show(); await Flush();
            try
            {
                var start = (Button)window.FindName("StartButton"); var point = start.TranslatePoint(new Point(), window);
                Check(point.Y + start.ActualHeight < window.ActualHeight && point.X >= 0); Save(window, "main-compact.png", 1);
            }
            finally { window.Close(); }
        });
    }

    private static void Save(Window window, string name, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * scale), (int)(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(artifacts, name)); encoder.Save(file);
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition()) { if (DateTime.UtcNow >= deadline) throw new Exception("UI state timed out"); await Task.Delay(20); }
    }
}

internal sealed class FakeUpdates(UpdateCheckResult result) : IUpdateChecker
{
    internal int Calls;
    public Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken token) { Calls++; return Task.FromResult(result); }
}
internal sealed class CancellableUpdates : IUpdateChecker
{
    internal bool Started, Cancelled;
    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken token)
    {
        Started = true;
        try { await Task.Delay(Timeout.Infinite, token); return new(UpdateCheckStatus.NoUpdate); }
        finally { Cancelled = token.IsCancellationRequested; }
    }
}
internal sealed class PendingUpdates : IUpdateChecker
{
    internal int Calls;
    internal TaskCompletionSource<UpdateCheckResult> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken token) { Calls++; return Result.Task; }
}

internal sealed class FakeServices(string destination) : IWindowsServices
{
    public string DefaultDestination => destination;
    internal int Released;
    public string? ChooseFolder(Window owner, string current) => current;
    public void OpenFolder(string folder) { }
    public IDisposable KeepAwake() => new Releaser(() => Released++);
    private sealed class Releaser(Action release) : IDisposable { public void Dispose() => release(); }
}
internal sealed class TestRunner(Func<Action<LoaderEvent>?, CancellationToken, Task<DownloadResult>> run) : IDownloadRunner
{
    public Task<DownloadResult> RunAsync(string link, string destination, Action<LoaderEvent>? report, CancellationToken token) => run(report, token);
}
internal sealed class PreviewRunner : IDownloadRunner
{
    public async Task<DownloadResult> RunAsync(string link, string destination, Action<LoaderEvent>? report, CancellationToken token)
    {
        var names = new[] { "Документ.pdf", "Интервью.mp4", "Обложка.png" };
        for (var i = 0; i < names.Length; i++) report?.Invoke(new FileDiscovered("/Материалы/" + names[i], names[i], 1024 * 1024));
        report?.Invoke(new InventoryFound(3, 3 * 1024 * 1024));
        report?.Invoke(new FileStarted("/Материалы/Документ.pdf", 1024 * 1024, false));
        report?.Invoke(new TransferProgress(1024 * 1024, 1024 * 1024));
        await Task.Delay(180, token);
        report?.Invoke(new FileCompleted("/Материалы/Документ.pdf"));
        var failures = new[] { new FileFailure("/Материалы/Интервью.mp4", "Сервер прервал передачу. Частичная загрузка сохранена."), new FileFailure("/Материалы/Обложка.png", "Существующий файл отличается от оригинала и сохранён без изменений.") };
        foreach (var failure in failures) report?.Invoke(new FileFailed(failure));
        report?.Invoke(new TotalsChanged(1, 0, 1024 * 1024, 2));
        return new(3, 1, 0, 1024 * 1024, 0, failures);
    }
}
internal sealed class EngineRunner : IDownloadRunner
{
    public Task<DownloadResult> RunAsync(string link, string destination, Action<LoaderEvent>? report, CancellationToken token)
        => new DownloadEngine(new Disk(), new Transfer()).RunAsync(destination, report, token);
    private sealed class Disk : IDiskClient
    {
        public Task<RemoteItem> RootAsync(CancellationToken token) => Task.FromResult(new RemoteItem("Folder", "dir"));
        public Task<IReadOnlyList<RemoteItem>> ChildrenAsync(string path, CancellationToken token)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("abcdef")));
            return Task.FromResult<IReadOnlyList<RemoteItem>>([new("bad.mp4", "file", 6, Sha256: hash), new("good.txt", "file", 6, Sha256: hash)]);
        }
        public Task<Uri> DownloadUrlAsync(string path, CancellationToken token) => Task.FromResult(new Uri("https://test.yandex.ru/file"));
    }
    private sealed class Transfer : IFileDownloader
    {
        public async Task TransferAsync(Uri uri, FileStream file, long size, Action<LoaderEvent> report, CancellationToken token)
        { file.SetLength(0); file.Position = 0; await file.WriteAsync(Encoding.UTF8.GetBytes("abcdef"), token); report(new TransferProgress(6, 6)); }
    }
}
