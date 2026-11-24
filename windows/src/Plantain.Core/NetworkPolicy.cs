namespace Plantain.Core;

public static class NetworkPolicy
{
    public static Uri ParsePublicLink(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || !IsPublicDisk(uri))
            throw new LoaderException("Нужна публичная HTTPS-ссылка Яндекс Диска.");
        return uri;
    }

    public static bool IsApi(Uri uri) => IsHttps(uri) && uri.Host == "cloud-api.yandex.net";
    public static bool IsDownload(Uri uri) => IsHttps(uri) &&
        (uri.Host.EndsWith(".yandex.ru", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".yandex.net", StringComparison.OrdinalIgnoreCase));

    internal static bool IsPublicDisk(Uri uri) => IsHttps(uri) &&
        uri.Host is "disk.yandex.ru" or "disk.yandex.com" or "disk.yandex.com.tr" or "disk.360.yandex.ru" or "yadi.sk";
    internal static bool IsPreviewStream(Uri uri) => IsHttps(uri) && uri.Host == "streaming.disk.yandex.net";

    private static bool IsHttps(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" &&
        uri.UserInfo.Length == 0 && uri.Port == 443;
}
