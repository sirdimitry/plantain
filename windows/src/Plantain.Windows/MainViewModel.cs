using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using Plantain.Core;

namespace Plantain.Windows;

public sealed record FailureRow(string Name, string RelativePath, string Reason, bool IsDirectory = false);

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly DownloadSession session;
    private readonly Dispatcher dispatcher;
    private readonly IWindowsServices services;
    private readonly Dictionary<string, FileDiscovered> discovered = new(StringComparer.Ordinal);
    private readonly HashSet<string> completedFiles = new(StringComparer.Ordinal);
    private readonly List<FileFailure> reportedFailures = [];
    private readonly ConcurrentQueue<LoaderEvent> pendingEvents = new();
    private bool batching;
    private readonly Stopwatch networkTime = new();
    private readonly DispatcherTimer timer;
    private CancellationTokenSource? cancellation;
    private Task? activeTask;
    private string destination, status = "Вставьте публичную ссылку Яндекс Диска", error = "", file = "";
    private string? resultFolder;
    private bool running, stopping, verifying, succeeded;
    private long total, completed, current, currentSize, networkBytes;
    private int count, done, skipped, errors;

    public MainViewModel(IDownloadRunner? runner = null, IWindowsServices? services = null)
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        this.services = services ?? new WindowsServices();
        session = new DownloadSession(runner);
        destination = this.services.DefaultDestination;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
            (_, _) => { DrainEvents(1000); Changed(nameof(SpeedText), nameof(EtaText)); }, dispatcher);
        timer.Stop();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<FailureRow> Failures { get; } = [];
    public string Link { get => session.Link; set { session.Link = value; Changed(nameof(Link), nameof(CanStart), nameof(StartText)); } }
    public string Destination => destination;
    public string DestinationLabel => destination.Length == 0 ? "Выберите папку сохранения" : destination;
    public bool IsRunning => running;
    public bool CanEdit => !running;
    public bool IsStopping => stopping;
    public bool CanStop => running && !stopping;
    public bool CanStart => !running && session.CanStart && destination.Length > 0;
    public string StartText => session.ButtonText;
    public string StopText => stopping ? "Останавливаю…" : "Остановить";
    public string Status => status;
    public string Error => error;
    public bool HasError => error.Length > 0;
    public bool HasIssues => Failures.Count > 0;
    public string IssuesText => $"Не скачано: {Failures.Count}. Открыть список";
    public string CurrentFile => file.Length == 0 ? (succeeded ? "Готово" : "—") : file;
    public string FileHeading => verifying ? "Проверка файла" : "Текущий файл";
    public double TotalFraction => total > 0 ? Math.Clamp((double)(completed + (verifying ? 0 : current)) / total, 0, 1) : succeeded ? 1 : 0;
    public double FileFraction => currentSize > 0 ? Math.Clamp((double)current / currentSize, 0, 1) : 0;
    public string Percent => TotalFraction.ToString("P1", CultureInfo.GetCultureInfo("ru-RU"));
    public string TotalBytesText => $"{Bytes(Math.Min(total, completed + (verifying ? 0 : current)))} из {Bytes(total)}";
    public string FileBytesText => $"{Bytes(current)} из {Bytes(currentSize)}";
    public string DoneText => $"{done} из {count}";
    public string SkippedText => skipped.ToString(CultureInfo.CurrentCulture);
    public string RemainingText => Math.Max(0, count - done).ToString(CultureInfo.CurrentCulture);
    public string ErrorCountText => errors.ToString(CultureInfo.CurrentCulture);
    private double Speed => networkTime.Elapsed.TotalSeconds > 0 ? networkBytes / networkTime.Elapsed.TotalSeconds : 0;
    public string SpeedText => Speed > 0 && running ? Bytes((long)Speed) + "/с" : "—";
    public string EtaText => Speed > 0 && running ? Duration(Math.Max(0, total - completed - (verifying ? 0 : current)) / Speed) : "—";
    public string FolderToOpen => resultFolder ?? destination;

    public Task StartAsync()
    {
        dispatcher.VerifyAccess();
        if (running || !CanStart) return Task.CompletedTask;
        activeTask = RunAsync();
        return activeTask;
    }

    private async Task RunAsync()
    {
        running = true; stopping = false; succeeded = false; verifying = false;
        error = ""; file = ""; resultFolder = null; status = "Подключаюсь к Яндекс Диску…";
        total = completed = current = currentSize = networkBytes = 0;
        count = done = skipped = errors = 0;
        discovered.Clear(); completedFiles.Clear(); reportedFailures.Clear(); Failures.Clear();
        pendingEvents.Clear();
        networkTime.Reset(); timer.Start();
        cancellation = new CancellationTokenSource();
        Refresh();
        IDisposable? awake = null;
        try
        {
            awake = services.KeepAwake();
            var token = cancellation.Token;
            var result = await Task.Run(() => session.RunAsync(destination, pendingEvents.Enqueue, token));
            DrainEvents();
            foreach (var failure in result.Failures) if (!reportedFailures.Contains(failure)) reportedFailures.Add(failure);
            succeeded = result.Succeeded;
            count = result.Files; done = result.Done; skipped = result.Skipped; completed = result.CompletedBytes;
            current = currentSize = 0; file = ""; verifying = false;
            RebuildFailures(result.Succeeded ? "" : "Файл не скачан. Повторите загрузку.");
            if (result.Succeeded)
                status = result.Files == 0 ? "Папка пуста — структура сохранена" : result.SizeOnlyFiles > 0
                    ? $"Все файлы на месте. {result.SizeOnlyFiles} — проверены только по размеру: сервер не отдал хеш."
                    : "Все файлы скачаны и проверены";
            else status = $"Готово {done} из {count}. Откройте список того, что не скачалось.";
        }
        catch (OperationCanceledException)
        {
            DrainEvents();
            status = "Остановлено. Готовые файлы и частичная загрузка сохранены.";
            RebuildFailures("Не скачано: загрузка остановлена.");
        }
        catch (Exception failure)
        {
            DrainEvents();
            error = failure is LoaderException ? failure.Message : "Не удалось завершить загрузку. Проверьте доступ к сети и папке.";
            status = "Загрузка не завершена";
            RebuildFailures(error);
            if (Failures.Count == 0) Failures.Add(new("Список файлов недоступен", "", error, true));
        }
        finally
        {
            try { awake?.Dispose(); }
            finally
            {
                timer.Stop(); networkTime.Stop();
                cancellation.Dispose(); cancellation = null;
                running = false; stopping = false; errors = Failures.Count;
                Refresh();
            }
        }
    }

    public void Stop()
    {
        dispatcher.VerifyAccess();
        if (!CanStop) return;
        stopping = true; status = "Останавливаю и сохраняю загрузку…";
        cancellation?.Cancel(); Refresh();
    }

    public async Task StopAndWaitAsync()
    {
        Stop();
        if (activeTask is not null) await activeTask;
    }

    public void SetDestination(string value)
    {
        if (running || destination == value) return;
        destination = value; resultFolder = null;
        session.ResetResult();
        total = completed = current = currentSize = networkBytes = 0;
        count = done = skipped = errors = 0; succeeded = verifying = false; file = error = "";
        discovered.Clear(); completedFiles.Clear(); reportedFailures.Clear(); Failures.Clear(); pendingEvents.Clear(); networkTime.Reset();
        status = destination.Length == 0 ? "Выберите папку сохранения" : "Папка сохранения выбрана";
        Refresh();
    }

    internal void Receive(LoaderEvent value)
    {
        switch (value)
        {
            case StatusChanged e: if (!stopping) status = e.Text; break;
            case DestinationSelected e: resultFolder = e.Path; break;
            case FileDiscovered e: discovered[e.RemotePath] = e; break;
            case FileCompleted e: completedFiles.Add(e.RemotePath); break;
            case FileFailed e: reportedFailures.Add(e.Failure); break;
            case InventoryFound e: count = e.Files; total = e.Bytes; break;
            case FileStarted e:
                file = discovered.TryGetValue(e.Name, out var item) ? item.Name : e.Name.TrimStart('/');
                currentSize = e.Size; current = 0; verifying = e.Verifying; break;
            case TransferProgress e:
                current = e.CurrentBytes;
                if (e.NetworkBytes > 0) { if (!networkTime.IsRunning) networkTime.Start(); networkBytes += e.NetworkBytes; }
                break;
            case TotalsChanged e: done = e.Done; skipped = e.Skipped; completed = e.CompletedBytes; errors = e.Errors; current = 0; break;
        }
        if (!batching) Refresh();
    }

    private void DrainEvents(int maximum = int.MaxValue)
    {
        batching = true;
        try { for (var i = 0; i < maximum && pendingEvents.TryDequeue(out var value); i++) Receive(value); }
        finally { batching = false; Refresh(); }
    }

    private void RebuildFailures(string fallback)
    {
        Failures.Clear();
        var byName = reportedFailures.GroupBy(x => x.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var item in discovered.Values.Where(x => !completedFiles.Contains(x.RemotePath)))
        {
            byName.TryGetValue(item.RemotePath, out var failure);
            var parent = item.RemotePath;
            while (failure is null && parent.Length > 1)
            {
                var slash = parent.LastIndexOf('/'); parent = slash > 0 ? parent[..slash] : "/";
                if (byName.TryGetValue(parent, out var candidate) && candidate.IsDirectory) failure = candidate;
            }
            Failures.Add(new(item.Name, item.RemotePath == "/" ? item.Name : item.RemotePath.TrimStart('/'), failure?.Message ?? fallback));
        }
        foreach (var failure in reportedFailures)
        {
            if (discovered.ContainsKey(failure.Name)) continue;
            var path = failure.Name.TrimStart('/');
            var name = failure.IsDirectory ? $"Папка «{(path.Length > 0 ? path.Split('/')[^1] : "корень") }»" : path.Split('/')[^1];
            Failures.Add(new(name, path, failure.Message, failure.IsDirectory));
        }
        Changed(nameof(HasIssues), nameof(IssuesText));
    }

    private void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    private void Changed(params string[] names) { foreach (var name in names) PropertyChanged?.Invoke(this, new(name)); }
    internal static string Bytes(long bytes)
    {
        var units = new[] { "Б", "КиБ", "МиБ", "ГиБ", "ТиБ" }; var value = (double)Math.Max(0, bytes); var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return value.ToString(index == 0 ? "0" : "0.0", CultureInfo.GetCultureInfo("ru-RU")) + " " + units[index];
    }
    internal static string Duration(double seconds) => seconds >= 3600 ? $"{(int)(seconds / 3600)} ч {(int)(seconds % 3600 / 60)} мин" :
        seconds >= 60 ? $"{(int)(seconds / 60)} мин {(int)(seconds % 60)} с" : $"{Math.Max(0, (int)seconds)} с";
}
