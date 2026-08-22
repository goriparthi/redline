using RedLine.Core;

namespace RedLine.Core.Tests;

/// <summary>
/// The settings catalogue is a contract with the Swift engine, and the fixture is real output
/// from `redline config --json` rather than something written here. SettingsContractTests
/// exists in both languages against this same file: rename a key or a kind and one of the two
/// fails immediately, instead of a settings page silently losing a control.
/// </summary>
public class SettingsContractTests
{
    private static IReadOnlyList<SettingDefinition> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "config-settings.json");
        var parsed = SettingsJson.ParseCatalog(File.ReadAllText(path));
        Assert.NotNull(parsed);
        return parsed;
    }

    [Fact]
    public void EverySettingTheEngineOffersIsReadable()
    {
        var settings = Load();
        Assert.Equal(12, settings.Count);
        Assert.All(settings, s =>
        {
            Assert.NotEqual("", s.Key);
            Assert.NotEqual("", s.Summary);
            // A kind this build cannot render is the one thing that would reach a person as a
            // control that does nothing
            Assert.NotEqual(SettingKind.Unknown, s.Kind);
        });
    }

    /// <summary>
    /// A number without bounds is a slider with no ends, and a choice without its options is
    /// an empty menu. Both would be a control that cannot be used.
    /// </summary>
    [Fact]
    public void EveryKindCarriesWhatItsControlNeeds()
    {
        foreach (var setting in Load())
        {
            switch (setting.Kind)
            {
                case SettingKind.Number:
                    Assert.NotNull(setting.Min);
                    Assert.NotNull(setting.Max);
                    Assert.True(setting.Min < setting.Max, setting.Key);
                    Assert.NotNull(setting.Number);
                    break;
                case SettingKind.Choice:
                case SettingKind.List:
                    Assert.NotEmpty(setting.Allowed);
                    break;
                case SettingKind.Bool:
                    Assert.True(setting.Value is "true" or "false", setting.Value);
                    break;
            }
        }
    }

    [Fact]
    public void TheSettingsTheWindowsShellShowsAreStillThere()
    {
        var settings = Load();

        var yellow = Assert.Single(settings, s => s.Key == "limitYellowPct");
        Assert.Equal(SettingKind.Number, yellow.Kind);
        Assert.Equal(1, yellow.Min);
        Assert.Equal(100, yellow.Max);
        Assert.Equal(60, yellow.Number);

        var providers = Assert.Single(settings, s => s.Key == "providers");
        Assert.Equal(SettingKind.List, providers.Kind);
        Assert.Equal(["Claude", "Codex", "Ollama"], providers.Allowed);
        Assert.Equal(["Claude", "Codex", "Ollama"], providers.Selected);

        var channel = Assert.Single(settings, s => s.Key == "updateChannel");
        Assert.Equal(SettingKind.Choice, channel.Kind);
        Assert.Equal(["stable", "beta"], channel.Allowed);

        var alerts = Assert.Single(settings, s => s.Key == "alerts");
        Assert.Equal(SettingKind.Bool, alerts.Kind);
        Assert.True(alerts.IsOn);
    }

    /// <summary>
    /// The value a control writes back has to be one the engine will accept, and for a choice
    /// or a list that means one of the words it published.
    /// </summary>
    [Fact]
    public void TheCurrentValueIsAlwaysOneTheEngineOffers()
    {
        foreach (var setting in Load())
        {
            if (setting.Kind == SettingKind.Choice)
            {
                Assert.Contains(setting.Value, setting.Allowed);
            }
            if (setting.Kind == SettingKind.List)
            {
                Assert.All(setting.Selected, one => Assert.Contains(one, setting.Allowed));
            }
        }
    }
}
