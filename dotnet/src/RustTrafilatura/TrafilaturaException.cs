namespace RustTrafilatura;

/// <summary>
/// What went wrong, one member per error kind in the wire contract, plus <see cref="Unknown"/>.
/// </summary>
public enum TrafilaturaErrorKind
{
    MissingDocument,
    HtmlLanguage,
    MissingMetadata,
    OutputTreeTooLong,
    OutputTooShort,
    InputTooLarge,

    /// <summary>
    /// The detected language differs from the target language. It occurs once the core's language
    /// classifier reports a language, and the classifier of this core version reports none.
    /// </summary>
    WrongLanguage,

    /// <summary>
    /// The text repeats one a shared duplicate cache already holds. It occurs only with such a cache, which
    /// this binding does not pass.
    /// </summary>
    DuplicateBody,

    /// <summary>
    /// The URL is on the core's URL blacklist. It occurs only with a blacklist, which this binding does not
    /// set.
    /// </summary>
    BlacklistedUrl,

    InvalidValue,
    InvalidRequest,
    Panic,

    /// <summary>A kind the native library reported and this client version does not recognise, or a response with no readable error object.</summary>
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
