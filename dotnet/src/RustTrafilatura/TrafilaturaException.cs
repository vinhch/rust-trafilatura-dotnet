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

    /// <summary>The call's wall-clock time budget expired; the underlying extraction finishes on a detached worker afterwards.</summary>
    TimeBudgetExceeded,

    /// <summary>The detached-worker pool the budget path runs on is full — a process-level condition, not a page-level one.</summary>
    WorkerCapExceeded,

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
