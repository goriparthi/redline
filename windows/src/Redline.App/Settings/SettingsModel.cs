// The settings window's state: the config it edits, the machine state beside it, and the section.
// Every control writes straight through to config.json; nothing here holds unsaved state.
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Redline.Core;

namespace Redline.App.Settings;

public enum SettingsSection { Providers, Monitoring, Limits, Appearance, Data, About }

public static class SettingsSectionInfo
{
    public static readonly SettingsSection[] All = Enum.GetValues<SettingsSection>();

    public static string Title(this SettingsSection s) => s switch
    {
        SettingsSection.Providers => "Providers",
        SettingsSection.Monitoring => "Refresh and Monitoring",
        SettingsSection.Limits => "Limits and Alerts",
        SettingsSection.Appearance => "Appearance",
        SettingsSection.Data => "Data and Privacy",
        _ => "About",
    };

    /// <summary>Swift's rawValue, so a caller can name a section in a command line or a menu.</summary>
    public static string RawValue(this SettingsSection s) => s.ToString().ToLowerInvariant();

    public static SettingsSection? Parse(string? raw) =>
        All.Cast<SettingsSection?>().FirstOrDefault(s => string.Equals(s!.Value.RawValue(), raw, StringComparison.OrdinalIgnoreCase));

    /// <summary>A Segoe Fluent Icons glyph standing in for the SF Symbol.</summary>
    public static string Glyph(this SettingsSection s) => s switch
    {
        SettingsSection.Providers => "",
        SettingsSection.Monitoring => "",
        SettingsSection.Limits => "",
        SettingsSection.Appearance => "",
        SettingsSection.Data => "",
        _ => "",
    };

    public static string Hint(this SettingsSection s) => s switch
    {
        SettingsSection.Providers => "Which tools RedLine reads, and where Claude's percentages come from",
        SettingsSection.Monitoring => "How often RedLine rescans, and what the tray shows",
        SettingsSection.Limits => "Where the thresholds sit, and what RedLine says when one is crossed",
        SettingsSection.Appearance => "The dashboard's appearance",
        SettingsSection.Data => "What is written to disk, and what leaves this PC",
        _ => "Version, updates, and third-party notices",
    };
}

public sealed class SettingsModel : INotifyPropertyChanged
{
    Config config;
    SettingsEnvironmentState state = new();
    SettingsSection section = SettingsSection.Providers;

    /// <summary>The config file this edits. Null means the real one; samples and tests pass a temp path.</summary>
    public string? ConfigPath { get; }

    public SettingsModel(Config? config = null, string? configPath = null)
    {
        ConfigPath = configPath;
        this.config = config ?? Config.Load(configPath);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Config Config { get => config; set { config = value; Raise(); } }
    public SettingsEnvironmentState State { get => state; set { state = value; Raise(); } }
    public SettingsSection Section { get => section; set { if (section == value) return; section = value; Raise(); } }
    public SettingsActions Actions { get; set; } = new();

    /// <summary>Called after any write, so the app reloads the config it is actually running on instead of drifting from the file.</summary>
    public Action? OnConfigChanged { get; set; }

    void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Writes the given keys and re-reads the file, so validation and clamping stay in Config.</summary>
    /// <remarks>Only for preferences whose whole effect is the stored value; side effects go through Actions.</remarks>
    public void Write(Dictionary<string, JsonNode?> values)
    {
        if (!Config.Write(values, ConfigPath)) return;
        Config = Config.Load(ConfigPath);
        OnConfigChanged?.Invoke();
    }

    public void Write(string key, JsonNode? value) => Write(new Dictionary<string, JsonNode?> { [key] = value });

    /// <summary>A preference the app owns: the app applies the value so its side effects run once.</summary>
    /// <remarks>Then re-reads, so the control settles on what the config accepted rather than on what was asked.</remarks>
    public void Apply<T>(Func<Config, T> current, Action<T> apply, T next)
    {
        if (EqualityComparer<T>.Default.Equals(current(config), next)) return;
        apply(next);
        Config = Config.Load(ConfigPath);
    }

    /// <summary>Re-reads the config, for the app to call after it has changed something itself.</summary>
    public void Reload() => Config = Config.Load(ConfigPath);

    public void SetProvider(string provider, bool on)
    {
        var providers = config.Providers.Where(p => p.Length > 0).ToList();
        if (on)
        {
            if (providers.Any(p => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase))) return;
            providers.Add(provider);
        }
        else
        {
            providers.RemoveAll(p => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase));
        }
        // An empty list means "read everything" further down, so the last provider cannot be
        // switched off here; the message beside the toggles says so
        if (providers.Count == 0) return;
        // Through the app, because changing what is read has to trigger a rescan
        Actions.SetProviders(Config.KnownProviders
            .Where(known => providers.Any(p => string.Equals(p, known, StringComparison.OrdinalIgnoreCase))).ToList());
        Config = Config.Load(ConfigPath);
    }

    public bool Reads(string provider) => config.Wants(provider);

    /// <summary>True when this is the only provider still on, so the toggle can say why it will not turn off.</summary>
    public bool IsLastEnabled(string provider) => Reads(provider) && config.Providers.Count <= 1;
}
