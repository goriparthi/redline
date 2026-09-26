// Text bars for menus that can only render strings. Eighth-width block glyphs give
// sub-character resolution so a small share is visible instead of rounding away to nothing.
using System.Globalization;

namespace Redline.Core;

public static class Sparkline
{
    internal static readonly string[] Eighths = { "", "▏", "▎", "▍", "▌", "▋", "▊", "▉" };

    /// A proportional bar of exactly `width` characters, padded with spaces so columns after
    /// it stay aligned in a monospaced font.
    public static string Bar(double share, int width = 10)
    {
        if (width <= 0) return "";
        var clamped = Math.Min(Math.Max(share, 0), 1);
        var exact = clamped * width;
        int full = (int)exact;
        var rest = exact - full;

        // Anything above zero gets at least a sliver, or a real 0.4% reads as unused
        if (full == 0 && rest > 0 && rest < 0.125) rest = 0.125;

        var output = new string('█', full);
        int step = (int)Math.Round(rest * 8, MidpointRounding.AwayFromZero);
        if (full < width && step > 0)
        {
            if (step >= 8)
            {
                output += "█";
                full += 1;
            }
            else
            {
                output += Eighths[step];
            }
        }
        int used = output.Length;
        return used < width ? output + new string(' ', width - used) : output;
    }

    /// Percentage with a fixed width, so a column of them lines up. Sub-1% shares read as
    /// "<1%" rather than "0%", which would look like nothing happened.
    public static string Percent(double share, int width = 4)
    {
        var pct = Math.Min(Math.Max(share, 0), 1) * 100;
        var text = pct > 0 && pct < 1
            ? "<1%"
            : ((int)Math.Round(pct, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + "%";
        return Pad(text, width, alignRight: true);
    }

    /// Truncates with an ellipsis when too long, pads when too short. Counts user-perceived
    /// characters, as Swift's String.count does.
    public static string Pad(string s, int to, bool alignRight = false)
    {
        if (to <= 0) return "";
        var info = new StringInfo(s);
        int count = info.LengthInTextElements;
        if (count > to)
        {
            if (to <= 1) return info.SubstringByTextElements(0, to);
            return info.SubstringByTextElements(0, to - 1) + "…";
        }
        var fill = new string(' ', to - count);
        return alignRight ? fill + s : s + fill;
    }

    /// Drops a vendor prefix so model names fit a menu column without losing the part that
    /// distinguishes them.
    public static string ShortModel(string model)
    {
        foreach (var prefix in new[] { "claude-", "gpt-", "models/" })
            if (model.StartsWith(prefix, StringComparison.Ordinal)) return model[prefix.Length..];
        return model;
    }
}
