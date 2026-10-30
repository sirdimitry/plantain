using System.Diagnostics;
using System.Reflection;
using Plantain.Core;

using Plantain.Core.Tests;

internal static partial class Suite
{
    internal static IEnumerable<TestCase> Stage3Cases(string scratch)
    {
        yield return new("W02 invalid names isolated; good files continue", async () =>
        {
            var folder = Dir(scratch);
            var bad = new[] { "CON.txt", "NUL", "COM1.jpg", "LPT¹.txt", "file:stream", "trail.", "trail ", "a?b", "x\u0001", "..", "C:relative", "\\server\\share", new string('x', 256) };
            var items = bad.Select(x => Item(x)).Append(Item("good.bin")).ToArray();
            await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http());
            var disk = new FakeDisk(new("Folder", "dir"), items, _ => Task.FromResult(server.BaseUri));
            var result = await new DownloadEngine(disk, downloader, new FastDelay()).RunAsync(folder);
            Check(!result.Succeeded && result.Failures.Count == bad.Length && result.Done == 1 && result.Files == items.Length && disk.Calls == 1);
            Check(File.ReadAllBytes(Path.Combine(folder, "Folder copy", "good.bin")).SequenceEqual(Data));
        });
        yield return new("W03 both case collisions skipped, valid sibling downloaded", async () =>
        {
            var folder = Dir(scratch); await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http());
            var disk = new FakeDisk(new("Folder", "dir"), [Item("Фото.jpg"), Item("фото.jpg"), Item("valid")], _ => Task.FromResult(server.BaseUri));
            var result = await new DownloadEngine(disk, downloader, new FastDelay()).RunAsync(folder);
            Check(result.Files == 3 && result.Done == 1 && result.Failures.Count == 2 && disk.Calls == 1);
            Check(!File.Exists(Path.Combine(folder, "Folder copy", "Фото.jpg")));
        });
        yield return new("W03 file-directory collision excludes both and subtree", async () =>
        {
            var folder = Dir(scratch); await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http());
            var disk = new FakeDisk(new("Folder", "dir"), [], _ => Task.FromResult(server.BaseUri))
            { Listing = path => path == "/" ? [Item("same"), new("SAME", "dir"), Item("good")] : throw new Exception("Conflicting subtree traversed") };
            var result = await new DownloadEngine(disk, downloader, new FastDelay()).RunAsync(folder);
            Check(result.Done == 1 && result.Failures.Count == 2 && !Directory.Exists(Path.Combine(folder, "Folder copy", "same")));
        });
        yield return new("W02 invalid directory excludes subtree only", async () =>
        {
            var folder = Dir(scratch); await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http());
            var disk = new FakeDisk(new("Folder", "dir"), [], _ => Task.FromResult(server.BaseUri))
            { Listing = path => path == "/" ? [new("CON", "dir"), Item("good")] : throw new Exception("Invalid subtree traversed") };
            var result = await new DownloadEngine(disk, downloader, new FastDelay()).RunAsync(folder);
            Check(result.Done == 1 && result.Failures.Count == 1);
        });
        yield return new("W02 root conflicts with cache retains partials", async () =>
        {
            var folder = Dir(scratch); var item = Item(".diskloader-partials"); var partial = Partial(folder, item); File.WriteAllBytes(partial, Data[..3]);
            using var downloader = new FileDownloader(); var disk = new FakeDisk(item, [], _ => throw new Exception("Unexpected network"));
            var result = await new DownloadEngine(disk, downloader).RunAsync(folder);
            Check(!result.Succeeded && result.Failures.Count == 1 && File.ReadAllBytes(partial).SequenceEqual(Data[..3]));
        });
        yield return new("W04 destination junction rejected before network", async () =>
        {
            var folder = Dir(scratch); var outside = Dir(scratch); var link = Path.Combine(folder, "link"); WindowsFixtures.Junction(link, outside);
            try
            {
                using var downloader = new FileDownloader();
                await Throws<LocalFileException>(() => new DownloadEngine(Disk(Item(), new Uri("https://downloader.disk.yandex.ru/x")), downloader).RunAsync(link));
                Check(!Directory.EnumerateFileSystemEntries(outside).Any());
            }
            finally { Directory.Delete(link); }
        });
        yield return new("W04 cache junction rejected; external partial preserved", async () =>
        {
            var folder = Dir(scratch); var outside = Dir(scratch); var link = Path.Combine(folder, ".diskloader-partials");
            var external = Path.Combine(outside, FileVerifier.PartialName(Item())); File.WriteAllText(external, "keep"); WindowsFixtures.Junction(link, outside);
            try
            {
                using var downloader = new FileDownloader();
                await Throws<LocalFileException>(() => new DownloadEngine(Disk(Item(), new Uri("https://downloader.disk.yandex.ru/x")), downloader).RunAsync(folder));
                Check(File.ReadAllText(external) == "keep");
            }
            finally { Directory.Delete(link); }
        });
        yield return new("W04 nested junction subtree skipped; sibling succeeds", async () =>
        {
            var folder = Dir(scratch); var root = Path.Combine(folder, "Folder copy"); Directory.CreateDirectory(root);
            var outside = Dir(scratch); var link = Path.Combine(root, "bad"); WindowsFixtures.Junction(link, outside);
            try
            {
                await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http());
                var disk = new FakeDisk(new("Folder", "dir"), [], _ => Task.FromResult(server.BaseUri))
                { Listing = path => path == "/" ? [new("bad", "dir"), Item("good")] : [Item("outside")] };
                var result = await new DownloadEngine(disk, downloader, new FastDelay()).RunAsync(folder);
                Check(result.Done == 1 && result.Failures.Count == 1 && !Directory.EnumerateFileSystemEntries(outside).Any());
            }
            finally { Directory.Delete(link); }
        });
        yield return new("W04 partial hardlink rejected without truncating victim", async () =>
        {
            var folder = Dir(scratch); var outside = Dir(scratch); var victim = Path.Combine(outside, "victim"); File.WriteAllText(victim, "keepme!");
            var partial = Partial(folder, Item()); WindowsFixtures.HardLink(partial, victim);
            await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http()); var disk = Disk(Item(), server.BaseUri);
            var result = await new DownloadEngine(disk, downloader, new FastDelay()).RunAsync(folder);
            Check(!result.Succeeded && result.Failures.Count == 1 && disk.Calls == 0 && File.ReadAllText(victim) == "keepme!");
        });
        yield return new("W04 pinned parent cannot be renamed during transfer", async () =>
        {
            var folder = Dir(scratch); var target = Path.Combine(folder, "Folder copy"); var attempted = false;
            await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http());
            var disk = new FakeDisk(new("Folder", "dir"), [Item()], _ =>
            {
                attempted = true;
                try { Directory.Move(target, target + "-moved"); throw new Exception("Pinned directory moved"); }
                catch (IOException) { }
                return Task.FromResult(server.BaseUri);
            });
            var result = await new DownloadEngine(disk, downloader, new FastDelay()).RunAsync(folder);
            Check(result.Succeeded && attempted && !Directory.Exists(target + "-moved"));
        });
        yield return new("W04 partial cannot be replaced between transfer and verification", async () =>
        {
            var folder = Dir(scratch); var partial = Partial(folder, Item()); var attempted = false;
            await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http());
            var result = await new DownloadEngine(Disk(Item(), server.BaseUri), downloader, new FastDelay()).RunAsync(folder, e =>
            {
                if (e is StatusChanged { Text: "Проверяю скачанный файл…" })
                {
                    attempted = true;
                    try { File.Move(partial, partial + "-moved"); throw new Exception("Partial moved while locked"); }
                    catch (IOException) { }
                }
            });
            Check(result.Succeeded && attempted && File.ReadAllBytes(Path.Combine(folder, "movie.mp4")).SequenceEqual(Data));
        });
        yield return new("W04 late reparse tagging never writes to external directory", async () =>
        {
            var folder = Dir(scratch); var outside = Dir(scratch); var target = Path.Combine(folder, "Folder copy"); var tagged = false;
            await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http());
            var disk = new FakeDisk(new("Folder", "dir"), [Item()], _ =>
            {
                WindowsFixtures.SetJunction(target, outside); tagged = true; return Task.FromResult(server.BaseUri);
            });
            try
            {
                var result = await new DownloadEngine(disk, downloader, new FastDelay()).RunAsync(folder);
                Check(tagged && !result.Succeeded && !Directory.EnumerateFileSystemEntries(outside).Any());
            }
            finally { if (tagged) Directory.Delete(target); }
        });
        yield return new("W06 concurrent engines rejected and lock released", async () =>
        {
            var folder = Dir(scratch);
            using (var pins = new DirectoryPins())
            {
                pins.Pin(folder); pins.Pin(Path.Combine(folder, ".diskloader-partials"), create: true, exclusiveCache: true);
                using var downloader = new FileDownloader();
                await Throws<LocalFileException>(() => new DownloadEngine(Disk(Item(), new Uri("https://downloader.disk.yandex.ru/x")), downloader).RunAsync(folder));
            }
            await using var server = new TestServer(Ranged); using var again = new FileDownloader(Http());
            Check((await new DownloadEngine(Disk(Item(), server.BaseUri), again, new FastDelay()).RunAsync(folder)).Succeeded);
        });
        yield return new("W06 lock released after forced process termination; partial resumes", async () =>
        {
            var folder = Dir(scratch); var ready = Path.Combine(Dir(scratch), "ready");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            foreach (var arg in new[] { "--hold-destination", folder, ready }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start) ?? throw new Exception("Cannot start test child");
            try
            {
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(ready) && deadline.Elapsed < TimeSpan.FromSeconds(8))
                { if (child.HasExited) throw new Exception("Test child exited"); await Task.Delay(25); }
                Check(File.Exists(ready), "Test child did not acquire lock");
                using var downloader = new FileDownloader();
                await Throws<LocalFileException>(() => new DownloadEngine(Disk(Item(), new Uri("https://downloader.disk.yandex.ru/x")), downloader).RunAsync(folder));
            }
            finally { if (!child.HasExited) child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            await using var server = new TestServer(Ranged); using var again = new FileDownloader(Http());
            var result = await new DownloadEngine(Disk(Item(), server.BaseUri), again, new FastDelay()).RunAsync(folder);
            Check(result.Succeeded && server.Requests.First().Header("Range") == "bytes=3-");
        });
        yield return new("W06 cancellation releases destination lock", async () =>
        {
            var folder = Dir(scratch); using var stop = new CancellationTokenSource();
            await using var server = new TestServer((_, _) => new([], 503)); using var downloader = new FileDownloader(Http());
            await Throws<OperationCanceledException>(() => new DownloadEngine(Disk(Item(), server.BaseUri), downloader, new CancelDelay(stop)).RunAsync(folder, cancellationToken: stop.Token));
            using var pins = new DirectoryPins(); pins.Pin(Path.Combine(folder, ".diskloader-partials"), exclusiveCache: true);
        });
        yield return new("W01 long Unicode path over MAX_PATH", async () =>
        {
            var folder = Dir(scratch); var names = Enumerable.Range(0, 5).Select(i => new string('я', 60) + i).ToArray();
            var paths = Enumerable.Range(0, names.Length).Select(i => "/" + string.Join('/', names.Take(i + 1))).ToArray();
            var disk = new FakeDisk(new("Folder", "dir"), [], _ => Task.FromResult(new Uri("https://unused.yandex.ru")))
            { Listing = path => path == "/" ? [new(names[0], "dir")] : Array.IndexOf(paths, path) is var i && i < names.Length - 1 ? [new(names[i + 1], "dir")] : [Item("Фото 🌱.bin")] };
            await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http());
            var real = new FakeDisk(new("Folder", "dir"), [], _ => Task.FromResult(server.BaseUri)) { Listing = disk.Listing };
            var result = await new DownloadEngine(real, downloader, new FastDelay()).RunAsync(folder);
            var target = Path.Combine(new[] { folder, "Folder copy" }.Concat(names).Append("Фото 🌱.bin").ToArray());
            Check(result.Succeeded && target.Length > 260 && File.ReadAllBytes(target).SequenceEqual(Data));
        });
        yield return new("W07 readonly partial preserved without network retries", async () =>
        {
            var folder = Dir(scratch); var partial = Partial(folder, Item()); File.WriteAllBytes(partial, Data[..3]); File.SetAttributes(partial, FileAttributes.ReadOnly);
            try
            {
                using var downloader = new FileDownloader(); var disk = new FakeDisk(Item(), [], _ => throw new Exception("Unexpected network"));
                var result = await new DownloadEngine(disk, downloader).RunAsync(folder);
                Check(!result.Succeeded && disk.Calls == 0 && File.ReadAllBytes(partial).SequenceEqual(Data[..3]));
            }
            finally { File.SetAttributes(partial, FileAttributes.Normal); }
        });
        yield return new("W08 FAT32 policy rejects over 4GiB; exFAT policy accepts", async () =>
        {
            FileSystemLimits.CheckFormat("FAT32", uint.MaxValue);
            await Throws<LocalFileException>(() => { FileSystemLimits.CheckFormat("FAT32", (long)uint.MaxValue + 1); return Task.CompletedTask; });
            FileSystemLimits.CheckFormat("exFAT", (long)uint.MaxValue + 1);
        });
        yield return new("W04 existing file blocks directory subtree, sibling continues", async () =>
        {
            var folder = Dir(scratch); var target = Path.Combine(folder, "Folder copy"); Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "blocked"), "keep");
            await using var server = new TestServer(Ranged); using var downloader = new FileDownloader(Http());
            var disk = new FakeDisk(new("Folder", "dir"), [], _ => Task.FromResult(server.BaseUri))
            { Listing = path => path == "/" ? [new("blocked", "dir"), Item("good")] : [Item()] };
            var result = await new DownloadEngine(disk, downloader, new FastDelay()).RunAsync(folder);
            Check(result.Done == 1 && result.Failures.Count == 1 && File.ReadAllText(Path.Combine(target, "blocked")) == "keep");
        });
    }
}
