using System.Text;

namespace RustTrafilatura;

/// <summary>
/// Extracts text, comments and metadata from a page. One call per page, with no state to hold
/// or dispose between calls.
/// </summary>
public static class Extractor
{
    /// <summary>
    /// The version document of the loaded native library, parsed: the ABI it implements, the JSON schema
    /// it speaks, the version of this binding's own crate and the version of the `rust-trafilatura` core
    /// it links.
    /// </summary>
    /// <remarks>
    /// The document is what the library says about itself, and this accessor does not compare it: it is
    /// the path a caller reads to see which library is loaded, including after
    /// <see cref="ContractVersionMismatchException"/> reported that `abi` or `schemaVersion` differs. The
    /// comparison runs on the first call that extracts (see
    /// <see cref="ExtractFromBytes(ReadOnlyMemory{byte}, ExtractionOptions)"/>), which can catch a
    /// library that reports different numbers and cannot catch one that reports these while implementing
    /// something else, because the document is still only the library's claim. `crate` and `core` are
    /// versioned independently of `abi`, so a different version is not a mismatch and is not compared;
    /// either is null when the document states none, and <see cref="NativeLibraryVersion.Document"/> is
    /// the library's own text.
    /// </remarks>
    /// <exception cref="ContractVersionMismatchException">
    /// The library's `rt_version` answered with a code that carried no document, so there is nothing to
    /// parse. A document that is readable is handed out as it stands, because this accessor does not
    /// compare it.
    /// </exception>
    public static NativeLibraryVersion LibraryVersion => LibraryContract.Version;

    /// <summary>
    /// Extracts from a page the caller already holds as bytes. Gzip detection, charset detection,
    /// decoding, normalization and parsing all happen inside the native library, which is given the
    /// caller's own memory: the page is never copied or decoded on the managed side, and a
    /// <see cref="byte"/> array arrives here through the implicit conversion with no allocation.
    /// </summary>
    /// <param name="html">
    /// The page's bytes: its whole length, a slice of an array, a pooled rent or any other memory
    /// view. Bytes that are not memory-backed, such as a `stackalloc` buffer or unmanaged memory, go
    /// to <see cref="ExtractFromSpan"/> instead. An empty buffer, which is what a null array becomes,
    /// is a valid request that the core itself rejects as too short.
    /// </param>
    /// <param name="options">The options for this page, or null for the core's own defaults.</param>
    /// <exception cref="ContractVersionMismatchException">
    /// The native library reports an `abi` or `schemaVersion` this assembly was not compiled for, or
    /// its `rt_version` answered with a code that carried no version document at all. The comparison is
    /// made once per process, on the first call that reaches here, and every later call throws that
    /// same failure until a usable library is loaded.
    /// </exception>
    /// <exception cref="TrafilaturaException">The request was rejected or the extraction failed.</exception>
    /// <exception cref="DllNotFoundException">The native library is missing, so the host could not load it.</exception>
    /// <exception cref="EntryPointNotFoundException">The library that loaded is stale or foreign and does not export this ABI.</exception>
    /// <exception cref="OverflowException">The response is larger than the platform's `int` limit, so it cannot be marshalled at all.</exception>
    public static ExtractionResult ExtractFromBytes(ReadOnlyMemory<byte> html, ExtractionOptions? options = null)
    {
        return ExtractFromSpan(html.Span, options);
    }

    /// <summary>
    /// Extracts from a page the caller already holds as a span, which is the shape every byte entry
    /// point funnels into. Gzip detection, charset detection, decoding, normalization and parsing all
    /// happen inside the native library, which is given the caller's own bytes: the page is never
    /// copied or decoded on the managed side, and neither an array nor a memory view costs an
    /// allocation to reach here.
    /// </summary>
    /// <param name="html">
    /// The page's bytes: an array (through the implicit conversion), a slice of one, a pooled rent, a
    /// span taken from a memory view, a `stackalloc` buffer, or unmanaged memory behind a pointer. An
    /// empty span is a valid request that the core itself rejects as too short.
    /// </param>
    /// <param name="options">The options for this page, or null for the core's own defaults.</param>
    /// <exception cref="ContractVersionMismatchException">
    /// The native library reports an `abi` or `schemaVersion` this assembly was not compiled for, or
    /// its `rt_version` answered with a code that carried no version document at all. The comparison is
    /// made once per process, on the first call that reaches here, and every later call throws that
    /// same failure until a usable library is loaded.
    /// </exception>
    /// <exception cref="TrafilaturaException">The request was rejected or the extraction failed.</exception>
    /// <exception cref="DllNotFoundException">The native library is missing, so the host could not load it.</exception>
    /// <exception cref="EntryPointNotFoundException">The library that loaded is stale or foreign and does not export this ABI.</exception>
    /// <exception cref="OverflowException">The response is larger than the platform's `int` limit, so it cannot be marshalled at all.</exception>
    public static ExtractionResult ExtractFromSpan(ReadOnlySpan<byte> html, ExtractionOptions? options = null)
    {
        LibraryContract.Ensure();

        return Interop.Extract(Request.Header(options), html, Response.Read);
    }

    /// <summary>
    /// Extracts from a page the caller already holds as a string, by encoding it to UTF-8 and taking
    /// the same path as <see cref="ExtractFromSpan"/>. For the same text the two are
    /// interchangeable, because charset detection is a no-op on .NET's UTF-8 output. An unpaired
    /// surrogate in <paramref name="html"/> becomes U+FFFD before the call.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="html"/> is null.</exception>
    /// <exception cref="ContractVersionMismatchException">The same check <see cref="ExtractFromSpan(ReadOnlySpan{byte}, ExtractionOptions)"/> makes, since this overload reaches it.</exception>
    /// <exception cref="TrafilaturaException">The request was rejected or the extraction failed.</exception>
    /// <exception cref="DllNotFoundException">The native library is missing, so the host could not load it.</exception>
    /// <exception cref="EntryPointNotFoundException">The library that loaded is stale or foreign and does not export this ABI.</exception>
    /// <exception cref="OverflowException">The response is larger than the platform's `int` limit, so it cannot be marshalled at all.</exception>
    public static ExtractionResult ExtractFromHtml(string html, ExtractionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);

        return ExtractFromSpan(Encoding.UTF8.GetBytes(html), options);
    }
}
