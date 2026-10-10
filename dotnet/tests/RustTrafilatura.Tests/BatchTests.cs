using Xunit;

namespace RustTrafilatura.Tests;

/// <summary>
/// <see cref="Extractor.ExtractMany"/>: many one-shot calls over the same ABI, bounded, and one
/// item per input whatever any single page did.
/// </summary>
public sealed class BatchTests
{
    /// <summary>A page with nothing to extract: the core rejects it rather than returning an empty result.</summary>
    private static byte[] Empty => [];

    [Fact]
    public void ExtractMany_Returns_Results_In_Input_Order()
    {
        // The pages do not all answer the same thing: the article carries a date, its soft-hyphen
        // variant carries the same one, an article without a date carries none, and the empty page is
        // rejected. An item swapped with its neighbour would land on a different answer.
        var batch = Extractor.ExtractMany([Fixtures.Article, Fixtures.NoDate, Empty, Fixtures.ArticleWithSoftHyphens]);

        Assert.Equal(4, batch.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero), batch[0].Result!.Metadata!.Date);
        Assert.Null(batch[1].Result!.Metadata!.Date);
        Assert.Equal(TrafilaturaErrorKind.OutputTooShort, batch[2].Error!.Kind);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero), batch[3].Result!.Metadata!.Date);
    }

    [Fact]
    public void ExtractMany_Captures_Per_Page_Failure()
    {
        var batch = Extractor.ExtractMany([Fixtures.Article, Empty, Fixtures.Article]);

        Assert.Equal(3, batch.Count);
        Assert.True(batch[0].Succeeded);
        Assert.True(batch[2].Succeeded);

        var failed = batch[1];
        Assert.False(failed.Succeeded);
        Assert.Null(failed.Result);
        Assert.Equal(TrafilaturaErrorKind.OutputTooShort, failed.Error!.Kind);
    }

    [Fact]
    public void ExtractMany_Matches_Sequential_Extraction()
    {
        var pages = new[] { Fixtures.Article, Empty, Fixtures.NoDate, Fixtures.ArticleWithSoftHyphens };

        var batch = Extractor.ExtractMany(pages);
        var sequential = pages.Select(ExtractOrError).ToList();

        Assert.Equal(sequential.Count, batch.Count);
        for (var index = 0; index < pages.Length; index++)
        {
            Assert.Equal(sequential[index].Result?.ContentText, batch[index].Result?.ContentText);
            Assert.Equal(sequential[index].Result?.Metadata?.Title, batch[index].Result?.Metadata?.Title);
            Assert.Equal(sequential[index].Result?.Metadata?.Date, batch[index].Result?.Metadata?.Date);
            Assert.Equal(sequential[index].Error, batch[index].Error?.Kind);

            // Exactly one of the two is set, never both and never neither.
            Assert.Equal(batch[index].Result is not null, batch[index].Succeeded);
        }
    }

    [Fact]
    public void ExtractMany_Rejects_Invalid_Arguments()
    {
        Assert.ThrowsAny<ArgumentException>(() => Extractor.ExtractMany(null!));
        Assert.ThrowsAny<ArgumentException>(() => Extractor.ExtractMany([]));
        Assert.ThrowsAny<ArgumentException>(() => Extractor.ExtractMany([Fixtures.Article, null!]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Extractor.ExtractMany([Fixtures.Article], maxDegreeOfParallelism: -1));
    }

    [Fact]
    public void ExtractMany_With_Degree_One_Still_Fills_Every_Item()
    {
        var batch = Extractor.ExtractMany([Fixtures.Article, Empty, Fixtures.NoDate], maxDegreeOfParallelism: 1);

        Assert.Equal(3, batch.Count);
        Assert.True(batch[0].Succeeded);
        Assert.Equal(TrafilaturaErrorKind.OutputTooShort, batch[1].Error!.Kind);
        Assert.True(batch[2].Succeeded);
    }

    /// <summary>The sequential counterpart of one batch item, for comparing the two paths.</summary>
    private static (ExtractionResult? Result, TrafilaturaErrorKind? Error) ExtractOrError(byte[] page)
    {
        try
        {
            return (Extractor.ExtractFromBytes(page), null);
        }
        catch (TrafilaturaException exception)
        {
            return (null, exception.Kind);
        }
    }
}
