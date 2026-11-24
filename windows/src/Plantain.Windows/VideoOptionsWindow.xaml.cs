using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using Plantain.Core;

namespace Plantain.Windows;

public partial class VideoOptionsWindow : Window
{
    private enum Mode { Original, Video, Audio }

    private sealed record Option(Mode Mode, int Height, string Title)
    {
        internal VideoRendition? Rendition(VideoPreviewFile file) => Mode == Mode.Audio
            ? file.Renditions.FirstOrDefault() : file.Renditions.FirstOrDefault(variant => variant.Height == Height);
        internal bool Includes(VideoPreviewFile file) => Mode == Mode.Original
            ? file.OriginalBytes.HasValue : Rendition(file) is not null;
        internal long? Size(VideoPreviewFile file) => Mode == Mode.Original ? file.OriginalBytes : Rendition(file)?.EstimatedBytes;
    }

    private sealed record Row(string Name, string Detail);
    private readonly string link, destination;
    private readonly IWindowsServices services;
    private readonly string? ffmpeg = VideoPreviewClient.FindFfmpeg();
    private IReadOnlyList<VideoPreviewFile> videos = [];
    private CancellationTokenSource? operation;
    private bool loaded, downloading;

    public VideoOptionsWindow(string link, string destination, IWindowsServices services)
    {
        this.link = link;
        this.destination = destination;
        this.services = services;
        InitializeComponent();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            CloseOrStop(this, new RoutedEventArgs());
        };
    }

    private async void Inspect(object sender, RoutedEventArgs args)
    {
        if (loaded) return;
        loaded = true;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        Progress.IsIndeterminate = true;
        Progress.Visibility = Visibility.Visible;
        try
        {
            using var client = new VideoPreviewClient();
            videos = await client.InspectAsync(link, new Progress<(int Done, int Total)>(scale =>
            {
                if (operation == cancellation && !cancellation.IsCancellationRequested)
                    SetStatus($"Проверено видео: {scale.Done} из {scale.Total}");
            }), cancellation.Token);
            var choices = new List<Option>();
            if (videos.Any(file => file.OriginalBytes.HasValue)) choices.Add(Label(new(Mode.Original, 0, "Исходное качество")));
            var heights = videos.SelectMany(file => file.Renditions).Select(variant => variant.Height).Distinct().Order().ToArray();
            choices.AddRange(heights.Select(height => Label(new(Mode.Video, height, $"{height}p"))));
            if (ffmpeg is not null && heights.Length > 0) choices.Add(Label(new(Mode.Audio, 0, "Только звук")));
            Choices.ItemsSource = choices;
            Choices.SelectedIndex = choices.Count > 0 ? 0 : -1;
            SetStatus(choices.Count > 0 ? "Выберите вариант загрузки." :
                videos.Count == 0 ? "По ссылке не найдено видео." : "Яндекс Диск не выдал доступных вариантов загрузки.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { SetStatus("Проверка остановлена."); }
        catch (Exception error)
        { SetStatus(error is LoaderException ? error.Message : "Не удалось проверить ссылку. Проверьте доступ к сети."); }
        finally
        {
            operation = null;
            Progress.Visibility = Visibility.Collapsed;
            Choices.IsEnabled = Choices.Items.Count > 0;
            DownloadButton.IsEnabled = Choices.SelectedItem is Option;
        }
    }

    private Option Label(Option option)
    {
        var files = videos.Where(option.Includes).ToArray();
        var sizes = files.Select(option.Size).ToArray();
        var size = sizes.All(value => value.HasValue) && sizes.Sum(value => (decimal)value.GetValueOrDefault()) is var sum && sum < long.MaxValue
            ? " · " + (option.Mode == Mode.Original ? "" : "≈ ") + MainViewModel.Bytes((long)sum) : "";
        return option with { Title = $"{option.Title} · {files.Length} из {videos.Count}{size}" };
    }

    private void ChoiceChanged(object sender, SelectionChangedEventArgs args)
    {
        if (Choices.SelectedItem is not Option option) return;
        Files.ItemsSource = videos.Select(file =>
        {
            var size = option.Size(file);
            var detail = !option.Includes(file) ? "Недоступно" :
                size.HasValue ? (option.Mode == Mode.Original ? "" : "≈ ") + MainViewModel.Bytes(size.Value) : "Размер неизвестен";
            if (option.Mode == Mode.Audio && option.Rendition(file) is { } variant) detail = $"{variant.Height}p · {detail}";
            return new Row(file.Name, detail);
        }).ToArray();
        FormatNote.Text = option.Mode switch
        {
            Mode.Original => "Файлы сохраняются в исходном виде.",
            Mode.Audio => "Сохранится дорожка M4A. Размер относится к трафику видеопотока.",
            _ => ffmpeg is null ? "Превью сохранится в MPEG-TS. Для MP4 и звука нужен FFmpeg в PATH."
                : "Превью сохранится в MP4."
        };
        DownloadButton.Content = option.Mode == Mode.Original ? "Скачать исходники" : "Скачать выбранное";
    }

    private async void Download(object sender, RoutedEventArgs args)
    {
        if (operation is not null || Choices.SelectedItem is not Option option) return;
        var selected = videos.Where(option.Includes).ToArray();
        if (selected.Length == 0) return;
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        operation = cancellation;
        downloading = true;
        Choices.IsEnabled = DownloadButton.IsEnabled = false;
        CloseButton.Content = "Остановить";
        Progress.Value = 0;
        Progress.Maximum = selected.Length;
        Progress.IsIndeterminate = option.Mode == Mode.Original;
        Progress.Visibility = Visibility.Visible;
        SetStatus("Готовлю загрузку…");
        var saved = 0;
        var failures = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var awake = services.KeepAwake();
            if (option.Mode == Mode.Original)
            {
                var paths = selected.Select(file => file.RemotePath).ToHashSet(StringComparer.Ordinal);
                var progress = new Progress<LoaderEvent>(item =>
                {
                    if (operation != cancellation || token.IsCancellationRequested) return;
                    if (item is FileStarted file) SetStatus("Обрабатываю " + file.Name.TrimStart('/') + "…");
                    if (item is FileCompleted) Progress.IsIndeterminate = false;
                    if (item is TotalsChanged totals)
                    {
                        Progress.Value = totals.Done;
                        saved = totals.Done;
                        if (saved > 0) OpenButton.Visibility = Visibility.Visible;
                    }
                });
                var result = await Task.Run(async () =>
                {
                    using var api = new DiskApi(link);
                    using var downloader = new FileDownloader();
                    return await new DownloadEngine(api, downloader, selectedPaths: paths)
                        .RunAsync(destination, ((IProgress<LoaderEvent>)progress).Report, token);
                }, token);
                saved = result.Done;
                foreach (var failure in result.Failures) failures[failure.Name] = failure.Message;
            }
            else
            {
                using var client = new VideoPreviewClient();
                for (var index = 0; index < selected.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var file = selected[index];
                    var variant = option.Rendition(file)!;
                    var finished = index;
                    SetStatus($"Загружаю {file.Name} · {variant.Height}p…");
                    var progress = new Progress<(int Done, int Total)>(scale =>
                    {
                        if (operation == cancellation && !token.IsCancellationRequested)
                            Progress.Value = finished + (double)scale.Done / scale.Total;
                    });
                    try
                    {
                        await client.SaveAsync(file, variant, destination, option.Mode == Mode.Audio, ffmpeg, progress, token);
                        saved++;
                        OpenButton.Visibility = Visibility.Visible;
                    }
                    catch (Exception error) when (!token.IsCancellationRequested)
                    { failures[file.Name] = error is LoaderException ? error.Message : "Проверьте доступ к сети и папке."; }
                    Progress.Value = index + 1;
                }
            }
            if (saved > 0) OpenButton.Visibility = Visibility.Visible;
            SetStatus($"Готово: {saved} из {selected.Length}." +
                (failures.Count == 0 ? "" : $" Ошибки: {failures.Count}. Причины указаны в списке."));
            if (failures.Count > 0)
                Files.ItemsSource = selected.Select(file => new Row(file.Name, failures.TryGetValue(file.Name, out var reason) ||
                    failures.TryGetValue(file.RemotePath, out reason) ? reason : "—")).ToArray();
        }
        catch (Exception) when (token.IsCancellationRequested)
        { SetStatus($"Остановлено. Готово: {saved} из {selected.Length}."); }
        catch (Exception error)
        { SetStatus(error is LoaderException ? error.Message : "Не удалось завершить загрузку. Проверьте доступ к сети и папке."); }
        finally
        {
            downloading = false;
            operation = null;
            Progress.Visibility = Visibility.Collapsed;
            Choices.IsEnabled = DownloadButton.IsEnabled = true;
            CloseButton.Content = "Закрыть";
        }
    }

    private void SetStatus(string message)
    {
        Status.Text = message;
        UIElementAutomationPeer.FromElement(Status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void CloseOrStop(object sender, RoutedEventArgs args)
    {
        if (downloading) { operation?.Cancel(); return; }
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        operation?.Cancel();
        args.Cancel = downloading;
    }

    private void OpenFolder(object sender, RoutedEventArgs args)
    {
        try { services.OpenFolder(destination); }
        catch { SetStatus("Не удалось открыть папку сохранения."); }
    }
}
