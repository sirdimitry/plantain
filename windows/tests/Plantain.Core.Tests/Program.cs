using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Plantain.Core;
using Plantain.Core.Tests;

if (args is ["--check-updates", var installed])
{
    var check = await new GitHubWindowsUpdates().CheckAsync(installed, default);
    Console.WriteLine(JsonSerializer.Serialize(check));
    return check.Status == UpdateCheckStatus.Unavailable ? 1 : 0;
}

if (args is ["--hold-destination", var heldDestination, var readyFile])
{
    using var pins = new DirectoryPins();
    pins.Pin(heldDestination, create: true);
    var cache = Path.Combine(heldDestination, ".diskloader-partials");
    pins.Pin(cache, create: true, exclusiveCache: true);
    await using var partial = pins.OpenPartial(Path.Combine(cache, FileVerifier.PartialName(new("movie.mp4", "file", 6,
        Sha256: Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("abcdef")))))));
    await partial.WriteAsync(Encoding.UTF8.GetBytes("abc"));
    await partial.FlushAsync();
    File.WriteAllText(readyFile, "ready");
    await Task.Delay(Timeout.Infinite);
    return 0;
}

var reportPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("artifacts/test-results.json");
var scratch = Path.Combine(Path.GetDirectoryName(reportPath)!, "scratch-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var results = new List<object>();
var failed = 0;
var tests = Suite.Cases(scratch).Concat(Suite.Stage3Cases(scratch)).Concat(Suite.SessionCases()).Concat(Suite.UpdateCases()).ToArray();
try
{
    foreach (var test in tests)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await test.Run().WaitAsync(TimeSpan.FromSeconds(30));
            Console.WriteLine("PASS " + test.Name);
            results.Add(new { name = test.Name, passed = true, milliseconds = watch.ElapsedMilliseconds, error = (string?)null });
        }
        catch (Exception error)
        {
            failed++;
            Console.WriteLine("FAIL " + test.Name + ": " + error.Message);
            results.Add(new { name = test.Name, passed = false, milliseconds = watch.ElapsedMilliseconds, error = error.ToString() });
        }
    }
}
finally { Directory.Delete(scratch, recursive: true); }
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
File.WriteAllText(reportPath, JsonSerializer.Serialize(new
{
    version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
    utc = DateTimeOffset.UtcNow,
    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
    total = tests.Length, passed = tests.Length - failed, failed, results
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"RESULT {tests.Length - failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;

internal sealed record TestCase(string Name, Func<Task> Run);
internal sealed class FastDelay : IRetryDelay
{
    internal List<TimeSpan> Values { get; } = [];
    public Task WaitAsync(TimeSpan delay, CancellationToken token) { token.ThrowIfCancellationRequested(); Values.Add(delay); return Task.CompletedTask; }
}
internal sealed class CancelDelay(CancellationTokenSource source) : IRetryDelay
{
    public async Task WaitAsync(TimeSpan delay, CancellationToken token) { source.Cancel(); await Task.Delay(delay, token); }
}
internal sealed class FakeDisk(RemoteItem root, IReadOnlyList<RemoteItem>? children, Func<int, Task<Uri>> url) : IDiskClient
{
    internal int Calls;
    internal Func<string, IReadOnlyList<RemoteItem>>? Listing;
    public Task<RemoteItem> RootAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(root); }
    public Task<IReadOnlyList<RemoteItem>> ChildrenAsync(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(Listing?.Invoke(path) ?? children ?? []); }
    public Task<Uri> DownloadUrlAsync(string path, CancellationToken token) { token.ThrowIfCancellationRequested(); return url(++Calls); }
}
internal sealed class RacePublisher : IFilePublisher
{
    public void Publish(FileStream partial, string destination) { File.WriteAllText(destination, "keep"); new FilePublisher().Publish(partial, destination); }
}
internal sealed class ThrowingDownloader : IFileDownloader
{
    internal int Calls;
    public Task TransferAsync(Uri uri, FileStream partial, long size, Action<LoaderEvent> report, CancellationToken token)
    { Calls++; throw new LocalFileException("Файл заблокирован."); }
}
internal sealed class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request));
}

internal static partial class Suite
{
    private static readonly byte[] Data = Encoding.UTF8.GetBytes("abcdef");
    private static readonly NetworkTimeouts NormalTimeouts = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
    private static RemoteItem Item(string name = "movie.mp4", byte[]? data = null, bool hash = true) => new(name, "file",
        (data ?? Data).LongLength, Sha256: hash ? Convert.ToHexStringLower(SHA256.HashData(data ?? Data)) : null);
    private static string Dir(string root) { var path = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void Check(bool condition, string message = "assertion failed") { if (!condition) throw new Exception(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static bool Loopback(Uri uri) => uri.IsAbsoluteUri && uri.Host == "127.0.0.1" && uri.UserInfo.Length == 0 && uri.Scheme == "http";
    private static SafeHttp Http(NetworkTimeouts? timeouts = null) => new(Loopback, timeouts ?? NormalTimeouts);
    private static DiskApi Api(TestServer server, IRetryDelay? delay = null, NetworkTimeouts? timeouts = null) =>
        new("https://disk.yandex.ru/d/synthetic", server.BaseUri, Http(timeouts), delay ?? new FastDelay(), Loopback);
    private static FakeDisk Disk(RemoteItem item, Uri uri) => new(item, [], _ => Task.FromResult(uri));
    private static string Partial(string folder, RemoteItem item)
    { var cache = Path.Combine(folder, ".diskloader-partials"); Directory.CreateDirectory(cache); return Path.Combine(cache, FileVerifier.PartialName(item)); }
    private static Response Ranged(Request request, int _) => request.Header("Range") is { } range
        ? new(Data[int.Parse(range[6..^1])..], 206, new Dictionary<string, string> { ["Content-Range"] = $"bytes {range[6..^1]}-5/6" })
        : new(Data);
    private static string Resource(string embedded) => "{\"name\":\"Folder\",\"type\":\"dir\",\"_embedded\":" + embedded + "}";
    private static async Task TransferCase(string root, Func<Request, int, Response> respond, byte[]? prefix,
        bool success, Type? failure = null)
    {
        var folder = Dir(root);
        var partial = Path.Combine(folder, "test.part");
        if (prefix is not null) File.WriteAllBytes(partial, prefix);
        await using var server = new TestServer(respond);
        using var downloader = new FileDownloader(Http());
        var events = new List<LoaderEvent>();
        if (success)
        {
            await downloader.TransferAsync(server.BaseUri, partial, 6, events.Add);
            Check(File.ReadAllBytes(partial).SequenceEqual(Data));
            Check(events.OfType<TransferProgress>().Sum(e => e.NetworkBytes) == 6 - (server.Requests.First().Header("Range") is not null && respond == Ranged ? 3 : 0));
        }
        else
        {
            var error = await Throws<LoaderException>(() => downloader.TransferAsync(server.BaseUri, partial, 6, events.Add));
            if (failure is not null) Check(error.GetType() == failure);
        }
    }

    internal static IEnumerable<TestCase> Cases(string scratch)
    {
        yield return new("B04 SHA256 and same-size corruption", async () =>
        {
            var path = Path.Combine(Dir(scratch), "file"); File.WriteAllBytes(path, Data);
            Check(await FileVerifier.VerifyAsync(path, Item()));
            File.WriteAllText(path, "badbad"); Check(!await FileVerifier.VerifyAsync(path, Item()));
        });
        yield return new("B04 MD5 and uppercase digest", async () =>
        {
            var path = Path.Combine(Dir(scratch), "file"); File.WriteAllBytes(path, Data);
            var item = new RemoteItem("file", "file", 6, Md5: Convert.ToHexString(MD5.HashData(Data)));
            Check(await FileVerifier.VerifyAsync(path, item));
        });
        yield return new("B04 SHA256 takes priority over MD5", async () =>
        {
            var path = Path.Combine(Dir(scratch), "file"); File.WriteAllBytes(path, Data);
            Check(await FileVerifier.VerifyAsync(path, Item() with { Md5 = "wrong" }));
            Check(!await FileVerifier.VerifyAsync(path, Item() with { Sha256 = "wrong", Md5 = Convert.ToHexStringLower(MD5.HashData(Data)) }));
        });
        yield return new("B05 size-only and wrong size", async () =>
        {
            var path = Path.Combine(Dir(scratch), "file"); File.WriteAllBytes(path, Data);
            Check(await FileVerifier.VerifyAsync(path, Item(hash: false)));
            Check(!await FileVerifier.VerifyAsync(path, Item(hash: false) with { Size = 7 }));
        });
        yield return new("B17 content-only partial fingerprint", () =>
        {
            var item = Item(); var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("6\n" + item.Sha256 + "\n"))) + ".part";
            Check(FileVerifier.PartialName(item) == expected);
            Check(FileVerifier.PartialName(item with { Name = "another" }) == expected);
            Check(FileVerifier.PartialName(Item(hash: false)).Length == 69); return Task.CompletedTask;
        });
        yield return new("B07 resume valid byte range", () => TransferCase(scratch, Ranged, Data[..3], true));
        yield return new("B08 ignored range truncates old bytes", () => TransferCase(scratch, (_, _) => new(Data), Data[..3], true));
        foreach (var range in new[] { "bytes 1-5/6", "bytes 3-4/6", "bytes 3-5/7", "items 3-5/6" })
            yield return new("B08 reject Content-Range " + range, () => TransferCase(scratch,
                (_, _) => new(Data[3..], 206, new Dictionary<string, string> { ["Content-Range"] = range }), Data[..3], false));
        yield return new("B09 short transfer is not complete", () => TransferCase(scratch, (_, _) => new(Data[..3]), null, false));
        yield return new("B09 overlong transfer rejected", () => TransferCase(scratch, (_, _) => new(Encoding.UTF8.GetBytes("abcdefg")), null, false));
        yield return new("B09 compressed transfer rejected", () => TransferCase(scratch,
            (_, _) => new(Data, Headers: new Dictionary<string, string> { ["Content-Encoding"] = "gzip" }), null, false));
        yield return new("B08 HTTP416 resets stale partial", async () =>
        {
            var path = Path.Combine(Dir(scratch), "file.part"); File.WriteAllBytes(path, Data[..3]);
            await using var server = new TestServer((_, _) => new([], 416));
            using var downloader = new FileDownloader(Http());
            await Throws<NetworkFailureException>(() => downloader.TransferAsync(server.BaseUri, path, 6, _ => { }));
            Check(new FileInfo(path).Length == 0);
        });
        yield return new("B01 nested and empty folders; root copy only", async () =>
        {
            var folder = Dir(scratch);
            await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http());
            var disk = new FakeDisk(new("Folder", "dir"), [], _ => Task.FromResult(server.BaseUri))
            {
                Listing = path => path switch { "/" => [new("Nested", "dir"), new("Empty", "dir")], "/Nested" => [Item()], _ => [] }
            };
            var result = await new DownloadEngine(disk, downloader, new FastDelay()).RunAsync(folder);
            Check(result.Succeeded && result.Done == 1 && result.CompletedBytes == 6);
            Check(File.ReadAllBytes(Path.Combine(folder, "Folder copy", "Nested", "movie.mp4")).SequenceEqual(Data));
            Check(Directory.Exists(Path.Combine(folder, "Folder copy", "Empty")));
            Check(!Directory.Exists(Path.Combine(folder, ".diskloader-partials")));
        });
        yield return new("B01 single file retains extension", async () =>
        {
            var folder = Dir(scratch); await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http());
            var result = await new DownloadEngine(Disk(Item(), server.BaseUri), downloader, new FastDelay()).RunAsync(folder);
            Check(result.Succeeded && File.Exists(Path.Combine(folder, "movie.mp4")), JsonSerializer.Serialize(result));
            Check(!File.Exists(Path.Combine(folder, "movie.mp4 copy")));
        });
        yield return new("B03 inventory credited before first download; restart skips", async () =>
        {
            var folder = Dir(scratch); Directory.CreateDirectory(Path.Combine(folder, "Folder copy"));
            File.WriteAllBytes(Path.Combine(folder, "Folder copy", "z-old"), Data);
            await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http());
            var disk = new FakeDisk(new("Folder", "dir"), [Item("a-new"), Item("z-old")], _ => Task.FromResult(server.BaseUri));
            var events = new List<LoaderEvent>(); var engine = new DownloadEngine(disk, downloader, new FastDelay());
            var result = await engine.RunAsync(folder, events.Add);
            var firstDownload = events.FindIndex(e => e is FileStarted { Verifying: false });
            Check(events.Take(firstDownload).Any(e => e is TotalsChanged { Done: 1, Skipped: 1, CompletedBytes: 6 }));
            Check(result.Succeeded && result.Skipped == 1);
            var again = await engine.RunAsync(folder); Check(again.Skipped == 2 && disk.Calls == 1);
        });
        yield return new("B13 conflict preserved while other files download", async () =>
        {
            var folder = Dir(scratch); var target = Path.Combine(folder, "Folder copy"); Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "conflict"), "keepme");
            await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http());
            var disk = new FakeDisk(new("Folder", "dir"), [Item("conflict"), Item("good")], _ => Task.FromResult(server.BaseUri));
            var result = await new DownloadEngine(disk, downloader, new FastDelay()).RunAsync(folder);
            Check(!result.Succeeded && result.Failures.Count == 1 && result.Done == 1);
            Check(File.ReadAllText(Path.Combine(target, "conflict")) == "keepme");
            Check(File.ReadAllBytes(Path.Combine(target, "good")).SequenceEqual(Data));
        });
        yield return new("B05 size-only summary covers existing and fresh files", async () =>
        {
            var folder = Dir(scratch); Directory.CreateDirectory(Path.Combine(folder, "Folder copy"));
            File.WriteAllBytes(Path.Combine(folder, "Folder copy", "old"), Data);
            await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http());
            var disk = new FakeDisk(new("Folder", "dir"), [Item("old", hash: false), Item("new", hash: false)], _ => Task.FromResult(server.BaseUri));
            var result = await new DownloadEngine(disk, downloader, new FastDelay()).RunAsync(folder);
            Check(result.Succeeded && result.SizeOnlyFiles == 2 && result.Skipped == 1);
        });
        yield return new("B06 no-hash partial never resumed", async () =>
        {
            var folder = Dir(scratch); var item = Item(hash: false); File.WriteAllBytes(Partial(folder, item), Data[..3]);
            await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http());
            var result = await new DownloadEngine(Disk(item, server.BaseUri), downloader, new FastDelay()).RunAsync(folder);
            Check(result.Succeeded && server.Requests.First().Header("Range") is null);
        });
        yield return new("B10 full partial published without network", async () =>
        {
            var folder = Dir(scratch); var item = Item(); File.WriteAllBytes(Partial(folder, item), Data);
            var disk = new FakeDisk(item, [], _ => throw new Exception("Unexpected network"));
            using var downloader = new FileDownloader();
            var result = await new DownloadEngine(disk, downloader, new FastDelay()).RunAsync(folder);
            Check(result.Succeeded && disk.Calls == 0 && File.Exists(Path.Combine(folder, item.Name)));
        });
        yield return new("B10 zero-byte file and hash", async () =>
        {
            var folder = Dir(scratch); await using var server = new TestServer((_, _) => new([])); using var downloader = new FileDownloader(Http());
            var result = await new DownloadEngine(Disk(Item(data: []), server.BaseUri), downloader, new FastDelay()).RunAsync(folder);
            Check(result.Succeeded && new FileInfo(Path.Combine(folder, "movie.mp4")).Length == 0);
        });
        yield return new("B18 empty folder completes with no files", async () =>
        {
            var folder = Dir(scratch); using var downloader = new FileDownloader();
            var disk = new FakeDisk(new("Empty", "dir"), [], _ => throw new Exception("Unexpected network"));
            var events = new List<LoaderEvent>(); var result = await new DownloadEngine(disk, downloader).RunAsync(folder, events.Add);
            Check(result.Succeeded && result.Files == 0 && events.Last() is DownloadFinished);
            Check(Directory.Exists(Path.Combine(folder, "Empty copy")));
        });
        yield return new("B09 checksum failure restarts and refreshes URL", async () =>
        {
            var folder = Dir(scratch); await using var server = new TestServer((_, n) => n == 1 ? Response.Text("badbad") : new(Data));
            using var downloader = new FileDownloader(Http()); var disk = Disk(Item(), server.BaseUri); var delay = new FastDelay();
            var result = await new DownloadEngine(disk, downloader, delay).RunAsync(folder);
            Check(result.Succeeded && disk.Calls == 2 && delay.Values.SequenceEqual([TimeSpan.FromSeconds(2)]));
            Check(server.Requests.All(r => r.Header("Range") is null));
        });
        yield return new("B15 expired download URL refreshed", async () =>
        {
            var folder = Dir(scratch); await using var server = new TestServer((_, n) => n == 1 ? new([], 403) : new(Data));
            using var downloader = new FileDownloader(Http()); var disk = Disk(Item(), server.BaseUri);
            var result = await new DownloadEngine(disk, downloader, new FastDelay()).RunAsync(folder);
            Check(result.Succeeded && disk.Calls == 2);
        });
        yield return new("skip current file preserves partial and continues next file", async () =>
        {
            var folder = Dir(scratch); var control = new DownloadControl();
            await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http());
            var disk = new FakeDisk(new("Folder", "dir"), [Item() with { Name = "a.mp4" }, Item() with { Name = "b.mp4" }], _ => Task.FromResult(server.BaseUri));
            var result = await new DownloadEngine(disk, downloader, new FastDelay(), control: control).RunAsync(folder, e =>
            { if (e is FileStarted { Name: "/a.mp4", Verifying: false }) control.SkipCurrent(); });
            Check(result.Done == 1 && result.Failures.Single().Name == "/a.mp4");
            Check(!File.Exists(Path.Combine(folder, "Folder copy", "a.mp4")) && File.Exists(Path.Combine(folder, "Folder copy", "b.mp4")));
            control.SkipCurrent(); // safe after the run; never cancels a future file
        });
        yield return new("download URL retries report countdown and can be skipped during waiting", async () =>
        {
            var control = new DownloadControl(); var events = new List<LoaderEvent>();
            var disk = new FakeDisk(Item(), [], _ => throw new RetryableApiException("API 503"));
            using var downloader = new FileDownloader();
            var result = await new DownloadEngine(disk, downloader, control: control).RunAsync(Dir(scratch), e =>
            { events.Add(e); if (e is RetryWaiting) control.SkipCurrent(); });
            Check(result.Failures.Single().Message.Contains("Пропущено") && disk.Calls == 1);
            Check(events.OfType<RetryWaiting>().Single().FailedAttempt == 1 && events.OfType<CurrentDownloadChanged>().Last().Active == false);
        });
        yield return new("B15 maximum five file attempts", async () =>
        {
            var folder = Dir(scratch); await using var server = new TestServer((_, _) => new([], 503));
            using var downloader = new FileDownloader(Http()); var disk = Disk(Item(), server.BaseUri); var delay = new FastDelay();
            var result = await new DownloadEngine(disk, downloader, delay).RunAsync(folder);
            Check(!result.Succeeded && result.Failures.Count == 1 && disk.Calls == 5 && delay.Values.Count == 4);
            Check(delay.Values.Select(x => x.TotalSeconds).SequenceEqual([2d, 4d, 8d, 16d]));
        });
        yield return new("B14 fatal API error not repeated by engine", async () =>
        {
            var disk = new FakeDisk(Item(), [], _ => throw new FatalApiException("API 400")); using var downloader = new FileDownloader();
            var result = await new DownloadEngine(disk, downloader, new FastDelay()).RunAsync(Dir(scratch));
            Check(!result.Succeeded && disk.Calls == 1);
        });
        yield return new("W07 local file error stops network retries", async () =>
        {
            var disk = Disk(Item(), new Uri("https://downloader.disk.yandex.ru/file")); var downloader = new ThrowingDownloader();
            var result = await new DownloadEngine(disk, downloader, new FastDelay()).RunAsync(Dir(scratch));
            Check(!result.Succeeded && disk.Calls == 1 && downloader.Calls == 1);
        });
        yield return new("W05 publication race preserves destination and partial", async () =>
        {
            var folder = Dir(scratch); var item = Item(); await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http());
            var disk = Disk(item, server.BaseUri); var result = await new DownloadEngine(disk, downloader, new FastDelay(), new RacePublisher()).RunAsync(folder);
            Check(!result.Succeeded && File.ReadAllText(Path.Combine(folder, item.Name)) == "keep" && disk.Calls == 1);
            Check(File.ReadAllBytes(Partial(folder, item)).SequenceEqual(Data));
        });
        yield return new("B11 cancellation closes file and restart resumes", async () =>
        {
            var data = new byte[1024 * 1024]; RandomNumberGenerator.Fill(data);
            var item = Item(data: data); var folder = Dir(scratch); using var stop = new CancellationTokenSource();
            await using (var server = new TestServer((_, _) => new([], Length: data.Length, WriteBody: async (stream, token) =>
            {
                for (var i = 0; i < data.Length; i += 16384)
                { await stream.WriteAsync(data.AsMemory(i, 16384), token); await stream.FlushAsync(token); await Task.Delay(20, token); }
            })))
            {
                using var downloader = new FileDownloader(Http());
                await Throws<OperationCanceledException>(() => new DownloadEngine(Disk(item, server.BaseUri), downloader, new FastDelay()).RunAsync(folder,
                    e => { if (e is TransferProgress { CurrentBytes: > 0 }) stop.Cancel(); }, stop.Token));
            }
            var partial = Partial(folder, item); var length = new FileInfo(partial).Length; Check(length > 0 && length < data.Length);
            using (new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            await using var resume = new TestServer((request, _) =>
            {
                Check(request.Header("Range") == $"bytes={length}-");
                return new(data[(int)length..], 206, new Dictionary<string, string> { ["Content-Range"] = $"bytes {length}-{data.Length - 1}/{data.Length}" });
            });
            using var again = new FileDownloader(Http());
            var result = await new DownloadEngine(Disk(item, resume.BaseUri), again, new FastDelay()).RunAsync(folder);
            Check(result.Succeeded && File.ReadAllBytes(Path.Combine(folder, item.Name)).SequenceEqual(data));
        });
        yield return new("B11 cancellation interrupts retry delay", async () =>
        {
            using var stop = new CancellationTokenSource(); var folder = Dir(scratch);
            await using var server = new TestServer((_, _) => new([], 503)); using var downloader = new FileDownloader(Http());
            var disk = Disk(Item(), server.BaseUri);
            await Throws<OperationCanceledException>(() => new DownloadEngine(disk, downloader, new CancelDelay(stop)).RunAsync(folder, cancellationToken: stop.Token));
            Check(disk.Calls == 1);
        });
        yield return new("B11 pre-cancelled verification", async () =>
        {
            using var stop = new CancellationTokenSource(); stop.Cancel();
            await Throws<OperationCanceledException>(() => FileVerifier.VerifyAsync("missing", Item(), cancellationToken: stop.Token));
        });
        yield return new("B02 API pagination and query escaping", async () =>
        {
            await using var server = new TestServer((request, n) =>
            {
                Check(request.Target.Contains("limit=200") && request.Target.Contains("sort=name"));
                Check(request.Target.Contains("path=%2F%D0%9F%D0%B0%D0%BF%D0%BA%D0%B0%20%26"));
                Check(request.Target.Contains("public_key=https%3A%2F%2F"));
                Check(request.Target.Contains("offset=" + (n - 1)));
                return Response.Text(Resource(n == 1 ? "{\"items\":[{\"name\":\"a\",\"type\":\"file\",\"size\":6}],\"total\":2}" : "{\"items\":[{\"name\":\"b\",\"type\":\"file\",\"size\":6}],\"total\":2}"));
            });
            using var api = Api(server); var children = await api.ChildrenAsync("/Папка &"); Check(children.Count == 2 && server.Requests.Count == 2);
        });
        yield return new("B02 pagination without total ends on empty page", async () =>
        {
            await using var server = new TestServer((_, n) => Response.Text(Resource(n == 1 ? "{\"items\":[{\"name\":\"a\",\"type\":\"dir\"}]}" : "{\"items\":[]}")));
            using var api = Api(server); Check((await api.ChildrenAsync("/")).Count == 1 && server.Requests.Count == 2);
        });
        yield return new("B02 premature empty listing fails", async () =>
        {
            await using var server = new TestServer((_, _) => Response.Text(Resource("{\"items\":[],\"total\":2}")));
            using var api = Api(server); await Throws<FatalApiException>(() => api.ChildrenAsync("/")); Check(server.Requests.Count == 1);
        });
        foreach (var status in new[] { 400, 401, 403, 404 })
            yield return new("B14 API HTTP" + status + " is final", async () =>
            {
                await using var server = new TestServer((_, _) => new([], status)); using var api = Api(server);
                await Throws<FatalApiException>(() => api.RootAsync()); Check(server.Requests.Count == 1);
            });
        foreach (var body in new[] { "not-json", "null", "{}", "{\"name\":\"Folder\",\"type\":\"dir\",\"_embedded\":{\"items\":null}}" })
            yield return new("B14 malformed API body " + body, async () =>
            {
                await using var server = new TestServer((_, _) => Response.Text(body)); using var api = Api(server);
                await Throws<FatalApiException>(() => api.ChildrenAsync("/")); Check(server.Requests.Count == 1);
            });
        foreach (var status in new[] { 408, 429, 503 })
            yield return new("B15 retry API HTTP" + status, async () =>
            {
                await using var server = new TestServer((_, n) => n < 3 ? new([], status) : Response.Text("{\"name\":\"x\",\"type\":\"file\",\"size\":6}"));
                var delay = new FastDelay(); using var api = Api(server, delay); Check((await api.RootAsync()).Name == "x" && server.Requests.Count == 3);
                Check(delay.Values.Select(x => x.TotalSeconds).SequenceEqual([1d, 2d]));
            });
        yield return new("B15 API stops after five attempts", async () =>
        {
            await using var server = new TestServer((_, _) => new([], 503)); var delay = new FastDelay(); using var api = Api(server, delay);
            await Throws<LoaderException>(() => api.RootAsync()); Check(server.Requests.Count == 5);
            Check(delay.Values.Select(x => x.TotalSeconds).SequenceEqual([1d, 2d, 4d, 8d]));
        });
        yield return new("B15 download URL API only one inner attempt", async () =>
        {
            await using var server = new TestServer((_, _) => new([], 503)); using var api = Api(server);
            await Throws<LoaderException>(() => api.DownloadUrlAsync("/file")); Check(server.Requests.Count == 1);
        });
        yield return new("B16 public and download URL policy", () =>
        {
            foreach (var host in new[] { "disk.yandex.ru", "disk.yandex.com", "disk.yandex.com.tr", "yadi.sk" })
                Check(NetworkPolicy.ParsePublicLink(" https://" + host + "/d/test ").Host == host);
            foreach (var value in new[] { "http://downloader.disk.yandex.ru/x", "https://yandex.ru.attacker.example/x", "https://user@downloader.disk.yandex.ru/x", "https://downloader.disk.yandex.ru:444/x", "https://evil.test/x" })
                Check(!NetworkPolicy.IsDownload(new Uri(value)));
            Check(NetworkPolicy.IsDownload(new Uri("https://downloader.disk.yandex.ru/file")));
            Check(NetworkPolicy.IsApi(new Uri("https://cloud-api.yandex.net/x"))); return Task.CompletedTask;
        });
        yield return new("B16 download API rejects unsafe href", async () =>
        {
            await using var server = new TestServer((_, _) => Response.Text("{\"href\":\"https://evil.test/file\"}")); using var api = Api(server);
            await Throws<FatalApiException>(() => api.DownloadUrlAsync("/"));
        });
        yield return new("B16 rejects cross-domain redirect before second request", async () =>
        {
            var calls = 0;
            using var http = new SafeHttp(NetworkPolicy.IsDownload, NormalTimeouts, new MockHandler(_ =>
            { calls++; return new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.test/file") } }; }));
            await Throws<FatalApiException>(() => http.GetAsync(new Uri("https://downloader.disk.yandex.ru/file"), 3, default)); Check(calls == 1);
        });
        yield return new("B16 permitted relative redirect preserves Range", async () =>
        {
            await using var server = new TestServer((request, n) =>
            {
                Check(request.Header("Range") == "bytes=3-");
                return n == 1 ? new([], 302, new Dictionary<string, string> { ["Location"] = "/final" }) : Ranged(request, n);
            });
            var partial = Path.Combine(Dir(scratch), "file.part"); File.WriteAllBytes(partial, Data[..3]); using var downloader = new FileDownloader(Http());
            await downloader.TransferAsync(server.BaseUri, partial, 6, _ => { }); Check(File.ReadAllBytes(partial).SequenceEqual(Data));
        });
        yield return new("B16 redirect loop bounded", async () =>
        {
            await using var server = new TestServer((_, _) => new([], 302, new Dictionary<string, string> { ["Location"] = "/again" }));
            using var http = Http(); await Throws<FatalApiException>(() => http.GetAsync(server.BaseUri, null, default)); Check(server.Requests.Count == 11);
        });
        yield return new("B16 standard TLS rejects untrusted certificate", async () =>
        {
            await using var server = new TestServer((_, _) => new(Data), tls: true);
            using var http = new SafeHttp(uri => uri.Host == "127.0.0.1" && uri.Scheme == "https", NormalTimeouts);
            await Throws<NetworkFailureException>(() => http.GetAsync(server.BaseUri, null, default)); Check(server.Requests.Count == 0);
        });
        yield return new("B17 no cookies auth or automatic redirects", async () =>
        {
            using var handler = SafeHttp.CreateHandler(); Check(!handler.AllowAutoRedirect && !handler.UseCookies && handler.Credentials is null && handler.DefaultProxyCredentials is null && !handler.PreAuthenticate);
            Check(handler.AutomaticDecompression == DecompressionMethods.None && handler.SslOptions.RemoteCertificateValidationCallback is null);
            await using var server = new TestServer((request, _) =>
            {
                Check(request.Header("Cookie") is null && request.Header("Authorization") is null);
                Check(request.Header("Cache-Control") == "no-store");
                return new(Data, Headers: new Dictionary<string, string> { ["Set-Cookie"] = "private=synthetic" });
            });
            using var http = Http(); using var first = await http.GetAsync(server.BaseUri, null, default); using var second = await http.GetAsync(server.BaseUri, null, default);
            Check(server.Requests.Count == 2);
        });
        foreach (var kind in new[] { "headers", "idle", "total" })
            yield return new("B15 transfer timeout " + kind, async () =>
            {
                var timeouts = kind switch
                {
                    "headers" => new NetworkTimeouts(TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)),
                    "idle" => new NetworkTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5)),
                    _ => new NetworkTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(150))
                };
                await using var server = new TestServer((_, _) => kind == "headers" ? new(Data, HeaderDelayMs: 2000) :
                    new([], Length: 6, WriteBody: async (stream, token) => { await Task.Delay(2000, token); await stream.WriteAsync(Data, token); }));
                using var downloader = new FileDownloader(Http(timeouts));
                await Throws<NetworkFailureException>(() => downloader.TransferAsync(server.BaseUri, Path.Combine(Dir(scratch), "file.part"), 6, _ => { }));
            });
        yield return new("B15 abrupt socket EOF is network failure", async () =>
        {
            await using var server = new TestServer((_, _) => new(Data[..3], Length: 6)); using var downloader = new FileDownloader(Http());
            await Throws<NetworkFailureException>(() => downloader.TransferAsync(server.BaseUri, Path.Combine(Dir(scratch), "file.part"), 6, _ => { }));
        });
        yield return new("B15 API resource timeout retries and is bounded", async () =>
        {
            await using var server = new TestServer((_, _) => new([], Length: 6, WriteBody: async (_, token) => await Task.Delay(2000, token)));
            using var api = Api(server, timeouts: new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(150)));
            await Throws<NetworkFailureException>(() => api.RootAsync()); Check(server.Requests.Count == 5);
        });
        yield return new("B11 cancellation at verification completion prevents acceptance", async () =>
        {
            var path = Path.Combine(Dir(scratch), "file"); var data = new byte[2 * 1024 * 1024]; File.WriteAllBytes(path, data);
            using var stop = new CancellationTokenSource();
            await Throws<OperationCanceledException>(() => FileVerifier.VerifyAsync(path, Item(data: data), _ => stop.Cancel(), stop.Token));
            Check(stop.IsCancellationRequested);
        });
        yield return new("W02 basic unsafe path rejected", () =>
        {
            foreach (var name in new[] { "../escape", "CON.txt", "NUL", "file:stream", "trailing.", "trailing ", "COM¹.txt", "C:relative", "a\\b" })
            {
                try { PathSafety.Child(scratch, name); throw new Exception("Unsafe name accepted"); }
                catch (LocalFileException) { }
            }
            return Task.CompletedTask;
        });
        yield return new("W03 ambiguous case rejected before writes", async () =>
        {
            var folder = Dir(scratch); var disk = new FakeDisk(new("Folder", "dir"), [Item("A"), Item("a")], _ => throw new Exception("Unexpected network"));
            using var downloader = new FileDownloader();
            var result = await new DownloadEngine(disk, downloader).RunAsync(folder);
            Check(!result.Succeeded && result.Failures.Count == 2 && result.Done == 0 && disk.Calls == 0);
            Check(!Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any());
        });
        yield return new("B01 unknown type or invalid file size rejected", async () =>
        {
            foreach (var item in new[] { new RemoteItem("bad", "unknown"), new RemoteItem("bad", "file"), Item() with { Size = -1 } })
            {
                using var downloader = new FileDownloader();
                await Throws<FatalApiException>(() => new DownloadEngine(Disk(item, new Uri("https://downloader.disk.yandex.ru/x")), downloader).RunAsync(Dir(scratch)));
            }
        });
        yield return new("B01 invalid root name and hash rejected before writes", async () =>
        {
            foreach (var item in new[] { new RemoteItem(null!, "dir"), Item() with { Sha256 = "wrong" } })
            {
                var folder = Dir(scratch); using var downloader = new FileDownloader();
                await Throws<FatalApiException>(() => new DownloadEngine(Disk(item, new Uri("https://downloader.disk.yandex.ru/x")), downloader).RunAsync(folder));
                Check(!Directory.EnumerateFileSystemEntries(folder).Any());
            }
        });
        yield return new("B01 API to engine end-to-end", async () =>
        {
            var folder = Dir(scratch); Uri? baseUri = null;
            await using var server = new TestServer((request, _) =>
            {
                if (request.Target.StartsWith("/v1/disk/public/resources/download?"))
                    return Response.Text(JsonSerializer.Serialize(new { href = new Uri(baseUri!, "payload").AbsoluteUri }));
                if (request.Target.StartsWith("/v1/disk/public/resources?"))
                    return Response.Text(JsonSerializer.Serialize(Item()));
                return Ranged(request, 0);
            });
            baseUri = server.BaseUri; using var api = Api(server); using var downloader = new FileDownloader(Http());
            var result = await new DownloadEngine(api, downloader, new FastDelay()).RunAsync(folder);
            Check(result.Succeeded && server.Requests.Count == 3 && File.ReadAllBytes(Path.Combine(folder, "movie.mp4")).SequenceEqual(Data));
        });
        yield return new("B09 oversized stale partial restarts from zero", async () =>
        {
            var folder = Dir(scratch); var item = Item(); File.WriteAllBytes(Partial(folder, item), new byte[7]);
            await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http());
            var result = await new DownloadEngine(Disk(item, server.BaseUri), downloader, new FastDelay()).RunAsync(folder);
            Check(result.Succeeded && server.Requests.First().Header("Range") is null);
        });
        yield return new("B09 interrupted HTTP response resumes on next attempt", async () =>
        {
            var folder = Dir(scratch); await using var server = new TestServer((request, n) => n == 1 ? new(Data[..3], Length: 6) : Ranged(request, n));
            using var downloader = new FileDownloader(Http());
            var result = await new DownloadEngine(Disk(Item(), server.BaseUri), downloader, new FastDelay()).RunAsync(folder);
            Check(result.Succeeded && server.Requests.Count == 2 && server.Requests.Last().Header("Range") == "bytes=3-");
        });
        yield return new("B11 API retry cancellation", async () =>
        {
            using var stop = new CancellationTokenSource(); await using var server = new TestServer((_, _) => new([], 503));
            using var api = Api(server, new CancelDelay(stop));
            await Throws<OperationCanceledException>(() => api.RootAsync(stop.Token)); Check(server.Requests.Count == 1);
        });
        yield return new("B16 API redirect cannot leave API host", async () =>
        {
            var calls = 0; using var http = new SafeHttp(NetworkPolicy.IsApi, NormalTimeouts, new MockHandler(_ =>
            { calls++; return new(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://downloader.disk.yandex.ru/file") } }; }));
            await Throws<FatalApiException>(() => http.GetAsync(new Uri("https://cloud-api.yandex.net/x"), null, default)); Check(calls == 1);
        });
        yield return new("B16 rejects invalid public links", async () =>
        {
            foreach (var value in new[] { "garbage", "http://disk.yandex.ru/d/x", "https://disk.yandex.ru.evil.test/d/x", "https://user:pass@disk.yandex.ru/d/x", "https://disk.yandex.ru:444/d/x" })
                await Throws<LoaderException>(() => { NetworkPolicy.ParsePublicLink(value); return Task.CompletedTask; });
        });
        yield return new("W07 real Windows sharing violation stops retries", async () =>
        {
            var folder = Dir(scratch); var item = Item(); var partial = Partial(folder, item); File.WriteAllBytes(partial, Data[..3]);
            using var held = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http()); var disk = Disk(item, server.BaseUri);
            var result = await new DownloadEngine(disk, downloader, new FastDelay()).RunAsync(folder);
            Check(!result.Succeeded && disk.Calls <= 1);
        });
        yield return new("B17 no persistent logs or source history in download tree", async () =>
        {
            var folder = Dir(scratch); await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http());
            var result = await new DownloadEngine(Disk(Item(), server.BaseUri), downloader, new FastDelay()).RunAsync(folder);
            Check(result.Succeeded && Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).SequenceEqual(["movie.mp4"]));
        });
        yield return new("B18 streaming begins before response completes", async () =>
        {
            var folder = Dir(scratch); var partial = Path.Combine(folder, "stream.part");
            var block = new byte[64 * 1024]; RandomNumberGenerator.Fill(block);
            const int blocks = 128; var seenProgress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var i = 0; i < blocks; i++) hash.AppendData(block);
            var item = new RemoteItem("stream", "file", block.Length * blocks, Sha256: Convert.ToHexStringLower(hash.GetHashAndReset()));
            await using var server = new TestServer((_, _) => new([], Length: item.Size, WriteBody: async (stream, token) =>
            {
                for (var i = 0; i < blocks; i++)
                {
                    if (i == 1) await Task.Delay(160, token);
                    if (i == 2) await seenProgress.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                    await stream.WriteAsync(block, token); await stream.FlushAsync(token);
                }
            }));
            using var downloader = new FileDownloader(Http());
            await downloader.TransferAsync(server.BaseUri, partial, item.Size!.Value,
                e => { if (e is TransferProgress { CurrentBytes: > 0 }) seenProgress.TrySetResult(); });
            Check(seenProgress.Task.IsCompleted && await FileVerifier.VerifyAsync(partial, item));
        });
    }
}
