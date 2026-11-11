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
    /// it speaks, the version of this binding's own crate and the version of the `trafilatura-rust` core
    /// it links.
    /// </summary>
    /// <remarks>
    /// The document is what the library says about itself, and this accessor does not compare it: it is
    /// the path a caller reads to see which library is loaded, including after
    /// <see cref="ContractVersionMismatchException"/> reported that `abi` or `schemaVersion` differs. The
    /// comparison runs on the first call that extracts (see
    /// <see cref="ExtractFromBytes(ReadOnlyMemory{byte}, ExtractionOptions, CancellationToken)"/>), which can catch a
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
    /// is a valid request that the core rejects as `MissingDocument`.
    /// </param>
    /// <param name="options">The options for this page, or null for the core's own defaults.</param>
    /// <param name="cancellationToken">
    /// Stops the extraction when cancelled. A token cancelled before the call throws without a native call;
    /// one cancelled during it stops the native work at its next check; one cancelled after native returned
    /// has no effect on the result.
    /// </param>
    /// <exception cref="OperationCanceledException">The token was cancelled before the call or during it. Carries <paramref name="cancellationToken"/>.</exception>
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
    public static ExtractionResult ExtractFromBytes(ReadOnlyMemory<byte> html, ExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        return ExtractFromSpan(html.Span, options, cancellationToken);
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
    /// empty span is a valid request that the core rejects as `MissingDocument`.
    /// </param>
    /// <param name="options">The options for this page, or null for the core's own defaults.</param>
    /// <param name="cancellationToken">
    /// Stops the extraction when cancelled. A token cancelled before the call throws without a native call;
    /// one cancelled during it stops the native work at its next check; one cancelled after native returned
    /// has no effect on the result.
    /// </param>
    /// <exception cref="OperationCanceledException">The token was cancelled before the call or during it. Carries <paramref name="cancellationToken"/>.</exception>
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
    public static ExtractionResult ExtractFromSpan(ReadOnlySpan<byte> html, ExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LibraryContract.Ensure();

        return Interop.Extract(Request.Header(options), html, Response.Read, cancellationToken);
    }

    /// <summary>
    /// Extracts from a page the caller already holds as a string. The string is encoded to UTF-8 and sent
    /// to the native library as text (`payloadText`). No binary check, decompression or charset
    /// detection runs on it; the byte entry points run all three. An unpaired surrogate in
    /// <paramref name="html"/> becomes U+FFFD before the call.
    /// </summary>
    /// <param name="html">The page as text. It is encoded to UTF-8 and sent as text; null throws <see cref="ArgumentNullException"/> before the token is checked.</param>
    /// <param name="options">The options for this page, or null for the core's own defaults.</param>
    /// <param name="cancellationToken">
    /// Stops the extraction when cancelled, with the same meaning as on <see cref="ExtractFromSpan(ReadOnlySpan{byte}, ExtractionOptions, CancellationToken)"/>.
    /// </param>
    /// <exception cref="OperationCanceledException">The token was cancelled before the call or during it. Carries <paramref name="cancellationToken"/>.</exception>
    /// <exception cref="ContractVersionMismatchException">The same check <see cref="ExtractFromSpan(ReadOnlySpan{byte}, ExtractionOptions, CancellationToken)"/> makes, run here before the call.</exception>
    /// <exception cref="TrafilaturaException">The request was rejected or the extraction failed.</exception>
    /// <exception cref="DllNotFoundException">The native library is missing, so the host could not load it.</exception>
    /// <exception cref="EntryPointNotFoundException">The library that loaded is stale or foreign and does not export this ABI.</exception>
    /// <exception cref="OverflowException">The response is larger than the platform's `int` limit, so it cannot be marshalled at all.</exception>
    public static ExtractionResult ExtractFromHtml(string html, ExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        cancellationToken.ThrowIfCancellationRequested();
        LibraryContract.Ensure();

        return Interop.Extract(Request.Header(options, payloadText: true), Encoding.UTF8.GetBytes(html), Response.Read, cancellationToken);
    }
}
