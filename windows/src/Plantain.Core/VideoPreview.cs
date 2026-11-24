using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Plantain.Core;

public sealed record VideoRendition(int Height, long? EstimatedBytes, Uri Playlist);
public sealed record VideoPreviewFile(string Name, string RemotePath, long? OriginalBytes,
    IReadOnlyList<VideoRendition> Renditions);

public sealed class VideoPreviewClient : IDisposable
{
    private readonly SafeHttp streams = new(NetworkPolicy.IsPreviewStream, NetworkTimeouts.Api);
    private static readonly Regex Prefetch = new(
        """<script[^>]+id="store-prefetch"[^>]*>\s*(\{.*?\})\s*</script>""",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public async Task<IReadOnlyList<VideoPreviewFile>> InspectAsync(string link,
        IProgress<(int Done, int Total)>? progress, CancellationToken token)
    {
        var source = NetworkPolicy.ParsePublicLink(link);
        using var api = new DiskApi(link);
        var root = await api.RootAsync(token).ConfigureAwait(false);
        var candidates = new List<(RemoteItem Item, string Path, Uri Page)>();
        var folders = new Stack<string>();
        if (root.Type == "dir") folders.Push("/");
        else if (root.Type == "file" && root.MediaType is null or "video") candidates.Add((root, "/", source));
        while (folders.TryPop(out var folder))
        {
            foreach (var item in await api.ChildrenAsync(folder, token).ConfigureAwait(false))
            {
                var path = folder.TrimEnd('/') + "/" + item.Name;
                if (item.Type == "dir") folders.Push(path);
                else if (item.Type == "file" && item.MediaType == "video")
                {
                    var page = new UriBuilder(source)
                    {
                        Path = source.AbsolutePath.TrimEnd('/') + "/" +
                            string.Join("/", path[1..].Split('/').Select(Uri.EscapeDataString)),
                        Fragment = ""
                    }.Uri;
                    candidates.Add((item, path, page));
                }
            }
        }

        var result = new System.Collections.Concurrent.ConcurrentBag<VideoPreviewFile>();
        var completed = 0;
        await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = token },
            async (candidate, ct) =>
            {
                var original = OptionalAsync(() => api.DownloadUrlAsync(candidate.Path, ct), ct);
                var renditions = await FindRenditionsAsync(candidate.Page, candidate.Path, ct).ConfigureAwait(false);
                result.Add(new(candidate.Path == "/" ? candidate.Item.Name : candidate.Path[1..],
                    candidate.Path, await original.ConfigureAwait(false) is null ? null : candidate.Item.Size, renditions));
                progress?.Report((Interlocked.Increment(ref completed), candidates.Count));
            }).ConfigureAwait(false);
        return result.OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private async Task<IReadOnlyList<VideoRendition>> FindRenditionsAsync(Uri page, string path, CancellationToken token)
    {
        // A failed metadata or playlist request can look like a video with no preview.
        // Retry only those files and stagger requests so parallel checks do not repeat in bursts.
        for (var attempt = 0; ; attempt++)
        {
            var renditions = await OptionalAsync(() => RenditionsAsync(page, path, token), token)
                .ConfigureAwait(false);
            if (renditions is { Count: > 0 } || attempt == 2) return renditions ?? [];

            var delay = TimeSpan.FromMilliseconds(2000 * (attempt + 1) + Random.Shared.Next(1000));
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
    }

    private static async Task<T?> OptionalAsync<T>(Func<Task<T>> operation, CancellationToken token) where T : class
    {
        try { return await operation().ConfigureAwait(false); }
        catch (Exception error) when (!token.IsCancellationRequested &&
            error is LoaderException or HttpRequestException or IOException or JsonException or
                OperationCanceledException or RegexMatchTimeoutException) { return null; }
    }

    private static JsonElement Field(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value : default;

    private static string? Text(JsonElement item, string name)
    {
        var value = Field(item, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static async Task<JsonElement> FetchMetadataAsync(Uri page, string remotePath, CancellationToken token)
    {
        using var handler = SafeHttp.CreateHandler();
        handler.UseCookies = true;
        handler.AutomaticDecompression = DecompressionMethods.All;
        handler.CookieContainer = new CookieContainer();
        using var browser = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(35) };
        browser.DefaultRequestHeaders.UserAgent.ParseAdd("Plantain-Windows/0.1");
        browser.DefaultRequestHeaders.Accept.ParseAdd("text/html, application/json;q=0.9");
        browser.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var ct = timeout.Token;
        string html;
        for (var redirect = 0; ; redirect++)
        {
            if (!NetworkPolicy.IsPublicDisk(page) || redirect > 8) return default;
            using var response = await browser.GetAsync(page, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 &&
                Uri.TryCreate(page, response.Headers.Location?.OriginalString, out var next))
            {
                page = next;
                continue;
            }
            response.EnsureSuccessStatusCode();
            html = await ReadTextAsync(response, ct).ConfigureAwait(false);
            break;
        }

        var match = Prefetch.Match(html);
        if (!match.Success) return default;
        using var document = JsonDocument.Parse(match.Groups[1].Value);
        var store = document.RootElement;
        var resources = Field(store, "resources");
        var root = Field(resources, Text(store, "rootResourceId") ?? "");
        var file = root;
        if (remotePath != "/")
        {
            var hash = Text(root, "hash") ?? Text(root, "path");
            if (hash is null || resources.ValueKind != JsonValueKind.Object) return default;
            file = resources.EnumerateObject().Select(resource => resource.Value)
                .FirstOrDefault(resource => Text(resource, "path") == hash + ":" + remotePath);
        }
        var metadata = Field(file, "videoStreams");
        if (Field(metadata, "videos").ValueKind == JsonValueKind.Array && Field(metadata, "videos").GetArrayLength() > 0)
            return metadata.Clone();
        var fileHash = Text(file, "path") ?? Text(file, "hash");
        var sk = Text(Field(store, "environment"), "sk");
        if (fileHash is null || sk is null) return default;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(page, "/public/api/get-video-streams"));
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            request.Headers.TryAddWithoutValidation("X-Retpath-Y", page.AbsoluteUri);
            request.Headers.TryAddWithoutValidation("Origin", page.GetLeftPart(UriPartial.Authority));
            request.Headers.Referrer = page;
            var json = JsonSerializer.Serialize(new { hash = fileHash, sk });
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Uri.EscapeDataString(json)));
            request.Content.Headers.ContentType = new("text/plain");
            using var reply = await browser.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (reply.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
            {
                var retry = reply.Headers.RetryAfter;
                var wait = retry?.Delta ?? (retry?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(3));
                if (wait > TimeSpan.FromSeconds(10)) return default;
                await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, ct).ConfigureAwait(false);
                continue;
            }
            if ((int)reply.StatusCode is not (200 or 400)) return default;
            using var responseJson = JsonDocument.Parse(await ReadTextAsync(reply, ct).ConfigureAwait(false));
            var value = responseJson.RootElement;
            if (reply.IsSuccessStatusCode && Field(value, "data").ValueKind == JsonValueKind.Object)
                return Field(value, "data").Clone();
            if (Field(value, "wrongSk").ValueKind != JsonValueKind.True || Text(value, "newSk") is not { } fresh)
                return default;
            sk = fresh;
        }
        return default;
    }

    private async Task<IReadOnlyList<VideoRendition>> RenditionsAsync(Uri page, string path, CancellationToken token)
    {
        var metadata = await FetchMetadataAsync(page, path, token).ConfigureAwait(false);
        var videos = Field(metadata, "videos");
        if (videos.ValueKind != JsonValueKind.Array) return [];
        var duration = Field(metadata, "duration");
        var millis = duration.ValueKind == JsonValueKind.Number && duration.TryGetDouble(out var number) ? number : 0;
        var variants = new Dictionary<int, Uri>();
        Uri? master = null;
        foreach (var video in videos.EnumerateArray())
        {
            if (!Uri.TryCreate(Text(video, "url"), UriKind.Absolute, out var url) || !NetworkPolicy.IsPreviewStream(url)) continue;
            if (Text(video, "dimension") == "adaptive") master = url;
            var height = Field(Field(video, "size"), "height");
            if (height.ValueKind == JsonValueKind.Number && height.TryGetInt32(out var pixels) && pixels > 0)
                variants[pixels] = url;
        }
        var rates = master is null ? null : await OptionalAsync(() => PlaylistAsync(master, token), token).ConfigureAwait(false);
        var bitrates = ParseRates(rates);
        var available = await Task.WhenAll(variants.Select(async pair =>
        {
            var rendition = new VideoRendition(pair.Key,
                bitrates.TryGetValue(pair.Key, out var rate) && rate * millis / 8000 is > 0 and < long.MaxValue
                    ? (long)(rate * millis / 8000) : null, pair.Value);
            return await OptionalAsync(() => SegmentsAsync(rendition, token), token).ConfigureAwait(false) is not null
                ? rendition : null;
        })).ConfigureAwait(false);
        return available.OfType<VideoRendition>().OrderBy(variant => variant.Height).ToArray();
    }

    private static Dictionary<int, double> ParseRates(string? master)
    {
        var rates = new Dictionary<int, double>();
        foreach (var line in Lines(master ?? ""))
        {
            const string prefix = "#EXT-X-STREAM-INF:";
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var fields = line[prefix.Length..].Split(',', StringSplitOptions.TrimEntries);
            var resolution = fields.FirstOrDefault(field => field.StartsWith("RESOLUTION=", StringComparison.Ordinal));
            var bandwidth = fields.FirstOrDefault(field => field.StartsWith("AVERAGE-BANDWIDTH=", StringComparison.Ordinal)) ??
                fields.FirstOrDefault(field => field.StartsWith("BANDWIDTH=", StringComparison.Ordinal));
            if (int.TryParse(resolution?.Split('x').LastOrDefault(), CultureInfo.InvariantCulture, out var height) &&
                double.TryParse(bandwidth?.Split('=').LastOrDefault(), CultureInfo.InvariantCulture, out var rate) && rate > 0)
                rates[height] = rate;
        }
        return rates;
    }

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task<string> PlaylistAsync(Uri url, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(streams.Timeouts.Total);
        using var response = await streams.GetAsync(url, null, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadTextAsync(response, timeout.Token).ConfigureAwait(false);
    }

    private static async Task<string> ReadTextAsync(HttpResponseMessage response, CancellationToken token)
    {
        const int limit = 16 * 1024 * 1024;
        using var output = new MemoryStream();
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[32 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > limit) throw new LoaderException("Слишком большой ответ Яндекс Диска.");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    private async Task<Uri[]> SegmentsAsync(VideoRendition variant, CancellationToken token)
    {
        var lines = Lines(await PlaylistAsync(variant.Playlist, token).ConfigureAwait(false));
        if (lines.FirstOrDefault() != "#EXTM3U" || !lines.Contains("#EXT-X-ENDLIST") ||
            lines.Any(line => line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal) || line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal)))
            throw new LoaderException("Яндекс Диск вернул неподдерживаемый плейлист.");
        var parts = lines.Where(line => !line.StartsWith('#')).Select(line =>
        {
            if (!Uri.TryCreate(variant.Playlist, line, out var url) || !NetworkPolicy.IsPreviewStream(url) ||
                !url.AbsolutePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                throw new LoaderException("Яндекс Диск вернул неподдерживаемый сегмент.");
            return url;
        }).ToArray();
        return parts.Length > 0 ? parts : throw new LoaderException("Плейлист превью пуст.");
    }

    public static string? FindFfmpeg()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (OperatingSystem.IsWindows() && File.Exists(bundled)) return bundled;

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var path = directory.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(path)) continue;
            var executable = Path.Combine(path, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(executable)) return executable;
        }
        return null;
    }

    public async Task SaveAsync(VideoPreviewFile file, VideoRendition variant, string destination, bool audioOnly,
        string? ffmpeg, Action<long, long>? progress, CancellationToken token)
    {
        if (audioOnly && ffmpeg is null) throw new LoaderException("Для сохранения звука нужен FFmpeg.");
        var parts = await SegmentsAsync(variant, token).ConfigureAwait(false);
        var names = file.Name.Split('/');
        var parent = Path.GetFullPath(destination);
        foreach (var folder in names[..^1]) parent = PathSafety.Child(parent, folder);
        var ext = audioOnly ? "m4a" : ffmpeg is null ? "ts" : "mp4";
        var label = audioOnly ? $"звук, {variant.Height}p" : $"{variant.Height}p";
        var name = $"{Path.GetFileNameWithoutExtension(names[^1])} ({label}).{ext}";
        PathSafety.ValidateName(name);
        using var pins = new DirectoryPins();
        var directory = pins.Pin(parent, create: true);
        await using var output = pins.OpenPartial(PathSafety.Child(parent, $".plantain-preview-{Guid.NewGuid():N}.part"));
        var published = false;
        try
        {
            if (ffmpeg is null) await CopySegmentsAsync(parts, output, progress, token).ConfigureAwait(false);
            else await RemuxAsync(ffmpeg, parts, output, audioOnly, progress, token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            WindowsFiles.Rename(output, directory, name);
            published = true;
        }
        finally { if (!published) WindowsFiles.RemoveTemporaryFile(output); }
    }

    private async Task CopySegmentsAsync(Uri[] parts, Stream output, Action<long, long>? progress,
        CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        long received = 0;
        for (var index = 0; index < parts.Length; index++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(streams.Timeouts.Total);
            using var response = await streams.GetAsync(parts[index], null, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) throw new LoaderException($"Не удалось загрузить сегмент {index + 1}.");
            using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            long length = 0;
            int count;
            while ((count = await streams.ReadAsync(input, buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                length += count;
                received += count;
                progress?.Invoke(received, count);
            }
            if (length == 0 || response.Content.Headers.ContentLength is long expected && length != expected)
                throw new LoaderException($"Сегмент {index + 1} загружен не полностью.");
        }
    }

    private async Task RemuxAsync(string ffmpeg, Uri[] parts, FileStream output, bool audioOnly,
        Action<long, long>? progress, CancellationToken token)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-nostdin", "-loglevel", "error", "-protocol_whitelist", "pipe",
            "-f", "mpegts", "-i", "pipe:0", "-map", audioOnly ? "0:a:0" : "0:v:0" })
            info.ArgumentList.Add(arg);
        if (!audioOnly) { info.ArgumentList.Add("-map"); info.ArgumentList.Add("0:a:0?"); }
        foreach (var arg in new[] { "-c", "copy", "-bsf:a", "aac_adtstoasc", "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
            "-f", "mp4", "pipe:1" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new LoaderException("Не удалось запустить FFmpeg.");
        void StopProcess() { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }
        using var registration = token.Register(StopProcess);
        async Task FeedAsync()
        {
            try { await CopySegmentsAsync(parts, process.StandardInput.BaseStream, progress, token).ConfigureAwait(false); }
            catch { StopProcess(); throw; }
            finally { process.StandardInput.Close(); }
        }
        async Task DrainAsync()
        {
            try { await process.StandardOutput.BaseStream.CopyToAsync(output, token).ConfigureAwait(false); }
            catch { StopProcess(); throw; }
        }
        await Task.WhenAll(FeedAsync(), DrainAsync(), process.StandardError.BaseStream.CopyToAsync(Stream.Null, token),
            process.WaitForExitAsync(token)).ConfigureAwait(false);
        if (process.ExitCode != 0 || output.Length == 0)
            throw new LoaderException(audioOnly ? "Не удалось извлечь звук." : "Не удалось сохранить MP4.");
    }

    public void Dispose() => streams.Dispose();
}
