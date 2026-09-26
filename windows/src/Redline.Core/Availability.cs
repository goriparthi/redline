// Which providers this machine actually has, so the UI adapts rather than showing controls
// for tools that are not installed.
namespace Redline.Core;

public sealed record ProviderAvailability(IReadOnlyList<string> Installed)
{
    public bool IsEmpty => Installed.Count == 0;
    public bool HasChoice => Installed.Count > 1;

    public bool Has(string provider) =>
        Installed.Any(p => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase));

    public List<string> TrackChoices =>
        HasChoice ? new[] { Config.AutoProvider }.Concat(Installed).ToList() : Installed.ToList();

    public bool Equals(ProviderAvailability? o) => o is not null && Installed.SequenceEqual(o.Installed);
    public override int GetHashCode() => Installed.Count;

    /// Installed when the tool's directory exists; Ollama also when seen running, Claude also
    /// with a signed-in account and nothing local.
    public static ProviderAvailability Detect(string? home = null, bool ollamaReachable = false,
                                              bool claudeAccount = false)
    {
        var root = home ?? RedlineHome.Url;
        bool Exists(string p) { var f = RedlineHome.Join(root, p); return Directory.Exists(f) || File.Exists(f); }
        var found = new List<string>();
        if (Exists(".claude/projects") || Exists(".claude") || claudeAccount) found.Add("Claude");
        if (Exists(".codex/sessions") || Exists(".codex")) found.Add("Codex");
        if (ollamaReachable || Exists(".ollama") || Exists(".local/share/redline/ollama.jsonl")) found.Add("Ollama");
        return new ProviderAvailability(found);
    }
}
