using System.Buffers;
using System.Runtime.InteropServices;
using Xunit;

namespace RustTrafilatura.Tests;

/// <summary>
/// The single-page byte entry points: the memory one takes anything memory-backed, the span one takes
/// everything else, so an array, a slice of one, a pooled rent, a `stackalloc` buffer and unmanaged
/// memory all reach the same pipeline. A <see cref="byte"/> array still arrives through the implicit
/// conversion, which is what keeps an existing call site compiling, and a null array converts to an
/// empty one instead of being rejected here.
/// </summary>
public sealed class BytesTests
{
    [Fact]
    public unsafe void Bytes_Entry_Points_Agree()
    {
        // The pooled rent is not decoration: this page is several times ArrayPool's default bucket, so
        // the rent is a real buffer. The slice starts at 16 rather than at 0, so an entry point that
        // ignored its own offset could not land on the page by accident.
        var sliceSource = new byte[Fixtures.Article.Length + 32];
        Fixtures.Article.CopyTo(sliceSource, 16);
        var pooled = ArrayPool<byte>.Shared.Rent(Fixtures.Article.Length);
        Fixtures.Article.CopyTo(pooled, 0);
        var buffer = Allocate(Fixtures.Article);
        Span<byte> stacked = stackalloc byte[Fixtures.Article.Length];
        Fixtures.Article.CopyTo(stacked);
        try
        {
            var expected = AssertAllFields(Extractor.ExtractFromBytes(Fixtures.Article, AllFields));

            AssertSame(expected, Extractor.ExtractFromBytes(sliceSource.AsMemory(16, Fixtures.Article.Length), AllFields));
            AssertSame(expected, Extractor.ExtractFromBytes(pooled.AsMemory(0, Fixtures.Article.Length), AllFields));
            AssertSame(expected, Extractor.ExtractFromBytes(Fixtures.Article.AsMemory(), AllFields));

            // The span entry point is the only one that reaches a buffer no memory view can express,
            // and for the memory-backed ones it is the same call one `.Span` away.
            AssertSame(expected, Extractor.ExtractFromSpan(Fixtures.Article, AllFields));
            AssertSame(expected, Extractor.ExtractFromSpan(sliceSource.AsSpan(16, Fixtures.Article.Length), AllFields));
            AssertSame(expected, Extractor.ExtractFromSpan(pooled.AsSpan(0, Fixtures.Article.Length), AllFields));
            AssertSame(expected, Extractor.ExtractFromSpan(stacked, AllFields));
            AssertSame(expected, Extractor.ExtractFromSpan(
                new ReadOnlySpan<byte>((byte*)buffer.ToPointer(), Fixtures.Article.Length),
                AllFields));

            // The string overload is the third path into the same pipeline, and the same text has to
            // come out of it field for field.
            AssertSame(expected, Extractor.ExtractFromHtml(Fixtures.ArticleHtml, AllFields));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            ArrayPool<byte>.Shared.Return(pooled);
        }
    }

    /// <summary>
    /// The view's length is the caller's, not the buffer's capacity: a shorter view of the same bytes
    /// cannot produce the full page's result. Both paths a capacity-ignoring implementation would get
    /// wrong are covered: a rent whose usable buffer is larger than the slice handed to the call, and
    /// an unmanaged buffer that holds the whole page while only its first part is declared.
    /// </summary>
    [Fact]
    public unsafe void A_Shorter_View_Does_Not_Produce_The_Full_Page()
    {
        var full = Extractor.ExtractFromBytes(Fixtures.Article, AllFields);

        // Only the first paragraph is inside this length, and the last paragraph's closing sentence is
        // what a view that read the whole buffer would pick up: it is the marker that separates the
        // two outcomes, because a run that stops at this length cannot have seen it.
        const int Truncated = 600;
        const string OnlyPastTheLength = "checkable by anyone outside the room";

        var pooled = ArrayPool<byte>.Shared.Rent(Fixtures.Article.Length);
        Fixtures.Article.CopyTo(pooled, 0);
        var buffer = Allocate(Fixtures.Article);
        try
        {
            var managed = Extractor.ExtractFromBytes(pooled.AsMemory(0, Truncated), AllFields);
            var unmanaged = Extractor.ExtractFromSpan(
                new ReadOnlySpan<byte>((byte*)buffer.ToPointer(), Truncated),
                AllFields);

            // The full page carries the marker, so the assertion below is about the length and not
            // about the fixture.
            Assert.Contains(OnlyPastTheLength, full.ContentText);
            Assert.DoesNotContain(OnlyPastTheLength, managed.ContentText);
            Assert.DoesNotContain(OnlyPastTheLength, unmanaged.ContentText);
            Assert.NotEqual(full.ContentText, managed.ContentText);

            // The two paths agree on the truncated page, and the same buffer still yields the whole
            // page when the caller declares its whole length.
            Assert.Equal(managed.ContentText, unmanaged.ContentText);
            var whole = Extractor.ExtractFromSpan(
                new ReadOnlySpan<byte>((byte*)buffer.ToPointer(), Fixtures.Article.Length),
                AllFields);
            Assert.Contains(OnlyPastTheLength, whole.ContentText);
            Assert.Equal(full.ContentText, whole.ContentText);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            ArrayPool<byte>.Shared.Return(pooled);
        }
    }

    /// <summary>The article's bytes in unmanaged memory, which the caller frees.</summary>
    private static nint Allocate(byte[] bytes)
    {
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, buffer, bytes.Length);
        return buffer;
    }

    /// <summary>
    /// The shapes of "no bytes at all" that the byte entry points accept: a default memory, a null
    /// array, which converts to one, and a default span. None is an argument error any more; all are
    /// empty input that the core itself rejects.
    /// </summary>
    [Fact]
    public void Null_Array_And_Empty_Buffer_Reach_The_Core()
    {
        byte[]? nullArray = null;

        var fromNull = Assert.Throws<TrafilaturaException>(() => Extractor.ExtractFromBytes(nullArray!));
        var fromDefault = Assert.Throws<TrafilaturaException>(() => Extractor.ExtractFromBytes(default(ReadOnlyMemory<byte>)));
        var fromEmpty = Assert.Throws<TrafilaturaException>(() => Extractor.ExtractFromBytes(Array.Empty<byte>()));
        var fromEmptySpan = Assert.Throws<TrafilaturaException>(() => Extractor.ExtractFromSpan(default(ReadOnlySpan<byte>)));

        Assert.Equal(TrafilaturaErrorKind.OutputTooShort, fromNull.Kind);
        Assert.Equal(TrafilaturaErrorKind.OutputTooShort, fromDefault.Kind);
        Assert.Equal(TrafilaturaErrorKind.OutputTooShort, fromEmptySpan.Kind);
        Assert.Equal(fromEmpty.Kind, fromNull.Kind);
        Assert.Equal(fromEmpty.Message, fromNull.Message);
        Assert.Equal(fromEmpty.Message, fromEmptySpan.Message);
    }

    /// <summary>Every response field is requested, so a member the reader left at its default shows up.</summary>
    private static ExtractionOptions AllFields => new()
    {
        ResponseFields = ["contentText", "commentsText", "contentHtml", "commentsHtml", "metadata"],
    };

    /// <summary>
    /// Field-for-field equality of the whole result, including every metadata member. The source is
    /// checked for content first, so a comparison that agreed because every source read back empty
    /// cannot pass.
    /// </summary>
    private static ExtractionResult AssertAllFields(ExtractionResult result)
    {
        Assert.Equal("Research results", result.Metadata!.Title);
        Assert.NotEmpty(result.ContentText!);
        Assert.NotNull(result.ContentHtml);
        return result;
    }

    private static void AssertSame(ExtractionResult expected, ExtractionResult actual)
    {
        Assert.Equal(expected.ContentText, actual.ContentText);
        Assert.Equal(expected.CommentsText, actual.CommentsText);
        Assert.Equal(expected.ContentHtml, actual.ContentHtml);
        Assert.Equal(expected.CommentsHtml, actual.CommentsHtml);
        Assert.Equal(expected.Metadata!.Title, actual.Metadata!.Title);
        Assert.Equal(expected.Metadata.Author, actual.Metadata.Author);
        Assert.Equal(expected.Metadata.Url, actual.Metadata.Url);
        Assert.Equal(expected.Metadata.Hostname, actual.Metadata.Hostname);
        Assert.Equal(expected.Metadata.Description, actual.Metadata.Description);
        Assert.Equal(expected.Metadata.Sitename, actual.Metadata.Sitename);
        Assert.Equal(expected.Metadata.Date, actual.Metadata.Date);
        Assert.Equal(expected.Metadata.Categories, actual.Metadata.Categories);
        Assert.Equal(expected.Metadata.Tags, actual.Metadata.Tags);
        Assert.Equal(expected.Metadata.Id, actual.Metadata.Id);
        Assert.Equal(expected.Metadata.Fingerprint, actual.Metadata.Fingerprint);
        Assert.Equal(expected.Metadata.License, actual.Metadata.License);
        Assert.Equal(expected.Metadata.Language, actual.Metadata.Language);
        Assert.Equal(expected.Metadata.Image, actual.Metadata.Image);
        Assert.Equal(expected.Metadata.PageType, actual.Metadata.PageType);
    }
}
