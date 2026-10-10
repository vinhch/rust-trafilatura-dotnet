using System.Globalization;

namespace RustTrafilatura;

/// <summary>
/// The loaded native library is not one this assembly can use: it reports an `abi` or a
/// `schemaVersion` this assembly was not compiled for, or it handed out no version document to
/// compare, so no extraction ran.
/// </summary>
/// <remarks>
/// Not a <see cref="TrafilaturaException"/>: that type means the extractor ran and refused the page,
/// and a caller that handles a rejected page must not handle a library it cannot use with it. An
/// `Actual` value is null when the library's document did not state that number at all, which is a
/// different finding from a number that differs, and both sides are reported rather than guessed. A
/// version read that fails outright reports both values as null for the same reason and keeps the
/// failure it produced in <see cref="Exception.InnerException"/>, so the code and the response the
/// library answered with are not lost. The library's whole document is what
/// <see cref="Extractor.LibraryVersion"/> hands out, and neither `crate` nor `core` is compared because
/// both are versioned independently of `abi`.
/// </remarks>
public sealed class ContractVersionMismatchException : Exception
{
    /// <summary>Reports both sides of the comparison this assembly made.</summary>
    /// <param name="expectedAbi">The `abi` this assembly was compiled for.</param>
    /// <param name="actualAbi">The `abi` the library reported, or null when it reported none.</param>
    /// <param name="expectedSchemaVersion">The `schemaVersion` this assembly was compiled for.</param>
    /// <param name="actualSchemaVersion">The `schemaVersion` the library reported, or null when it reported none.</param>
    public ContractVersionMismatchException(
        int expectedAbi,
        int? actualAbi,
        int expectedSchemaVersion,
        int? actualSchemaVersion)
        : base(BuildMessage(expectedAbi, actualAbi, expectedSchemaVersion, actualSchemaVersion))
    {
        ExpectedAbi = expectedAbi;
        ActualAbi = actualAbi;
        ExpectedSchemaVersion = expectedSchemaVersion;
        ActualSchemaVersion = actualSchemaVersion;
    }

    /// <summary>
    /// The same comparison for a version read that failed outright: there is no document at all, so
    /// both actual values are absent and the failure that produced them travels as the inner
    /// exception.
    /// </summary>
    internal ContractVersionMismatchException(
        int expectedAbi,
        int? actualAbi,
        int expectedSchemaVersion,
        int? actualSchemaVersion,
        Exception innerException)
        : base(BuildMessage(expectedAbi, actualAbi, expectedSchemaVersion, actualSchemaVersion), innerException)
    {
        ExpectedAbi = expectedAbi;
        ActualAbi = actualAbi;
        ExpectedSchemaVersion = expectedSchemaVersion;
        ActualSchemaVersion = actualSchemaVersion;
    }

    /// <summary>The `abi` this assembly was compiled for.</summary>
    public int ExpectedAbi { get; }

    /// <summary>The `abi` the loaded library reported, or null when its version document stated none.</summary>
    public int? ActualAbi { get; }

    /// <summary>The `schemaVersion` this assembly was compiled for.</summary>
    public int ExpectedSchemaVersion { get; }

    /// <summary>The `schemaVersion` the loaded library reported, or null when its version document stated none.</summary>
    public int? ActualSchemaVersion { get; }

    /// <summary>
    /// Both sides and the fix, because the message is the part of a failure that survives a log: the
    /// numbers alone say what differed but not what the caller can install to stop it.
    /// </summary>
    private static string BuildMessage(
        int expectedAbi,
        int? actualAbi,
        int expectedSchemaVersion,
        int? actualSchemaVersion) =>
        $"The loaded native library is not the one this assembly was compiled against: it reports abi "
        + $"{Describe(actualAbi)} and schemaVersion {Describe(actualSchemaVersion)}, while this assembly requires abi "
        + $"{expectedAbi} and schemaVersion {expectedSchemaVersion}. Install the RustTrafilatura package version that "
        + "matches this assembly, or replace the loaded library with the one that package ships (scripts/build.ps1 or "
        + "scripts/build.sh rebuilds a checkout's library).";

    /// <summary>The number, or the phrase the message uses for a document that did not state one.</summary>
    private static string Describe(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "(not reported)";
}
