// Renders a ProviderMark. The mark data itself lives in RedlineCore so a non-AppKit shell
// can draw it too; only the NSImage loading and the SwiftUI view are here.
import SwiftUI
import RedlineCore

/// Loads a provider mark as a template image, once per mark.
///
/// Prefers the compiled asset catalog and falls back to the embedded vector, because the
/// SwiftPM bundle path assembles Resources by hand and never runs actool.
enum ProviderMarkImage {
    private static let lock = NSLock()
    private static var cache: [ProviderMark: NSImage] = [:]

    static func image(for mark: ProviderMark) -> NSImage? {
        lock.lock()
        defer { lock.unlock() }
        if let hit = cache[mark] { return hit }
        let image = NSImage(named: mark.assetName)
            ?? NSImage(data: Data(mark.svg.utf8))
        guard let image else { return nil }
        // Template rendering is what lets one monochrome mark serve light, dark and
        // increased-contrast appearances without a second asset.
        image.isTemplate = true
        cache[mark] = image
        return image
    }
}

/// A provider mark at a given size: monochrome, template rendered, proportions preserved.
///
/// The mark is never restyled, cropped or combined. Colour belongs to the RedLine-owned chip
/// or dot around it, which is why this view only ever takes a size.
public struct ProviderGlyph: View {
    private let mark: ProviderMark
    private let size: CGFloat
    private let decorative: Bool

    /// - Parameter decorative: true when adjacent text already names the provider, so the
    ///   glyph is hidden from assistive technology rather than read out twice.
    public init(_ mark: ProviderMark, size: CGFloat = 15, decorative: Bool = true) {
        self.mark = mark
        self.size = size
        self.decorative = decorative
    }

    public var body: some View {
        Group {
            if let image = ProviderMarkImage.image(for: mark) {
                Image(nsImage: image)
                    .resizable()
                    .renderingMode(.template)
                    // Square viewBoxes, and scaledToFit regardless, so the mark can never
                    // be stretched by the frame it is given
                    .scaledToFit()
            } else {
                // A mark that will not load must not silently leave a hole where a provider
                // was named; the frame stays occupied.
                RoundedRectangle(cornerRadius: size * 0.2, style: .continuous)
                    .strokeBorder(lineWidth: max(1, size * 0.08))
                    .opacity(0.5)
            }
        }
        .frame(width: size, height: size)
        .modifier(GlyphAccessibility(label: mark.accessibilityLabel, decorative: decorative))
    }
}

private struct GlyphAccessibility: ViewModifier {
    let label: String
    let decorative: Bool

    func body(content: Content) -> some View {
        if decorative {
            content.accessibilityHidden(true)
        } else {
            content.accessibilityLabel(label)
        }
    }
}
