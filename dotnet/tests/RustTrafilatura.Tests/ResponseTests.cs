using System.Globalization;
using System.Text.Json;
using Xunit;

namespace RustTrafilatura.Tests;

/// <summary>
/// The response read path: the result comes out of the bytes the native library returned, so these
/// tests compare it with that response rather than with another call, and drive the reader's remaining
/// branches with envelopes no build of this ABI emits.
/// </summary>
public sealed class ResponseTests
{
    /// <summary>Every response field, so a member the reader left at its default shows up.</summary>
    private static ExtractionOptions AllFields => new()
    {
        ResponseFields = ["contentText", "commentsText", "contentHtml", "commentsHtml", "metadata"],
    };

    /// <summary>
    /// The reader reproduces the response it was handed, member for member, for the pages whose
    /// response carries the two encodings a reader over text would not meet: a letter as more than one
    /// byte and a quotation mark as an escape. Both public entry points are compared with it, so both
    /// reach this reader.
    /// </summary>
    [Fact]
    public void Response_Is_Read_From_The_Native_Buffer()
    {
        var article = ReadAsACallerWould(Fixtures.Article, AllFields);
        AssertMatchesTheResponse(article.Response, Extractor.ExtractFromHtml(Fixtures.ArticleHtml, AllFields));

        // U+00E9 is two bytes in the response, so a reader that took one byte per character would
        // fail here rather than on a page whose response is ASCII.
        var nonAscii = ReadAsACallerWould(Fixtures.Windows1252, AllFields);
        Assert.Contains("Caf\u00E9", nonAscii.Result.ContentText);
        Assert.True(nonAscii.Response.AsSpan().IndexOf("Caf\u00E9"u8) >= 0);

        // The same page's quote arrives as `\"`, which only a reader that unescapes finds.
        var quoted = ReadAsACallerWould(Fixtures.Quoted, AllFields);
        Assert.Contains("said \"the caf\u00E9 stays open\"", quoted.Result.ContentText);
        Assert.True(quoted.Response.AsSpan().IndexOf("\\\""u8) >= 0);
        AssertMatchesTheResponse(quoted.Response, Extractor.ExtractFromHtml(Fixtures.QuotedHtml, AllFields));

        // A page URL is what fills `hostname`, so this response carries a value where every other
        // fixture leaves that member empty: an empty member reads the same whether it was read or
        // never looked at, which is what the assertion below and the comparison above rule out.
        var addressed = ReadAsACallerWould(
            Fixtures.Article,
            new ExtractionOptions { Url = "https://example.org/article", ResponseFields = ["metadata"] });
        Assert.Equal("example.org", addressed.Result.Metadata!.Hostname);

        // The rest of the pages, so the reader is exercised on the responses the other entry-point
        // tests already compare their results against, including the two shapes a request makes:
        // a field that was not asked for and a requested field that found nothing.
        ReadAsACallerWould(Fixtures.ArticleWithSoftHyphens, AllFields);
        ReadAsACallerWould(Fixtures.NoDate, AllFields);
        ReadAsACallerWould(Fixtures.Gzip, AllFields);
        ReadAsACallerWould(Fixtures.Article, null);
        ReadAsACallerWould(Fixtures.Article, new ExtractionOptions { ResponseFields = ["metadata"] });
        ReadAsACallerWould(Fixtures.Article, new ExtractionOptions { ResponseFields = ["contentHtml", "commentsHtml"] });
    }

    /// <summary>
    /// A member the response does not carry keeps the default the contract gives it: an absent content
    /// field is null while an absent metadata string is an empty string, and an absent array is the
    /// shared empty one. The envelope names no key at all, which no response does - the wrapper writes
    /// every key and leaves a field it was not asked for at JSON null - so this is the absent branch
    /// alone, which the null branch below does not reach.
    /// </summary>
    [Fact]
    public void Missing_Members_Keep_Their_Empty_Defaults()
    {
        var result = Response.Read(0, """{"metadata":{}}"""u8);

        Assert.Null(result.ContentText);
        Assert.Null(result.CommentsText);
        Assert.Null(result.ContentHtml);
        Assert.Null(result.CommentsHtml);

        var metadata = result.Metadata!;
        Assert.Equal(string.Empty, metadata.Title);
        Assert.Equal(string.Empty, metadata.Author);
        Assert.Equal(string.Empty, metadata.Url);
        Assert.Equal(string.Empty, metadata.Hostname);
        Assert.Equal(string.Empty, metadata.Description);
        Assert.Equal(string.Empty, metadata.Sitename);
        Assert.Null(metadata.Date);
        Assert.Equal(string.Empty, metadata.Id);
        Assert.Equal(string.Empty, metadata.Fingerprint);
        Assert.Equal(string.Empty, metadata.License);
        Assert.Equal(string.Empty, metadata.Language);
        Assert.Equal(string.Empty, metadata.Image);
        Assert.Equal(string.Empty, metadata.PageType);
        Assert.Same(Array.Empty<string>(), metadata.Categories);
        Assert.Same(Array.Empty<string>(), metadata.Tags);
    }

    /// <summary>
    /// The one distinction the wire contract turns on: a field that was not requested is JSON null and
    /// a requested field that found nothing is an empty string. A reader that collapsed either into
    /// the other would answer a different question.
    /// </summary>
    [Fact]
    public void Json_Null_And_Empty_String_Stay_Distinct()
    {
        var result = Response.Read(
            0,
            """{"contentText":null,"commentsText":"","contentHtml":"","metadata":null}"""u8);

        Assert.Null(result.ContentText);
        Assert.Equal(string.Empty, result.CommentsText);
        Assert.Equal(string.Empty, result.ContentHtml);
        Assert.Null(result.CommentsHtml);
        Assert.Null(result.Metadata);
    }

    /// <summary>
    /// A value whose JSON kind is not the one the contract names leaves its member at the default and
    /// disturbs no other member: the reader has to return to the level it was reading, which is what
    /// reading a nested value as the enclosing one would break.
    /// </summary>
    [Fact]
    public void Non_String_Values_Are_Skipped_Whole()
    {
        var result = Response.Read(
            0,
            """{"contentHtml":{"nested":"value"},"contentText":"kept","commentsText":7}"""u8);

        Assert.Null(result.ContentHtml);
        Assert.Equal("kept", result.ContentText);
        Assert.Null(result.CommentsText);
    }

    /// <summary>
    /// `metadata` is read inside its own object: its members never become the result's, and a
    /// `metadata` that is not an object is null rather than an error.
    /// </summary>
    [Fact]
    public void Metadata_Members_Are_Read_Inside_Their_Own_Object()
    {
        var nested = Response.Read(
            0,
            """{"metadata":{"title":"inside","nested":{"title":"deeper"}},"contentText":"after"}"""u8);

        Assert.Equal("inside", nested.Metadata!.Title);
        Assert.Equal("after", nested.ContentText);

        Assert.Null(Response.Read(0, """{"metadata":null,"contentText":"after"}"""u8).Metadata);
        Assert.Null(Response.Read(0, """{"metadata":[],"contentText":"after"}"""u8).Metadata);
        Assert.Equal("after", Response.Read(0, """{"metadata":[],"contentText":"after"}"""u8).ContentText);
    }

    /// <summary>
    /// An array member reads as the strings it holds, whatever else it holds, and every other shape
    /// reads as the shared empty array: the identity assertion is what keeps an empty array from
    /// building a list per call.
    /// </summary>
    [Fact]
    public void Categories_And_Tags_Read_As_Arrays_Or_Not_At_All()
    {
        var populated = Response.Read(0, """{"metadata":{"categories":["a","b"],"tags":["c"]}}"""u8).Metadata!;
        Assert.Equal(["a", "b"], populated.Categories);
        Assert.Equal(["c"], populated.Tags);

        var mixed = Response.Read(0, """{"metadata":{"categories":["a",null,"b",[1]]}}"""u8).Metadata!;
        Assert.Equal(["a", "b"], mixed.Categories);

        var scalar = Response.Read(0, """{"metadata":{"categories":"a","tags":7}}"""u8).Metadata!;
        Assert.Same(Array.Empty<string>(), scalar.Categories);
        Assert.Same(Array.Empty<string>(), scalar.Tags);
    }

    /// <summary>
    /// Every named member reads the value its own key carries. Each key holds a different value,
    /// because an empty member reads the same whether the reader read it or never looked: the
    /// contract's default is what an absent branch and a faithful one agree on, so a response whose
    /// value is not the default is the only kind that separates them.
    /// </summary>
    [Fact]
    public void Every_Metadata_Member_Reads_Its_Own_Key()
    {
        var result = Response.Read(
            0,
            """
            {"metadata":{"title":"title-value","author":"author-value","url":"url-value","hostname":"hostname-value","description":"description-value","sitename":"sitename-value","date":"2026-10-08T00:00:00Z","categories":["categories-1","categories-2"],"tags":["tags-1"],"id":"id-value","fingerprint":"fingerprint-value","license":"license-value","language":"language-value","image":"image-value","pageType":"pageType-value"}}
            """u8);

        var metadata = result.Metadata!;
        Assert.Equal("title-value", metadata.Title);
        Assert.Equal("author-value", metadata.Author);
        Assert.Equal("url-value", metadata.Url);
        Assert.Equal("hostname-value", metadata.Hostname);
        Assert.Equal("description-value", metadata.Description);
        Assert.Equal("sitename-value", metadata.Sitename);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero), metadata.Date);
        Assert.Equal(["categories-1", "categories-2"], metadata.Categories);
        Assert.Equal(["tags-1"], metadata.Tags);
        Assert.Equal("id-value", metadata.Id);
        Assert.Equal("fingerprint-value", metadata.Fingerprint);
        Assert.Equal("license-value", metadata.License);
        Assert.Equal("language-value", metadata.Language);
        Assert.Equal("image-value", metadata.Image);
        Assert.Equal("pageType-value", metadata.PageType);
    }

    /// <summary>
    /// Envelopes no build of this ABI emits, because the wrapper always writes an object: a root that
    /// is not one, a truncated document, content after the root object and no document at all. Each has
    /// to fail rather than read as a response whose every member was absent. The assertion is on the
    /// base type, as the failure mapping's own catch is, since the reader reports its own subclasses.
    /// </summary>
    [Fact]
    public void Malformed_Envelopes_Are_Rejected()
    {
        Assert.ThrowsAny<JsonException>(() => Response.Read(0, "[1]"u8));
        Assert.ThrowsAny<JsonException>(() => Response.Read(0, """{"contentText":"""u8));
        Assert.ThrowsAny<JsonException>(() => Response.Read(0, """{"contentText":"x"} trailing"""u8));
        Assert.ThrowsAny<JsonException>(() => Response.Read(0, ReadOnlySpan<byte>.Empty));
    }

    /// <summary>
    /// A reader that throws still releases the response buffer, because the free runs in a `finally`
    /// around the read. A leak is not observable from managed code - the ABI's allocation counter is
    /// behind the crate's `test-hooks` feature and is not exported - so what is pinned is the sequence
    /// a broken lifetime would break: a failing call, then a call that still returns the whole page.
    /// </summary>
    [Fact]
    public void A_Failing_Extraction_Still_Frees_The_Response_Buffer()
    {
        // The URL is one the core rejects, so this is an extraction error whose envelope the reader
        // has to parse before it throws.
        var rejected = new ExtractionOptions { Url = "ftp://example.org/a" };

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var failure = Assert.Throws<TrafilaturaException>(
                () => Extractor.ExtractFromBytes(Fixtures.Article, rejected));
            Assert.Equal(TrafilaturaErrorKind.InvalidUrl, failure.Kind);

            var result = Extractor.ExtractFromBytes(Fixtures.Article, AllFields);
            Assert.Equal("Research results", result.Metadata!.Title);
            Assert.NotEmpty(result.ContentText!);
        }
    }

    /// <summary>
    /// One call as a caller makes it, plus the same call's response read back through the ABI, so both
    /// the public result and a direct read can be compared with the response they came from.
    /// </summary>
    private static (byte[] Response, ExtractionResult Result) ReadAsACallerWould(byte[] page, ExtractionOptions? options)
    {
        var capture = new RawResponse.Capture();
        var code = Interop.Extract(Request.Header(options), page, capture.Read);
        Assert.Equal(0, code);

        var result = Extractor.ExtractFromBytes(page, options);

        AssertMatchesTheResponse(capture.Bytes, result);
        AssertMatchesTheResponse(capture.Bytes, Response.Read(code, capture.Bytes));
        return (capture.Bytes, result);
    }

    /// <summary>
    /// Compares a result with the response it came from, member for member and through a different
    /// parser: every value is read from the response with <see cref="JsonDocument"/> and compared with
    /// the reader's own, so a member the reader missed, invented, or mapped to the wrong key or kind
    /// fails here whatever the mapping is.
    /// </summary>
    private static void AssertMatchesTheResponse(byte[] response, ExtractionResult result)
    {
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;

        Assert.Equal(ReadText(root, "contentText"), result.ContentText);
        Assert.Equal(ReadText(root, "commentsText"), result.CommentsText);
        Assert.Equal(ReadText(root, "contentHtml"), result.ContentHtml);
        Assert.Equal(ReadText(root, "commentsHtml"), result.CommentsHtml);

        if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
        {
            Assert.Null(result.Metadata);
            return;
        }

        var actual = result.Metadata!;
        Assert.Equal(ReadText(metadata, "title") ?? string.Empty, actual.Title);
        Assert.Equal(ReadText(metadata, "author") ?? string.Empty, actual.Author);
        Assert.Equal(ReadText(metadata, "url") ?? string.Empty, actual.Url);
        Assert.Equal(ReadText(metadata, "hostname") ?? string.Empty, actual.Hostname);
        Assert.Equal(ReadText(metadata, "description") ?? string.Empty, actual.Description);
        Assert.Equal(ReadText(metadata, "sitename") ?? string.Empty, actual.Sitename);
        Assert.Equal(
            ReadText(metadata, "date") is { } date
                ? DateTimeOffset.Parse(date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                : null,
            actual.Date);
        Assert.Equal(ReadTexts(metadata, "categories"), actual.Categories);
        Assert.Equal(ReadTexts(metadata, "tags"), actual.Tags);
        Assert.Equal(ReadText(metadata, "id") ?? string.Empty, actual.Id);
        Assert.Equal(ReadText(metadata, "fingerprint") ?? string.Empty, actual.Fingerprint);
        Assert.Equal(ReadText(metadata, "license") ?? string.Empty, actual.License);
        Assert.Equal(ReadText(metadata, "language") ?? string.Empty, actual.Language);
        Assert.Equal(ReadText(metadata, "image") ?? string.Empty, actual.Image);
        Assert.Equal(ReadText(metadata, "pageType") ?? string.Empty, actual.PageType);
    }

    private static string? ReadText(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> ReadTexts(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray()
            : Array.Empty<string>();
}
