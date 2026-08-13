// Text bars for the menu, which can only render strings. Eighth-width block glyphs give
// sub-character resolution so a small share is visible instead of rounding away to nothing.
import Foundation

public enum Sparkline {
    static let eighths = ["", "▏", "▎", "▍", "▌", "▋", "▊", "▉"]

    /// A proportional bar of exactly `width` characters, padded with spaces so columns after
    /// it stay aligned in a monospaced font.
    public static func bar(share: Double, width: Int = 10) -> String {
        guard width > 0 else { return "" }
        let clamped = min(max(share, 0), 1)
        let exact = clamped * Double(width)
        var full = Int(exact)
        var rest = exact - Double(full)

        // Anything above zero gets at least a sliver, or a real 0.4% reads as unused
        if full == 0, rest > 0, rest < 0.125 { rest = 0.125 }

        var out = String(repeating: "█", count: full)
        let step = Int((rest * 8).rounded())
        if full < width, step > 0 {
            if step >= 8 {
                out += "█"
                full += 1
            } else {
                out += Sparkline.eighths[step]
            }
        }
        let used = out.count
        return used < width ? out + String(repeating: " ", count: width - used) : out
    }

    /// Percentage with a fixed width, so a column of them lines up. Sub-1% shares read as
    /// "<1%" rather than "0%", which would look like nothing happened.
    public static func percent(_ share: Double, width: Int = 4) -> String {
        let pct = min(max(share, 0), 1) * 100
        let text: String
        if pct > 0, pct < 1 {
            text = "<1%"
        } else {
            text = "\(Int(pct.rounded()))%"
        }
        return pad(text, to: width, alignRight: true)
    }

    /// Truncates with an ellipsis when too long, pads when too short.
    public static func pad(_ s: String, to width: Int, alignRight: Bool = false) -> String {
        guard width > 0 else { return "" }
        if s.count > width {
            guard width > 1 else { return String(s.prefix(width)) }
            return String(s.prefix(width - 1)) + "…"
        }
        let fill = String(repeating: " ", count: width - s.count)
        return alignRight ? fill + s : s + fill
    }

    /// Drops a vendor prefix so model names fit a menu column without losing the part that
    /// distinguishes them.
    public static func shortModel(_ model: String) -> String {
        for prefix in ["claude-", "gpt-", "models/"] where model.hasPrefix(prefix) {
            return String(model.dropFirst(prefix.count))
        }
        return model
    }
}
