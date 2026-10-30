namespace Plantain.Core;

public sealed class DownloadControl
{
    private readonly object sync = new();
    private CancellationTokenSource? current;
    internal void Set(CancellationTokenSource? value) { lock (sync) current = value; }
    public void SkipCurrent() { lock (sync) current?.Cancel(); }
}
