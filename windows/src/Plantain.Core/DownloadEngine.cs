namespace Plantain.Core;

public interface IFilePublisher
{
    void Publish(FileStream partial, string destination);
}

public sealed class FilePublisher : IFilePublisher
{
    public void Publish(FileStream partial, string destination)
    {
        using var pins = new DirectoryPins();
        var parent = pins.Pin(Path.GetDirectoryName(destination)!);
        PathSafety.ValidateName(Path.GetFileName(destination));
        WindowsFiles.Rename(partial, parent, Path.GetFileName(destination));
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
        using var pins = new DirectoryPins();
        pins.Pin(destination, create: true);
        var cache = PathSafety.Child(destination, ".diskloader-partials");
        // DELETE access and no delete/write sharing make this directory a kernel-held
        // destination lock. Process exit releases it; no lock file, URL or stale marker.
        pins.Pin(cache, create: true, exclusiveCache: true);
        report(new StatusChanged("Собираю список файлов…"));
        var root = await api.RootAsync(cancellationToken).ConfigureAwait(false);
        ValidateItem(root);
        var failures = new List<FileFailure>();
        var files = new List<Work>();
        var directories = new List<(string Remote, string Local)>();
        var pending = new Stack<(string Remote, string Local, RemoteItem Item)>();
        var knownFiles = 0;
        var sizeOnly = 0;
        long totalBytes = 0;

        void Fail(string name, string message, bool directory = false)
        {
            var failure = new FileFailure(name, message, directory);
            failures.Add(failure);
            report(new FileFailed(failure));
        }

        void Count(RemoteItem item, string remote)
        {
            if (item.Type != "file") return;
            knownFiles++;
            report(new FileDiscovered(remote, item.Name, item.Size!.Value));
            if (!item.HasHash) sizeOnly++;
            try { totalBytes = checked(totalBytes + item.Size!.Value); }
            catch (OverflowException) { throw new FatalApiException("Суммарный размер файлов превышает поддерживаемый предел."); }
        }

        Count(root, "/");
        string target;
        try
        {
            PathSafety.ValidateName(root.Name);
            target = PathSafety.Child(destination, root.Type == "dir" ? root.Name + " copy" : root.Name);
            if (string.Equals(target, cache, StringComparison.OrdinalIgnoreCase))
                throw new LocalFileException("Имя результата совпадает со служебной папкой.");
            pending.Push(("/", target, root));
        }
        catch (LocalFileException error) { target = destination; Fail("/", error.Message, root.Type == "dir"); }
        report(new DestinationSelected(root.Type == "dir" ? target : destination));
        while (pending.TryPop(out var entry))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Item.Type == "file") { files.Add(new(entry.Remote, entry.Local, entry.Item)); continue; }
            directories.Add((entry.Remote, entry.Local));
            var children = await api.ChildrenAsync(entry.Remote, cancellationToken).ConfigureAwait(false);
            var collisions = children.Where(x => x is not null && x.Name is not null)
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
                .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateItem(child);
                var remote = (entry.Remote == "/" ? "" : entry.Remote) + "/" + child.Name;
                Count(child, remote);
                if (collisions.Contains(child.Name))
                {
                    Fail(remote, "Имя совпадает с другим объектом без учёта регистра. Оба объекта пропущены.", child.Type == "dir");
                    continue;
                }
                try { pending.Push((remote, PathSafety.Child(entry.Local, child.Name), child)); }
                catch (LocalFileException error) { Fail(remote, error.Message, child.Type == "dir"); }
            }
        }
        files.Sort((a, b) => StringComparer.Ordinal.Compare(a.Remote, b.Remote));
        report(new InventoryFound(knownFiles, totalBytes));
        cancellationToken.ThrowIfCancellationRequested();
        var blocked = new List<string>();
        bool IsBlocked(string path) => blocked.Any(dir => path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        foreach (var directory in directories.OrderBy(x => x.Local.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsBlocked(directory.Local)) continue;
            try { pins.Pin(directory.Local, create: true); }
            catch (Exception error) when (IsLocal(error))
            { blocked.Add(directory.Local); Fail(directory.Remote, LocalMessage(error), true); }
        }
        var done = 0;
        var skipped = 0;
        long completed = 0;
        var missing = new List<Work>();
        report(new StatusChanged("Проверяю уже скачанные файлы…"));
        foreach (var work in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsBlocked(work.Local)) continue;
            try
            {
                FileSystemLimits.CheckSize(destination, work.Item.Size!.Value);
                FileStream existing;
                try { existing = pins.OpenRead(work.Local); }
                catch (FileNotFoundException) { missing.Add(work); continue; }
                await using (existing)
                {
                    report(new FileStarted(work.Remote, work.Item.Size!.Value, true));
                    if (await FileVerifier.VerifyAsync(existing, work.Item,
                        value => report(new TransferProgress(value, 0)), cancellationToken).ConfigureAwait(false))
                    { done++; skipped++; completed += work.Item.Size.Value; report(new FileCompleted(work.Remote)); }
                    else Fail(work.Remote, "Существующий файл отличается от оригинала и сохранён без изменений.");
                }
            }
            catch (Exception error) when (IsLocal(error)) { Fail(work.Remote, LocalMessage(error)); }
            report(new TotalsChanged(done, skipped, completed, failures.Count));
        }
        report(new StatusChanged($"Уже готово: {skipped}. Скачиваю недостающие файлы…"));
        foreach (var work in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            report(new FileStarted(work.Remote, work.Item.Size!.Value, false));
            try
            {
                await DownloadAsync(work, cache, pins, report, cancellationToken).ConfigureAwait(false);
                done++;
                completed += work.Item.Size.Value;
                report(new FileCompleted(work.Remote));
            }
            catch (Exception error) when (error is LoaderException || IsLocal(error))
            { Fail(work.Remote, LocalMessage(error)); }
            report(new TotalsChanged(done, skipped, completed, failures.Count));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var result = new DownloadResult(knownFiles, done, skipped, completed, sizeOnly, failures.AsReadOnly());
        report(new DownloadFinished(result));
        return result;
    }

    private static bool IsLocal(Exception error) => error is IOException or UnauthorizedAccessException or LocalFileException;
    private static string LocalMessage(Exception error) => error is LoaderException ? error.Message :
        "Не удалось сохранить или проверить файл. Проверьте место, доступ и блокировку.";

    private static void ValidateItem(RemoteItem? item)
    {
        if (item is null || string.IsNullOrEmpty(item.Name) || item.Type is not ("file" or "dir") ||
            (item.Type == "file" && item.Size is not >= 0))
            throw new FatalApiException("Некорректные имя, тип или размер объекта в ответе API.");
        foreach (var (hash, length) in new[] { (item.Sha256, 64), (item.Md5, 32) })
            if (hash is not null && (hash.Length != length || !hash.All(Uri.IsHexDigit)))
                throw new FatalApiException("Некорректная контрольная сумма в ответе API.");
    }

    private async Task DownloadAsync(Work work, string cache, DirectoryPins pins, Action<LoaderEvent> report, CancellationToken token)
    {
        var partial = PathSafety.Child(cache, FileVerifier.PartialName(work.Item));
        await using var file = pins.OpenPartial(partial);
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!work.Item.HasHash || file.Length > work.Item.Size) file.SetLength(0);
                if (file.Length != work.Item.Size)
                {
                    report(new StatusChanged($"Получаю ссылку · попытка {attempt} из 20"));
                    var url = await api.DownloadUrlAsync(work.Remote, token).ConfigureAwait(false);
                    await downloader.TransferAsync(url, file, work.Item.Size!.Value, report, token).ConfigureAwait(false);
                }
                report(new StatusChanged("Проверяю скачанный файл…"));
                await file.FlushAsync(token).ConfigureAwait(false);
                if (!await FileVerifier.VerifyAsync(file, work.Item, cancellationToken: token).ConfigureAwait(false))
                {
                    file.SetLength(0);
                    throw new NetworkFailureException("Контрольная сумма или размер не совпали. Повторяю с начала.");
                }
                token.ThrowIfCancellationRequested();
                publisher.Publish(file, work.Local);
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

internal static class FileSystemLimits
{
    internal static void CheckSize(string destination, long size)
    {
        string format;
        try { format = new DriveInfo(Path.GetPathRoot(destination)!).DriveFormat; }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { return; }
        CheckFormat(format, size);
    }

    internal static void CheckFormat(string format, long size)
    {
        if (format.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && size > uint.MaxValue)
            throw new LocalFileException("Файл слишком большой для FAT32. Выберите том NTFS или exFAT.");
    }
}
