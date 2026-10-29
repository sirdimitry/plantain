import AppKit
let source = NSImage(contentsOfFile: CommandLine.arguments[1])!
let output = CommandLine.arguments[2]
try FileManager.default.createDirectory(atPath: output, withIntermediateDirectories: true)
for size in [16, 32, 128, 256, 512] {
 for scale in [1, 2] {
  let pixels = size * scale
  let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels,
      bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
      colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
  NSGraphicsContext.saveGraphicsState()
  let context = NSGraphicsContext(bitmapImageRep: rep)!
  context.imageInterpolation = .high
  NSGraphicsContext.current = context
  source.draw(in: NSRect(x: 0, y: 0, width: pixels, height: pixels), from: .zero, operation: .sourceOver, fraction: 1)
  NSGraphicsContext.restoreGraphicsState()
  let suffix = scale == 2 ? "@2x" : ""
  try rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: output + "/icon_\(size)x\(size)\(suffix).png"))
 }
}

func chunk(_ name: String, _ payload: Data) -> Data {
    var data = Data(name.utf8)
    var length = UInt32(payload.count + 8).bigEndian
    withUnsafeBytes(of: &length) { data.append(contentsOf: $0) }
    data.append(payload)
    return data
}
if CommandLine.arguments.count > 3 {
    let entries = [("icp4", "icon_16x16.png"), ("icp5", "icon_32x32.png"),
        ("icp6", "icon_32x32@2x.png"), ("ic07", "icon_128x128.png"),
        ("ic08", "icon_256x256.png"), ("ic09", "icon_512x512.png"),
        ("ic10", "icon_512x512@2x.png")]
    var body = Data()
    for (kind, filename) in entries {
        body.append(chunk(kind, try Data(contentsOf: URL(fileURLWithPath: output + "/" + filename))))
    }
    try chunk("icns", body).write(to: URL(fileURLWithPath: CommandLine.arguments[3]))
}
