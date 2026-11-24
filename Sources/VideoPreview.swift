import Foundation

private let publicDiskHosts: Set<String> = [
    "disk.yandex.ru", "disk.yandex.com", "disk.yandex.com.tr", "disk.360.yandex.ru", "yadi.sk"
]

private func webURL(_ url: URL, hosts: Set<String>) -> Bool {
    url.scheme == "https" && url.user == nil && url.password == nil &&
        (url.port == nil || url.port == 443) && hosts.contains(url.host ?? "")
}

func isPublicDiskURL(_ url: URL) -> Bool {
    webURL(url, hosts: publicDiskHosts)
}

private let streamHosts: Set<String> = ["streaming.disk.yandex.net"]

private final class PreviewRedirects: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        let hosts = task.originalRequest?.url?.host == "streaming.disk.yandex.net" ? streamHosts : publicDiskHosts
        guard task.originalRequest?.httpMethod != "POST" else { completionHandler(nil); return }
        completionHandler(request.url.map { webURL($0, hosts: hosts) } == true ? request : nil)
    }
}

struct PreviewVariant: Sendable {
    let height: Int
    let url: URL
    let estimatedBytes: Int64?
}

struct PreviewFile: Identifiable, Sendable {
    let path: [String]
    let variants: [PreviewVariant]
    let originalBytes: Int64?
    let remotePath: String
    var id: String { path.joined(separator: "/") }
    var name: String { path.joined(separator: " / ") }
}

private struct PublicVideoStore: Decodable {
    struct Resource: Decodable {
        let name: String
        let type: String
        let videoStreams: Streams?
        let path: String?
        let hash: String?
    }
    struct Streams: Decodable {
        struct Video: Decodable {
            struct Size: Decodable { let height: Int? }
            let dimension: String
            let size: Size?
            let url: URL?
        }
        let duration: Double?
        let videos: [Video]
    }
    struct Environment: Decodable { let sk: String? }
    struct Reply: Decodable {
        let data: Streams?
        let wrongSk: Bool?
        let newSk: String?
    }
    let rootResourceId: String
    let resources: [String: Resource]
    let environment: Environment?
}

final class VideoPreviewClient: @unchecked Sendable {
    private let session: URLSession

    init() {
        let config = transientNetworkConfiguration()
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 120
        session = URLSession(configuration: config, delegate: PreviewRedirects(), delegateQueue: nil)
    }

    deinit { session.invalidateAndCancel() }

    func inspect(_ source: String, progress: @Sendable (Int, Int) async -> Void) async throws -> [PreviewFile] {
        guard let url = URL(string: source), isPublicDiskURL(url) else {
            throw LoaderError("Нужна публичная ссылка на файл или папку Яндекс Диска.")
        }
        let api = DiskAPI(link: source)
        let root = try await api.rootItem()
        var paths: [([String], URL, Int64?, String)] = []
        if root.type == "dir" {
            var folders: [[String]] = [[]]
            while let folder = folders.popLast() {
                try Task.checkCancellation()
                for item in try await api.children("/" + folder.joined(separator: "/")) {
                    let path = folder + [item.name]
                    if item.type == "dir" {
                        folders.append(path)
                    } else if item.mediaType == "video" {
                        paths.append((path, path.reduce(url) { $0.appendingPathComponent($1) },
                                      item.size, "/" + path.joined(separator: "/")))
                    }
                }
            }
        } else if root.type == "file", root.mediaType == "video" || root.mediaType == nil {
            paths = [([root.name], url, root.size, "/")]
        }
        return try await withThrowingTaskGroup(of: PreviewFile.self) { group in
            var pending = paths.makeIterator()
            func enqueue() {
                guard let (path, page, size, remotePath) = pending.next() else { return }
                group.addTask {
                    async let variants = try? self.variants(on: page, remotePath: remotePath)
                    async let original = try? api.downloadURL(remotePath)
                    let result = await PreviewFile(path: path, variants: variants ?? [],
                                                   originalBytes: original == nil ? nil : size,
                                                   remotePath: remotePath)
                    try Task.checkCancellation()
                    return result
                }
            }
            for _ in 0..<min(paths.count, 4) { enqueue() }
            var files: [PreviewFile] = []
            while let result = try await group.next() {
                files.append(result)
                await progress(files.count, paths.count)
                enqueue()
            }
            return files.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        }
    }

    private func body(_ url: URL) async throws -> String {
        let (data, response) = try await session.data(from: url)
        return try text(data, response)
    }

    private func text(_ data: Data, _ response: URLResponse) throws -> String {
        guard let http = response as? HTTPURLResponse, http.statusCode == 200,
              data.count <= 16 * 1024 * 1024, let string = String(data: data, encoding: .utf8) else {
            throw LoaderError("Яндекс Диск не вернул данные превью.")
        }
        return string
    }

    private func streams(on page: URL, remotePath: String) async throws -> PublicVideoStore.Streams? {
        // The public viewer may fetch missing metadata with an anonymous GET/POST session.
        let config = URLSessionConfiguration.ephemeral
        config.urlCache = nil
        config.urlCredentialStorage = nil
        config.timeoutIntervalForRequest = 20
        config.timeoutIntervalForResource = 45
        let browser = URLSession(configuration: config, delegate: PreviewRedirects(), delegateQueue: nil)
        defer { browser.invalidateAndCancel() }
        let (data, response) = try await browser.data(from: page)
        let html = try text(data, response)
        let pattern = #"<script[^>]+id="store-prefetch"[^>]*>\s*(\{.*?\})\s*</script>"#
        let regex = try NSRegularExpression(pattern: pattern, options: .dotMatchesLineSeparators)
        guard let match = regex.firstMatch(in: html, range: NSRange(html.startIndex..., in: html)),
              let range = Range(match.range(at: 1), in: html) else { return nil }
        let store = try JSONDecoder().decode(PublicVideoStore.self, from: Data(html[range].utf8))
        guard let root = store.resources[store.rootResourceId] else { return nil }
        let file: PublicVideoStore.Resource?
        if root.type == "file", remotePath == "/" {
            file = root
        } else if let hash = root.hash ?? root.path {
            file = store.resources.values.first { $0.path == hash + ":" + remotePath }
        } else {
            file = nil
        }
        guard let file else { return nil }
        if let streams = file.videoStreams, !streams.videos.isEmpty { return streams }
        guard let hash = file.path ?? file.hash, var sk = store.environment?.sk,
              let finalPage = response.url, isPublicDiskURL(finalPage),
              let endpoint = URL(string: "/public/api/get-video-streams", relativeTo: finalPage)?.absoluteURL else {
            return nil
        }
        let unescaped = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.!~*'()")
        for _ in 0..<2 {
            let json = try JSONEncoder().encode(["hash": hash, "sk": sk])
            var request = URLRequest(url: endpoint)
            request.httpMethod = "POST"
            request.setValue("text/plain", forHTTPHeaderField: "Content-Type")
            request.setValue("XMLHttpRequest", forHTTPHeaderField: "X-Requested-With")
            request.setValue(finalPage.absoluteString, forHTTPHeaderField: "X-Retpath-Y")
            request.setValue(finalPage.absoluteString, forHTTPHeaderField: "Referer")
            request.setValue("https://" + (finalPage.host ?? ""), forHTTPHeaderField: "Origin")
            request.httpBody = String(decoding: json, as: UTF8.self)
                .addingPercentEncoding(withAllowedCharacters: unescaped)?.data(using: .utf8)
            let (result, _) = try await browser.data(for: request)
            let reply = try JSONDecoder().decode(PublicVideoStore.Reply.self, from: result)
            if let streams = reply.data { return streams }
            guard reply.wrongSk == true, let refreshed = reply.newSk else { return nil }
            sk = refreshed
        }
        return nil
    }

    private func variants(on page: URL, remotePath: String) async throws -> [PreviewVariant] {
        guard let streams = try await streams(on: page, remotePath: remotePath) else { return [] }
        var rates: [Int: Double] = [:]
        if let master = streams.videos.first(where: { $0.dimension == "adaptive" })?.url,
           webURL(master, hosts: streamHosts), let playlist = try? await body(master) {
            for line in playlist.split(whereSeparator: \.isNewline) where line.hasPrefix("#EXT-X-STREAM-INF:") {
                let fields = line.dropFirst("#EXT-X-STREAM-INF:".count).split(separator: ",")
                    .map { $0.trimmingCharacters(in: .whitespaces) }
                let resolution = fields.first { $0.hasPrefix("RESOLUTION=") }
                let bandwidth = fields.first { $0.hasPrefix("AVERAGE-BANDWIDTH=") } ??
                    fields.first { $0.hasPrefix("BANDWIDTH=") }
                if let height = resolution?.split(separator: "x").last.flatMap({ Int($0) }),
                   let rate = bandwidth?.split(separator: "=").last.flatMap({ Double($0) }), rate > 0 {
                    rates[height] = rate
                }
            }
        }
        var variants: [Int: PreviewVariant] = [:]
        for video in streams.videos {
            guard let height = video.size?.height, height > 0, let url = video.url,
                  webURL(url, hosts: streamHosts) else { continue }
            let estimate = rates[height].map { $0 * (streams.duration ?? 0) / 8_000 }
            let size = estimate.flatMap { $0 > 0 && $0 < Double(Int64.max) ? Int64($0) : nil }
            variants[height] = PreviewVariant(height: height, url: url, estimatedBytes: size)
        }
        return await withTaskGroup(of: PreviewVariant?.self) { group in
            for variant in variants.values {
                group.addTask {
                    guard (try? await self.segments(for: variant)) != nil else { return nil }
                    return variant
                }
            }
            var available: [PreviewVariant] = []
            for await variant in group {
                if let variant { available.append(variant) }
            }
            return available.sorted { $0.height < $1.height }
        }
    }

    static var ffmpeg: URL? {
        ["/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/opt/local/bin/ffmpeg"]
            .first(where: FileManager.default.isExecutableFile(atPath:)).map { URL(fileURLWithPath: $0) }
    }

    private func segments(for variant: PreviewVariant) async throws -> [URL] {
        let playlist = try await body(variant.url)
        let lines = playlist.split(whereSeparator: \.isNewline)
        let segmentLines = lines.filter { !$0.hasPrefix("#") }
        guard lines.first == "#EXTM3U", lines.contains("#EXT-X-ENDLIST"),
              !lines.contains(where: { $0.hasPrefix("#EXT-X-KEY:") || $0.hasPrefix("#EXT-X-MAP:") ||
                  $0.hasPrefix("#EXT-X-BYTERANGE:") }),
              !segmentLines.isEmpty else { throw LoaderError("Яндекс Диск вернул неподдерживаемый плейлист.") }
        return try segmentLines.map { line in
            guard let url = URL(string: String(line), relativeTo: variant.url)?.absoluteURL,
                  webURL(url, hosts: streamHosts), url.pathExtension == "ts" else {
                throw LoaderError("Яндекс Диск вернул неподдерживаемый сегмент видео.")
            }
            return url
        }
    }

    func save(_ file: PreviewFile, variant: PreviewVariant, to destination: URL,
              audioOnly: Bool, encoder: URL?, progress: @Sendable (Int, Int) async -> Void) async throws {
        if audioOnly && encoder == nil { throw LoaderError("Для сохранения звука нужен FFmpeg.") }
        let segments = try await segments(for: variant)
        let fm = FileManager.default
        let parent = try file.path.dropLast().reduce(destination, safeChild)
        try fm.createDirectory(at: parent, withIntermediateDirectories: true)
        let staging = try safeChild(parent, ".plantain-preview-" + UUID().uuidString)
        try fm.createDirectory(at: staging, withIntermediateDirectories: false)
        defer { try? fm.removeItem(at: staging) }
        let transport = staging.appendingPathComponent("preview.ts")
        guard fm.createFile(atPath: transport.path, contents: nil) else {
            throw LoaderError("Не удалось создать временный файл превью.")
        }
        let output = try FileHandle(forWritingTo: transport)
        do {
            defer { try? output.close() }
            for (index, segment) in segments.enumerated() {
                try Task.checkCancellation()
                let (part, response) = try await session.download(from: segment)
                defer { try? fm.removeItem(at: part) }
                guard let http = response as? HTTPURLResponse, http.statusCode == 200, fileSize(part) > 0,
                      http.expectedContentLength < 0 || fileSize(part) == http.expectedContentLength else {
                    throw LoaderError("Не удалось загрузить сегмент \(index + 1).")
                }
                let input = try FileHandle(forReadingFrom: part)
                defer { try? input.close() }
                while let chunk = try input.read(upToCount: 1024 * 1024), !chunk.isEmpty {
                    try Task.checkCancellation()
                    try output.write(contentsOf: chunk)
                }
                await progress(index + 1, segments.count)
            }
            try output.synchronize()
        }

        let ext = audioOnly ? "m4a" : encoder == nil ? "ts" : "mp4"
        let result = encoder == nil ? transport : staging.appendingPathComponent("ready." + ext)
        if let encoder {
            let process = Process()
            process.executableURL = encoder
            process.arguments = ["-nostdin", "-loglevel", "error", "-protocol_whitelist", "file",
                                 "-f", "mpegts", "-i", transport.path] +
                (audioOnly ? ["-map", "0:a:0"] : ["-map", "0:v:0", "-map", "0:a:0?"]) +
                ["-c", "copy", "-movflags", "+faststart", result.path]
            process.standardOutput = FileHandle.nullDevice
            process.standardError = FileHandle.nullDevice
            try Task.checkCancellation()
            try process.run()
            defer {
                if process.isRunning {
                    process.terminate()
                    process.waitUntilExit()
                }
            }
            while process.isRunning { try await Task.sleep(nanoseconds: 100_000_000) }
            guard process.terminationStatus == 0, fileSize(result) > 0 else {
                throw LoaderError(audioOnly ? "Не удалось извлечь звуковую дорожку." : "Не удалось сохранить MP4.")
            }
        }

        try Task.checkCancellation()
        let stem = (file.path.last as NSString?)?.deletingPathExtension ?? "Видео"
        let label = audioOnly ? "звук, \(variant.height)p" : "\(variant.height)p"
        var copy = 1
        while true {
            let suffix = copy == 1 ? "" : " \(copy)"
            let target = try safeChild(parent, "\(stem) (\(label))\(suffix).\(ext)")
            do {
                try fm.moveItem(at: result, to: target)
                return
            } catch let error as NSError where error.domain == NSCocoaErrorDomain &&
                error.code == NSFileWriteFileExistsError {
                copy += 1
            }
        }
    }
}
