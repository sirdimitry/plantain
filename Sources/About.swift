import SwiftUI
import AppKit

struct AboutCommand: View {
    @Environment(\.openWindow) private var openWindow
    var body: some View {
        Button("Об этом приложении") { openWindow(id: "about") }
    }
}
struct XMark: Shape {
    func path(in rect: CGRect) -> Path {
        let scale = min(rect.width, rect.height) / 24
        func point(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: x * scale, y: y * scale) }
        var p = Path()
        p.addLines([point(18.9,1.15),point(22.58,1.15),point(14.54,10.34),point(24,22.85),
                    point(16.59,22.85),point(10.79,15.27),point(4.15,22.85),point(0.47,22.85),
                    point(9.07,13.02),point(0,1.15),point(7.59,1.15),point(12.83,8.08),point(18.9,1.15)])
        p.closeSubpath()
        p.addLines([point(17.61,20.64),point(19.65,20.64),point(6.49,3.24),point(4.3,3.24)])
        p.closeSubpath()
        return p
    }
}
struct AboutView: View {
    var body: some View {
        VStack(spacing: 14) {
            if let url = Bundle.main.url(forResource: "AppArtwork", withExtension: "png"),
               let icon = NSImage(contentsOf: url) {
                Image(nsImage: icon).renderingMode(.original).resizable().interpolation(.high)
                    .frame(width: 104, height: 104).accessibilityHidden(true)
            }
            VStack(spacing: 5) {
                Text("Подорожник").font(.system(size: 21, weight: .semibold))
                Text("Версия " + (Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""))
                    .font(.system(size: 12)).foregroundStyle(.secondary)
            }
            Text("Подорожник — скачает, когда Диск не может")
                .font(.system(size: 13, weight: .medium))
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
            Text("Ссылки и выбранная папка не запоминаются.")
                .font(.system(size: 12)).foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 16) {
                Link(destination: URL(string: "https://github.com/sirdimitry")!) {
                    Group {
                        if let url = Bundle.main.url(forResource: "GitHub", withExtension: "png"),
                           let icon = NSImage(contentsOf: url) {
                            Image(nsImage: icon).renderingMode(.original).resizable().interpolation(.high)
                        }
                    }.frame(width: 34, height: 34)
                        .frame(width: 48, height: 44).background(.white, in: RoundedRectangle(cornerRadius: 9))
                }.accessibilityLabel("GitHub: sirdimitry").help("https://github.com/sirdimitry")
                Link(destination: URL(string: "https://x.com/sirdimitry")!) {
                    XMark().fill(.black, style: FillStyle(eoFill: true)).frame(width: 23, height: 23)
                        .frame(width: 48, height: 44).background(.white, in: RoundedRectangle(cornerRadius: 9))
                }.accessibilityLabel("X: sirdimitry").help("https://x.com/sirdimitry")
            }.padding(.top, 4)
        }.padding(28).frame(width: 340)
    }
}
