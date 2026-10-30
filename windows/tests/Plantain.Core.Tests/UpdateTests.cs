using System.Net;
using System.Text.Json;
using Plantain.Core;

internal static partial class Suite
{
    private static object Release(string version, string prefix = "windows-v", bool draft = false, bool preview = true,
        bool zip = true, bool checksum = true, string platform = "win-x64")
    {
        var name = $"Plantain-{version}-{platform}.zip";
        var assets = new List<object>();
        if (zip) assets.Add(new { name, state = "uploaded", size = 100 });
        if (checksum) assets.Add(new { name = name + ".sha256", state = "uploaded", size = 90 });
        return new { tag_name = prefix + version, draft, prerelease = preview, assets,
            html_url = "https://evil.example/not-used" };
    }
    private static Task<UpdateCheckResult> CheckRelease(object[] releases, string current = "0.1.0-dev.6")
    {
        var checker = new GitHubWindowsUpdates(() => new HttpClient(new MockHandler(request =>
        {
            Check(request.Method == HttpMethod.Get && request.Content is null && request.Headers.Authorization is null);
            Check(request.RequestUri!.Host == "api.github.com" && request.RequestUri.AbsolutePath == "/repos/sirdimitry/plantain/releases");
            Check(request.Headers.UserAgent.ToString() == "Plantain-Windows");
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(releases)) };
        })));
        return checker.CheckAsync(current, default);
    }

    internal static IEnumerable<TestCase> UpdateCases()
    {
        yield return new("U01 Windows update selects exact ZIP and SHA256, constructs official URL", async () =>
        {
            var result = await CheckRelease([Release("0.1.0-dev.7")]);
            Check(result.Status == UpdateCheckStatus.Available && result.Update!.Version == "0.1.0-dev.7");
            Check(result.Update!.ReleasePage.AbsoluteUri == "https://github.com/sirdimitry/plantain/releases/tag/windows-v0.1.0-dev.7");
        });
        yield return new("U02 macOS releases never offered in Windows", async () =>
            Check((await CheckRelease([Release("99.0.0", prefix: "v", preview: false)])).Status == UpdateCheckStatus.NoUpdate));
        yield return new("U03 equal and older releases never offered", async () =>
            Check((await CheckRelease([Release("0.1.0-dev.6"), Release("0.1.0-dev.5")])).Status == UpdateCheckStatus.NoUpdate));
        yield return new("U04 numeric prerelease comparison dev10 greater than dev9", async () =>
            Check((await CheckRelease([Release("0.1.0-dev.10")], "0.1.0-dev.9")).Status == UpdateCheckStatus.Available));
        yield return new("U05 selects highest version regardless of listing order", async () =>
            Check((await CheckRelease([Release("0.1.0-dev.8"), Release("0.2.0", preview: false), Release("0.1.0-dev.10")])).Update!.Version == "0.2.0"));
        foreach (var (name, release) in new (string, object)[]
        {
            ("draft", Release("0.2.0", draft: true)), ("missing ZIP", Release("0.2.0", zip: false)),
            ("missing checksum", Release("0.2.0", checksum: false)), ("ARM64", Release("0.2.0", platform: "win-arm64")),
            ("invalid version", Release("banana")), ("numeric leading zero", Release("0.1.0-dev.07"))
        })
            yield return new("U06 rejects " + name, async () => Check((await CheckRelease([release])).Status == UpdateCheckStatus.NoUpdate));
        yield return new("U07 stable installation does not offer preview channel", async () =>
            Check((await CheckRelease([Release("2.0.0-dev.1")], "1.0.0")).Status == UpdateCheckStatus.NoUpdate));
        yield return new("U08 preview installation may upgrade to stable", async () =>
            Check((await CheckRelease([Release("0.1.0", preview: false)])).Status == UpdateCheckStatus.Available));
        yield return new("U09 semantic version ignores build metadata and stable beats prerelease", () =>
        {
            ReleaseVersion.TryParse("1.0.0+abc", out var a); ReleaseVersion.TryParse("1.0.0+xyz", out var b);
            ReleaseVersion.TryParse("1.0.0-rc.1", out var rc); Check(a!.CompareTo(b) == 0 && a.CompareTo(rc) > 0);
            Check(!ReleaseVersion.TryParse("1.00.0", out _) && !ReleaseVersion.TryParse("1.0.0-", out _)); return Task.CompletedTask;
        });
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.Found })
            yield return new("U10 unavailable or redirect HTTP " + (int)status + " is quiet", async () =>
            {
                var checker = new GitHubWindowsUpdates(() => new HttpClient(new MockHandler(_ => new(status))));
                Check((await checker.CheckAsync("0.1.0-dev.6", default)).Status == UpdateCheckStatus.Unavailable);
            });
        yield return new("U11 network failure is quiet", async () =>
        {
            var checker = new GitHubWindowsUpdates(() => new HttpClient(new MockHandler(_ => throw new HttpRequestException("offline"))));
            Check((await checker.CheckAsync("0.1.0-dev.6", default)).Status == UpdateCheckStatus.Unavailable);
        });
        yield return new("U12 malformed JSON is quiet", async () =>
        {
            var checker = new GitHubWindowsUpdates(() => new HttpClient(new MockHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("not JSON") })));
            Check((await checker.CheckAsync("0.1.0-dev.6", default)).Status == UpdateCheckStatus.Unavailable);
        });
        yield return new("U13 pagination finds Windows release after macOS page", async () =>
        {
            var calls = 0;
            var checker = new GitHubWindowsUpdates(() => new HttpClient(new MockHandler(request =>
            {
                calls++;
                Check(request.RequestUri!.Query.Contains("page=" + calls));
                var items = calls == 1 ? Enumerable.Range(0, 100).Select(_ => Release("99.0.0", prefix: "v")).ToArray() : [Release("0.1.0-dev.7")];
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(items)) };
            })));
            Check((await checker.CheckAsync("0.1.0-dev.6", default)).Status == UpdateCheckStatus.Available && calls == 2);
        });
        yield return new("U14 closed window cancellation propagates", async () =>
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            var checker = new GitHubWindowsUpdates(() => new HttpClient(new MockHandler(_ => throw new Exception("Request should not run"))));
            await Throws<OperationCanceledException>(() => checker.CheckAsync("0.1.0-dev.6", cancel.Token));
        });
    }
}
