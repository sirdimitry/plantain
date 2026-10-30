using System.Net;

namespace Plantain.Core;

internal sealed record NetworkTimeouts(TimeSpan Headers, TimeSpan IdleRead, TimeSpan Total)
{
    public static NetworkTimeouts Api { get; } = new(TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(90));
    public static NetworkTimeouts Download { get; } = new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), TimeSpan.FromDays(7));
}

// No automatic redirects: every destination is checked before a request is sent.
internal sealed class SafeHttp : IDisposable
{
    private readonly HttpClient client;
    private readonly Func<Uri, bool> allowed;
    internal NetworkTimeouts Timeouts { get; }

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        DefaultProxyCredentials = null,
        PreAuthenticate = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(45),
        ActivityHeadersPropagator = null
    };

    internal SafeHttp(Func<Uri, bool> allowed, NetworkTimeouts timeouts, HttpMessageHandler? handler = null)
    {
        this.allowed = allowed;
        Timeouts = timeouts;
        client = new HttpClient(handler ?? CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal async Task<HttpResponseMessage> GetAsync(Uri uri, long? offset, CancellationToken token)
    {
        for (var redirect = 0; redirect <= 10; redirect++)
        {
            token.ThrowIfCancellationRequested();
            if (!allowed(uri)) throw new FatalApiException("Небезопасный адрес или перенаправление.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
            request.Headers.TryAddWithoutValidation("Accept", offset.HasValue ? "*/*" : "application/json");
            if (offset > 0) request.Headers.Range = new(offset, null);
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(token);
            headers.CancelAfter(Timeouts.Headers);
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new NetworkFailureException("Сервер не прислал ответ вовремя."); }
            catch (HttpRequestException) { throw new NetworkFailureException("Не удалось установить защищённое соединение с сервером."); }

            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || !Uri.TryCreate(uri, location, out var next))
                throw new FatalApiException("Сервер вернул некорректное перенаправление.");
            uri = next;
        }
        throw new FatalApiException("Слишком много перенаправлений.");
    }

    internal async ValueTask<int> ReadAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        idle.CancelAfter(Timeouts.IdleRead);
        try { return await stream.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new NetworkFailureException("Сервер перестал передавать данные."); }
        catch (IOException) { throw new NetworkFailureException("Соединение прервалось при получении данных."); }
        catch (HttpRequestException) { throw new NetworkFailureException("Соединение прервалось при получении данных."); }
    }

    public void Dispose() => client.Dispose();
}
