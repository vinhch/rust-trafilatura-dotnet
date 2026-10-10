using System.Text.Json;

namespace RustTrafilatura;

/// <summary>
/// The contract the loaded native library has to meet: the version document it reports, read once per
/// process, and the comparison that refuses a library built for a different `abi` or JSON schema.
/// </summary>
internal static class LibraryContract
{
    /// <summary>The `abi` and the JSON schema this assembly was compiled for; a library that reports other numbers is read with the wrong contract.</summary>
    private const int RequiredAbi = 1;

    /// <summary>The other half of <see cref="RequiredAbi"/>: the schema version of the response this client parses.</summary>
    private const int RequiredSchemaVersion = 1;

    private static readonly Lazy<string> VersionDocument = new(() =>
    {
        var (code, document) = Interop.Version();
        return ReadDocument(code, document);
    });

    /// <summary>
    /// The same document, parsed once: the accessor below hands the fields out, and a document that does
    /// not parse states nothing rather than failing, because nothing here is compared.
    /// </summary>
    private static readonly Lazy<NativeLibraryVersion> ParsedVersionDocument = new(ReadVersion);

    /// <summary>
    /// The one contract comparison per process: `Lazy` runs the factory at most once, so no extraction
    /// after the first calls `rt_version` at all, and null means the library passed.
    /// </summary>
    private static readonly Lazy<ContractVersionMismatchException?> ContractMismatch = new(() => Check(VersionDocument.Value));

    /// <summary>The loaded library's version document, parsed.</summary>
    internal static NativeLibraryVersion Version => ParsedVersionDocument.Value;

    /// <summary>
    /// The fields of the version document, or a member left unset where the document does not state it. A
    /// document that does not parse states nothing rather than throwing, which is the same finding as one
    /// that omits a key: this hands out the library's claim, and the comparison that can fail is
    /// <see cref="Check(string)"/>.
    /// </summary>
    private static NativeLibraryVersion ReadVersion()
    {
        var document = VersionDocument.Value;

        try
        {
            using var parsed = JsonDocument.Parse(document);

            return new NativeLibraryVersion
            {
                Abi = JsonMembers.Number(parsed.RootElement, "abi"),
                SchemaVersion = JsonMembers.Number(parsed.RootElement, "schemaVersion"),
                Crate = JsonMembers.Text(parsed.RootElement, "crate"),
                Core = JsonMembers.Text(parsed.RootElement, "core"),
                Document = document,
            };
        }
        catch (JsonException)
        {
            return new NativeLibraryVersion { Document = document };
        }
    }

    /// <summary>
    /// The version document a `rt_version` call produced, or the mismatch of a library that produced
    /// none: a non-zero code hands out no document to compare, so there is nothing for
    /// <see cref="Check(string)"/> to read and the library is one this assembly cannot use.
    /// </summary>
    /// <remarks>
    /// Internal, and taking the call's return code and document as arguments, so a version read that
    /// fails is assertable without a native library that misbehaves. The failure the read itself
    /// produced stays on the mismatch as its inner exception, because the code and the response are
    /// the only record of what the library answered.
    /// </remarks>
    internal static string ReadDocument(int code, string document) =>
        code == 0
            ? document
            : throw new ContractVersionMismatchException(
                RequiredAbi,
                null,
                RequiredSchemaVersion,
                null,
                Failures.Read(code, document));

    /// <summary>
    /// Refuses to extract against a library whose contract version differs from the one this assembly
    /// was compiled for. Runs before an extraction rather than from a static constructor or from
    /// <see cref="Extractor.LibraryVersion"/>: a static constructor failure reaches the caller as
    /// <see cref="TypeInitializationException"/>, which hides the finding, and the version accessor is
    /// the path a caller reads to see which library is loaded.
    /// </summary>
    /// <remarks>
    /// This is the first use of the native library, so what it finds is what a caller sees first: a
    /// missing or foreign library still reports the <see cref="DllNotFoundException"/> or
    /// <see cref="EntryPointNotFoundException"/> the extraction call used to report, with the same
    /// diagnostics, because the document is read through the same <see cref="Interop"/> calls. A
    /// `rt_version` that answers with a non-zero code reads as a mismatch with neither number
    /// reported, because it handed out no document to compare. `Lazy` caches that first outcome,
    /// mismatch or failure, so later calls report it without loading the library again, and a mismatch
    /// is the same exception instance every time.
    /// </remarks>
    internal static void Ensure()
    {
        // The same instance every time is deliberate: the mismatch is a property of the loaded library
        // and cannot change under this process, so an extraction number one thousand reports exactly
        // what the first one did.
        if (ContractMismatch.Value is { } mismatch)
        {
            throw mismatch;
        }
    }

    /// <summary>
    /// The comparison the first extraction makes against the library's own version document, or null
    /// when that document reports the `abi` and the `schemaVersion` this assembly was compiled for.
    /// </summary>
    /// <remarks>
    /// Internal, and taking the document as text, so every outcome is assertable without a native
    /// library built for a different ABI: the first-use path throws what this returns, so what this
    /// returns is what a mismatched library produces. A document that does not parse, or that does not
    /// state both numbers, reports neither of them: the comparison passes only on a document that
    /// carries both.
    /// </remarks>
    internal static ContractVersionMismatchException? Check(string versionDocument)
    {
        int? abi = null;
        int? schemaVersion = null;
        try
        {
            using var document = JsonDocument.Parse(versionDocument);
            abi = JsonMembers.Number(document.RootElement, "abi");
            schemaVersion = JsonMembers.Number(document.RootElement, "schemaVersion");
        }
        catch (JsonException)
        {
            // A document that does not parse states no contract version, which is the same finding as
            // one that omits a key, and neither may pass the comparison by default.
        }

        return abi == RequiredAbi && schemaVersion == RequiredSchemaVersion
            ? null
            : new ContractVersionMismatchException(RequiredAbi, abi, RequiredSchemaVersion, schemaVersion);
    }
}
