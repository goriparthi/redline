// Shared SwiftUI brand pieces. This is the one place the core reaches beyond plain parsing,
// because the app and the widget are separate binaries and must not draw different marks.
// Still no AppKit, no network, no Keychain here.
import SwiftUI

public extension Color {
    init(brand c: BrandColor) { self.init(red: c.red, green: c.green, blue: c.blue) }
}

public enum BrandUI {
    public static let carbon = Color(brand: Brand.carbon)
    public static let graphite = Color(brand: Brand.graphite)
    public static let steel = Color(brand: Brand.steel)
    public static let chalk = Color(brand: Brand.chalk)
    public static let signal = Color(brand: Brand.signal)
    public static let amber = Color(brand: Brand.amber)
    public static let clear = Color(brand: Brand.clear)

    /// Providers keep one colour everywhere they appear: menu, dashboard, widget.
    public static func color(forProvider provider: String) -> Color {
        switch provider.lowercased() {
        case "claude": return chalk
        case "codex":  return steel
        case "ollama": return clear
        default:       return amber
        }
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
        .accessibilityLabel("Redline")
    }
}

/// A usage rail that ends at its limit, echoing the mark.
public struct LimitRail: View {
    private let utilization: Double
    private let height: CGFloat
    private let showsLimit: Bool

    public init(utilization: Double, height: CGFloat = 6, showsLimit: Bool = true) {
        self.utilization = utilization
        self.height = height
        self.showsLimit = showsLimit
    }

    public var body: some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                Capsule().fill(BrandUI.carbon)
                Capsule()
                    .fill(BrandUI.statusColor(utilization))
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

/// Which glyph identifies a track.
///
/// Deliberately original iconography rather than vendor logos. Anthropic, OpenAI, and Ollama
/// all restrict third-party use of their marks, and a redrawn approximation would breach the
/// usual requirement that a mark be reproduced unaltered. These read as generic categories:
/// a remote endpoint, source code, local layers.
public enum TrackGlyph: String, CaseIterable {
    case redline   // all providers, the app's own mark
    case hosted    // Claude: a remote model behind an endpoint
    case code      // Codex: source code
    case layers    // Ollama: model weights held locally

    public static func of(provider: String?) -> TrackGlyph {
        switch provider?.lowercased() {
        case nil:       return .redline
        case "claude":  return .hosted
        case "codex":   return .code
        case "ollama":  return .layers
        default:        return .redline
        }
    }
}

/// A tinted tile carrying the track glyph. Sits beside the title so the track is readable at a
/// glance, with the Redline mark kept separate as the app's own signature.
public struct TrackBadge: View {
    private let glyph: TrackGlyph
    private let tint: Color
    private let size: CGFloat

    public init(provider: String?, size: CGFloat = 22) {
        self.glyph = TrackGlyph.of(provider: provider)
        self.tint = provider.map { BrandUI.color(forProvider: $0) } ?? BrandUI.chalk
        self.size = size
    }

    private var inset: CGFloat { size * 0.26 }

    public var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: size * 0.28, style: .continuous)
                .fill(tint.opacity(0.16))
            RoundedRectangle(cornerRadius: size * 0.28, style: .continuous)
                .stroke(tint.opacity(0.45), lineWidth: max(1, size * 0.045))
            shape
                .frame(width: size - inset * 2, height: size - inset * 2)
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }

    @ViewBuilder
    private var shape: some View {
        switch glyph {
        case .redline:
            RedlineMark(size: size - inset * 2)
        case .hosted:
            HostedGlyph(tint: tint)
        case .code:
            CodeGlyph(tint: tint)
        case .layers:
            LayersGlyph(tint: tint)
        }
    }
}

/// A ring with a satellite dot: a model reached over the network.
private struct HostedGlyph: View {
    let tint: Color

    var body: some View {
        GeometryReader { geo in
            let s = min(geo.size.width, geo.size.height)
            ZStack {
                Circle()
                    .stroke(tint, lineWidth: s * 0.14)
                    .frame(width: s * 0.72, height: s * 0.72)
                Circle()
                    .fill(tint)
                    .frame(width: s * 0.26, height: s * 0.26)
                    .offset(x: s * 0.34, y: -s * 0.34)
            }
            .frame(width: geo.size.width, height: geo.size.height)
        }
    }
}

/// Opposed chevrons: source code.
private struct CodeGlyph: View {
    let tint: Color

    var body: some View {
        GeometryReader { geo in
            let w = geo.size.width, h = geo.size.height
            Path { p in
                p.move(to: CGPoint(x: w * 0.38, y: h * 0.16))
                p.addLine(to: CGPoint(x: w * 0.08, y: h * 0.5))
                p.addLine(to: CGPoint(x: w * 0.38, y: h * 0.84))
                p.move(to: CGPoint(x: w * 0.62, y: h * 0.16))
                p.addLine(to: CGPoint(x: w * 0.92, y: h * 0.5))
                p.addLine(to: CGPoint(x: w * 0.62, y: h * 0.84))
            }
            .stroke(tint, style: StrokeStyle(lineWidth: min(w, h) * 0.15,
                                            lineCap: .round, lineJoin: .round))
        }
    }
}

/// Stacked bars: weights sitting on local disk.
private struct LayersGlyph: View {
    let tint: Color

    var body: some View {
        GeometryReader { geo in
            let w = geo.size.width, h = geo.size.height
            let bar = h * 0.19
            VStack(alignment: .leading, spacing: h * 0.115) {
                Capsule().fill(tint).frame(width: w, height: bar)
                Capsule().fill(tint.opacity(0.75)).frame(width: w * 0.74, height: bar)
                Capsule().fill(tint.opacity(0.5)).frame(width: w * 0.48, height: bar)
            }
            .frame(width: w, height: h, alignment: .leading)
        }
    }
}
