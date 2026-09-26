// Semver-style ordering for the updater. A prerelease sorts below its release
// (0.4.0-beta.1 < 0.4.0) and numerically among its own kind (beta.2 < beta.10).
namespace Redline.Core;

public static class VersionCompare
{
    public static bool IsNewer(string candidate, string than) => Compare(candidate, than) > 0;

    private static int Compare(string lhs, string rhs)
    {
        var (lCore, lPre) = Split(lhs);
        var (rCore, rPre) = Split(rhs);
        for (var i = 0; i < Math.Max(lCore.Length, rCore.Length); i++)
        {
            var x = i < lCore.Length ? lCore[i] : 0;
            var y = i < rCore.Length ? rCore[i] : 0;
            if (x != y) return x > y ? 1 : -1;
        }
        return (lPre.Length == 0, rPre.Length == 0) switch
        {
            (true, true) => 0,
            (true, false) => 1,
            (false, true) => -1,
            _ => ComparePrerelease(lPre, rPre),
        };
    }

    private static (int[] Core, string[] Pre) Split(string version)
    {
        var trimmed = version.StartsWith('v') ? version[1..] : version;
        var parts = trimmed.Split('-', 2, StringSplitOptions.RemoveEmptyEntries);
        var core = (parts.FirstOrDefault() ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => IntOf(p) ?? 0).ToArray();
        var pre = parts.Length > 1 ? parts[1].Split('.', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        return (core, pre);
    }

    // Swift's Int(_:) accepts only an optional sign and ASCII digits
    private static int? IntOf(string s) =>
        s.Length > 0 && s.TrimStart('+', '-').Length > 0 && s.TrimStart('+', '-').All(char.IsAsciiDigit) &&
        int.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign,
                     System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    private static int ComparePrerelease(string[] a, string[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            // Semver: with all shared identifiers equal, the shorter list orders first
            if (i >= a.Length) return -1;
            if (i >= b.Length) return 1;
            string x = a[i], y = b[i];
            switch (IntOf(x), IntOf(y))
            {
                case ({ } nx, { } ny):
                    if (nx != ny) return nx > ny ? 1 : -1;
                    break;
                case ({ }, null): return -1;
                case (null, { }): return 1;
                default:
                    var c = string.CompareOrdinal(x, y);
                    if (c != 0) return c > 0 ? 1 : -1;
                    break;
            }
        }
        return 0;
    }
}
