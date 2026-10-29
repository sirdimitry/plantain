import SwiftUI
import AppKit

let accent = Color(red: 0.05, green: 0.40, blue: 0.90)
func bytes(_ value: Int64) -> String {
    let units = ["Б", "КиБ", "МиБ", "ГиБ", "ТиБ"]
    var number = Double(max(0, value)), index = 0
    while number >= 1024 && index < units.count - 1 { number /= 1024; index += 1 }
    return String(format: index == 0 ? "%.0f %@" : "%.1f %@", number, units[index])
}
func timeText(_ seconds: Double) -> String {
    guard seconds.isFinite, seconds >= 0 else { return "—" }
    let value = Int(min(seconds, 999 * 3600))
    if value >= 3600 { return "\(value/3600) ч \((value%3600)/60) мин" }
    if value >= 60 { return "\(value/60) мин \(value%60) с" }
    return "\(value) с"
}

@MainActor final class LoaderModel: ObservableObject {
    @Published var link = ""
    @Published var running = false
    @Published var stopping = false
    @Published var status = "Вставьте ссылку на папку или файл"
    @Published var error: String?
    @Published var finished = false
    @Published var count = 0
    @Published var total: Int64 = 0
    @Published var completed: Int64 = 0
    @Published var done = 0
    @Published var skipped = 0
    @Published var errors = 0
    @Published var file = ""
    @Published var fileSize: Int64 = 0
    @Published var current: Int64 = 0
    @Published var verifying = false
    @Published var folder: URL?
    @Published var destination = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0]
    @Published var networkBytes: Int64 = 0
    @Published var elapsed: Double = 0
    private var started: Date?
    private var timer: Timer?
    private var task: Task<Void, Never>?
    private var assertion: NSObjectProtocol?
    var speed: Double { elapsed > 0 ? Double(networkBytes) / elapsed : 0 }
    var totalValue: Int64 { min(total, completed + (verifying ? 0 : current)) }
    var fraction: Double { total > 0 ? Double(totalValue) / Double(total) : (finished && errors == 0 ? 1 : 0) }
    var fileFraction: Double { fileSize > 0 ? min(1, Double(current) / Double(fileSize)) : 0 }
    var eta: String { speed > 0 ? timeText(Double(max(0, total-totalValue)) / speed) : "—" }
    var canStart: Bool { !running && !link.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }

    func start() {
        guard !running else { return }
        let source = link.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let url = URL(string: source), url.scheme == "https", url.user == nil, url.password == nil,
              url.port == nil || url.port == 443,
              ["disk.yandex.ru", "disk.yandex.com", "disk.yandex.com.tr", "yadi.sk"].contains(url.host ?? "") else {
            error = "Нужна публичная ссылка Яндекс Диска, начинающаяся с https://disk.yandex.ru/"
            return
        }
        link = source
        running = true; stopping = false; finished = false; error = nil
        count = 0; total = 0; completed = 0; done = 0; skipped = 0; errors = 0
        file = ""; fileSize = 0; current = 0; verifying = true; networkBytes = 0; elapsed = 0
        status = "Подключаюсь к Яндекс Диску…"
        started = nil
        assertion = ProcessInfo.processInfo.beginActivity(options: [.userInitiated, .idleSystemSleepDisabled], reason: "Скачивание файлов с Яндекс Диска")
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            Task { @MainActor in
                if let start = self?.started { self?.elapsed = Date().timeIntervalSince(start) }
            }
        }
        let destination = self.destination
        folder = nil
        let reporter: Reporter = { [weak self] event in
            DispatchQueue.main.async { self?.receive(event) }
        }
        task = Task.detached(priority: .userInitiated) { [weak self] in
            do {
                let engine = DownloadEngine(api: DiskAPI(link: source), destination: destination, report: reporter)
                try await engine.run()
                await self?.end(cancelled: false, failure: nil)
            } catch {
                let cancelled = Task.isCancelled || error is CancellationError || (error as? URLError)?.code == .cancelled
                await self?.end(cancelled: cancelled, failure: cancelled ? nil : error.localizedDescription)
            }
        }
    }
    private func receive(_ event: LoaderEvent) {
        switch event {
        case .status(let value): status = value
        case .destination(let value): folder = value
        case .inventory(let files, let size): count = files; total = size
        case .file(let name, let size, let checking):
            file = name; fileSize = size; current = 0; verifying = checking
        case .progress(let value, let received):
            current = value
            if received > 0 {
                if started == nil { started = Date() }
                networkBytes += received
            }
        case .totals(let files, let old, let size, let failures):
            done = files; skipped = old; completed = size; errors = failures
            if !verifying { current = 0; file = ""; fileSize = 0 }
        case .finished(let failures):
            current = 0; file = ""; fileSize = 0; verifying = false
            finished = true
            status = failures.isEmpty ? (count == 0 ? "Папка пуста — структура сохранена" : "Все файлы скачаны и проверены") : "Осталось скачать: \(count-done). Можно повторить загрузку."
            if !failures.isEmpty {
                error = "\(failures.count) ошибок. " + failures.prefix(2).joined(separator: "\n")
            }
        }
    }
    private func end(cancelled: Bool, failure: String?) {
        running = false; stopping = false; task = nil
        timer?.invalidate(); timer = nil
        if let assertion { ProcessInfo.processInfo.endActivity(assertion); self.assertion = nil }
        if cancelled { status = "Остановлено. Готовые файлы и частичная загрузка сохранены." }
        if let failure { error = failure; status = "Не удалось завершить загрузку" }
    }
    func stop() {
        stopping = true
        status = "Останавливаю и сохраняю загрузку…"
        task?.cancel()
    }
    var defaultDestination: URL { FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0] }
    var isDefaultDestination: Bool { destination.standardizedFileURL == defaultDestination.standardizedFileURL }
    var destinationLabel: String { isDefaultDestination ? "Загрузки" : destination.path }
    func setDestination(_ url: URL) {
        guard !running else { return }
        destination = url
        folder = nil
        finished = false; done = 0; skipped = 0; errors = 0
        count = 0; total = 0; completed = 0; current = 0; fileSize = 0; file = ""
        error = nil; status = "Папка сохранения выбрана"
        elapsed = 0; networkBytes = 0
    }
    func chooseDestination() {
        guard !running else { return }
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = true
        panel.directoryURL = destination
        panel.prompt = "Выбрать"
        panel.message = "В выбранном месте будет создана папка с исходным названием и окончанием copy."
        let completion: (NSApplication.ModalResponse) -> Void = { [weak self] response in
            if response == .OK, let url = panel.url { self?.setDestination(url) }
            clearPrivatePreferences()
        }
        if let window = NSApp.keyWindow { panel.beginSheetModal(for: window, completionHandler: completion) }
        else { panel.begin(completionHandler: completion) }
    }
    func openFolder() { NSWorkspace.shared.open(folder ?? destination) }

}

struct Meter: View {
    let value: Double
    var height: CGFloat = 10
    @Environment(\.colorScheme) private var scheme
    var body: some View {
        GeometryReader { geometry in
            ZStack(alignment: .leading) {
                Capsule().fill(scheme == .dark ? Color.white.opacity(0.19) : Color.black.opacity(0.14))
                    .overlay(Capsule().strokeBorder(Color.primary.opacity(0.10), lineWidth: 0.5))
                Capsule().fill(scheme == .dark ? Color(red: 0.10, green: 0.65, blue: 1) : accent).frame(width: max(0, geometry.size.width * min(1, max(0, value))))
            }
        }
        .frame(height: height)
        .accessibilityElement()
        .accessibilityValue(Text("\(Int(value * 100)) процентов"))
    }
}
struct Detail: View {
    let title: String
    let value: String
    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title).font(.system(size: 11, weight: .medium)).foregroundStyle(.secondary)
            Text(value).font(.system(size: 14, weight: .medium, design: .rounded)).monospacedDigit()
        }.frame(maxWidth: .infinity, alignment: .leading)
    }
}
struct ContentView: View {
    @StateObject private var model = LoaderModel()
    @FocusState private var linkFocused: Bool
    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack(spacing: 12) {
                if let url = Bundle.main.url(forResource: "AppArtwork", withExtension: "png"),
                   let icon = NSImage(contentsOf: url) {
                    Image(nsImage: icon).renderingMode(.original).resizable().interpolation(.high)
                        .frame(width: 54, height: 54).accessibilityHidden(true)
                }
                VStack(alignment: .leading, spacing: 3) {
                    Text("Подорожник").font(.system(size: 22, weight: .semibold))
                    Text("скачает, когда Диск не может").font(.system(size: 12)).foregroundStyle(.secondary)
                }
                Spacer()
            }
            VStack(alignment: .leading, spacing: 8) {
                Text("Ссылка Яндекс Диска").font(.system(size: 12, weight: .medium))
                TextField("https://disk.yandex.ru/d/…", text: $model.link)
                    .textFieldStyle(.plain)
                    .font(.system(size: 14))
                    .padding(.horizontal, 12).padding(.vertical, 12)
                    .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 9))
                    .overlay(RoundedRectangle(cornerRadius: 9).strokeBorder(Color.primary.opacity(0.15)))
                    .focused($linkFocused).disabled(model.running)
                    .onSubmit { model.start() }
                    .accessibilityLabel("Публичная ссылка Яндекс Диска")
            }
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text("Куда сохранить").font(.system(size: 12, weight: .medium))
                    Spacer()
                    if !model.isDefaultDestination {
                        Button("В «Загрузки»") { model.setDestination(model.defaultDestination) }
                            .buttonStyle(.borderless).font(.system(size: 11)).disabled(model.running)
                    }
                }
                HStack(spacing: 10) {
                    Image(systemName: "folder").foregroundStyle(.secondary)
                    Text(model.destinationLabel).font(.system(size: 12)).lineLimit(1)
                        .truncationMode(.middle).help(model.destination.path)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .accessibilityLabel("Папка сохранения: " + model.destination.path)
                    Button("Выбрать…") { model.chooseDestination() }
                        .controlSize(.regular).disabled(model.running)
                        .accessibilityLabel("Выбрать папку сохранения")
                }
                Text("Внутри будет создана папка «Название copy»")
                    .font(.system(size: 11)).foregroundStyle(.secondary)
            }
            VStack(alignment: .leading, spacing: 18) {
                HStack(alignment: .firstTextBaseline) {
                    Text("Вся загрузка").font(.system(size: 13, weight: .semibold))
                    Spacer()
                    Text(String(format: "%.1f%%", model.fraction * 100))
                        .font(.system(size: 22, weight: .medium, design: .rounded)).monospacedDigit()
                }
                VStack(spacing: 9) {
                    Meter(value: model.fraction, height: 12).accessibilityLabel("Прогресс всей загрузки")
                    HStack {
                        Text("\(bytes(model.totalValue)) из \(bytes(model.total))")
                        Spacer()
                        Text("Осталось \(bytes(max(0, model.total-model.totalValue)))")
                    }.font(.system(size: 11)).foregroundStyle(.secondary).monospacedDigit()
                }
                HStack(spacing: 20) {
                    Detail(title: "ГОТОВО ФАЙЛОВ", value: "\(model.done) из \(model.count)")
                    Detail(title: "ОСТАЛОСЬ ФАЙЛОВ", value: "\(max(0, model.count-model.done))")
                    Detail(title: "УЖЕ ЕСТЬ", value: "\(model.skipped)")
                }
                Divider().opacity(0.6)
                VStack(alignment: .leading, spacing: 9) {
                    HStack {
                        Text(model.verifying ? "Проверка файла" : "Текущий файл")
                            .font(.system(size: 11, weight: .medium)).foregroundStyle(.secondary)
                        Spacer()
                        if model.fileSize > 0 {
                            Text(String(format: "%.0f%%", model.fileFraction * 100)).font(.system(size: 11)).monospacedDigit()
                        }
                    }
                    Text(model.file.isEmpty ? (model.finished ? "Готово" : "Ожидание") : model.file)
                        .font(.system(size: 12, weight: .medium)).lineLimit(2).truncationMode(.middle)
                        .frame(height: 32, alignment: .topLeading).help(model.file)
                    Meter(value: model.fileFraction, height: 9).accessibilityLabel("Прогресс текущего файла")
                    HStack {
                        Text("\(bytes(model.current)) из \(bytes(model.fileSize))")
                        Spacer()
                        Text("Осталось \(bytes(max(0, model.fileSize-model.current)))")
                    }.font(.system(size: 11)).foregroundStyle(.secondary).monospacedDigit()
                }
            }
            .padding(20)
            .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 14))
            .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(Color.primary.opacity(0.06)))
            HStack(spacing: 20) {
                Detail(title: "СКОРОСТЬ", value: model.speed > 0 ? bytes(Int64(model.speed)) + "/с" : "—")
                Detail(title: "ОСТАЛОСЬ ПРИМЕРНО", value: model.eta)
                Detail(title: "ОШИБОК", value: "\(model.errors)")
            }.padding(.horizontal, 2)
            VStack(alignment: .leading, spacing: 8) {
                HStack(alignment: .top, spacing: 8) {
                    if model.running {
                        ProgressView().controlSize(.small).scaleEffect(0.8).frame(width: 14, height: 14)
                    } else {
                        Image(systemName: model.finished && model.errors == 0 ? "checkmark.circle.fill" : "info.circle")
                            .foregroundStyle(model.finished && model.errors == 0 ? accent : .secondary)
                    }
                    Text(model.status).font(.system(size: 12)).lineLimit(3)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                if let error = model.error {
                    Text(error).font(.system(size: 11)).foregroundStyle(Color(nsColor: .systemRed))
                        .lineLimit(4).textSelection(.enabled)
                }
            }.frame(minHeight: 36, alignment: .top)
            HStack {
                Button { model.openFolder() } label: { Label("Открыть папку", systemImage: "folder") }
                    .buttonStyle(.borderless).foregroundStyle(.secondary)
                    .keyboardShortcut("o", modifiers: .command)
                Spacer()
                if model.running {
                    Button(model.stopping ? "Останавливаю…" : "Остановить") { model.stop() }
                        .controlSize(.large).disabled(model.stopping)
                        .keyboardShortcut(".", modifiers: .command)
                } else {
                    Button { model.start() } label: {
                        Label(model.finished || model.done > 0 ? "Продолжить" : "Скачать", systemImage: "arrow.down")
                            .frame(minWidth: 100)
                    }
                    .buttonStyle(.borderedProminent).tint(accent).controlSize(.large)
                    .disabled(!model.canStart).keyboardShortcut(.defaultAction)
                }
            }
        }
        .padding(24)
        .frame(width: 580)
        .background(Color(nsColor: .windowBackgroundColor))
        .onAppear { linkFocused = true }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.willTerminateNotification)) { _ in model.stop() }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
    func applicationShouldSaveSecureApplicationState(_ app: NSApplication) -> Bool { false }
    func applicationShouldRestoreSecureApplicationState(_ app: NSApplication) -> Bool { false }
    func applicationWillTerminate(_ notification: Notification) {
        clearPrivatePreferences()
        clearLegacyWindowState()
    }
}
@main struct DiskLoaderApp: App {
    init() {
        clearPrivatePreferences()
        clearLegacyWindowState()
        UserDefaults.standard.register(defaults: ["NSQuitAlwaysKeepsWindows": false])
    }
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate
    var body: some Scene {
        WindowGroup("Подорожник") { ContentView() }
            .windowStyle(.hiddenTitleBar)
            .windowResizability(.contentSize)
            .commands {
                CommandGroup(replacing: .newItem) {}
                CommandGroup(replacing: .appInfo) { AboutCommand() }
            }
        Window("Об этом приложении", id: "about") { AboutView() }
            .windowResizability(.contentSize)
    }
}
