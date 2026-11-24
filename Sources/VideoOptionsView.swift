import SwiftUI
import AppKit

enum VideoChoice: Hashable {
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
        case .audio:
            return file.variants.min { left, right in
                (left.estimatedBytes ?? Int64.max, left.height) < (right.estimatedBytes ?? Int64.max, right.height)
            }
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
    let download: ([PreviewFile], VideoChoice, URL?) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var files: [PreviewFile] = []
    @State private var choice: VideoChoice = .original
    @State private var status = "Проверяю ссылку и доступные потоки…"
    @State private var inspecting = true
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
                ScrollView {
                    VStack(alignment: .leading, spacing: 8) {
                        ForEach(files) { file in
                            HStack {
                                Text(file.name).lineLimit(1).truncationMode(.middle).help(file.name)
                                Spacer()
                                if choice.includes(file) {
                                    if choice == .audio, let variant = choice.variant(for: file) {
                                        Text("\(variant.height)p → MP3").foregroundStyle(.secondary)
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
                    Text("Размеры показывают трафик потока. Сохранится MP3.")
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
            Text(status).font(.callout).textSelection(.enabled)
            HStack {
                Spacer()
                Button("Закрыть") { dismiss() }.keyboardShortcut(.cancelAction)
                if !choices.isEmpty {
                    Button(choice == .original ? "Скачать исходники" : "Скачать выбранное") { download(selected, choice, encoder) }
                        .buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction)
                        .disabled(inspecting || selected.isEmpty)
                }
            }
        }
        .padding(22)
        .frame(width: 530)
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
    }

    private func updateInspection(_ done: Int, _ total: Int) {
        status = "Проверено видео: \(done) из \(total)"
    }

}
