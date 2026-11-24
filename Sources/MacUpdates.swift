import Foundation
import SwiftUI
import AppKit

struct MacUpdate: Identifiable, Equatable {
    let version: String
    var id: String { version }
    var releasePage: URL {
        URL(string: "https://github.com/sirdimitry/plantain/releases/tag/v" + version)!
    }
}

enum MacUpdates {
    static var installedVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
    }

    private struct Release: Decodable {
        struct Asset: Decodable {
            let name: String
            let state: String
            let size: Int64
        }
        let tag_name: String
        let draft: Bool
        let prerelease: Bool
        let assets: [Asset]
    }

    private static func parts(_ version: String) -> [Int]? {
        let fields = version.split(separator: ".", omittingEmptySubsequences: false)
        guard fields.count == 3,
              fields.allSatisfy({ !$0.isEmpty && $0.utf8.allSatisfy { (48...57).contains($0) } &&
                  ($0.count == 1 || $0.first != "0") }) else { return nil }
        let result = fields.compactMap { Int($0) }
        return result.count == 3 ? result : nil
    }

    static func selectedRelease(in data: Data, installed: String) throws -> MacUpdate? {
        guard var best = parts(installed) else { return nil }
        let releases = try JSONDecoder().decode([Release].self, from: data)
        var update: MacUpdate?
        for release in releases where !release.draft && !release.prerelease && release.tag_name.hasPrefix("v") {
            let version = String(release.tag_name.dropFirst())
            guard let components = parts(version), best.lexicographicallyPrecedes(components) else { continue }
            let dmg = "Plantain-\(version)-universal.dmg"
            let uploaded = Set(release.assets.filter { $0.size > 0 && $0.state == "uploaded" }.map(\.name))
            guard uploaded.contains(dmg), uploaded.contains(dmg + ".sha256") else { continue }
            best = components
            update = MacUpdate(version: version)
        }
        return update
    }

    static func check() async -> MacUpdate? {
        let config = transientNetworkConfiguration()
        config.timeoutIntervalForRequest = 6
        config.timeoutIntervalForResource = 9
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        var request = URLRequest(url: URL(string: "https://api.github.com/repos/sirdimitry/plantain/releases?per_page=100")!)
        request.setValue("Plantain-macOS", forHTTPHeaderField: "User-Agent")
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        do {
            let (data, response) = try await session.data(for: request)
            guard (response as? HTTPURLResponse)?.statusCode == 200, response.url?.host == "api.github.com" else { return nil }
            return try selectedRelease(in: data, installed: installedVersion)
        } catch { return nil }
    }
}

struct MacUpdateView: View {
    let update: MacUpdate
    @Environment(\.dismiss) private var dismiss
    @State private var openingFailed = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 14) {
                Image(systemName: "arrow.down.circle.fill")
                    .font(.system(size: 34)).accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 5) {
                    Text("Вышла новая версия").font(.title3.weight(.semibold))
                    Text("Подорожник \(update.version)").font(.title2.weight(.bold))
                }
            }
            .foregroundStyle(.white)
            .padding(22)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color(red: 0.74, green: 0.10, blue: 0.21))
            VStack(alignment: .leading, spacing: 12) {
                Text("Установлена версия \(MacUpdates.installedVersion)")
                    .font(.callout).foregroundStyle(.secondary)
                Text(openingFailed ? "Не удалось открыть GitHub." : "Откроется страница релиза для macOS на GitHub.")
                    .font(.callout)
                HStack {
                    Spacer()
                    Button("Отмена") { dismiss() }.keyboardShortcut(.cancelAction)
                    Button("Скачать") {
                        if NSWorkspace.shared.open(update.releasePage) { dismiss() }
                        else { openingFailed = true }
                    }
                    .buttonStyle(.borderedProminent).tint(Color(red: 0.74, green: 0.10, blue: 0.21))
                    .keyboardShortcut(.defaultAction)
                }
            }.padding(22)
        }.frame(width: 425)
    }
}
