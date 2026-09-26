using System.Text;
using System.Text.Json.Nodes;
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class OllamaLocalityTests
{
    [Fact]
    public void CloudModelsAreDetectedByTag()
    {
        Assert.True(OllamaLocality.IsCloud("deepseek-v4-flash:cloud"));
        Assert.True(OllamaLocality.IsCloud("gpt-oss:120b-cloud"));
        Assert.False(OllamaLocality.IsCloud("qwen3-coder:30b"));
        Assert.False(OllamaLocality.IsCloud("cloudqwen:7b"));
    }

    [Fact]
    public void CloudModelsGetTheGlyphAndLocalOnesDoNot()
    {
        Assert.Equal("☁ x:cloud", OllamaLocality.Marked("x:cloud"));
        Assert.Equal("qwen3-coder:30b", OllamaLocality.Marked("qwen3-coder:30b"));
    }
}

public sealed class ServiceStatusTests
{
    [Fact]
    public void StatuspageParse()
    {
        var json = "{\"page\":{\"name\":\"Claude\"},\"status\":{\"indicator\":\"minor\",\"description\":\"Degraded\"}}";
        var r = ServiceStatus.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal("minor", r?.Indicator);
        Assert.Equal("Degraded", r?.Description);
        Assert.Equal(false, r?.IsOperational);
        Assert.Equal("minor incident reported", r?.Phrase);
    }

    [Fact]
    public void GarbageParsesToNil()
    {
        Assert.Null(ServiceStatus.Parse(Encoding.UTF8.GetBytes("not json")));
        Assert.Null(ServiceStatus.Parse(Encoding.UTF8.GetBytes("{\"status\":{}}")));
    }
}

public sealed class OllamaParseTests
{
    [Fact]
    public void ParsesTagsWithDetails()
    {
        var json = new JsonObject
        {
            ["models"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "qwen3-coder:30b", ["size"] = 18_500_000_000L,
                    ["modified_at"] = "2026-08-01T10:00:00Z",
                    ["details"] = new JsonObject { ["parameter_size"] = "30.5B", ["quantization_level"] = "Q4_K_M" },
                },
                new JsonObject { ["name"] = "llama3:8b", ["size"] = 4_700_000_000L }),
        };
        var models = OllamaParse.Models(json);
        Assert.Equal(new[] { "llama3:8b", "qwen3-coder:30b" }, models.Select(m => m.Name)); // sorted by name
        var qwen = models[1];
        Assert.Equal("30.5B", qwen.ParameterSize);
        Assert.Equal("Q4_K_M", qwen.Quantization);
        Assert.Equal("qwen3-coder", qwen.Family);
        Assert.Equal("30b", qwen.Tag);
        Assert.NotNull(qwen.ModifiedAt);
    }

    [Fact]
    public void AcceptsModelKeyInsteadOfName()
    {
        var models = OllamaParse.Models(new JsonObject
        {
            ["models"] = new JsonArray(new JsonObject { ["model"] = "mistral:7b", ["size"] = 4_000 }),
        });
        Assert.Equal("mistral:7b", models.FirstOrDefault()?.Name);
    }

    [Fact]
    public void SkipsEntriesWithoutAName()
    {
        Assert.Empty(OllamaParse.Models(new JsonObject { ["models"] = new JsonArray(new JsonObject { ["size"] = 1 }) }));
    }

    [Fact]
    public void MissingModelsKeyIsEmptyNotACrash()
    {
        Assert.Empty(OllamaParse.Models(new JsonObject()));
        Assert.Empty(OllamaParse.Running(new JsonObject()));
    }

    [Fact]
    public void ParsesRunningWithVramShare()
    {
        var json = new JsonObject
        {
            ["models"] = new JsonArray(new JsonObject
            {
                ["name"] = "qwen3-coder:30b", ["size"] = 20_000, ["size_vram"] = 15_000,
                ["expires_at"] = "2026-08-12T22:00:00Z",
            }),
        };
        var running = OllamaParse.Running(json);
        Assert.Single(running);
        Assert.Equal(0.75, running[0].VramShare, 3);
        Assert.NotNull(running[0].ExpiresAt);
    }

    [Fact]
    public void FullyOnCPUReportsZeroVramShare()
    {
        var running = OllamaParse.Running(new JsonObject
        {
            ["models"] = new JsonArray(new JsonObject { ["name"] = "m", ["size"] = 100, ["size_vram"] = 0 }),
        });
        Assert.Equal(0.0, running[0].VramShare);
    }

    [Fact]
    public void ZeroSizeDoesNotDivideByZero()
    {
        var running = OllamaParse.Running(new JsonObject
        {
            ["models"] = new JsonArray(new JsonObject { ["name"] = "m", ["size"] = 0, ["size_vram"] = 0 }),
        });
        Assert.Equal(0.0, running[0].VramShare);
    }

    [Fact]
    public void ByteFormatting()
    {
        Assert.Equal("18.5 GB", Ollama.FmtBytes(18_500_000_000));
        Assert.Equal("5 MB", Ollama.FmtBytes(4_700_000));
        Assert.Equal("512 B", Ollama.FmtBytes(512));
    }
}

public sealed class LimitIdentityTests
{
    [Fact]
    public void IdIncludesProviderSoProvidersDoNotCollide()
    {
        var claude = new LimitWindow("Claude", "seven_day", 4, null);
        var codex = new LimitWindow("Codex", "seven_day", 17, null);
        Assert.NotEqual(claude.Id, codex.Id); // a shared key must not produce a shared identity
        Assert.Equal("Claude|seven_day", claude.Id);
    }
}
