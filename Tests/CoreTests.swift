import Foundation
import CryptoKit

func check(_ condition: @autoclosure () -> Bool, _ message: String) throws {
    guard condition() else { throw LoaderError("FAIL: " + message) }
    print("PASS: " + message)
}
final class Captures: @unchecked Sendable {
    private let lock = NSLock()
    var values: [LoaderEvent] = []
    func add(_ value: LoaderEvent) { lock.lock(); values.append(value); lock.unlock() }
}
struct FakeDisk: DiskClient {
    let link = "test-source"
    let root: RemoteItem
    let files: [RemoteItem]
    func rootItem() async throws -> RemoteItem { root }
    func children(_ path: String) async throws -> [RemoteItem] { path == "/" ? files : [] }
    func downloadURL(_ path: String) async throws -> URL { URL(string: "http://127.0.0.1:18876/normal")! }
}
func item(_ name: String, type: String = "file", data: Data? = nil) -> RemoteItem {
    RemoteItem(name: name, type: type, size: data.map { Int64($0.count) }, md5: nil,
               sha256: data.map { SHA256.hash(data: $0).map { String(format:"%02x", $0) }.joined() }, embedded: nil)
}
@main struct Tests {
 static func main() async throws {
    let fm = FileManager.default
    let base = fm.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try fm.createDirectory(at: base, withIntermediateDirectories: true)
    defer { try? fm.removeItem(at: base) }
    let data = Data("abcdef".utf8)
    let meta = item("file", data: data)
    let file = base.appendingPathComponent("verified")
    try data.write(to: file)
    let good = try verify(file, meta)
    try check(good, "SHA-256 accepts intact file")
    try Data("badbad".utf8).write(to: file)
    let bad = try verify(file, meta)
    try check(!bad, "SHA-256 rejects same-size corruption")
    do { _ = try safeChild(base, "../escape"); throw LoaderError("FAIL: traversal accepted") }
    catch let e as LoaderError { try check(!e.message.hasPrefix("FAIL"), "unsafe names rejected") }
    let symlink = base.appendingPathComponent("symlink")
    try fm.createSymbolicLink(at: symlink, withDestinationURL: file)
    do { _ = try safeChild(base, "symlink"); throw LoaderError("FAIL: symlink accepted") }
    catch let e as LoaderError { try check(!e.message.hasPrefix("FAIL"), "symlink destinations rejected") }
    for endpoint in ["normal", "ignore"] {
        let part = base.appendingPathComponent(endpoint)
        try Data("abc".utf8).write(to: part)
        let transfer = FileTransfer(partial: part, offset: 3, expected: 6, report: { _ in })
        try await transfer.start(URL(string:"http://127.0.0.1:18876/" + endpoint)!)
        let saved = try Data(contentsOf: part)
        try check(saved == data, endpoint == "normal" ? "Range resumes partial file" : "ignored Range restarts safely")
    }
    do {
        let part = base.appendingPathComponent("wrong")
        let transfer = FileTransfer(partial: part, offset: 0, expected: 6, report: { _ in })
        try await transfer.start(URL(string:"http://127.0.0.1:18876/wrong")!)
        throw LoaderError("FAIL: bad range accepted")
    } catch let e as LoaderError { try check(!e.message.hasPrefix("FAIL"), "invalid Content-Range rejected") }
    let cancelledPart = base.appendingPathComponent("cancelled")
    let task = Task {
        let transfer = FileTransfer(partial: cancelledPart, offset: 0, expected: 1024*1024, report: { _ in })
        try await transfer.start(URL(string:"http://127.0.0.1:18876/slow")!)
    }
    try await Task.sleep(nanoseconds: 200_000_000)
    task.cancel()
    do { try await task.value; throw LoaderError("FAIL: cancellation ignored") }
    catch { try check(fileSize(cancelledPart) > 0 && fileSize(cancelledPart) < 1024*1024, "cancel keeps partial bytes") }
    let root = item("Folder", type: "dir")
    let api = FakeDisk(root: root, files: [item("a-new", data: data), item("z-existing", data: data), item("Empty", type: "dir")])
    let target = base.appendingPathComponent("Folder copy")
    try fm.createDirectory(at: target, withIntermediateDirectories: true)
    try data.write(to: target.appendingPathComponent("z-existing"))
    let captures = Captures()
    try await DownloadEngine(api: api, destination: base, report: { captures.add($0) }).run()
    let newFile = try Data(contentsOf: target.appendingPathComponent("a-new"))
    try check(newFile == data, "engine downloads missing file")
    try check(exists(target.appendingPathComponent("Empty")), "empty directory preserved")
    var credited = false, beforeDownload = false
    for event in captures.values {
        if case .totals(let done, let skipped, let bytes, _) = event, done == 1, skipped == 1, bytes == 6 { credited = true }
        if case .file("a-new", _, false) = event { beforeDownload = credited; break }
    }
    try check(beforeDownload, "all existing bytes credited before first download")
    let again = Captures()
    try await DownloadEngine(api: api, destination: base, report: { again.add($0) }).run()
    let counts = again.values.compactMap { event -> Int? in
        if case .totals(_, let skipped, _, _) = event { return skipped }; return nil
    }
    try check(counts.last == 2, "restart verifies and skips all complete files")
    try Data("keepme".utf8).write(to: target.appendingPathComponent("a-new"))
    let conflicting = Captures()
    try await DownloadEngine(api: api, destination: base, report: { conflicting.add($0) }).run()
    let kept = try Data(contentsOf: target.appendingPathComponent("a-new"))
    try check(kept == Data("keepme".utf8), "conflicting local file preserved")
    let defaultsDomain = "diskloader.test." + UUID().uuidString
    let prefs = UserDefaults(suiteName: defaultsDomain)!
    defer { prefs.removePersistentDomain(forName: defaultsDomain) }
    prefs.set("private-source-marker", forKey: "lastLink")
    prefs.set("private-destination-marker", forKey: "NSOSPLastRootDirectory")
    prefs.set("another-private-folder", forKey: "NSNavLastRootDirectory")
    clearPrivatePreferences(prefs, domain: defaultsDomain)
    try check(prefs.string(forKey: "lastLink") == nil, "legacy source preference deleted")
    try check(prefs.string(forKey: "NSOSPLastRootDirectory") == nil && prefs.string(forKey: "NSNavLastRootDirectory") == nil,
              "legacy folder-picker history deleted")
    let config = transientNetworkConfiguration()
    try check(config.urlCache == nil && config.urlCredentialStorage == nil, "network cache and credential storage disabled")
    try check(config.httpCookieStorage == nil && !config.httpShouldSetCookies, "cookies disabled")
    try check(!allowedDownloadURL(URL(string: "https://yandex.ru.attacker.example/file")!), "lookalike external host rejected")
    try check(!allowedDownloadURL(URL(string: "http://downloader.disk.yandex.ru/file")!), "HTTP downgrade rejected")
    try check(allowedDownloadURL(URL(string: "https://downloader.disk.yandex.ru/file")!), "Yandex HTTPS download allowed")
    let leftovers = try fm.contentsOfDirectory(at: base, includingPropertiesForKeys: nil)
    try check(!leftovers.contains { $0.lastPathComponent.hasPrefix(".yadisk-errors-") }, "no persistent error report or path history")
    let partialName = partialFileName(size: 6, sha256: meta.sha256, md5: nil)
    try check(partialName.count == 69 && !partialName.contains("test-source"), "partial filename uses content fingerprint only")
    print("ALL CHECKS PASSED")
 }
}
