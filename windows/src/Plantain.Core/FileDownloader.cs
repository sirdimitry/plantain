namespace Plantain.Core;

public interface IFileDownloader
{
    Task TransferAsync(Uri uri, string partial, long expectedSize, Action<LoaderEvent> report,
        CancellationToken cancellationToken);
}

public sealed class FileDownloader : IFileDownloader, IDisposable
{
    private readonly SafeHttp http;
    public FileDownloader() : this(new SafeHttp(NetworkPolicy.IsDownload, NetworkTimeouts.Download)) { }
    internal FileDownloader(SafeHttp http) => this.http = http;

    public async Task TransferAsync(Uri uri, string partial, long expectedSize, Action<LoaderEvent> report,
        CancellationToken cancellationToken = default)
    {
        if (expectedSize < 0) throw new FatalApiException("Некорректный размер файла.");
        cancellationToken.ThrowIfCancellationRequested();
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(http.Timeouts.Total);
        try
        {
            var offset = PathSafety.Exists(partial) ? new FileInfo(partial).Length : 0;
            using var response = await http.GetAsync(uri, offset, total.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status == 200) offset = 0;
            else if (status == 206)
            {
                var range = response.Content.Headers.ContentRange;
                if (range is null || range.Unit != "bytes" || range.From != offset || range.Length != expectedSize ||
                    range.To != expectedSize - 1 || range.To < range.From)
                    throw new NetworkFailureException("Сервер вернул неверную часть файла.");
            }
            else
            {
                if (status == 416 && PathSafety.Exists(partial)) File.Delete(partial);
                throw new NetworkFailureException($"Сервер скачивания: HTTP {status}.");
            }
            if (response.Content.Headers.ContentEncoding.Any(value => value != "identity"))
                throw new NetworkFailureException("Сервер вернул сжатые данные вместо исходного файла.");
            PathSafety.CheckParents(partial);
            await using var file = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (offset == 0) file.SetLength(0);
            file.Position = offset;
            report(new TransferProgress(offset, 0));
            using var stream = await response.Content.ReadAsStreamAsync(total.Token).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            long pending = 0;
            var last = Environment.TickCount64;
            try
            {
                int read;
                while ((read = await http.ReadAsync(stream, buffer, total.Token).ConfigureAwait(false)) > 0)
                {
                    if (read > expectedSize - offset) throw new NetworkFailureException("Размер файла превысил объявленный.");
                    await file.WriteAsync(buffer.AsMemory(0, read), total.Token).ConfigureAwait(false);
                    offset += read;
                    pending += read;
                    if (Environment.TickCount64 - last >= 120)
                    {
                        report(new TransferProgress(offset, pending));
                        pending = 0;
                        last = Environment.TickCount64;
                    }
                }
                await file.FlushAsync(total.Token).ConfigureAwait(false);
                if (offset != expectedSize) throw new NetworkFailureException("Файл получен не полностью.");
            }
            finally { report(new TransferProgress(offset, pending)); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new NetworkFailureException("Превышено общее время передачи файла."); }
        // Network reads are translated inside SafeHttp. Remaining IO errors are local and must not retry.
        catch (IOException) { throw new LocalFileException("Не удалось записать файл: проверьте свободное место, доступ и блокировку файла."); }
        catch (UnauthorizedAccessException) { throw new LocalFileException("Нет доступа для записи файла."); }
    }

    public void Dispose() => http.Dispose();
}
