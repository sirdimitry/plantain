import SwiftUI
import AppKit

private enum VideoChoice: Hashable {
    case original
    case video(Int)
    case audio

    var title: String {
        switch self {
        case .original: return "Исходное качество"
        case .video(let height): return "\(height)p"
        case .audio: return "Только звук"
        }
    }

    func variant(for file: PreviewFile) -> PreviewVariant? {
        switch self {
        case .original: return nil
        case .video(let height): return file.variants.first { $0.height == height }
        case .audio: return file.variants.first
        }
    }

    func includes(_ file: PreviewFile) -> Bool {
        self == .original ? file.originalBytes != nil : variant(for: file) != nil
    }

    func transferSize(for file: PreviewFile) -> Int64? {
        self == .original ? file.originalBytes : variant(for: file)?.estimatedBytes
    }
}

@MainActor struct VideoOptionsView: View {
    let source: String
    let destination: URL
    let downloadOriginal: (Set<String>) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var files: [PreviewFile] = []
    @State private var choice: VideoChoice = .original
    @State private var status = "Проверяю ссылку и доступные потоки…"
    @State private var inspecting = true
    @State private var transfer: Task<Void, Never>?
    @State private var sent = 0
    @State private var segmentProgress = 0.0
    @State private var hasResult = false
    @State private var encoder = VideoPreviewClient.ffmpeg

    private var choices: [VideoChoice] {
        let heights = Set(files.flatMap { $0.variants.map(\.height) }).sorted()
        var result = files.contains { $0.originalBytes != nil } ? [VideoChoice.original] : []
        result += heights.map(VideoChoice.video)
        if !heights.isEmpty, encoder != nil { result.append(.audio) }
        return result
    }

    private var selected: [PreviewFile] { files.filter(choice.includes) }

    private func choiceLabel(_ option: VideoChoice) -> String {
        let included = files.filter(option.includes)
        let sizes = included.compactMap(option.transferSize)
        let total = sizes.reduce(0.0) { $0 + Double($1) }
        let amount = sizes.count == included.count && total < Double(Int64.max)
            ? " · \(option == .original ? "" : "≈ ")\(bytes(Int64(total)))" : ""
        return "\(option.title) · \(included.count) из \(files.count)\(amount)"
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Качество видео").font(.title2.weight(.semibold))
            if !choices.isEmpty {
                Picker("Вариант загрузки", selection: $choice) {
                    ForEach(choices, id: \.self) { option in
                        Text(choiceLabel(option)).tag(option)
                    }
                }
                .disabled(transfer != nil)
                ScrollView {
                    VStack(alignment: .leading, spacing: 8) {
                        ForEach(files) { file in
                            HStack {
                                Text(file.name).lineLimit(1).truncationMode(.middle).help(file.name)
                                Spacer()
                                if choice.includes(file) {
                                    if choice == .audio, let variant = choice.variant(for: file) {
                                        Text("\(variant.height)p → M4A").foregroundStyle(.secondary)
                                    } else if let size = choice.transferSize(for: file) {
                                        Text((choice == .original ? "" : "≈ ") + bytes(size))
                                            .foregroundStyle(.secondary)
                                    }
                                } else {
                                    Text("Недоступно").foregroundStyle(.secondary)
                                }
                            }.font(.callout)
                        }
                    }.frame(maxWidth: .infinity, alignment: .leading)
                }.frame(maxHeight: 180)
                if choice == .audio {
                    Text("Размеры показывают трафик потока. Сохранится звуковая дорожка M4A.")
                        .font(.caption).foregroundStyle(.secondary)
                } else if choice != .original {
                    Text(encoder == nil ? "Превью сохранится в формате MPEG-TS." : "Превью сохранится в MP4.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                if encoder == nil && choices.contains(where: { if case .video = $0 { return true }; return false }) {
                    Text("Для MP4 и извлечения звука установите FFmpeg и откройте проверку заново.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            if inspecting { ProgressView().controlSize(.small) }
            if transfer != nil && !selected.isEmpty {
                ProgressView(value: Double(sent) + segmentProgress, total: Double(selected.count))
            }
            Text(status).font(.callout).textSelection(.enabled)
            HStack {
                if hasResult {
                    Button("Открыть папку") { NSWorkspace.shared.open(destination) }
                }
                Spacer()
                Button(transfer == nil ? "Закрыть" : "Остановить") {
                    if let transfer { transfer.cancel() } else { dismiss() }
                }.keyboardShortcut(.cancelAction)
                if !choices.isEmpty {
                    Button(choice == .original ? "Скачать исходники" : "Скачать выбранное") { startDownload() }
                        .buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction)
                        .disabled(inspecting || transfer != nil || selected.isEmpty)
                }
            }
        }
        .padding(22)
        .frame(width: 530)
        .interactiveDismissDisabled(transfer != nil)
        .task {
            do {
                files = try await VideoPreviewClient().inspect(source) { done, total in
                    await updateInspection(done, total)
                }
                if let first = choices.first {
                    choice = first
                    status = "Выберите вариант загрузки."
                } else {
                    status = files.isEmpty ? "По ссылке не найдено видео." : "Яндекс Диск не выдал доступные варианты загрузки."
                }
            } catch {
                status = error.localizedDescription
            }
            inspecting = false
        }
        .onDisappear { transfer?.cancel() }
    }

    private func updateInspection(_ done: Int, _ total: Int) {
        status = "Проверено видео: \(done) из \(total)"
    }

    private func updateTransfer(_ done: Int, _ total: Int) {
        segmentProgress = Double(done) / Double(total)
    }

    private func startDownload() {
        let chosen = choice
        if chosen == .original {
            downloadOriginal(Set(selected.map(\.remotePath)))
            return
        }
        let batch = selected
        sent = 0
        segmentProgress = 0
        transfer = Task {
            let activity = ProcessInfo.processInfo.beginActivity(
                options: [.userInitiated, .idleSystemSleepDisabled], reason: "Скачивание видеопревью")
            defer {
                ProcessInfo.processInfo.endActivity(activity)
                transfer = nil
            }
            let client = VideoPreviewClient()
            var failures: [String] = []
            var saved = 0
            for file in batch {
                guard !Task.isCancelled, let variant = chosen.variant(for: file) else { break }
                status = "Загружаю \(file.name) · \(variant.height)p…"
                do {
                    try await client.save(file, variant: variant, to: destination,
                                          audioOnly: chosen == .audio, encoder: encoder) { done, total in
                        await updateTransfer(done, total)
                    }
                    saved += 1
                    hasResult = true
                } catch {
                    if Task.isCancelled { break }
                    failures.append("\(file.name) — \(error.localizedDescription)")
                }
                sent += 1
                segmentProgress = 0
            }
            if Task.isCancelled {
                status = "Остановлено. Сохранено: \(saved) из \(batch.count)."
            } else if failures.isEmpty {
                status = "Сохранено: \(saved) из \(batch.count)."
            } else {
                status = "Сохранено: \(saved) из \(batch.count).\n" + failures.joined(separator: "\n")
            }
        }
    }
}
