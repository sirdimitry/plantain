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
    private readonly string link;
    private readonly string? ffmpeg = VideoPreviewClient.FindFfmpeg();
    private IReadOnlyList<VideoPreviewFile> videos = [];
    private CancellationTokenSource? operation;
    private bool loaded;
    internal VideoSelection? Selection { get; private set; }

    public VideoOptionsWindow(string link)
    {
        this.link = link;
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

    private void Download(object sender, RoutedEventArgs args)
    {
        if (operation is not null || Choices.SelectedItem is not Option option) return;
        var selected = videos.Where(option.Includes).ToArray();
        if (selected.Length == 0) return;
        Selection = option.Mode == Mode.Original
            ? new VideoSelection(selected.Select(file => file.RemotePath).ToHashSet(StringComparer.Ordinal), [], "", false, null)
            : new VideoSelection(null, selected.Select(file => (file, option.Rendition(file)!)).ToArray(),
                option.Mode == Mode.Audio ? "Только звук" : $"{option.Height}p", option.Mode == Mode.Audio, ffmpeg);
        DialogResult = true;
    }

    private void SetStatus(string message)
    {
        Status.Text = message;
        UIElementAutomationPeer.FromElement(Status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void CloseOrStop(object sender, RoutedEventArgs args) => Close();

    private void OnClosing(object? sender, CancelEventArgs args) => operation?.Cancel();
}
