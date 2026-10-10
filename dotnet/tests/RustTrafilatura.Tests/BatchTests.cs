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
        // Every index gets a marker no other index has: its own URL, and its own title, both of which
        // come back in that page's metadata. One page in the middle is empty, so the batch is not
        // uniform either. An item swapped with its neighbour would report the other page's marker.
        var pages = new[] { Marked(0), Marked(1), Empty, Marked(3) };
        var batch = Extractor.ExtractMany(
            pages,
            new ExtractionOptions { ResponseFields = ["metadata", "contentText"] });

        Assert.Equal(4, batch.Count);
        for (var index = 0; index < pages.Length; index++)
        {
            if (index == 2)
            {
                Assert.False(batch[index].Succeeded);
                Assert.Equal(TrafilaturaErrorKind.OutputTooShort, batch[index].Error!.Kind);
                continue;
            }

            Assert.Equal($"https://example.org/article?page={index}", batch[index].Result!.Metadata!.Url);
            Assert.Equal($"Page {index} marker", batch[index].Result!.Metadata!.Title);
        }
    }

    /// <summary>
    /// One page whose declared URL and title both name its index, so an item can be traced back to the
    /// input it came from. The page itself is the same article: only the markers differ.
    /// </summary>
    private static byte[] Marked(int index)
    {
        var html = System.Text.Encoding.UTF8.GetString(Fixtures.Article)
            .Replace("<title>Research results</title>", $"<title>Page {index} marker</title>", StringComparison.Ordinal)
            .Replace("<h1>Research results</h1>", $"<h1>Page {index} marker</h1>", StringComparison.Ordinal)
            .Replace(
                "<meta property=\"og:site_name\"",
                $"<link rel=\"canonical\" href=\"https://example.org/article?page={index}\"><meta property=\"og:site_name\"",
                StringComparison.Ordinal);

        return System.Text.Encoding.UTF8.GetBytes(html);
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

    /// <summary>
    /// The rule for what a batch throws when the extraction itself cannot run: one collected failure
    /// is unwrapped, so a single-page failure survives as itself, and anything else stays the loop's
    /// aggregate because no single failure can stand for it. Driven directly rather than through a
    /// native library that fails to load, because a normal run has that library in place and a test
    /// that skips itself there asserts nothing.
    /// </summary>
    [Fact]
    public void Batch_Failure_Is_Unwrapped_Only_When_It_Is_Alone()
    {
        var failure = new DllNotFoundException("test");

        Assert.Same(failure, Extractor.NormalizeBatchFailure(new AggregateException(failure)));

        var two = new AggregateException(new DllNotFoundException("test"), new DllNotFoundException("test"));
        Assert.Same(two, Extractor.NormalizeBatchFailure(two));

        var none = new AggregateException();
        Assert.Same(none, Extractor.NormalizeBatchFailure(none));
    }

    /// <summary>
    /// <see cref="ExtractionBatchItem.Succeeded"/> is the "exactly one of the two" rule, so a default
    /// instance -- neither a result nor an error -- does not report success.
    /// </summary>
    [Fact]
    public void Batch_Item_Is_Not_Successful_Without_A_Result()
    {
        Assert.False(new ExtractionBatchItem().Succeeded);
        Assert.False(new ExtractionBatchItem { Error = new TrafilaturaException(TrafilaturaErrorKind.Io, "test") }.Succeeded);
        Assert.True(new ExtractionBatchItem { Result = new ExtractionResult() }.Succeeded);
        Assert.False(
            new ExtractionBatchItem
            {
                Result = new ExtractionResult(),
                Error = new TrafilaturaException(TrafilaturaErrorKind.Io, "test"),
            }.Succeeded);
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
