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
    public void LibraryVersion_Matches_TheCrateAndCoreItWasBuiltFrom()
    {
        var version = Extractor.LibraryVersion;

        Assert.Equal(1, version.Abi);
        // Read from rust/Cargo.toml and rust/Cargo.lock rather than repeated here: the assertion is that
        // the loaded library was built from the sources in this checkout, and a copy of either number
        // would go stale on the next bump instead of failing.
        Assert.Equal(Repository.CrateVersion(), version.Crate);
        Assert.Equal(Repository.CoreVersion(), version.Core);
    }

    /// <summary>
    /// The document reports the ABI and the schema this client was built for. `LibraryVersion` is the
    /// library's own claim, and the first extraction is what compares those two numbers against the
    /// ones this assembly was compiled for; this test pins the claim they are compared with.
    /// </summary>
    [Fact]
    public void LibraryVersion_Reports_The_Shipped_Abi()
    {
        var version = Extractor.LibraryVersion;

        Assert.Equal(1, version.Abi);
        Assert.Equal(1, version.SchemaVersion);
    }
}
