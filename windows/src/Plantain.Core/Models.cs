using System.Text.Json.Serialization;

namespace Plantain.Core;

public sealed record RemoteItem(
    [property: JsonRequired, JsonPropertyName("name")] string Name,
    [property: JsonRequired, JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("size")] long? Size = null,
    [property: JsonPropertyName("md5")] string? Md5 = null,
    [property: JsonPropertyName("sha256")] string? Sha256 = null,
    [property: JsonPropertyName("_embedded")] Listing? Embedded = null)
{
    [JsonIgnore] public bool HasHash => Sha256 is not null || Md5 is not null;
}

public sealed record Listing(
    [property: JsonRequired, JsonPropertyName("items")] RemoteItem[] Items,
    [property: JsonPropertyName("total")] long? Total = null);

internal sealed record DownloadLink([property: JsonRequired, JsonPropertyName("href")] string Href);

public interface IDiskClient
{
    Task<RemoteItem> RootAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<RemoteItem>> ChildrenAsync(string path, CancellationToken cancellationToken);
    Task<Uri> DownloadUrlAsync(string path, CancellationToken cancellationToken);
}

public abstract record LoaderEvent;
public sealed record StatusChanged(string Text) : LoaderEvent;
public sealed record DestinationSelected(string Path) : LoaderEvent;
public sealed record InventoryFound(int Files, long Bytes) : LoaderEvent;
public sealed record FileDiscovered(string RemotePath, string Name, long Size) : LoaderEvent;
public sealed record FileCompleted(string RemotePath) : LoaderEvent;
public sealed record FileFailed(FileFailure Failure) : LoaderEvent;
public sealed record FileStarted(string Name, long Size, bool Verifying) : LoaderEvent;
public sealed record TransferProgress(long CurrentBytes, long NetworkBytes) : LoaderEvent;
public sealed record TotalsChanged(int Done, int Skipped, long CompletedBytes, int Errors) : LoaderEvent;
public sealed record DownloadFinished(DownloadResult Result) : LoaderEvent;
public sealed record FileFailure(string Name, string Message, bool IsDirectory = false);
public sealed record DownloadResult(int Files, int Done, int Skipped, long CompletedBytes,
    int SizeOnlyFiles, IReadOnlyList<FileFailure> Failures)
{
    public bool Succeeded => Failures.Count == 0 && Done == Files;
}

public class LoaderException(string message) : Exception(message);
public sealed class FatalApiException(string message) : LoaderException(message);
public sealed class NetworkFailureException(string message) : LoaderException(message);
public sealed class LocalFileException(string message) : LoaderException(message);
internal sealed class RetryableApiException(string message) : LoaderException(message);

public interface IRetryDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class RetryDelay : IRetryDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public static class RetryRules
{
    public static bool IsRetryableStatus(int status) => status is 408 or 429 or >= 500 and <= 599;
    public static TimeSpan ApiDelay(int failedAttempt) => TimeSpan.FromSeconds(Math.Min(1 << Math.Min(failedAttempt - 1, 4), 15));
    public static TimeSpan FileDelay(int failedAttempt) => TimeSpan.FromSeconds(Math.Min(1 << Math.Min(failedAttempt, 5), 30));
}
