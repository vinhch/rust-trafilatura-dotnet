using System.Text.Json.Serialization;

namespace RustTrafilatura;

/// <summary>How much of the page the extractor keeps, mirroring the core's `ExtractionFocus`.</summary>
/// <remarks>
/// The wire names are stated twice on purpose, once per asset: `[JsonStringEnumMemberName]` exists from
/// `net9.0` on, and the `net8.0` asset states the same names in HeaderContext.cs's converters. Both
/// spellings have to stay equal to the core's, which is what the byte-identity test pins.
/// </remarks>
public enum ExtractionFocus
{
    /// <summary>The core's default: neither recall nor precision is preferred.</summary>
#if NET9_0_OR_GREATER
    [JsonStringEnumMemberName("balanced")]
#endif
    Balanced,

#if NET9_0_OR_GREATER
    [JsonStringEnumMemberName("favorRecall")]
#endif
    FavorRecall,

#if NET9_0_OR_GREATER
    [JsonStringEnumMemberName("favorPrecision")]
#endif
    FavorPrecision,
}

/// <summary>How hard the extractor looks for a publication date, mirroring the core's `HtmlDateMode`.</summary>
public enum HtmlDateMode
{
    /// <summary>The core's default.</summary>
#if NET9_0_OR_GREATER
    [JsonStringEnumMemberName("default")]
#endif
    Default,

#if NET9_0_OR_GREATER
    [JsonStringEnumMemberName("fast")]
#endif
    Fast,

#if NET9_0_OR_GREATER
    [JsonStringEnumMemberName("extensive")]
#endif
    Extensive,
}

/// <summary>
/// The tuning knobs of one extraction, one member per key of the request's `config` object. An unset
/// member means the core's default for that key, whose value is in each member's documentation.
/// </summary>
public sealed class ExtractionConfig
{
    /// <summary>Default 250.</summary>
    public long? MinExtractedSize { get; set; }

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
    /// The URL the page was fetched from, or null or an empty string for "no URL". The extraction
    /// uses it for the metadata this document cannot carry itself, and any string is passed through.
    /// This is a header key rather than an option, which is why it is not serialized here.
    /// </summary>
    [JsonIgnore]
    public string? Url { get; set; }

    /// <summary>
    /// The response fields to return, named from `contentText`, `commentsText` and
    /// `metadata`; null asks for the default of text, comments and metadata.
    /// A field not named here is null in the result, while a named field that found nothing is an
    /// empty string. This shapes the response rather than the extraction, so it travels at the
    /// header's top level rather than inside `options`, which is why it is not serialized here.
    /// </summary>
    [JsonIgnore]
    public List<string>? ResponseFields { get; set; }

    /// <summary>The language to expect, or null for no expectation.</summary>
    public string? TargetLanguage { get; set; }

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

    /// <summary>Authors dropped from the metadata when they match, or null for the core's default of none. The document is not rejected.</summary>
    public List<string>? BlacklistedAuthors { get; set; }

    /// <summary>Whether a document without essential metadata is rejected. Null is the core's default of false.</summary>
    public bool? HasEssentialMetadata { get; set; }

    /// <summary>An upper bound on the number of direct children the extracted body may have, or null for the core's default of 0, meaning no limit. It bounds the shape of the extraction's output, not the size or the nesting depth of the input.</summary>
    public long? MaxTreeSize { get; set; }

    /// <summary>How hard to look for a date. Null is the core's default of <see cref="HtmlDateMode.Default"/>.</summary>
    public HtmlDateMode? HtmlDateMode { get; set; }

    /// <summary>The size thresholds, or null to take the core's defaults for all of them.</summary>
    public ExtractionConfig? Config { get; set; }
}
