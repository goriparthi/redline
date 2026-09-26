// Brand tokens from brand/tokens/redline-tokens.json, kept here so the app and the widget
// cannot drift on what counts as healthy, approaching, or at the limit.
namespace Redline.Core;

public sealed record BrandColor
{
    public double Red { get; }
    public double Green { get; }
    public double Blue { get; }
    public uint Rgb { get; }

    public BrandColor(uint hex)
    {
        Rgb = hex & 0xFFFFFF;
        Red = ((hex >> 16) & 0xFF) / 255.0;
        Green = ((hex >> 8) & 0xFF) / 255.0;
        Blue = (hex & 0xFF) / 255.0;
    }

    public byte R => (byte)((Rgb >> 16) & 0xFF);
    public byte G => (byte)((Rgb >> 8) & 0xFF);
    public byte B => (byte)(Rgb & 0xFF);

    /// "#RRGGBB", for WPF brushes and HTML.
    public string Hex => $"#{Rgb:X6}";
}

public enum BrandStatus { Healthy, Approaching, AtLimit }

public static class Brand
{
    public static readonly BrandColor Carbon = new(0x0B0D10);   // main background
    public static readonly BrandColor Graphite = new(0x171A1F); // elevated surfaces
    public static readonly BrandColor Steel = new(0x818792);    // secondary text and structure
    public static readonly BrandColor Chalk = new(0xF4F1EA);    // primary text
    public static readonly BrandColor Signal = new(0xFF3B30);   // at the limit
    public static readonly BrandColor Amber = new(0xFF9F0A);    // approaching the limit
    public static readonly BrandColor Clear = new(0x32D74B);    // healthy

    public static BrandColor Color(this BrandStatus status) => status switch
    {
        BrandStatus.Healthy => Clear,
        BrandStatus.Approaching => Amber,
        _ => Signal,
    };

    public static BrandStatus Status(double utilization, double approachingPct, double atLimitPct)
    {
        if (utilization >= atLimitPct) return BrandStatus.AtLimit;
        if (utilization >= approachingPct) return BrandStatus.Approaching;
        return BrandStatus.Healthy;
    }

    // Brand voice: report the fact, never scold. "Approaching your limit" over "danger".
    public static string Phrase(BrandStatus status) => status switch
    {
        BrandStatus.Healthy => "Healthy",
        BrandStatus.Approaching => "Approaching your limit",
        _ => "Limit reached",
    };
}
