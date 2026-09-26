// Where a number came from, so a reading that disagrees with another tool can be argued about.
namespace Redline.Core;

public enum Provenance { Official, LocalEstimate, Experimental, Unknown }

public static class ProvenanceExt
{
    public static string RawValue(this Provenance p) => p switch
    {
        Provenance.Official => "official",
        Provenance.LocalEstimate => "local_estimate",
        Provenance.Experimental => "experimental",
        _ => "unknown",
    };

    public static string Label(this Provenance p) => p switch
    {
        Provenance.Official => "official",
        Provenance.LocalEstimate => "local estimate",
        Provenance.Experimental => "experimental",
        _ => "unknown",
    };

    public static string Note(this Provenance p) => p switch
    {
        Provenance.Official => "reported by the provider",
        Provenance.LocalEstimate => "computed here from local files and your pricing table",
        Provenance.Experimental => "read from an undocumented endpoint",
        _ => "source not recorded",
    };

    public static Provenance FromRaw(string? raw) => raw switch
    {
        "official" => Provenance.Official,
        "local_estimate" => Provenance.LocalEstimate,
        "experimental" => Provenance.Experimental,
        _ => Provenance.Unknown,
    };
}
