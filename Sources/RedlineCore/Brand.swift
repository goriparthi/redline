// Brand tokens from brand/tokens/redline-tokens.json, kept here so the app and the widget
// cannot drift on what counts as healthy, approaching, or at the limit.
import Foundation

public struct BrandColor: Equatable {
    public let red: Double
    public let green: Double
    public let blue: Double

    public init(_ hex: UInt32) {
        red   = Double((hex >> 16) & 0xFF) / 255
        green = Double((hex >> 8) & 0xFF) / 255
        blue  = Double(hex & 0xFF) / 255
    }
}

public enum Brand {
    public static let carbon   = BrandColor(0x0B0D10)  // main background
    public static let graphite = BrandColor(0x171A1F)  // elevated surfaces
    public static let steel    = BrandColor(0x818792)  // secondary text and structure
    public static let chalk    = BrandColor(0xF4F1EA)  // primary text
    public static let signal   = BrandColor(0xFF3B30)  // at the limit
    public static let amber    = BrandColor(0xFF9F0A)  // approaching the limit
    public static let clear    = BrandColor(0x32D74B)  // healthy

    public enum Status {
        case healthy, approaching, atLimit

        public var color: BrandColor {
            switch self {
            case .healthy:     return Brand.clear
            case .approaching: return Brand.amber
            case .atLimit:     return Brand.signal
            }
        }
    }

    public static func status(for utilization: Double,
                             approachingPct: Double,
                             atLimitPct: Double) -> Status {
        if utilization >= atLimitPct { return .atLimit }
        if utilization >= approachingPct { return .approaching }
        return .healthy
    }

    // Brand voice: report the fact, never scold. "Approaching your limit" over "danger".
    public static func phrase(for status: Status) -> String {
        switch status {
        case .healthy:     return "Healthy"
        case .approaching: return "Approaching your limit"
        case .atLimit:     return "Limit reached"
        }
    }
}
