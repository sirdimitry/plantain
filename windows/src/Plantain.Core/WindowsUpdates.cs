using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Plantain.Core;

public enum UpdateCheckStatus { NoUpdate, Available, Unavailable }
public sealed record WindowsUpdate(string Version, Uri ReleasePage);
public sealed record UpdateCheckResult(UpdateCheckStatus Status, WindowsUpdate? Update = null);
public interface IUpdateChecker
{
    Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken);
}

// Windows releases deliberately have a separate tag namespace from macOS v1.x DMGs.
public sealed class GitHubWindowsUpdates : IUpdateChecker
{
    public const string TagPrefix = "windows-v";
    private readonly Func<HttpClient> createClient;
    public GitHubWindowsUpdates() : this(() => new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null, DefaultProxyCredentials = null,
        ActivityHeadersPropagator = null, AutomaticDecompression = DecompressionMethods.None
    })) { }
    internal GitHubWindowsUpdates(Func<HttpClient> createClient) => this.createClient = createClient;

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReleaseVersion.TryParse(currentVersion, out var current)) return new(UpdateCheckStatus.Unavailable);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var client = createClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.MaxResponseContentBufferSize = 4 * 1024 * 1024;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Plantain-Windows");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.CacheControl = new() { NoStore = true };
            WindowsUpdate? best = null;
            ReleaseVersion? bestVersion = null;
            for (var page = 1; page <= 5; page++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                using var response = await client.GetAsync($"https://api.github.com/repos/sirdimitry/plantain/releases?per_page=100&page={page}", timeout.Token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK) return new(UpdateCheckStatus.Unavailable);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                if (json.RootElement.ValueKind != JsonValueKind.Array) return new(UpdateCheckStatus.Unavailable);
                foreach (var release in json.RootElement.EnumerateArray())
                {
                    var candidate = Select(release, current!);
                    if (candidate is null) continue;
                    ReleaseVersion.TryParse(candidate.Version, out var version);
                    if (bestVersion is null || version!.CompareTo(bestVersion) > 0) { best = candidate; bestVersion = version; }
                }
                if (json.RootElement.GetArrayLength() < 100)
                    return best is null ? new(UpdateCheckStatus.NoUpdate) : new(UpdateCheckStatus.Available, best);
            }
            // A capped/incomplete listing is not evidence of being up to date.
            return new(UpdateCheckStatus.Unavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(UpdateCheckStatus.Unavailable); }
        catch (HttpRequestException) { return new(UpdateCheckStatus.Unavailable); }
        catch (JsonException) { return new(UpdateCheckStatus.Unavailable); }
    }

    private static WindowsUpdate? Select(JsonElement release, ReleaseVersion current)
    {
        if (release.ValueKind != JsonValueKind.Object || !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
            !release.TryGetProperty("prerelease", out var previewFlag) || previewFlag.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !release.TryGetProperty("tag_name", out var tagProperty) || tagProperty.ValueKind != JsonValueKind.String) return null;
        var tag = tagProperty.GetString()!;
        if (!tag.StartsWith(TagPrefix, StringComparison.Ordinal) ||
            !ReleaseVersion.TryParse(tag[TagPrefix.Length..], out var version) || version!.CompareTo(current) <= 0) return null;
        if (!current.IsPreview && (version.IsPreview || Flag(release, "prerelease"))) return null;
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        var archive = $"Plantain-{version.Text}-win-x64.zip";
        if (!HasAsset(assets, archive) || !HasAsset(assets, archive + ".sha256")) return null;
        // Construct the official URL; never launch a server-supplied arbitrary html_url.
        return new(version.Text, new Uri("https://github.com/sirdimitry/plantain/releases/tag/" + Uri.EscapeDataString(tag)));
    }

    private static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
    private static bool HasAsset(JsonElement assets, string name) => assets.EnumerateArray().Any(asset =>
        asset.ValueKind == JsonValueKind.Object && asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() == name &&
        asset.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() == "uploaded" &&
        asset.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var length) && length > 0);
}

internal sealed partial class ReleaseVersion : IComparable<ReleaseVersion>
{
    internal required string Text { get; init; }
    private int[] Core { get; init; } = [];
    private string[] Preview { get; init; } = [];
    internal bool IsPreview => Preview.Length > 0;

    internal static bool TryParse(string text, out ReleaseVersion? value)
    {
        value = null;
        if (text.Length > 150) return false;
        var match = Pattern().Match(text);
        if (!match.Success) return false;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
            if (!int.TryParse(match.Groups[i + 1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        var preview = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (preview.Any(x => x.Length > 1 && x.All(char.IsAsciiDigit) && x[0] == '0')) return false;
        value = new() { Text = text, Core = numbers, Preview = preview };
        return true;
    }
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < 3; i++) if (Core[i] != other.Core[i]) return Core[i].CompareTo(other.Core[i]);
        if (Preview.Length == 0 || other.Preview.Length == 0) return (other.Preview.Length > 0 ? 1 : 0) - (Preview.Length > 0 ? 1 : 0);
        for (var i = 0; i < Math.Min(Preview.Length, other.Preview.Length); i++)
        {
            var leftNumeric = Preview[i].All(char.IsAsciiDigit); var rightNumeric = other.Preview[i].All(char.IsAsciiDigit);
            var compared = leftNumeric && rightNumeric ? BigInteger.Parse(Preview[i], CultureInfo.InvariantCulture).CompareTo(BigInteger.Parse(other.Preview[i], CultureInfo.InvariantCulture)) :
                leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1) : StringComparer.Ordinal.Compare(Preview[i], other.Preview[i]);
            if (compared != 0) return compared;
        }
        return Preview.Length.CompareTo(other.Preview.Length);
    }
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
