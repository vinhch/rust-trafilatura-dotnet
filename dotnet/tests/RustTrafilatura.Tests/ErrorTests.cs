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
    /// <summary>
    /// A URL the core rejects as its own input. The core validates it before extraction, so the
    /// failure is an extraction error (code 1) rather than a rejected request.
    /// </summary>
    [Fact]
    public void InvalidUrl_Throws_TrafilaturaException()
    {
        var exception = Assert.Throws<TrafilaturaException>(() =>
            Extractor.ExtractFromBytes(Fixtures.Article, new ExtractionOptions { Url = "ftp://example.org/a" }));

        Assert.Equal(TrafilaturaErrorKind.InvalidUrl, exception.Kind);

        // A payload-carrying kind brings its context along, and it names the caller's own URL.
        using var context = JsonDocument.Parse(exception.ContextJson!);
        Assert.Equal("ftp://example.org/a", context.RootElement.GetProperty("url").GetString());
        Assert.Contains("ftp://example.org/a", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A zero-length payload, which is a valid request whose extraction the core rejects. It reaches
    /// this error with the core's defaults, where <c>minOutputSize</c> is 1: a parseable
    /// one-paragraph page is kept, so the counts in the context are the payload's.
    /// </summary>
    [Fact]
    public void InsufficientContent_Throws_TrafilaturaException()
    {
        var exception = Assert.Throws<TrafilaturaException>(() => Extractor.ExtractFromBytes(ReadOnlyMemory<byte>.Empty));

        Assert.Equal(TrafilaturaErrorKind.OutputTooShort, exception.Kind);
        Assert.Equal("text and comments are not long enough: 0 0", exception.Message);

        using var context = JsonDocument.Parse(exception.ContextJson!);
        Assert.Equal(0, context.RootElement.GetProperty("content").GetInt32());
        Assert.Equal(0, context.RootElement.GetProperty("comments").GetInt32());
    }

    /// <summary>
    /// A header that is not a JSON object is rejected before the payload is read, which is code 2 and
    /// its own kind. The managed surface never builds such a header, so the test reaches the ABI.
    /// </summary>
    [Fact]
    public void MalformedJson_Throws_InvalidRequest()
    {
        var (code, response) = Interop.Extract("{"u8, Fixtures.Article, RawResponse.Read);

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

        var (code, response) = Interop.Extract(header, [], RawResponse.Read);

        Assert.Equal(1, code);
        using var document = JsonDocument.Parse(response);
        Assert.Equal(
            "OutputTooShort",
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
        Assert.Equal(TrafilaturaErrorKind.Unknown, Failures.Read(1, """{"schemaVersion":1}""").Kind);
        Assert.Equal(TrafilaturaErrorKind.Unknown, Failures.Read(1, "not json").Kind);

        // The parse failure itself is kept, so a library that answers with something unexpected can
        // still be diagnosed from the exception the caller caught. Parse reports JsonReaderException,
        // whose base is the JsonException the mapping catches.
        Assert.IsAssignableFrom<JsonException>(Failures.Read(1, "not json").InnerException);
    }

    /// <summary>
    /// A budget expiry carries its own kind and its context names the budget that expired, so a
    /// caller can treat it as its timeout and not as an extraction failure.
    /// </summary>
    [Fact]
    public void TimeBudgetExceeded_Maps_To_Own_Kind()
    {
        var exception = Failures.Read(
            1,
            """{"schemaVersion":1,"error":{"kind":"TimeBudgetExceeded","message":"extraction exceeded the 50 ms time budget","context":{"budgetMs":50}}}""");

        Assert.Equal(TrafilaturaErrorKind.TimeBudgetExceeded, exception.Kind);
        Assert.Contains("budgetMs", exception.ContextJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// A full detached-worker pool carries its own kind with the limit in its context: it reports
    /// that the process is saturated, which a caller must answer with backoff rather than with a
    /// per-page timeout.
    /// </summary>
    [Fact]
    public void WorkerCapExceeded_Maps_To_Own_Kind()
    {
        var exception = Failures.Read(
            1,
            """{"schemaVersion":1,"error":{"kind":"WorkerCapExceeded","message":"16 detached workers are already live","context":{"limit":16}}}""");

        Assert.Equal(TrafilaturaErrorKind.WorkerCapExceeded, exception.Kind);
        Assert.Contains("limit", exception.ContextJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// The enum is the wire contract's kind table: one member per kind in section 6 plus
    /// <see cref="TrafilaturaErrorKind.Unknown"/>. A missing or renamed member would otherwise fall
    /// through the kind switch to <c>Unknown</c> in production, turning a named failure into an
    /// anonymous one, which no other test would notice.
    /// </summary>
    [Fact]
    public void Error_Kind_Enum_Matches_The_Contract()
    {
        string[] contract =
        [
            "Io",
            "UnsupportedCharset",
            "CharsetNotDetected",
            "MissingDocument",
            "HtmlLanguage",
            "RequiredTitle",
            "RequiredUrl",
            "RequiredDate",
            "OutputTreeTooLong",
            "OutputTooShort",
            "DuplicateBody",
            "WrongLanguage",
            "InvalidUrl",
            "InvalidRequest",
            "TimeBudgetExceeded",
            "WorkerCapExceeded",
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
    /// for field, with nothing aborting the process. The options instance is shared, which is what
    /// <see cref="Extractor.ExtractMany"/> does with it too.
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
        var expected = Extractor.ExtractFromBytes(Fixtures.Article, options);

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
