using RedLine.Core;

namespace RedLine.Core.Tests;

public class SettingsTests
{
    private const string Catalog = """
        {"settings":[
          {"key":"alerts","kind":"bool","summary":"say something","value":"true"},
          {"key":"limitYellowPct","kind":"number","min":1,"max":100,
           "summary":"approaching","value":"60"},
          {"key":"limitWindows","kind":"choice","allowed":["all","session"],
           "summary":"which windows","value":"all"},
          {"key":"providers","kind":"list","allowed":["Claude","Codex","Ollama"],
           "summary":"which sources","value":"Claude,Ollama"}
        ]}
        """;

    [Fact]
    public void EveryKindSurvivesTheRoundTrip()
    {
        var settings = SettingsJson.ParseCatalog(Catalog);
        Assert.NotNull(settings);
        Assert.Equal(4, settings.Count);
        Assert.Equal(SettingKind.Bool, settings[0].Kind);
        Assert.True(settings[0].IsOn);
        Assert.Equal(SettingKind.Number, settings[1].Kind);
        Assert.Equal(1, settings[1].Min);
        Assert.Equal(100, settings[1].Max);
        Assert.Equal(60, settings[1].Number);
        Assert.Equal(SettingKind.Choice, settings[2].Kind);
        Assert.Equal(["all", "session"], settings[2].Allowed);
        Assert.Equal(SettingKind.List, settings[3].Kind);
        Assert.Equal(["Claude", "Ollama"], settings[3].Selected);
    }

    /// <summary>
    /// A newer engine may offer a kind this build has never seen. Dropping it would hide a
    /// setting with no sign that anything was missing, which is the worse failure.
    /// </summary>
    [Fact]
    public void AnUnfamiliarKindIsKeptRatherThanDropped()
    {
        var settings = SettingsJson.ParseCatalog(
            """{"settings":[{"key":"theme","kind":"colour","summary":"","value":"carbon"}]}""");
        Assert.NotNull(settings);
        var only = Assert.Single(settings);
        Assert.Equal(SettingKind.Unknown, only.Kind);
        Assert.Equal("carbon", only.Value);
    }

    [Fact]
    public void SomethingUnreadableIsNullRatherThanEmpty()
    {
        Assert.Null(SettingsJson.ParseCatalog("not json"));
        Assert.Null(SettingsJson.ParseOutcome("not json"));
        Assert.Null(SettingsJson.ParseOutcome("""{"outcome":"invented"}"""));
    }

    [Fact]
    public void AChangeReportsWhatItWasAndWhatItBecame()
    {
        var outcome = SettingsJson.ParseOutcome(
            """{"outcome":"changed","key":"limitYellowPct","from":"60","to":"70"}""");
        Assert.NotNull(outcome);
        Assert.Equal(SettingsOutcomeKind.Changed, outcome.Kind);
        Assert.Equal("60", outcome.Previous);
        Assert.Equal("70", outcome.Value);
        Assert.True(outcome.Accepted);
        Assert.Equal("", outcome.Message);
    }

    /// <summary>
    /// Setting a value to what it already is is not a failure, and a UI that reported one
    /// would be arguing with a person about a change they did not make.
    /// </summary>
    [Fact]
    public void AlreadyThatIsAccepted()
    {
        var outcome = SettingsJson.ParseOutcome(
            """{"outcome":"unchanged","key":"alerts","value":"true"}""");
        Assert.NotNull(outcome);
        Assert.Equal(SettingsOutcomeKind.Unchanged, outcome.Kind);
        Assert.True(outcome.Accepted);
        Assert.Equal("true", outcome.Value);
    }

    /// <summary>The reason comes from the engine, because the engine is the only thing that
    /// knows what it will accept.</summary>
    [Fact]
    public void ARefusalCarriesTheEnginesOwnWords()
    {
        var outcome = SettingsJson.ParseOutcome(
            """
            {"outcome":"rejected","key":"limitYellowPct","expected":"a number from 1 to 100"}
            """);
        Assert.NotNull(outcome);
        Assert.Equal(SettingsOutcomeKind.Rejected, outcome.Kind);
        Assert.False(outcome.Accepted);
        Assert.Equal("a number from 1 to 100", outcome.Expected);
        Assert.Equal("limitYellowPct needs a number from 1 to 100", outcome.Message);
    }

    [Fact]
    public void AKeyTheEngineDoesNotHaveIsSaidOutLoud()
    {
        var outcome = SettingsJson.ParseOutcome("""{"outcome":"unknownKey","key":"nosuch"}""");
        Assert.NotNull(outcome);
        Assert.Equal(SettingsOutcomeKind.UnknownKey, outcome.Kind);
        Assert.Equal("no such setting: nosuch", outcome.Message);
    }

    [Fact]
    public void AWriteThatFailedReportsWhy()
    {
        var outcome = SettingsJson.ParseOutcome(
            """{"outcome":"failed","message":"could not write /etc/config.json"}""");
        Assert.NotNull(outcome);
        Assert.Equal(SettingsOutcomeKind.Failed, outcome.Kind);
        Assert.False(outcome.Accepted);
        Assert.Contains("could not write", outcome.Message);
    }

    /// <summary>
    /// A whole number goes back as a whole number. "70.00" would be stored, read back as
    /// "70", and every comparison after that would say the value never took.
    /// </summary>
    [Theory]
    [InlineData(70, "70")]
    [InlineData(86400, "86400")]
    [InlineData(0.5, "0.5")]
    public void ANumberIsWrittenInTheShapeTheEngineWritesOne(double value, string expected)
    {
        Assert.Equal(expected, SettingDefinition.Format(value));
    }

    /// <summary>
    /// On a machine whose decimal separator is a comma, a value read or written in the local
    /// convention would reach the engine as a different number. Both directions are invariant.
    /// </summary>
    [Fact]
    public void ALocaleWithACommaForADecimalPointChangesNothing()
    {
        var was = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("0.5", SettingDefinition.Format(0.5));

            var settings = SettingsJson.ParseCatalog(
                """{"settings":[{"key":"x","kind":"number","summary":"","value":"0.5"}]}""");
            Assert.NotNull(settings);
            Assert.Equal(0.5, settings[0].Number);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = was;
        }
    }
}
