import Foundation
import CryptoKit

func check(_ condition: @autoclosure () -> Bool, _ message: String) throws {
    guard condition() else { throw LoaderError("FAIL: " + message) }
    print("PASS: " + message)
}
// FIX: порт тестового сервера приходит из окружения, тест больше не привязан
// к 18876. Иначе занятый порт ломал проверки или подключал их к чужому сервису.
let testPort = ProcessInfo.processInfo.environment["TEST_PORT"] ?? "18876"
func testURL(_ path: String) -> URL { URL(string: "http://127.0.0.1:\(testPort)/" + path)! }
final class Captures: @unchecked Sendable {
    private let lock = NSLock()
    var values: [LoaderEvent] = []
    func add(_ value: LoaderEvent) { lock.lock(); values.append(value); lock.unlock() }
}
// FIX: счётчик запросов ссылки — проверяет, что постоянная ошибка не повторяется.
final class Counter: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0
    func increment() { lock.lock(); value += 1; lock.unlock() }
    var count: Int { lock.lock(); defer { lock.unlock() }; return value }
}
struct FakeDisk: DiskClient {
    let link = "test-source"
    let root: RemoteItem
    let files: [RemoteItem]
    func rootItem() async throws -> RemoteItem { root }
    func children(_ path: String) async throws -> [RemoteItem] { path == "/" ? files : [] }
    func downloadURL(_ path: String) async throws -> URL { testURL("normal") }
}
// FIX: имитирует постоянную ошибку API при получении ссылки (HTTP 400).
struct RejectingDisk: DiskClient {
    let link = "test-source"
    let root: RemoteItem
    let requests: Counter
    func rootItem() async throws -> RemoteItem { root }
    func children(_ path: String) async throws -> [RemoteItem] { [] }
    func downloadURL(_ path: String) async throws -> URL {
        requests.increment()
        throw FatalFailure(error: LoaderError("Яндекс Диск: HTTP 400"))
    }
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
    // FIX: сервер может не отдавать хеш; такой файл проверяется по размеру.
    let plain = base.appendingPathComponent("plain")
    try data.write(to: plain)
    let noHash = RemoteItem(name: "plain", type: "file", size: 6, md5: nil, sha256: nil, embedded: nil)
    let noHashVerified = try verify(plain, noHash)
    try check(noHashVerified, "no-checksum file accepted by matching size")
    try check(targetName(for: "video.mp4", isDirectory: false) == "video.mp4",
              "single file keeps its own name")
    try check(targetName(for: "archive.tar.gz", isDirectory: false) == "archive.tar.gz",
              "single file name is not mangled")
    try check(targetName(for: "Folder", isDirectory: true) == "Folder copy",
              "folder keeps plain copy suffix")
    try check(!isRetryable(status: 404) && !isRetryable(status: 403) && !isRetryable(status: 400),
              "final HTTP statuses are not retried")
    try check(isRetryable(status: 503) && isRetryable(status: 429) && isRetryable(status: 408),
              "transient HTTP statuses are retried")
    try check(isLocalFileSystemError(NSError(domain: NSPOSIXErrorDomain, code: 18)),
              "POSIX filesystem errors stop retries")
    try check(!isLocalFileSystemError(URLError(.timedOut)), "network errors still retry")
    // FIX: ECONNRESET в NSPOSIXErrorDomain — сетевой обрыв, повтор должен остаться.
    try check(!isLocalFileSystemError(NSError(domain: NSPOSIXErrorDomain, code: Int(ECONNRESET))),
              "connection reset is not treated as a filesystem error")
    try check(isLocalFileSystemError(NSError(domain: NSPOSIXErrorDomain, code: Int(ENOSPC))) &&
              isLocalFileSystemError(NSError(domain: NSPOSIXErrorDomain, code: Int(EXDEV))),
              "disk-full and cross-device errors stop retries")
    try check(isLocalFileSystemError(NSError(domain: NSCocoaErrorDomain, code: NSFileWriteNoPermissionError)),
              "cocoa file errors stop retries")
    for endpoint in ["normal", "ignore"] {
        let part = base.appendingPathComponent(endpoint)
        try Data("abc".utf8).write(to: part)
        let transfer = FileTransfer(partial: part, offset: 3, expected: 6, report: { _ in })
        try await transfer.start(testURL(endpoint))
        let saved = try Data(contentsOf: part)
        try check(saved == data, endpoint == "normal" ? "Range resumes partial file" : "ignored Range restarts safely")
    }
    do {
        let part = base.appendingPathComponent("wrong")
        let transfer = FileTransfer(partial: part, offset: 0, expected: 6, report: { _ in })
        try await transfer.start(testURL("wrong"))
        throw LoaderError("FAIL: bad range accepted")
    } catch let e as LoaderError { try check(!e.message.hasPrefix("FAIL"), "invalid Content-Range rejected") }
    let cancelledPart = base.appendingPathComponent("cancelled")
    let task = Task {
        let transfer = FileTransfer(partial: cancelledPart, offset: 0, expected: 1024*1024, report: { _ in })
        try await transfer.start(testURL("slow"))
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
    // FIX: одиночный файл должен сохраняться под своим именем, без приписки " copy".
    let singleBase = base.appendingPathComponent("single")
    try fm.createDirectory(at: singleBase, withIntermediateDirectories: true)
    try await DownloadEngine(api: FakeDisk(root: item("movie.mp4", data: data), files: []),
                             destination: singleBase, report: { _ in }).run()
    try check(exists(singleBase.appendingPathComponent("movie.mp4")),
              "single file saved without copy suffix")
    // FIX: постоянная ошибка API при получении ссылки не должна повторяться 20 раз.
    let rejectBase = base.appendingPathComponent("reject")
    try fm.createDirectory(at: rejectBase, withIntermediateDirectories: true)
    let requests = Counter()
    let rejected = Captures()
    try await DownloadEngine(api: RejectingDisk(root: item("reject.mp4", data: data), requests: requests),
                             destination: rejectBase, report: { rejected.add($0) }).run()
    let rejectedFailures = rejected.values.compactMap { event -> Int? in
        if case .finished(let failures, _) = event { return failures.count }
        return nil
    }
    try check(requests.count == 1 && rejectedFailures.last == 1,
              "permanent API error is requested only once")
    // FIX: проверяем сам DiskAPI: HTTP 400 должен приходить как FatalFailure и не
    // повторяться. Повтор занимал бы ~15 секунд, поэтому проверяем и время.
    let started = Date()
    do {
        _ = try await DiskAPI(link: "test", base: "http://127.0.0.1:\(testPort)")
            .get(RemoteItem.self, path: "/")
        throw LoaderError("FAIL: API 400 accepted")
    } catch is FatalFailure {
        try check(Date().timeIntervalSince(started) < 5, "API 400 is final and not retried")
    }
    // FIX: файл без хеша проверяется только по размеру — и уже готовый, и свежескачанный.
    let plainBase = base.appendingPathComponent("sizeonly")
    let plainTarget = plainBase.appendingPathComponent("Plain copy")
    try fm.createDirectory(at: plainTarget, withIntermediateDirectories: true)
    try data.write(to: plainTarget.appendingPathComponent("plain.bin"))
    let plainChild = RemoteItem(name: "plain.bin", type: "file", size: 6, md5: nil, sha256: nil, embedded: nil)
    let freshChild = RemoteItem(name: "fresh.bin", type: "file", size: 6, md5: nil, sha256: nil, embedded: nil)
    let plainEvents = Captures()
    try await DownloadEngine(api: FakeDisk(root: item("Plain", type: "dir"), files: [plainChild, freshChild]),
                             destination: plainBase, report: { plainEvents.add($0) }).run()
    let plainSummary = plainEvents.values.compactMap { event -> (Int, Int)? in
        if case .finished(let failures, let sizeOnly) = event { return (failures.count, sizeOnly) }
        return nil
    }
    try check(plainSummary.last?.0 == 0 && plainSummary.last?.1 == 2 &&
              exists(plainTarget.appendingPathComponent("fresh.bin")),
              "size-only verification reported separately")
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
