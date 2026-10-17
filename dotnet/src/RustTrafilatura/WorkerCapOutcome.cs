namespace RustTrafilatura;

/// <summary>
/// What a <see cref="Extractor.ConfigureWorkerCap"/> call found: the cap was applied, or the
/// cap was already fixed — by an earlier configure or by the first budgeted call reading the
/// environment — and the process keeps that value.
/// </summary>
public enum WorkerCapOutcome
{
    /// <summary>The cap was not fixed yet, and this call set it for the process.</summary>
    Applied,

    /// <summary>
    /// The cap was already fixed, so nothing changed: the caller is running on a value it did
    /// not set, which is worth reporting rather than silently keeping.
    /// </summary>
    AlreadyFixed,
}
