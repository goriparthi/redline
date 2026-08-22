using System.Globalization;

namespace RedLine.Core;

/// <summary>
/// The two functions every figure a person reads goes through. Deliberately a port of Swift's
/// fmtTokens and fmtCost rather than idiomatic .NET formatting, because these two have to
/// agree exactly: Tests/Fixtures/formatting.json is asserted from both languages.
///
/// Invariant culture throughout. A German locale would otherwise render $1,234,567.89 as
/// $1.234.567,89 on one platform and not the other.
/// </summary>
public static class Formatting
{
    /// <summary>"1.1K", "2.5M", "3.2B", or the number itself below a thousand.</summary>
    public static string Tokens(long n)
    {
        double d = n;
        if (d >= 1_000_000_000) return Scaled(d / 1_000_000_000) + "B";
        if (d >= 1_000_000) return Scaled(d / 1_000_000) + "M";
        if (d >= 1_000) return Scaled(d / 1_000) + "K";
        return n.ToString(CultureInfo.InvariantCulture);
    }

    private static string Scaled(double value) =>
        // Away from zero, matching C's printf rather than .NET's default, so a figure ending
        // in a five reads the same on both sides
        Math.Round(value, 1, MidpointRounding.AwayFromZero)
            .ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>
    /// "$24,320.91". Grouped past four digits, because an ungrouped dollar figure misreads by
    /// 10x at a glance, which is exactly the glance a tray icon is for.
    /// </summary>
    public static string Cost(double c)
    {
        var fixedPart = Math.Round(Math.Abs(c), 2, MidpointRounding.AwayFromZero)
            .ToString("0.00", CultureInfo.InvariantCulture);
        var sign = c < 0 ? "-$" : "$";
        var dot = fixedPart.IndexOf('.');
        if (dot < 0) return sign + fixedPart;

        var whole = fixedPart[..dot];
        var grouped = new System.Text.StringBuilder(whole.Length + whole.Length / 3);
        for (var i = 0; i < whole.Length; i++)
        {
            if (i > 0 && (whole.Length - i) % 3 == 0) grouped.Append(',');
            grouped.Append(whole[i]);
        }
        return sign + grouped + fixedPart[dot..];
    }
}
