import Foundation

// Navigation and source choices live in memory only. Remove preferences created
// by pre-1.0.2 versions and the app's own standard file-panel history.
func clearPrivatePreferences(_ defaults: UserDefaults = .standard, domain: String? = Bundle.main.bundleIdentifier) {
    defaults.removeObject(forKey: "lastLink")
    if let domain, let values = defaults.persistentDomain(forName: domain) {
        for key in values.keys where key.hasPrefix("NSNav") || key.hasPrefix("NSOSP") || key.hasPrefix("NSDocument") || key == "NSWindow Frame GoToSheet" {
            defaults.removeObject(forKey: key)
        }
    }
    defaults.synchronize()
}
func clearLegacyWindowState() {
    guard let identifier = Bundle.main.bundleIdentifier,
          let library = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask).first else { return }
    let state = library.appendingPathComponent("Saved Application State", isDirectory: true)
        .appendingPathComponent(identifier + ".savedState", isDirectory: true)
    // Only this app's old UI-restoration archive; never downloaded files.
    try? FileManager.default.removeItem(at: state)
}
func transientNetworkConfiguration() -> URLSessionConfiguration {
    let config = URLSessionConfiguration.ephemeral
    config.httpCookieStorage = nil
    config.httpShouldSetCookies = false
    config.httpCookieAcceptPolicy = .never
    config.urlCredentialStorage = nil
    config.urlCache = nil
    config.requestCachePolicy = .reloadIgnoringLocalCacheData
    return config
}
func allowedDownloadURL(_ url: URL) -> Bool {
    guard url.scheme == "https", url.user == nil, url.password == nil,
          url.port == nil || url.port == 443, let host = url.host?.lowercased() else { return false }
    return host.hasSuffix(".yandex.ru") || host.hasSuffix(".yandex.net")
}
func partialFileName(size: Int64, sha256: String?, md5: String?) -> String {
    // No source link, folder name, destination path, or account identifier.
    let identity = [String(size), sha256 ?? "", md5 ?? ""].joined(separator: "\n")
    return hexHash(identity) + ".part"
}
