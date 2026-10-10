using System.Globalization;
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
    public static ExtractionResult ExtractFromHtml(string html, ExtractionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);

        return ExtractFromBytes(Encoding.UTF8.GetBytes(html), options);
    }

    /// <summary>
    /// Assembles the request header. `responseFields` belongs at its top level while every core
    /// option belongs inside `options`, and a member that was not set is left out so the native side
    /// applies its own default instead of a value this layer invented.
    /// </summary>
    private static byte[] BuildHeader(ExtractionOptions? options) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new RequestHeader { ResponseFields = options?.ResponseFields, Options = options },
            HeaderJsonOptions);

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
    /// The wire header. <see cref="HeaderJson"/> gives it camelCase names and drops unset members, so
    /// an option the caller left alone cannot shadow the native library's default.
    /// </summary>
    private sealed class RequestHeader
    {
        public int SchemaVersion { get; init; } = 1;

        public List<string>? ResponseFields { get; init; }

        public ExtractionOptions? Options { get; init; }
    }
}
