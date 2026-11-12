using System.Text;
using System.Text.Json;
using Xunit;

namespace RustTrafilatura.Tests;

/// <summary>
/// The failure half of the wire contract: which return code becomes which exception, what that
/// exception carries, and the two failure modes the wrapper keeps apart.
/// </summary>
public sealed class ErrorTests
{
    /// <summary>A `Url` that is not an absolute URL is passed through: without `IncludeImages` the core does not use it to join anything.</summary>
    [Fact]
    public void Non_Url_Value_Extracts()
    {
        var result = Extractor.ExtractFromHtml(Fixtures.ArticleHtml, new ExtractionOptions { Url = "not a url" }, TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrEmpty(result.ContentText));
    }

    /// <summary>
    /// A zero-length payload, which is a valid request whose extraction the core rejects as an empty
    /// tree. The rejection carries no context.
    /// </summary>
    [Fact]
    public void EmptyPayload_Throws_MissingDocument()
    {
        var exception = Assert.Throws<TrafilaturaException>(() => Extractor.ExtractFromBytes(ReadOnlyMemory<byte>.Empty, null, TestContext.Current.CancellationToken));

        Assert.Equal(TrafilaturaErrorKind.MissingDocument, exception.Kind);
        Assert.Equal("page rejected: empty tree", exception.Message);
        Assert.Null(exception.ContextJson);
    }

    /// <summary>
    /// A page without the date and the URL that <c>HasEssentialMetadata</c> asks for is rejected, and the
    /// context names the missing fields in the core's check order.
    /// </summary>
    [Fact]
    public void Missing_Metadata_Lists_The_Missing_Fields_In_Order()
    {
        var exception = Assert.Throws<TrafilaturaException>(() => Extractor.ExtractFromHtml(
            Fixtures.NoDateHtml,
            new ExtractionOptions { HasEssentialMetadata = true },
            TestContext.Current.CancellationToken));

        Assert.Equal(TrafilaturaErrorKind.MissingMetadata, exception.Kind);
        Assert.Equal("""{"missing":["date","url"]}""", exception.ContextJson);
    }

    /// <summary>
    /// A minimum text size the page does not reach is rejected, and the context carries both counts the
    /// core measured.
    /// </summary>
    [Fact]
    public void Too_Short_Carries_Text_And_Comment_Counts()
    {
        var exception = Assert.Throws<TrafilaturaException>(() => Extractor.ExtractFromBytes(
            Fixtures.Article,
            new ExtractionOptions { Config = new ExtractionConfig { MinOutputSize = 10000 } },
            TestContext.Current.CancellationToken));

        Assert.Equal(TrafilaturaErrorKind.OutputTooShort, exception.Kind);
        using var context = JsonDocument.Parse(exception.ContextJson!);
        Assert.True(context.RootElement.GetProperty("textChars").GetInt64() > 0);
        Assert.Equal(0, context.RootElement.GetProperty("commentsChars").GetInt64());
    }

    /// <summary>
    /// A child limit the body exceeds is rejected, and the context names the limit and the child count.
    /// </summary>
    [Fact]
    public void Tree_Limit_Names_The_Limit_And_Children()
    {
        var exception = Assert.Throws<TrafilaturaException>(() => Extractor.ExtractFromBytes(
            Fixtures.Article,
            new ExtractionOptions { MaxTreeSize = 1 },
            TestContext.Current.CancellationToken));

        Assert.Equal(TrafilaturaErrorKind.OutputTreeTooLong, exception.Kind);
        using var context = JsonDocument.Parse(exception.ContextJson!);
        Assert.Equal("maxTreeSize", context.RootElement.GetProperty("limit").GetString());
        Assert.True(context.RootElement.GetProperty("children").GetInt64() > 1);
    }

    /// <summary>
    /// A page whose locale names another language than the target is rejected as an HTML language
    /// mismatch.
    /// </summary>
    [Fact]
    public void Html_Lang_Mismatch_Is_Html_Language()
    {
        var exception = Assert.Throws<TrafilaturaException>(() => Extractor.ExtractFromHtml(
            Fixtures.GermanLocaleHtml,
            new ExtractionOptions { TargetLanguage = "en" },
            TestContext.Current.CancellationToken));

        Assert.Equal(TrafilaturaErrorKind.HtmlLanguage, exception.Kind);
    }

    /// <summary>
    /// An image source the core cannot join to the page URL is rejected as an invalid value, not extracted
    /// without the image.
    /// </summary>
    [Fact]
    public void Unparseable_Image_Url_Is_Invalid_Value()
    {
        var exception = Assert.Throws<TrafilaturaException>(() => Extractor.ExtractFromHtml(
            Fixtures.BrokenImageHtml,
            new ExtractionOptions { IncludeImages = true, Url = "https://example.org/" },
            TestContext.Current.CancellationToken));

        Assert.Equal(TrafilaturaErrorKind.InvalidValue, exception.Kind);
    }

    /// <summary>
    /// A header that is not a JSON object is rejected before the payload is read, which is code 2 and
    /// its own kind. The managed surface never builds such a header, so the test reaches the ABI.
    /// </summary>
    [Fact]
    public void MalformedJson_Throws_InvalidRequest()
    {
        var (code, response) = Interop.Extract("{"u8, Fixtures.Article, RawResponse.Read, TestContext.Current.CancellationToken);

        Assert.Equal(2, code);
        using var document = JsonDocument.Parse(response);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("InvalidRequest", error.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrEmpty(error.GetProperty("message").GetString()));
        Assert.Contains("reason", error.GetProperty("context").GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The other side of that distinction: the header this client sends with a zero-length payload is
    /// a valid request, so the core's own error comes back as code 1 instead of the code 2 a rejected
    /// request returns.
    /// </summary>
    [Fact]
    public void EmptyBytes_Is_Not_InvalidRequest()
    {
        var header = Encoding.UTF8.GetBytes(Request.HeaderJson(null).GetRawText());

        var (code, response) = Interop.Extract(header, [], RawResponse.Read, TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        using var document = JsonDocument.Parse(response);
        Assert.Equal(
            "MissingDocument",
            document.RootElement.GetProperty("error").GetProperty("kind").GetString());
    }

    /// <summary>
    /// A kind this client version does not recognise reads back as <see cref="TrafilaturaErrorKind.Unknown"/>
    /// with the message and context the library sent, because a newer native library must not make the
    /// managed side throw something its caller cannot catch as a <see cref="TrafilaturaException"/>.
    /// No build of this ABI emits an unknown kind, so the mapping is driven directly; the caller still
    /// receives the exception this returns, which is why it is the seam worth asserting on.
    /// </summary>
    [Fact]
    public void Unknown_Kind_Does_Not_Throw_On_Parse()
    {
        var unknown = Failures.Read(
            1,
            """{"error":{"kind":"FutureKind","message":"a kind this client predates","context":{"why":"because"}}}""");

        Assert.Equal(TrafilaturaErrorKind.Unknown, unknown.Kind);
        Assert.Equal("a kind this client predates", unknown.Message);
        Assert.Equal("""{"why":"because"}""", unknown.ContextJson);

        // The other two shapes a failure can take: an envelope without an error object, and text that
        // is not JSON at all. Both still produce an exception rather than a JSON error nobody asked for.
        Assert.Equal(TrafilaturaErrorKind.Unknown, Failures.Read(1, """{"schemaVersion":2}""").Kind);
        Assert.Equal(TrafilaturaErrorKind.Unknown, Failures.Read(1, "not json").Kind);

        // The parse failure itself is kept, so a library that answers with something unexpected can
        // still be diagnosed from the exception the caller caught. Parse reports JsonReaderException,
        // whose base is the JsonException the mapping catches.
        Assert.IsAssignableFrom<JsonException>(Failures.Read(1, "not json").InnerException);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"error\":{\"kind\":\"Panic\"}}]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void Non_Object_Response_Root_Is_Unknown(string response)
    {
        var exception = Failures.Read(1, response);

        Assert.Equal(TrafilaturaErrorKind.Unknown, exception.Kind);
        Assert.Contains("without an error object", exception.Message);
    }

    [Fact]
    public void Json_Text_On_A_Non_Object_Is_Null()
    {
        using var document = JsonDocument.Parse("""["crate"]""");

        Assert.Null(JsonMembers.Text(document.RootElement, "crate"));
    }

    /// <summary>
    /// The enum is the wire contract's kind table: one member per row of the README error table, that is
    /// the core kinds from <c>rust/src/contract.rs</c> <c>error_kind</c>, <c>InvalidRequest</c> and
    /// <c>Panic</c> from <c>rust/src/lib.rs</c>, and <see cref="TrafilaturaErrorKind.Unknown"/>. A missing or renamed member would otherwise fall
    /// through the kind switch to <c>Unknown</c> in production, turning a named failure into an
    /// anonymous one, which no other test would notice. <c>Cancelled</c> is the one wire kind with no
    /// member: a cancelled call throws <see cref="OperationCanceledException"/> instead, so the kind
    /// never reaches this enum.
    /// </summary>
    [Fact]
    public void Error_Kind_Enum_Matches_The_Contract()
    {
        string[] contract =
        [
            "MissingDocument",
            "HtmlLanguage",
            "MissingMetadata",
            "OutputTreeTooLong",
            "OutputTooShort",
            "InputTooLarge",
            "WrongLanguage",
            "DuplicateBody",
            "BlacklistedUrl",
            "InvalidValue",
            "InvalidRequest",
            "Panic",
            "Unknown",
        ];

        // Declaration order is not part of the contract; the members are.
        Assert.Equal(
            contract.Order(StringComparer.Ordinal),
            Enum.GetNames<TrafilaturaErrorKind>().Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// One thousand concurrent calls over the real ABI return what the sequential call returns, field
    /// for field, with nothing aborting the process. The options instance is shared across the calls.
    /// </summary>
    [Fact]
    public void Parallel_1000_Calls_AreConsistent()
    {
        const int callCount = 1000;
        var options = new ExtractionOptions
        {
            Url = "https://example.org/article",
            ResponseFields = ["contentText", "metadata"],
        };

        // Sequential first: it is the baseline the parallel calls are compared with, and it warms the
        // serializer's caches so the parallel section measures the ABI rather than a first use.
        var expected = Extractor.ExtractFromBytes(Fixtures.Article, options, TestContext.Current.CancellationToken);

        var actual = new ExtractionResult?[callCount];
        Parallel.For(
            0,
            callCount,
            index => actual[index] = Extractor.ExtractFromBytes(Fixtures.Article, options));

        for (var index = 0; index < callCount; index++)
        {
            var result = actual[index];
            Assert.NotNull(result);
            Assert.Equal(expected.ContentText, result.ContentText);
            Assert.Equal(expected.CommentsText, result.CommentsText);
            Assert.Equal(expected.Metadata!.Title, result.Metadata!.Title);
            Assert.Equal(expected.Metadata!.Date, result.Metadata!.Date);
        }
    }
}
