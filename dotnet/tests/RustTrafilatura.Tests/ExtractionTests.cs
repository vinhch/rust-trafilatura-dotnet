using System.Text.Json;
using Xunit;

namespace RustTrafilatura.Tests;

public sealed class ExtractionTests
{
    [Fact]
    public void Extract_FromBytes_ReturnsTextAndMetadata()
    {
        var result = Extractor.ExtractFromBytes(Fixtures.Article, new ExtractionOptions { ResponseFields = ["contentText", "metadata"] });

        // Text, not markup: echoing the payload back would still contain "<article" and "<p>".
        Assert.Contains("research team", result.ContentText);
        Assert.DoesNotContain("<article", result.ContentText);
        Assert.DoesNotContain("<p>", result.ContentText);
        Assert.DoesNotContain("</", result.ContentText);
        Assert.Equal("Research results", result.Metadata!.Title);
        Assert.Null(result.ContentHtml);
    }

    [Fact]
    public void Version_Matches_CrateVersion()
    {
        using var version = JsonDocument.Parse(Extractor.AbiVersion);

        Assert.Equal(1, version.RootElement.GetProperty("abi").GetInt32());
        Assert.Equal("2.2.8", version.RootElement.GetProperty("crate").GetString());
    }

    /// <summary>
    /// The document reports the ABI and the schema this client was built for. `AbiVersion` is the
    /// library's own claim, and the first extraction is what compares those two numbers against the
    /// ones this assembly was compiled for; this test pins the claim they are compared with.
    /// </summary>
    [Fact]
    public void AbiVersion_Reports_The_Shipped_Abi()
    {
        using var version = JsonDocument.Parse(Extractor.AbiVersion);

        Assert.Equal(1, version.RootElement.GetProperty("abi").GetInt32());
        Assert.Equal(1, version.RootElement.GetProperty("schemaVersion").GetInt32());
    }
}
