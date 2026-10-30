using Plantain.Core;

internal sealed class StubRunner(Func<CancellationToken, Task<DownloadResult>> run) : IDownloadRunner
{
    internal int Calls;
    internal List<string> Links = [];
    public Task<DownloadResult> RunAsync(string publicLink, string destination, Action<LoaderEvent>? report, CancellationToken token)
    { Calls++; Links.Add(publicLink); return run(token); }
}

internal static partial class Suite
{
    internal static IEnumerable<TestCase> SessionCases()
    {
        const string link = "https://disk.yandex.ru/d/synthetic";
        var success = new DownloadResult(1, 1, 0, 6, 0, []);
        yield return new("S01 success clears source and disables repeat action; result retained", async () =>
        {
            var runner = new StubRunner(_ => Task.FromResult(success)); var session = new DownloadSession(runner) { Link = link };
            Check(session.CanStart && session.ButtonText == "Скачать");
            await session.RunAsync("unused");
            Check(session.Link == "" && !session.CanStart && !session.IsRunning && session.ButtonText == "Скачать" && session.LastResult == success);
            await Throws<LoaderException>(() => session.RunAsync("unused")); Check(runner.Calls == 1);
        });
        yield return new("S02 empty folder success also clears source", async () =>
        {
            var session = new DownloadSession(new StubRunner(_ => Task.FromResult(new DownloadResult(0, 0, 0, 0, 0, [])))) { Link = link };
            await session.RunAsync("unused"); Check(session.Link == "" && !session.CanStart && session.LastResult!.Succeeded);
        });
        yield return new("S03 partial failure retains source for resume", async () =>
        {
            var partial = new DownloadResult(2, 1, 0, 6, 0, [new("failed", "network")]);
            var session = new DownloadSession(new StubRunner(_ => Task.FromResult(partial))) { Link = link };
            await session.RunAsync("unused"); Check(session.Link == link && session.CanStart && session.ButtonText == "Продолжить");
        });
        yield return new("S04 cancellation retains source for resume", async () =>
        {
            var session = new DownloadSession(new StubRunner(_ => throw new OperationCanceledException())) { Link = link };
            await Throws<OperationCanceledException>(() => session.RunAsync("unused"));
            Check(session.Link == link && !session.IsRunning && session.CanStart && session.ButtonText == "Продолжить");
        });
        yield return new("S05 fatal failure retains source for retry", async () =>
        {
            var session = new DownloadSession(new StubRunner(_ => throw new FatalApiException("API 404"))) { Link = link };
            await Throws<FatalApiException>(() => session.RunAsync("unused")); Check(session.Link == link && session.CanStart);
        });
        yield return new("S06 new link starts a new download after success", async () =>
        {
            var runner = new StubRunner(_ => Task.FromResult(success)); var session = new DownloadSession(runner) { Link = link };
            await session.RunAsync("unused"); session.Link = "https://disk.yandex.ru/d/new";
            await session.RunAsync("unused"); Check(runner.Links.SequenceEqual([link, "https://disk.yandex.ru/d/new"]) && session.Link == "");
        });
        yield return new("S07 running session rejects edits and second start", async () =>
        {
            var gate = new TaskCompletionSource<DownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var runner = new StubRunner(_ => gate.Task); var session = new DownloadSession(runner) { Link = link };
            var run = session.RunAsync("unused"); Check(session.IsRunning && !session.CanStart);
            try
            {
                await Throws<LoaderException>(() => session.RunAsync("unused"));
                await Throws<LoaderException>(() => { session.Link = "new"; return Task.CompletedTask; });
            }
            finally { gate.SetResult(success); await run; }
            Check(runner.Calls == 1 && session.Link == "");
        });
    }
}
