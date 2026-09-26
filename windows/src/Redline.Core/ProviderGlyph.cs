// The third-party provider marks. Only the identity lives in Core; the vector data and its
// rendering belong to the app.
namespace Redline.Core;

/// One provider mark, used only to identify a provider, never as RedLine's own identity.
public enum ProviderMark { Codex, Anthropic, Claude, Ollama }

public static class ProviderMarkExt
{
    public static string RawValue(this ProviderMark m) => m switch
    {
        ProviderMark.Codex => "Codex",
        ProviderMark.Anthropic => "Anthropic",
        ProviderMark.Claude => "Claude",
        _ => "Ollama",
    };

    /// The asset name, for the build paths that ship the marks as resources.
    public static string AssetName(this ProviderMark m) => m.RawValue();

    /// Spoken label. Paired with visible text the glyph is decorative and hidden instead.
    public static string AccessibilityLabel(this ProviderMark m) => m switch
    {
        ProviderMark.Codex => "Codex, the OpenAI coding tool",
        ProviderMark.Anthropic => "Anthropic, the Claude provider",
        ProviderMark.Claude => "Claude, the Anthropic product",
        _ => "Ollama, running models on this PC",
    };
}
