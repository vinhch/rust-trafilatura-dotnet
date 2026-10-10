namespace RustTrafilatura;

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
