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

/// <summary>How much of the page the extractor keeps, mirroring the core's `ExtractionFocus`.</summary>
public enum ExtractionFocus
{
    /// <summary>The core's default: neither recall nor precision is preferred.</summary>
    Balanced,

    FavorRecall,

    FavorPrecision,
}

/// <summary>How hard the extractor looks for a publication date, mirroring the core's `HtmlDateMode`.</summary>
public enum HtmlDateMode
{
    /// <summary>The core's default.</summary>
    Default,

    Fast,

    Extensive,

    Disabled,
}

/// <summary>
/// The tuning knobs of one extraction, one member per key of the request's `config` object. An unset
/// member means the core's default for that key, whose value is in each member's documentation.
/// </summary>
public sealed class ExtractionConfig
{
    /// <summary>Default 4096.</summary>
    public long? CacheSize { get; set; }

    /// <summary>Default 2.</summary>
    public long? MaxDuplicateCount { get; set; }

    /// <summary>Default 100.</summary>
    public long? MinDuplicateCheckSize { get; set; }

    /// <summary>Default 250.</summary>
    public long? MinExtractedSize { get; set; }

    /// <summary>Default 1.</summary>
    public long? MinExtractedCommentSize { get; set; }

    /// <summary>Default 1.</summary>
    public long? MinOutputSize { get; set; }

    /// <summary>Default 1.</summary>
    public long? MinOutputCommentSize { get; set; }
}

/// <summary>
/// The options for one extraction. Every member is optional and an unset member takes the native
/// library's own default, so an empty instance asks for exactly what no instance asks for.
/// </summary>
/// <remarks>
/// The native library rejects an unknown key rather than ignoring it, so a member here that does not
/// belong in `options` fails loudly instead of quietly doing nothing.
/// </remarks>
public sealed class ExtractionOptions
{
    /// <summary>
    /// A known `encoding_rs` label to decode the payload with, or null to detect it from the payload.
    /// The payload is always raw bytes, so this always applies. This is a header key rather than an
    /// option, which is why it is not serialized here.
    /// </summary>
    [JsonIgnore]
    public string? InputEncoding { get; set; }

    /// <summary>
    /// The absolute `http`/`https` URL the page was fetched from, or null for "no URL". The extraction
    /// uses it for the metadata this document cannot carry itself, and any other value is rejected.
    /// This is a header key rather than an option, which is why it is not serialized here.
    /// </summary>
    [JsonIgnore]
    public string? Url { get; set; }

    /// <summary>
    /// The response fields to return, named from `contentText`, `commentsText`, `contentHtml`,
    /// `commentsHtml` and `metadata`; null asks for the default of text, comments and metadata.
    /// A field not named here is null in the result, while a named field that found nothing is an
    /// empty string. This shapes the response rather than the extraction, so it travels at the
    /// header's top level rather than inside `options`, which is why it is not serialized here.
    /// </summary>
    [JsonIgnore]
    public List<string>? ResponseFields { get; set; }

    /// <summary>The language to expect, or null for no expectation.</summary>
    public string? TargetLanguage { get; set; }

    /// <summary>
    /// Whether to compare against the bundled readability-lxml as well. The default is false, which is
    /// FAST: native recall and baseline recovery only.
    /// </summary>
    public bool? EnableFallback { get; set; }

    /// <summary>How much of the page to keep. Null is the core's default of <see cref="ExtractionFocus.Balanced"/>.</summary>
    public ExtractionFocus? Focus { get; set; }

    /// <summary>Whether to drop the page's comments. Null is the core's default of false.</summary>
    public bool? ExcludeComments { get; set; }

    /// <summary>Whether to drop tables. Null is the core's default of false.</summary>
    public bool? ExcludeTables { get; set; }

    /// <summary>Whether to keep image references. Null is the core's default of false.</summary>
    public bool? IncludeImages { get; set; }

    /// <summary>Whether to keep links. Null is the core's default of false.</summary>
    public bool? IncludeLinks { get; set; }

    /// <summary>Authors whose documents are rejected, or null for the core's default of none.</summary>
    public List<string>? BlacklistedAuthors { get; set; }

    /// <summary>Whether to remove repeated content. Null is the core's default of false.</summary>
    public bool? Deduplicate { get; set; }

    /// <summary>Whether a document without essential metadata is rejected. Null is the core's default of false.</summary>
    public bool? HasEssentialMetadata { get; set; }

    /// <summary>An upper bound on the tree size in bytes, or null for the core's default of 0, meaning no limit.</summary>
    public long? MaxTreeSize { get; set; }

    /// <summary>How hard to look for a date. Null is the core's default of <see cref="HtmlDateMode.Default"/>.</summary>
    public HtmlDateMode? HtmlDateMode { get; set; }

    /// <summary>A CSS selector whose matching nodes are removed before extraction, or null for none.</summary>
    public string? PruneSelector { get; set; }

    /// <summary>The size thresholds, or null to take the core's defaults for all of them.</summary>
    public ExtractionConfig? Config { get; set; }
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

/// <summary>
/// One input page of a batch. Exactly one of <see cref="Result"/> and <see cref="Error"/> is set:
/// a page whose extraction failed reports it here instead of discarding the pages around it.
/// </summary>
public sealed class ExtractionBatchItem
{
    /// <summary>The extraction, or null when this page failed.</summary>
    public ExtractionResult? Result { get; init; }

    /// <summary>Why this page failed, or null when it did not.</summary>
    public TrafilaturaException? Error { get; init; }

    /// <summary>
    /// Whether this page produced a result: true only when <see cref="Result"/> is set and
    /// <see cref="Error"/> is not, because a default instance has neither.
    /// </summary>
    public bool Succeeded => Result is not null && Error is null;
}
