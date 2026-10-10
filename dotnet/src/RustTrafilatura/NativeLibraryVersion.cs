namespace RustTrafilatura;

/// <summary>
/// The version document of a loaded native library, parsed: the ABI it implements, the JSON schema it
/// speaks, the version of this binding's own crate and the version of the `rust-trafilatura` core it
/// links.
/// </summary>
/// <remarks>
/// A member is null when the document does not state it, which is what a foreign library swapped onto the
/// load path produces; nothing here is compared, and <see cref="Document"/> keeps the library's own text.
/// The two versions are not interchangeable: `crate` is this binding's release and moves with the package
/// version, while `core` is the extraction engine behind it and moves only when that dependency does.
/// </remarks>
public sealed class NativeLibraryVersion
{
    /// <summary>The ABI the library implements, or null when the document states no number.</summary>
    public int? Abi { get; init; }

    /// <summary>The JSON schema it speaks, or null when the document states no number.</summary>
    public int? SchemaVersion { get; init; }

    /// <summary>The version of `rust-trafilatura-ffi`, the crate this binding is built from.</summary>
    public string? Crate { get; init; }

    /// <summary>The version of the `rust-trafilatura` core the library links.</summary>
    public string? Core { get; init; }

    /// <summary>The document as the library sent it, verbatim.</summary>
    public string Document { get; init; } = string.Empty;
}
