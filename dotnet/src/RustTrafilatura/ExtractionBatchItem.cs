namespace RustTrafilatura;

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
