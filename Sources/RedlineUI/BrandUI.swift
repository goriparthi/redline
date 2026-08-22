// Shared SwiftUI brand pieces. This is the one place the core reaches beyond plain parsing,
// because the app and the widget are separate binaries and must not draw different marks.
// Still no network and no Keychain here.
import SwiftUI
import RedlineCore

public extension Color {
    init(brand c: BrandColor) { self.init(red: c.red, green: c.green, blue: c.blue) }
}

/// Fixed brand tones, for the surfaces that paint Carbon in every OS theme and force
/// `.dark` so the dynamic tokens in `RL` resolve to their dark values: the widget and the
/// setup window. Anything that follows the OS appearance uses `RL` instead.
public enum BrandUI {
    public static let carbon = Color(brand: Brand.carbon)
    public static let graphite = Color(brand: Brand.graphite)
    public static let steel = Color(brand: Brand.steel)
    public static let chalk = Color(brand: Brand.chalk)
    public static let signal = Color(brand: Brand.signal)
    public static let amber = Color(brand: Brand.amber)
    public static let clear = Color(brand: Brand.clear)

    /// A provider keeps one accent everywhere it appears: menu, dashboard, widget. The accent
    /// surrounds the provider's mark; it is never applied to the mark itself.
    public static func color(forProvider provider: String) -> Color {
        ProviderIdentity.accent(for: provider)
    }

    public static func color(forStatus status: Brand.Status) -> Color {
        Color(brand: status.color)
    }

    public static func statusColor(_ utilization: Double,
                                   approaching: Double = 60,
                                   atLimit: Double = 85) -> Color {
        color(forStatus: Brand.status(for: utilization,
                                      approachingPct: approaching, atLimitPct: atLimit))
    }
}

/// The symbol from brand/logo/redline-symbol.svg: three streams meeting one limit line.
/// Traced from the same coordinates so the artwork and the app cannot drift apart.
public struct RedlineMark: View {
    private let size: CGFloat

    public init(size: CGFloat = 26) { self.size = size }

    private func p(_ x: CGFloat, _ y: CGFloat) -> CGPoint {
        CGPoint(x: x / 256 * size, y: y / 256 * size)
    }

    public var body: some View {
        ZStack {
            Path { path in
                path.move(to: p(48, 65))
                path.addCurve(to: p(139, 125), control1: p(92, 65), control2: p(108, 108))
                path.move(to: p(48, 191))
                path.addCurve(to: p(139, 131), control1: p(92, 191), control2: p(108, 148))
            }
            .stroke(BrandUI.chalk,
                    style: StrokeStyle(lineWidth: size * 18 / 256, lineCap: .round))
            Path { path in
                path.move(to: p(48, 128)); path.addLine(to: p(178, 128))
                path.move(to: p(118, 57)); path.addLine(to: p(151, 57))
                path.addCurve(to: p(207, 103), control1: p(187, 57), control2: p(207, 76))
                path.addCurve(to: p(184, 139), control1: p(207, 121), control2: p(198, 131))
                path.move(to: p(163, 143)); path.addLine(to: p(207, 199))
            }
            .stroke(BrandUI.steel, style: StrokeStyle(lineWidth: size * 18 / 256,
                                                     lineCap: .round, lineJoin: .round))
            // The limit line, the one red element
            Path { path in
                path.move(to: p(126, 128)); path.addLine(to: p(214, 128))
            }
            .stroke(BrandUI.signal,
                    style: StrokeStyle(lineWidth: size * 8 / 256, lineCap: .round))
        }
        .frame(width: size, height: size)
        .accessibilityLabel("RedLine")
    }
}

/// The RedLine mark that follows the window's appearance, for surfaces that do not force
/// dark. The fixed `RedlineMark` above is chalk-on-carbon and disappears on paper.
public struct RedlineMarkAdaptive: View {
    private let size: CGFloat

    public init(size: CGFloat = 26) { self.size = size }

    public var body: some View {
        RedlineMark(size: size)
            // The mark's chalk and steel strokes are drawn from the fixed dark tones, so on a
            // light window the whole mark is re-inked rather than redrawn twice.
            .colorMultiply(RL.Ink.primary)
            .accessibilityLabel("RedLine")
    }
}

/// A usage rail that ends at its limit, echoing the mark. Kept as the widget's rail: fixed
/// brand tones, no environment to read, no animation in a timeline-rendered view.
public struct LimitRail: View {
    private let utilization: Double
    private let height: CGFloat
    private let showsLimit: Bool
    /// Stale readings drain to steel: the shape stays, the status palette is what says live.
    private let stale: Bool

    public init(utilization: Double, height: CGFloat = 6, showsLimit: Bool = true,
                stale: Bool = false) {
        self.utilization = utilization
        self.height = height
        self.showsLimit = showsLimit
        self.stale = stale
    }

    public var body: some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                Capsule().fill(BrandUI.carbon)
                Capsule()
                    .fill(stale ? BrandUI.steel : BrandUI.statusColor(utilization))
                    // Always a sliver for a real but small share, never nothing
                    .frame(width: max(height * 0.6,
                                      geo.size.width * min(max(utilization, 0), 100) / 100))
                if showsLimit {
                    Rectangle()
                        .fill(BrandUI.signal)
                        .frame(width: 2)
                        .offset(x: geo.size.width - 2)
                }
            }
        }
        .frame(height: height)
    }
}

/// A tinted tile carrying the provider's own mark. Sits beside the title so the provider is
/// readable at a glance, with the RedLine mark kept separate as the app's own signature.
///
/// The mark inside is a third-party glyph, monochrome and unaltered; the tile around it is
/// RedLine's. A provider is never identified by the tile alone: every caller puts the
/// provider's name in the row beside it.
public struct TrackBadge: View {
    private let provider: String?
    private let size: CGFloat

    public init(provider: String?, size: CGFloat = 22) {
        self.provider = provider
        self.size = size
    }

    public var body: some View {
        ProviderTile(provider: provider, size: size)
    }
}

/// The provider mark without the tile, for a place where a tinted tile would outweigh the
/// text beside it. The tint is passed in rather than derived, because a caller that
/// rasterises this has to resolve the colour for an appearance itself; a bitmap cannot
/// follow a theme.
public struct TrackMark: View {
    private let provider: String?
    private let tint: Color
    private let size: CGFloat

    public init(provider: String?, tint: Color, size: CGFloat = 12) {
        self.provider = provider
        self.tint = tint
        self.size = size
    }

    public var body: some View {
        Group {
            if let mark = ProviderIdentity.of(provider)?.mark {
                ProviderGlyph(mark, size: size)
                    .foregroundStyle(tint)
            } else {
                RedlineMark(size: size)
            }
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}
