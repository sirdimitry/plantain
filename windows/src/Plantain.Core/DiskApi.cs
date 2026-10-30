using System.Globalization;
using System.Text.Json;

namespace Plantain.Core;

public sealed class DiskApi : IDiskClient, IDisposable
{
    private readonly string link;
    private readonly Uri apiBase;
    private readonly SafeHttp http;
    private readonly IRetryDelay delay;
    private readonly Func<Uri, bool> allowedDownload;

    public DiskApi(string publicLink) : this(NetworkPolicy.ParsePublicLink(publicLink).AbsoluteUri,
        new Uri("https://cloud-api.yandex.net"), new SafeHttp(NetworkPolicy.IsApi, NetworkTimeouts.Api),
        new RetryDelay(), NetworkPolicy.IsDownload) { }

    internal DiskApi(string link, Uri apiBase, SafeHttp http, IRetryDelay delay, Func<Uri, bool> allowedDownload)
    {
        this.link = link;
        this.apiBase = apiBase;
        this.http = http;
        this.delay = delay;
        this.allowedDownload = allowedDownload;
    }

    public Task<RemoteItem> RootAsync(CancellationToken cancellationToken = default) => GetAsync<RemoteItem>("/", false, null, cancellationToken);

    public async Task<IReadOnlyList<RemoteItem>> ChildrenAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = new List<RemoteItem>();
        long offset = 0;
        while (true)
        {
            var resource = await GetAsync<RemoteItem>(path, false, offset, cancellationToken).ConfigureAwait(false);
            var listing = resource.Embedded ?? throw new FatalApiException("Не удалось получить список папки.");
            if (listing.Items is null || listing.Total < 0) throw new FatalApiException("Некорректный список папки.");
            if (listing.Items.Length == 0)
            {
                if (offset < listing.Total) throw new FatalApiException("Список папки получен не полностью.");
                break;
            }
            foreach (var item in listing.Items)
            {
                if (item is null) throw new FatalApiException("Некорректный объект в списке папки.");
                result.Add(item);
            }
            offset = checked(offset + listing.Items.Length);
            if (listing.Total.HasValue && offset >= listing.Total) break;
        }
        return result;
    }

    public async Task<Uri> DownloadUrlAsync(string path, CancellationToken cancellationToken = default)
    {
        var response = await GetAsync<DownloadLink>(path, true, null, cancellationToken).ConfigureAwait(false);
        if (!Uri.TryCreate(response.Href, UriKind.Absolute, out var uri) || !allowedDownload(uri))
            throw new FatalApiException("Сервер не предоставил безопасную ссылку скачивания.");
        return uri;
    }

    private async Task<T> GetAsync<T>(string path, bool download, long? offset, CancellationToken token)
    {
        var query = $"public_key={Uri.EscapeDataString(link)}&path={Uri.EscapeDataString(path)}";
        if (offset.HasValue) query += "&limit=200&sort=name&offset=" + offset.Value.ToString(CultureInfo.InvariantCulture);
        var uri = new Uri(apiBase, "/v1/disk/public/resources" + (download ? "/download" : "") + "?" + query);
        var attempts = download ? 1 : 5;
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            using var total = CancellationTokenSource.CreateLinkedTokenSource(token);
            total.CancelAfter(http.Timeouts.Total);
            try
            {
                using var response = await http.GetAsync(uri, null, total.Token).ConfigureAwait(false);
                var code = (int)response.StatusCode;
                if (code != 200)
                {
                    if (RetryRules.IsRetryableStatus(code)) throw new RetryableApiException($"Яндекс Диск: HTTP {code}.");
                    throw new FatalApiException(code switch
                    {
                        404 => "Папка или файл не найдены. Проверьте ссылку и доступ по ней.",
                        401 or 403 => "Ссылка не публичная или доступ закрыт.",
                        _ => $"Яндекс Диск: HTTP {code}."
                    });
                }
                // Bound metadata as well as file-transfer memory. Never cache API bodies on disk.
                using var bytes = new MemoryStream();
                using var stream = await response.Content.ReadAsStreamAsync(total.Token).ConfigureAwait(false);
                var buffer = new byte[32 * 1024];
                int read;
                while ((read = await http.ReadAsync(stream, buffer, total.Token).ConfigureAwait(false)) > 0)
                {
                    if (bytes.Length + read > 16 * 1024 * 1024) throw new FatalApiException("Слишком большой ответ API.");
                    bytes.Write(buffer, 0, read);
                }
                return JsonSerializer.Deserialize<T>(bytes.GetBuffer().AsSpan(0, (int)bytes.Length))
                    ?? throw new FatalApiException("Пустой ответ API.");
            }
            catch (JsonException) { throw new FatalApiException("Не удалось прочитать ответ API."); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                if (attempt >= attempts) throw new NetworkFailureException("Превышено время ожидания API.");
            }
            catch (Exception error) when (error is NetworkFailureException or RetryableApiException)
            { if (attempt >= attempts) throw; }
            await delay.WaitAsync(RetryRules.ApiDelay(attempt), token).ConfigureAwait(false);
        }
    }

    public void Dispose() => http.Dispose();
}
