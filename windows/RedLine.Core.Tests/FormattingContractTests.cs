using System.Text.Json;
using RedLine.Core;

namespace RedLine.Core.Tests;

/// <summary>
/// The same table the Swift suite asserts against. If these two ever disagree, one platform
/// is showing a person a different number for the same usage, which is the failure this whole
/// arrangement exists to prevent.
/// </summary>
public class FormattingContractTests
{
    private sealed record IntCase(long Value, string Expect);
    private sealed record DoubleCase(double Value, string Expect);
    private sealed record Table(IntCase[] Tokens, DoubleCase[] Cost);

    private static Table Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "formatting.json");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var table = JsonSerializer.Deserialize<Table>(File.ReadAllText(path), options);
        Assert.NotNull(table);
        return table;
    }

    [Fact]
    public void TokenFormattingMatchesTheSharedTable()
    {
        foreach (var row in Load().Tokens)
        {
            Assert.Equal(row.Expect, Formatting.Tokens(row.Value));
        }
    }

    [Fact]
    public void CostFormattingMatchesTheSharedTable()
    {
        foreach (var row in Load().Cost)
        {
            Assert.Equal(row.Expect, Formatting.Cost(row.Value));
        }
    }

    /// <summary>
    /// A German or French locale groups and separates differently. The engine does not, so
    /// neither may this, or the same usage reads differently depending on the machine.
    /// </summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("hi-IN")]
    public void FormattingDoesNotFollowTheMachinesLocale(string culture)
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo(culture);
            Assert.Equal("$1,234,567.89", Formatting.Cost(1234567.89));
            Assert.Equal("1.1K", Formatting.Tokens(1100));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
