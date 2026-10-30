using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Plantain.Core;

public static class FileVerifier
{
    public static string PartialName(RemoteItem item)
    {
        var identity = string.Join('\n', item.Size?.ToString(CultureInfo.InvariantCulture) ?? "0", item.Sha256 ?? "", item.Md5 ?? "");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".part";
    }

    public static async Task<bool> VerifyAsync(string path, RemoteItem item, Action<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (new FileInfo(path).Length != item.Size) return false;
        if (!item.HasHash) return true;
        using var hash = IncrementalHash.CreateHash(item.Sha256 is not null ? HashAlgorithmName.SHA256 : HashAlgorithmName.MD5);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        long bytes = 0;
        var last = Environment.TickCount64;
        int read;
        while ((read = await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
            bytes += read;
            if (Environment.TickCount64 - last >= 120)
            {
                progress?.Invoke(bytes);
                last = Environment.TickCount64;
            }
        }
        progress?.Invoke(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        return bytes == item.Size && string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()),
            item.Sha256 ?? item.Md5, StringComparison.OrdinalIgnoreCase);
    }
}
