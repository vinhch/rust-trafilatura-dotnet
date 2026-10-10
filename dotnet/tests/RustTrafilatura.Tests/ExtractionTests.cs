using System.Text.Json;
using Xunit;

namespace RustTrafilatura.Tests;

public sealed class ExtractionTests
{
    [Fact]
    public void Extract_FromBytes_ReturnsTextAndMetadata()
    {
        var result = Extractor.ExtractFromBytes(Fixtures.Article, new ExtractionOptions { ResponseFields = ["contentText", "metadata"] });

        Assert.Contains("research team", result.ContentText);
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
}
