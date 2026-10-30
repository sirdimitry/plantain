namespace Plantain.Core;

public interface IDownloadRunner
{
    void SkipCurrent() { }
    Task<DownloadResult> RunAsync(string publicLink, string destination, Action<LoaderEvent>? report,
        CancellationToken cancellationToken);
}

public sealed class DiskDownloadRunner : IDownloadRunner
{
    private readonly DownloadControl control = new();
    public void SkipCurrent() => control.SkipCurrent();
    public async Task<DownloadResult> RunAsync(string publicLink, string destination, Action<LoaderEvent>? report,
        CancellationToken cancellationToken)
    {
        using var api = new DiskApi(publicLink);
        using var downloader = new FileDownloader();
        return await new DownloadEngine(api, downloader, control: control).RunAsync(destination, report, cancellationToken).ConfigureAwait(false);
    }
}

// In-memory state for the future Windows UI. A completed download stays visible,
// but its source is no longer an actionable input. Cancellation/errors retain it for retry.
public sealed class DownloadSession(IDownloadRunner? runner = null)
{
    private readonly IDownloadRunner runner = runner ?? new DiskDownloadRunner();
    private readonly object sync = new();
    private string link = "";
    private bool running, canResume;
    private DownloadResult? lastResult;

    public string Link
    {
        get { lock (sync) return link; }
        set
        {
            lock (sync)
            {
                if (running) throw new LoaderException("Нельзя менять ссылку во время загрузки.");
                if (link != value) canResume = false;
                link = value ?? "";
            }
        }
    }
    public bool IsRunning { get { lock (sync) return running; } }
    public bool CanStart { get { lock (sync) return !running && !string.IsNullOrWhiteSpace(link); } }
    public string ButtonText { get { lock (sync) return canResume ? "Продолжить" : "Скачать"; } }
    public DownloadResult? LastResult { get { lock (sync) return lastResult; } }

    public void SkipCurrent() => runner.SkipCurrent();

    public void ResetResult()
    {
        lock (sync)
        {
            if (running) throw new LoaderException("Нельзя сбросить результат во время загрузки.");
            lastResult = null; canResume = false;
        }
    }

    public async Task<DownloadResult> RunAsync(string destination, Action<LoaderEvent>? report = null,
        CancellationToken cancellationToken = default)
    {
        string source;
        lock (sync)
        {
            if (running) throw new LoaderException("Загрузка уже запущена.");
            cancellationToken.ThrowIfCancellationRequested();
            source = NetworkPolicy.ParsePublicLink(link).AbsoluteUri;
            link = source;
            running = true;
            lastResult = null;
        }
        try
        {
            var result = await runner.RunAsync(source, destination, report, cancellationToken).ConfigureAwait(false);
            lock (sync)
            {
                lastResult = result;
                canResume = !result.Succeeded;
                if (result.Succeeded) link = "";
            }
            return result;
        }
        catch
        {
            lock (sync) canResume = true;
            throw;
        }
        finally { lock (sync) running = false; }
    }
}
