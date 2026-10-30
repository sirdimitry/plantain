namespace Plantain.Core;

public interface IFilePublisher
{
    void Publish(string partial, string destination);
}

public sealed class FilePublisher : IFilePublisher
{
    public void Publish(string partial, string destination)
    {
        PathSafety.CheckParents(partial);
        PathSafety.CheckParents(destination);
        File.Move(partial, destination, overwrite: false);
    }
}

public sealed class DownloadEngine(IDiskClient api, IFileDownloader downloader, IRetryDelay? retryDelay = null,
    IFilePublisher? filePublisher = null)
{
    private readonly IRetryDelay delay = retryDelay ?? new RetryDelay();
    private readonly IFilePublisher publisher = filePublisher ?? new FilePublisher();
    private sealed record Work(string Remote, string Local, RemoteItem Item);

    public async Task<DownloadResult> RunAsync(string destination, Action<LoaderEvent>? report = null,
        CancellationToken cancellationToken = default)
    {
        report ??= _ => { };
        cancellationToken.ThrowIfCancellationRequested();
        destination = Path.GetFullPath(destination);
        PathSafety.CheckParents(destination);
        report(new StatusChanged("Собираю список файлов…"));
        var root = await api.RootAsync(cancellationToken).ConfigureAwait(false);
        ValidateItem(root);
        var target = PathSafety.Child(destination, root.Type == "dir" ? root.Name + " copy" : root.Name);
        var cache = PathSafety.Child(destination, ".diskloader-partials");
        if (string.Equals(target, cache, StringComparison.OrdinalIgnoreCase))
            throw new LocalFileException("Имя результата совпадает со служебной папкой.");
        var pending = new Stack<(string Remote, string Local, RemoteItem Item)>();
        var files = new List<Work>();
        var directories = new List<string>();
        var failures = new List<FileFailure>();
        pending.Push(("/", target, root));
        report(new DestinationSelected(root.Type == "dir" ? target : destination));
        while (pending.TryPop(out var entry))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Item.Type == "dir")
            {
                directories.Add(entry.Local);
                var children = await api.ChildrenAsync(entry.Remote, cancellationToken).ConfigureAwait(false);
                foreach (var child in children)
                {
                    ValidateItem(child);
                    var childPath = PathSafety.Child(entry.Local, child.Name);
                    pending.Push(((entry.Remote == "/" ? "" : entry.Remote) + "/" + child.Name, childPath, child));
                }
            }
            else if (entry.Item.Type == "file" && entry.Item.Size is >= 0)
                files.Add(new Work(entry.Remote, entry.Local, entry.Item));
            else throw new FatalApiException("Неизвестный объект или некорректный размер файла в папке.");
        }
        // Reject ambiguous inventories before writing anything. Stage 3 will isolate conflicts per subtree.
        if (files.Select(x => x.Local).Concat(directories).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count + directories.Count)
            throw new LocalFileException("В папке есть совпадающие имена Windows. Загрузка не начата.");
        files.Sort((a, b) => StringComparer.Ordinal.Compare(a.Remote, b.Remote));
        long totalBytes = 0;
        try { foreach (var file in files) totalBytes = checked(totalBytes + file.Item.Size!.Value); }
        catch (OverflowException) { throw new FatalApiException("Суммарный размер файлов превышает поддерживаемый предел."); }
        report(new InventoryFound(files.Count, totalBytes));
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);
        foreach (var directory in directories) Directory.CreateDirectory(directory);
        var done = 0;
        var skipped = 0;
        long completed = 0;
        var missing = new List<Work>();
        report(new StatusChanged("Проверяю уже скачанные файлы…"));
        foreach (var work in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!PathSafety.Exists(work.Local)) { missing.Add(work); continue; }
                report(new FileStarted(work.Remote, work.Item.Size!.Value, true));
                if (await FileVerifier.VerifyAsync(work.Local, work.Item,
                    value => report(new TransferProgress(value, 0)), cancellationToken).ConfigureAwait(false))
                { done++; skipped++; completed += work.Item.Size.Value; }
                else failures.Add(new(work.Remote, "Существующий файл отличается от оригинала и сохранён без изменений."));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or LocalFileException)
            { failures.Add(new(work.Remote, "Не удалось проверить существующий файл. Проверьте доступ и блокировку.")); }
            report(new TotalsChanged(done, skipped, completed, failures.Count));
        }
        report(new StatusChanged($"Уже готово: {skipped}. Скачиваю недостающие файлы…"));
        cancellationToken.ThrowIfCancellationRequested();
        if (missing.Count > 0) Directory.CreateDirectory(cache);
        foreach (var work in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            report(new FileStarted(work.Remote, work.Item.Size!.Value, false));
            try
            {
                await DownloadAsync(work, cache, report, cancellationToken).ConfigureAwait(false);
                done++;
                completed += work.Item.Size.Value;
            }
            catch (Exception error) when (error is LoaderException or IOException or UnauthorizedAccessException)
            {
                failures.Add(new(work.Remote, error is LoaderException ? error.Message : "Не удалось сохранить файл. Проверьте место, доступ и блокировку."));
            }
            report(new TotalsChanged(done, skipped, completed, failures.Count));
        }
        // Cleanup must not turn successful downloads into failures.
        try { if (Directory.Exists(cache) && !Directory.EnumerateFileSystemEntries(cache).Any()) Directory.Delete(cache); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var result = new DownloadResult(files.Count, done, skipped, completed,
            files.Count(x => !x.Item.HasHash), failures.AsReadOnly());
        report(new DownloadFinished(result));
        return result;
    }

    private static void ValidateItem(RemoteItem? item)
    {
        if (item is null || string.IsNullOrEmpty(item.Name) || item.Type is not ("file" or "dir") ||
            (item.Type == "file" && item.Size is not >= 0))
            throw new FatalApiException("Некорректные имя, тип или размер объекта в ответе API.");
        foreach (var (hash, length) in new[] { (item.Sha256, 64), (item.Md5, 32) })
            if (hash is not null && (hash.Length != length || !hash.All(Uri.IsHexDigit)))
                throw new FatalApiException("Некорректная контрольная сумма в ответе API.");
    }

    private async Task DownloadAsync(Work work, string cache, Action<LoaderEvent> report, CancellationToken token)
    {
        var partial = PathSafety.Child(cache, FileVerifier.PartialName(work.Item));
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (PathSafety.Exists(partial) && (!work.Item.HasHash || new FileInfo(partial).Length > work.Item.Size)) File.Delete(partial);
                if (!PathSafety.Exists(partial) || new FileInfo(partial).Length != work.Item.Size)
                {
                    report(new StatusChanged($"Получаю ссылку · попытка {attempt} из 20"));
                    var url = await api.DownloadUrlAsync(work.Remote, token).ConfigureAwait(false);
                    await downloader.TransferAsync(url, partial, work.Item.Size!.Value, report, token).ConfigureAwait(false);
                }
                report(new StatusChanged("Проверяю скачанный файл…"));
                if (!await FileVerifier.VerifyAsync(partial, work.Item, cancellationToken: token).ConfigureAwait(false))
                {
                    File.Delete(partial);
                    throw new NetworkFailureException("Контрольная сумма или размер не совпали. Повторяю с начала.");
                }
                token.ThrowIfCancellationRequested();
                publisher.Publish(partial, work.Local);
                return;
            }
            catch (NetworkFailureException) when (attempt < 20)
            {
                report(new StatusChanged($"Повтор скачивания через {RetryRules.FileDelay(attempt).TotalSeconds:0} с · {attempt}/20"));
                await delay.WaitAsync(RetryRules.FileDelay(attempt), token).ConfigureAwait(false);
            }
            catch (RetryableApiException) when (attempt < 20)
            { await delay.WaitAsync(RetryRules.FileDelay(attempt), token).ConfigureAwait(false); }
        }
    }
}
