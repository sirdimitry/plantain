import Foundation
import CryptoKit

struct LoaderError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
    init(_ text: String) { message = text }
}
struct RemoteItem: Decodable {
    let name: String
    let type: String
    let size: Int64?
    let md5: String?
    let sha256: String?
    let embedded: Listing?
    enum CodingKeys: String, CodingKey {
        case name, type, size, md5, sha256
        case embedded = "_embedded"
    }
}
struct Listing: Decodable { let items: [RemoteItem]; let total: Int? }
struct DownloadLink: Decodable { let href: String }
struct WorkItem {
    let remote: String
    let local: URL
    let item: RemoteItem
    var size: Int64 { item.size ?? 0 }
}
enum LoaderEvent {
    case status(String)
    case destination(URL)
    case inventory(Int, Int64)
    case file(String, Int64, Bool)
    case progress(Int64, Int64)
    case totals(Int, Int, Int64, Int)
    case finished([String])
}
typealias Reporter = @Sendable (LoaderEvent) -> Void

func safeChild(_ parent: URL, _ name: String) throws -> URL {
    guard !name.isEmpty, name != ".", name != "..", !name.contains("/"),
          !name.contains("\\"), !name.contains("\0") else {
        throw LoaderError("Не удаётся сохранить имя файла: \(name)")
    }
    let url = parent.appendingPathComponent(name)
    if (try? url.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true {
        throw LoaderError("На месте файла находится символическая ссылка: \(name)")
    }
    return url
}
func hexHash(_ value: String) -> String {
    SHA256.hash(data: Data(value.utf8)).map { String(format: "%02x", $0) }.joined()
}
func fileSize(_ url: URL) -> Int64 {
    (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.int64Value ?? 0
}
func exists(_ url: URL) -> Bool { FileManager.default.fileExists(atPath: url.path) }
func verify(_ url: URL, _ item: RemoteItem, report: ((Int64) -> Void)? = nil) throws -> Bool {
    guard exists(url), fileSize(url) == item.size else { return false }
    guard item.sha256 != nil || item.md5 != nil else { return false }
    let handle = try FileHandle(forReadingFrom: url)
    defer { try? handle.close() }
    var sha = SHA256(), md5 = Insecure.MD5(), bytes: Int64 = 0
    var last = Date.distantPast
    while let data = try handle.read(upToCount: 1024 * 1024), !data.isEmpty {
        try Task.checkCancellation()
        if item.sha256 != nil { sha.update(data: data) } else { md5.update(data: data) }
        bytes += Int64(data.count)
        if Date().timeIntervalSince(last) > 0.12 {
            report?(bytes)
            last = Date()
        }
    }
    report?(bytes)
    let digest = item.sha256 != nil ? sha.finalize().map { String(format: "%02x", $0) }.joined()
        : md5.finalize().map { String(format: "%02x", $0) }.joined()
    return digest == (item.sha256 ?? item.md5 ?? "").lowercased()
}

// Receives data on a serial delegate queue and writes it directly to disk.
// Memory use stays bounded even for multi-gigabyte videos.
final class FileTransfer: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private var task: URLSessionDataTask?
    private var cancelled = false
    private var session: URLSession?
    private var continuation: CheckedContinuation<Void, Error>?
    private var handle: FileHandle?
    private var failure: Error?
    private var offset: Int64
    private let expected: Int64
    private let partial: URL
    private let report: Reporter
    private var last = Date.distantPast
    private var pendingNetwork: Int64 = 0

    init(partial: URL, offset: Int64, expected: Int64, report: @escaping Reporter) {
        self.partial = partial; self.offset = offset; self.expected = expected; self.report = report
    }
    func start(_ url: URL) async throws {
        try await withTaskCancellationHandler(operation: {
            try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
                self.continuation = cont
                let config = transientNetworkConfiguration()
                config.timeoutIntervalForRequest = 60
                config.timeoutIntervalForResource = 7 * 24 * 3600
                config.httpCookieStorage = nil
                config.requestCachePolicy = .reloadIgnoringLocalCacheData
                let queue = OperationQueue()
                queue.maxConcurrentOperationCount = 1
                let session = URLSession(configuration: config, delegate: self, delegateQueue: queue)
                self.session = session
                var request = URLRequest(url: url)
                request.setValue("identity", forHTTPHeaderField: "Accept-Encoding")
                if self.offset > 0 { request.setValue("bytes=\(self.offset)-", forHTTPHeaderField: "Range") }
                let task = session.dataTask(with: request)
                self.lock.lock()
                self.task = task
                let cancelled = self.cancelled
                self.lock.unlock()
                task.resume()
                if cancelled { task.cancel() }
            }
        }, onCancel: { self.cancel() })
    }
    private func cancel() {
        lock.lock(); cancelled = true; let task = task; lock.unlock()
        task?.cancel()
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask,
                    didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        do {
            guard let http = response as? HTTPURLResponse else { throw LoaderError("Нет ответа сервера") }
            if http.statusCode == 200 {
                offset = 0
            } else if http.statusCode == 206 {
                let range = http.value(forHTTPHeaderField: "Content-Range") ?? ""
                guard range.hasPrefix("bytes \(offset)-"), range.hasSuffix("/\(expected)") else {
                    throw LoaderError("Сервер вернул неверную часть файла")
                }
            } else {
                if http.statusCode == 416 { try? FileManager.default.removeItem(at: partial) }
                throw LoaderError("Сервер скачивания: HTTP \(http.statusCode)")
            }
            if !exists(partial) {
                guard FileManager.default.createFile(atPath: partial.path, contents: nil) else {
                    throw LoaderError("Не удалось создать временный файл")
                }
            }
            handle = try FileHandle(forWritingTo: partial)
            if offset == 0 { try handle?.truncate(atOffset: 0) }
            try handle?.seek(toOffset: UInt64(offset))
            report(.progress(offset, 0))
            completionHandler(.allow)
        } catch {
            failure = error
            completionHandler(.cancel)
        }
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard failure == nil else { return }
        do {
            guard offset + Int64(data.count) <= expected else { throw LoaderError("Размер файла не совпал") }
            try handle?.write(contentsOf: data)
            offset += Int64(data.count)
            pendingNetwork += Int64(data.count)
            if Date().timeIntervalSince(last) > 0.12 {
                report(.progress(offset, pendingNetwork))
                pendingNetwork = 0
                last = Date()
            }
        } catch { failure = error; dataTask.cancel() }
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        do { try handle?.close() } catch { if failure == nil { failure = error } }
        report(.progress(offset, pendingNetwork))
        let resultError = failure ?? error
        let continuation = continuation
        self.continuation = nil
        session.finishTasksAndInvalidate()
        self.session = nil
        if let resultError { continuation?.resume(throwing: resultError) }
        else if offset != expected { continuation?.resume(throwing: LoaderError("Файл получен не полностью")) }
        else { continuation?.resume() }
    }
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        guard let url = request.url, allowedDownloadURL(url) else {
            failure = LoaderError("Небезопасное перенаправление")
            completionHandler(nil); return
        }
        completionHandler(request)
    }
}

protocol DiskClient: Sendable {
    var link: String { get }
    func rootItem() async throws -> RemoteItem
    func children(_ path: String) async throws -> [RemoteItem]
    func downloadURL(_ path: String) async throws -> URL
}

final class APIRedirectPolicy: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        guard let url = request.url, url.scheme == "https", url.host == "cloud-api.yandex.net" else {
            completionHandler(nil); return
        }
        completionHandler(request)
    }
}

final class DiskAPI: DiskClient, @unchecked Sendable {
    let link: String
    private let session: URLSession
    init(link: String) {
        self.link = link
        let config = transientNetworkConfiguration()
        config.timeoutIntervalForRequest = 45
        config.timeoutIntervalForResource = 90
        config.httpCookieStorage = nil
        session = URLSession(configuration: config, delegate: APIRedirectPolicy(), delegateQueue: nil)
    }
    deinit { session.invalidateAndCancel() }
    func rootItem() async throws -> RemoteItem { try await get(RemoteItem.self, path: "/") }
    func get<T: Decodable>(_ type: T.Type, path: String, download: Bool = false,
                           offset: Int? = nil) async throws -> T {
        var url = URLComponents(string: "https://cloud-api.yandex.net/v1/disk/public/resources" + (download ? "/download" : ""))!
        var params = [URLQueryItem(name: "public_key", value: link), URLQueryItem(name: "path", value: path)]
        if let offset {
            params += [URLQueryItem(name: "limit", value: "200"), URLQueryItem(name: "offset", value: String(offset)),
                       URLQueryItem(name: "sort", value: "name")]
        }
        url.queryItems = params
        let attempts = download ? 1 : 5
        var last: Error = LoaderError("Нет ответа от Яндекс Диска")
        for attempt in 0..<attempts {
            try Task.checkCancellation()
            do {
                var request = URLRequest(url: url.url!)
                request.setValue("application/json", forHTTPHeaderField: "Accept")
                let (data, response) = try await session.data(for: request)
                guard let http = response as? HTTPURLResponse else { throw LoaderError("Нет ответа сервера") }
                guard http.statusCode == 200 else {
                    if http.statusCode == 404 && !download { throw LoaderError("Папка не найдена. Проверьте ссылку и доступ по ней.") }
                    throw LoaderError("Яндекс Диск: HTTP \(http.statusCode)")
                }
                return try JSONDecoder().decode(type, from: data)
            } catch {
                try Task.checkCancellation()
                last = error
                if attempt + 1 < attempts { try await Task.sleep(nanoseconds: UInt64(min(1 << attempt, 15)) * 1_000_000_000) }
            }
        }
        throw last
    }
    func children(_ path: String) async throws -> [RemoteItem] {
        var result: [RemoteItem] = [], offset = 0
        while true {
            let response = try await get(RemoteItem.self, path: path, offset: offset)
            guard let listing = response.embedded else { throw LoaderError("Не удалось получить список папки") }
            if listing.items.isEmpty {
                if let total = listing.total, offset < total { throw LoaderError("Список папки получен не полностью. Попробуйте ещё раз.") }
                break
            }
            result += listing.items
            offset += listing.items.count
            if let total = listing.total, offset >= total { break }
        }
        return result
    }
    func downloadURL(_ path: String) async throws -> URL {
        let result = try await get(DownloadLink.self, path: path, download: true)
        guard let url = URL(string: result.href), allowedDownloadURL(url) else { throw LoaderError("Нет ссылки скачивания") }
        return url
    }
}

struct DownloadEngine {
    let api: any DiskClient
    let destination: URL
    let report: Reporter
    private let fm = FileManager.default

    func run() async throws {
        report(.status("Собираю список файлов…"))
        let root = try await api.rootItem()
        let target = try safeChild(destination, root.name + " copy")
        let cache = try safeChild(destination, ".diskloader-partials")
        try fm.createDirectory(at: cache, withIntermediateDirectories: true)
        report(.destination(root.type == "dir" ? target : destination))
        var files: [WorkItem] = []
        var pending = [("/", target, root)]
        while let (remote, local, item) = pending.popLast() {
            try Task.checkCancellation()
            if item.type == "dir" {
                try fm.createDirectory(at: local, withIntermediateDirectories: true)
                report(.status("Собираю список · найдено файлов: \(files.count)"))
                for child in try await api.children(remote) {
                    let childRemote = (remote == "/" ? "" : remote) + "/" + child.name
                    pending.append((childRemote, try safeChild(local, child.name), child))
                }
            } else if item.type == "file", let size = item.size, size >= 0 {
                files.append(WorkItem(remote: remote, local: local, item: item))
            } else { throw LoaderError("Неизвестный объект в папке: \(item.name)") }
        }
        files.sort { $0.remote < $1.remote }
        report(.inventory(files.count, files.reduce(0) { $0 + $1.size }))
        var completed: Int64 = 0, done = 0, skipped = 0
        var failures: [String] = [], missing: [WorkItem] = []
        report(.status("Проверяю уже скачанные файлы…"))
        for work in files {
            try Task.checkCancellation()
            guard exists(work.local) else { missing.append(work); continue }
            report(.file(displayName(work, target: target), work.size, true))
            do {
                if try verify(work.local, work.item, report: { report(.progress($0, 0)) }) {
                    done += 1; skipped += 1; completed += work.size
                } else {
                    failures.append("\(work.remote): существующий файл отличается от оригинала. Он сохранён без изменений.")
                }
            } catch {
                try Task.checkCancellation()
                failures.append("\(work.remote): \(error.localizedDescription)")
            }
            report(.totals(done, skipped, completed, failures.count))
        }
        report(.status("Уже готово: \(skipped). Скачиваю недостающие файлы…"))
        for work in missing {
            try Task.checkCancellation()
            report(.file(displayName(work, target: target), work.size, false))
            do {
                try await download(work, cache: cache)
                done += 1; completed += work.size
            } catch {
                try Task.checkCancellation()
                failures.append("\(work.remote): \(error.localizedDescription)")
            }
            report(.totals(done, skipped, completed, failures.count))
        }
        if (try? fm.contentsOfDirectory(atPath: cache.path).isEmpty) == true {
            try? fm.removeItem(at: cache)
        }
        // Error details are displayed in memory only, never written as a report.
        report(.finished(failures))
    }
    private func displayName(_ work: WorkItem, target: URL) -> String {
        let prefix = target.path + "/"
        return work.local.path.hasPrefix(prefix) ? String(work.local.path.dropFirst(prefix.count)) : work.item.name
    }
    private func download(_ work: WorkItem, cache: URL) async throws {
        let partial = try safeChild(cache, partialFileName(size: work.size, sha256: work.item.sha256, md5: work.item.md5))
        var last: Error = LoaderError("Не удалось скачать файл")
        for attempt in 1...20 {
            try Task.checkCancellation()
            do {
                if exists(partial) && (fileSize(partial) > work.size || (work.item.sha256 == nil && work.item.md5 == nil)) {
                    try fm.removeItem(at: partial)
                }
                let offset = fileSize(partial)
                report(.progress(offset, 0))
                report(.status("Получаю ссылку · попытка \(attempt) из 20"))
                if offset != work.size || !exists(partial) {
                    let url = try await api.downloadURL(work.remote)
                    report(.status("Скачиваю · попытка \(attempt) из 20"))
                    let transfer = FileTransfer(partial: partial, offset: offset, expected: work.size, report: report)
                    try await transfer.start(url)
                }
                guard fileSize(partial) == work.size else { throw LoaderError("Файл скачан не полностью") }
                report(.status("Проверяю скачанный файл…"))
                if work.item.sha256 != nil || work.item.md5 != nil {
                    guard try verify(partial, work.item) else {
                        try fm.removeItem(at: partial)
                        throw LoaderError("Контрольная сумма не совпала. Повторяю с начала.")
                    }
                }
                // Never overwrite a local file, including one created during the transfer.
                try fm.linkItem(at: partial, to: work.local)
                try fm.removeItem(at: partial)
                return
            } catch {
                try Task.checkCancellation()
                last = error
                // Local filesystem errors (permissions, disk full, conflicts) do not benefit from network retries.
                if let cocoa = error as NSError?, cocoa.domain == NSCocoaErrorDomain { throw error }
                if attempt < 20 {
                    let delay = min(1 << min(attempt, 5), 30)
                    for remaining in stride(from: delay, through: 1, by: -1) {
                        report(.status("\(error.localizedDescription) · повтор через \(remaining) с · \(attempt)/20"))
                        try await Task.sleep(nanoseconds: 1_000_000_000)
                    }
                }
            }
        }
        throw LoaderError("После 20 попыток: \(last.localizedDescription). Частичная загрузка сохранена.")
    }
}
