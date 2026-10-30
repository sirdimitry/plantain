namespace Plantain.Core;

public static class NetworkPolicy
{
    public static Uri ParsePublicLink(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || !IsHttps(uri) ||
            uri.Host is not ("disk.yandex.ru" or "disk.yandex.com" or "disk.yandex.com.tr" or "yadi.sk"))
            throw new LoaderException("Нужна публичная HTTPS-ссылка Яндекс Диска.");
        return uri;
    }

    public static bool IsApi(Uri uri) => IsHttps(uri) && uri.Host == "cloud-api.yandex.net";
    public static bool IsDownload(Uri uri) => IsHttps(uri) &&
        (uri.Host.EndsWith(".yandex.ru", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".yandex.net", StringComparison.OrdinalIgnoreCase));

    private static bool IsHttps(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" &&
        uri.UserInfo.Length == 0 && uri.Port == 443;
}
