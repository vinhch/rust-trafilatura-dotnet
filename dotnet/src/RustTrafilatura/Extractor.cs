using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RustTrafilatura;

/// <summary>
/// Extracts text, comments, HTML and metadata from a page. One call per page, with no state to hold
/// or dispose between calls.
/// </summary>
public static class Extractor
{
    private static readonly JsonSerializerOptions HeaderJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly Lazy<string> VersionDocument = new(ReadVersionDocument);

    /// <summary>
    /// The version document of the native library, `{"abi":1,"schemaVersion":1,"crate":"2.2.8"}`:
    /// the ABI it implements, the JSON schema it speaks and the crate version it was built from.
    /// </summary>
    public static string AbiVersion => VersionDocument.Value;

    /// <summary>
    /// Extracts from a page the caller already holds as bytes. Gzip detection, charset detection,
    /// decoding, normalization and parsing all happen inside the native library, which is given the
    /// caller's own array: the page is never copied or decoded on the managed side.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="html"/> is null.</exception>
    /// <exception cref="TrafilaturaException">The request was rejected or the extraction failed.</exception>
    /// <exception cref="DllNotFoundException">The native library is missing, so the host could not load it.</exception>
    /// <exception cref="EntryPointNotFoundException">The library that loaded is stale or foreign and does not export this ABI.</exception>
    /// <exception cref="OverflowException">The response is larger than the platform's `int` limit, so it cannot be marshalled at all.</exception>
    public static ExtractionResult ExtractFromBytes(byte[] html, ExtractionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);

        var (code, response) = Interop.Extract(BuildHeader(options), html);
        return ReadResult(code, response);
    }

    /// <summary>
    /// Extracts from a page the caller already holds as a string, by encoding it to UTF-8 and taking
    /// the same path as <see cref="ExtractFromBytes"/>. For the same text the two are
    /// interchangeable, because charset detection is a no-op on .NET's UTF-8 output. An unpaired
    /// surrogate in <paramref name="html"/> becomes U+FFFD before the call.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="html"/> is null.</exception>
    /// <exception cref="TrafilaturaException">The request was rejected or the extraction failed.</exception>
    /// <exception cref="DllNotFoundException">The native library is missing, so the host could not load it.</exception>
    /// <exception cref="EntryPointNotFoundException">The library that loaded is stale or foreign and does not export this ABI.</exception>
    /// <exception cref="OverflowException">The response is larger than the platform's `int` limit, so it cannot be marshalled at all.</exception>
    public static ExtractionResult ExtractFromHtml(string html, ExtractionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);

        return ExtractFromBytes(Encoding.UTF8.GetBytes(html), options);
    }

    /// <summary>
    /// Extracts from many pages with bounded parallelism, returning one item per input in input order.
    /// A page that fails becomes its own item's <see cref="ExtractionBatchItem.Error"/> instead of
    /// aborting the batch, so one bad page cannot discard the good ones around it. Every call is an
    /// independent one-shot extraction, so the batch is worth its cost only across pages: a single
    /// page gains nothing from it.
    /// </summary>
    /// <param name="pages">The pages to extract, one per item in the returned list.</param>
    /// <param name="options">The options for every page in the batch.</param>
    /// <param name="maxDegreeOfParallelism">
    /// How many extractions may run at once; 0 means <see cref="Environment.ProcessorCount"/>.
    /// </param>
    /// <remarks>
    /// What a batch throws when the extraction itself cannot run depends on how many failures the
    /// parallel loop collected. With exactly one it throws that failure, so a batch reports a missing
    /// or stale native library as the same exception a single-page call reports. With more than one
    /// it throws the <see cref="AggregateException"/> the loop built, because none of them can stand
    /// for the others, and a batch of two pages against one missing library is enough to reach that.
    /// A page that fails to extract is neither: it becomes its item's
    /// <see cref="ExtractionBatchItem.Error"/> and the call returns normally.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="pages"/> is null or empty, or one of its elements is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxDegreeOfParallelism"/> is negative.</exception>
    /// <exception cref="AggregateException">More than one failure was collected, so none of them can stand for the others.</exception>
    /// <exception cref="DllNotFoundException">
    /// The native library is missing and it was the only failure collected, so the batch reports it
    /// as itself. Several pages against the same missing library report it inside an
    /// <see cref="AggregateException"/> instead.
    /// </exception>
    /// <exception cref="EntryPointNotFoundException">
    /// The library that loaded is stale or foreign, does not export this ABI, and was the only
    /// failure collected. As with the missing library, several pages report it aggregated.
    /// </exception>
    public static IReadOnlyList<ExtractionBatchItem> ExtractMany(
        IReadOnlyList<byte[]> pages,
        ExtractionOptions? options = null,
        int maxDegreeOfParallelism = 0)
    {
        if (pages is null)
        {
            throw new ArgumentException("The pages to extract must not be null.", nameof(pages));
        }

        if (pages.Count == 0)
        {
            throw new ArgumentException("The pages to extract must not be empty.", nameof(pages));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(maxDegreeOfParallelism);

        for (var index = 0; index < pages.Count; index++)
        {
            if (pages[index] is null)
            {
                throw new ArgumentException($"The page at index {index} is null.", nameof(pages));
            }
        }

        // Written by index rather than appended: a concurrent collection would make the input order
        // depend on which page finished first, and order is part of this helper's contract.
        var items = new ExtractionBatchItem[pages.Count];
        try
        {
            Parallel.For(
                0,
                pages.Count,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxDegreeOfParallelism == 0
                        ? Environment.ProcessorCount
                        : maxDegreeOfParallelism,
                },
                index =>
                {
                    try
                    {
                        items[index] = new ExtractionBatchItem { Result = ExtractFromBytes(pages[index], options) };
                    }
                    catch (TrafilaturaException exception)
                    {
                        items[index] = new ExtractionBatchItem { Error = exception };
                    }
                });
        }
        catch (AggregateException exception)
        {
            // Unwrapped with ExceptionDispatchInfo so the caller catches the inner exception itself,
            // stack trace included, instead of the loop's wrapper.
            ExceptionDispatchInfo.Capture(NormalizeBatchFailure(exception)).Throw();
        }

        return items;
    }

    /// <summary>
    /// Decides what a batch throws when the parallel loop itself faulted. One collected failure is
    /// unwrapped, because the loop wrapped it and the caller would otherwise have to look inside; two
    /// or more stay wrapped, because no single one of them can stand for the others, and so does an
    /// aggregate with none, which no single failure can replace either.
    /// </summary>
    /// <remarks>
    /// Split out of the catch so the rule is assertable without a native library that fails to load:
    /// the caller throws what this returns, so returning the aggregate here is what makes a batch
    /// report an <see cref="AggregateException"/>.
    /// </remarks>
    internal static Exception NormalizeBatchFailure(AggregateException exception) =>
        exception.InnerExceptions.Count == 1 ? exception.InnerExceptions[0] : exception;

    /// <summary>
    /// Assembles the request header. `responseFields`, `url` and `inputEncoding` belong at its top
    /// level while every core option belongs inside `options`, and a member that was not set is left
    /// out so the native side applies its own default instead of a value this layer invented.
    /// </summary>
    private static byte[] BuildHeader(ExtractionOptions? options) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new RequestHeader
            {
                ResponseFields = options?.ResponseFields,
                Url = options?.Url,
                InputEncoding = options?.InputEncoding,
                Options = options,
            },
            HeaderJsonOptions);

    /// <summary>
    /// The request header as JSON, for a caller that needs to see the wire shape itself. The element is
    /// cloned and the document disposed: the element's own buffers come from a pool and are returned
    /// when the document is, so keeping the element without the clone would hand back a value that
    /// dies with it.
    /// </summary>
    internal static JsonElement BuildHeaderJson(ExtractionOptions? options)
    {
        using var document = JsonDocument.Parse(BuildHeader(options));
        return document.RootElement.Clone();
    }

    private static ExtractionResult ReadResult(int code, string response)
    {
        if (code != 0)
        {
            throw ReadFailure(code, response);
        }

        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        return new ExtractionResult
        {
            ContentText = ReadString(root, "contentText"),
            CommentsText = ReadString(root, "commentsText"),
            ContentHtml = ReadString(root, "contentHtml"),
            CommentsHtml = ReadString(root, "commentsHtml"),
            Metadata = ReadMetadata(root),
        };
    }

    /// <summary>
    /// Maps the `metadata` object, or null when it was not requested: the wire contract keeps
    /// "not asked for" (null) apart from "nothing found" (empty strings and empty arrays).
    /// </summary>
    private static Metadata? ReadMetadata(JsonElement root)
    {
        if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new Metadata
        {
            Title = ReadString(metadata, "title") ?? string.Empty,
            Author = ReadString(metadata, "author") ?? string.Empty,
            Url = ReadString(metadata, "url") ?? string.Empty,
            Hostname = ReadString(metadata, "hostname") ?? string.Empty,
            Description = ReadString(metadata, "description") ?? string.Empty,
            Sitename = ReadString(metadata, "sitename") ?? string.Empty,
            Date = ReadDate(metadata),
            Categories = ReadStringArray(metadata, "categories"),
            Tags = ReadStringArray(metadata, "tags"),
            Id = ReadString(metadata, "id") ?? string.Empty,
            Fingerprint = ReadString(metadata, "fingerprint") ?? string.Empty,
            License = ReadString(metadata, "license") ?? string.Empty,
            Language = ReadString(metadata, "language") ?? string.Empty,
            Image = ReadString(metadata, "image") ?? string.Empty,
            PageType = ReadString(metadata, "pageType") ?? string.Empty,
        };
    }

    /// <summary>
    /// Turns a non-zero return code into an exception. The code names the class and the response
    /// carries the kind, the message and any context that kind has; a response that cannot be read
    /// still produces an exception rather than a JSON error nobody asked for.
    /// </summary>
    private static TrafilaturaException ReadFailure(int code, string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                return new TrafilaturaException(
                    TrafilaturaErrorKind.Unknown,
                    $"The native library returned code {code} without an error object.");
            }

            return new TrafilaturaException(
                ReadKind(ReadString(error, "kind")),
                ReadString(error, "message") ?? string.Empty,
                ReadContext(error));
        }
        catch (JsonException exception)
        {
            return new TrafilaturaException(
                TrafilaturaErrorKind.Unknown,
                $"The native library returned code {code} with a response that is not JSON.",
                contextJson: null,
                exception);
        }
    }

    private static string ReadVersionDocument()
    {
        var (code, response) = Interop.Version();
        if (code != 0)
        {
            throw ReadFailure(code, response);
        }

        return response;
    }

    /// <summary>
    /// A string member, or null when it is absent or is not a string. A field that was not requested
    /// is JSON null and a requested field that found nothing is an empty string, so the JSON kind is
    /// the whole difference between them.
    /// </summary>
    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Null is "the page carries no date"; any other value is an RFC 3339 timestamp, parsed with
    /// round-trip style so the offset the library reported survives.
    /// </summary>
    private static DateTimeOffset? ReadDate(JsonElement metadata) =>
        ReadString(metadata, "date") is { } date
            ? DateTimeOffset.Parse(date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (ReadStringFromValue(item) is { } text)
            {
                items.Add(text);
            }
        }

        return items;
    }

    /// <summary>The error object's `context` as JSON text; the kinds without a payload leave the key out.</summary>
    private static string? ReadContext(JsonElement error) =>
        error.TryGetProperty("context", out var context) && context.ValueKind != JsonValueKind.Null
            ? context.GetRawText()
            : null;

    private static string? ReadStringFromValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Maps the wire kind onto the enum, and anything this client does not know onto <see cref="TrafilaturaErrorKind.Unknown"/>.</summary>
    private static TrafilaturaErrorKind ReadKind(string? kind) => kind switch
    {
        "Io" => TrafilaturaErrorKind.Io,
        "UnsupportedCharset" => TrafilaturaErrorKind.UnsupportedCharset,
        "CharsetNotDetected" => TrafilaturaErrorKind.CharsetNotDetected,
        "MissingDocument" => TrafilaturaErrorKind.MissingDocument,
        "HtmlLanguage" => TrafilaturaErrorKind.HtmlLanguage,
        "RequiredTitle" => TrafilaturaErrorKind.RequiredTitle,
        "RequiredUrl" => TrafilaturaErrorKind.RequiredUrl,
        "RequiredDate" => TrafilaturaErrorKind.RequiredDate,
        "OutputTreeTooLong" => TrafilaturaErrorKind.OutputTreeTooLong,
        "OutputTooShort" => TrafilaturaErrorKind.OutputTooShort,
        "DuplicateBody" => TrafilaturaErrorKind.DuplicateBody,
        "WrongLanguage" => TrafilaturaErrorKind.WrongLanguage,
        "InvalidUrl" => TrafilaturaErrorKind.InvalidUrl,
        "InvalidRequest" => TrafilaturaErrorKind.InvalidRequest,
        "Panic" => TrafilaturaErrorKind.Panic,
        _ => TrafilaturaErrorKind.Unknown,
    };

    /// <summary>
    /// The wire header. <see cref="HeaderJsonOptions"/> gives it camelCase names and drops unset
    /// members, so an option the caller left alone cannot shadow the native library's default.
    /// </summary>
    private sealed class RequestHeader
    {
        public int SchemaVersion { get; init; } = 1;

        public string? Url { get; init; }

        public string? InputEncoding { get; init; }

        public List<string>? ResponseFields { get; init; }

        public ExtractionOptions? Options { get; init; }
    }
}
