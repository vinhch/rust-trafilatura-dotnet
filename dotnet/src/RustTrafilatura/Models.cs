using System.Text.Json.Serialization;

namespace RustTrafilatura;

/// <summary>
/// What went wrong, one member per error kind in the wire contract, plus <see cref="Unknown"/>.
/// </summary>
public enum TrafilaturaErrorKind
{
    Io,
    UnsupportedCharset,
    CharsetNotDetected,
    MissingDocument,
    HtmlLanguage,
    RequiredTitle,
    RequiredUrl,
    RequiredDate,
    OutputTreeTooLong,
    OutputTooShort,
    DuplicateBody,
    WrongLanguage,
    InvalidUrl,
    InvalidRequest,
    Panic,

    /// <summary>A kind the native library reported and this client version does not recognise.</summary>
    Unknown,
}

/// <summary>The failure of one extraction.</summary>
public sealed class TrafilaturaException : Exception
{
    public TrafilaturaException(
        TrafilaturaErrorKind kind,
        string message,
        string? contextJson = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        ContextJson = contextJson;
    }

    /// <summary>The failure class.</summary>
    public TrafilaturaErrorKind Kind { get; }

    /// <summary>The error's context as JSON text, or null when this kind carries none.</summary>
    public string? ContextJson { get; }
}

/// <summary>
/// The options for one extraction. Every member is optional and an unset member takes the native
/// library's own default, so an empty instance asks for exactly what no instance asks for.
/// </summary>
public sealed class ExtractionOptions
{
    /// <summary>
    /// The response fields to return, named from `contentText`, `commentsText`, `contentHtml`,
    /// `commentsHtml` and `metadata`; null asks for the default of text, comments and metadata.
    /// A field not named here is null in the result, while a named field that found nothing is an
    /// empty string. This shapes the response rather than the extraction, so it travels at the
    /// header's top level rather than inside `options`, which is why it is not serialized here.
    /// </summary>
    [JsonIgnore]
    public List<string>? ResponseFields { get; set; }
}

/// <summary>
/// The metadata of an extracted document. A value the page does not carry is an empty string; only
/// <see cref="Date"/> is null, because "the page has no date" and "the page has an empty date" are
/// different answers.
/// </summary>
public sealed class Metadata
{
    public string Title { get; init; } = string.Empty;

    public string Author { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string Hostname { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Sitename { get; init; } = string.Empty;

    /// <summary>The publication date, or null when the page carries none.</summary>
    public DateTimeOffset? Date { get; init; }

    /// <summary>Always an array, never null: empty when the page has no categories.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Always an array, never null: empty when the page has no tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    public string Id { get; init; } = string.Empty;

    public string Fingerprint { get; init; } = string.Empty;

    public string License { get; init; } = string.Empty;

    public string Language { get; init; } = string.Empty;

    public string Image { get; init; } = string.Empty;

    public string PageType { get; init; } = string.Empty;
}

/// <summary>
/// The result of one extraction. Every member is null when its response field was not requested,
/// which is what separates it from a requested field that found nothing (an empty string).
/// </summary>
public sealed class ExtractionResult
{
    public string? ContentText { get; init; }

    public string? CommentsText { get; init; }

    public string? ContentHtml { get; init; }

    public string? CommentsHtml { get; init; }

    public Metadata? Metadata { get; init; }
}
