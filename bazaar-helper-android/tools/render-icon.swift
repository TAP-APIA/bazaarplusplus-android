#!/usr/bin/env swift
// Regenerates the adaptive-icon foreground layers from the desktop app icon.
//
//   swift bazaar-assistant/android/tools/render-icon.swift
//
// Background and monochrome layers are plain XML vectors under app/src/main/res/drawable;
// only the raster foreground needs regenerating. Canvas follows the Android adaptive spec:
// 108dp layers, badge centred at 82dp so the framed badge stays inside the 72dp safe zone.
import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

let root = URL(fileURLWithPath: #filePath)
  .deletingLastPathComponent()  // tools
  .deletingLastPathComponent()  // android
let artwork = root.deletingLastPathComponent()
  .appendingPathComponent("desktop/src-tauri/icons/icon.png")
let res = root.appendingPathComponent("app/src/main/res")

guard let source = CGImageSourceCreateWithURL(artwork as CFURL, nil),
  let art = CGImageSourceCreateImageAtIndex(source, 0, nil)
else {
  FileHandle.standardError.write("Missing desktop icon at \(artwork.path)\n".data(using: .utf8)!)
  exit(1)
}

let badgeDp: CGFloat = 82
let densities: [(String, Int)] = [
  ("mipmap-mdpi", 108), ("mipmap-hdpi", 162), ("mipmap-xhdpi", 216),
  ("mipmap-xxhdpi", 324), ("mipmap-xxxhdpi", 432),
]

for (folder, side) in densities {
  let s = CGFloat(side)
  guard
    let context = CGContext(
      data: nil, width: side, height: side, bitsPerComponent: 8, bytesPerRow: 0,
      space: CGColorSpaceCreateDeviceRGB(),
      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
  else { fatalError("cannot create \(side)px context") }
  context.interpolationQuality = .high

  let width = s * badgeDp / 108
  let rect = CGRect(x: (s - width) / 2, y: (s - width) / 2, width: width, height: width)

  // Soft shadow behind the badge; the launcher adds its own drop shadow on top.
  context.saveGState()
  context.setShadow(
    offset: CGSize(width: 0, height: -s * 0.013), blur: s * 0.032,
    color: CGColor(red: 0, green: 0, blue: 0, alpha: 0.45))
  context.setFillColor(CGColor(red: 0, green: 0, blue: 0, alpha: 1))
  let radius = width * 0.22
  context.addPath(
    CGPath(
      roundedRect: rect.insetBy(dx: width * 0.03, dy: width * 0.03),
      cornerWidth: radius, cornerHeight: radius, transform: nil))
  context.fillPath()
  context.restoreGState()

  context.draw(art, in: rect)

  let folderURL = res.appendingPathComponent(folder)
  try? FileManager.default.createDirectory(at: folderURL, withIntermediateDirectories: true)
  let output = folderURL.appendingPathComponent("ic_launcher_foreground.png")
  guard
    let destination = CGImageDestinationCreateWithURL(
      output as CFURL, UTType.png.identifier as CFString, 1, nil),
    let image = context.makeImage()
  else { fatalError("cannot write \(output.path)") }
  CGImageDestinationAddImage(destination, image, nil)
  guard CGImageDestinationFinalize(destination) else { fatalError("cannot finish \(output.path)") }
  print("wrote \(output.path)")
}
