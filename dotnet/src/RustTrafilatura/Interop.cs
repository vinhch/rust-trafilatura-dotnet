using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RustTrafilatura;

/// <summary>
/// Reads one response out of the native bytes it arrived in.
/// </summary>
/// <remarks>
/// The response is a span over the buffer the ABI handed out, so the reader is only called inside the
/// region that owns that buffer and must not keep the span. A delegate is what carries a span
/// parameter between the two target frameworks: a `Func&lt;ReadOnlySpan&lt;byte&gt;, TResult&gt;` does
/// not compile on the `net8.0` asset, whose `Func&lt;T, TResult&gt;` has no `allows ref struct`
/// constraint (CS9244), while a delegate declared here compiles on both.
/// </remarks>
/// <param name="code">The ABI's return code: 0 ok, 1 extraction error, 2 invalid request, 3 a caught panic.</param>
/// <param name="response">The ABI's UTF-8 JSON response, valid only for the duration of this call.</param>
internal delegate TResult ResponseReader<TResult>(int code, ReadOnlySpan<byte> response);

/// <summary>
/// The native library's C ABI, spelled exactly as `rust/src/lib.rs` exports it.
/// </summary>
/// <remarks>
/// Buffers stay in this file: the callers pass spans and get text back, and every buffer the native
/// side hands out is released here, in a `finally`, with the same `(ptr, len, cap)` it was written
/// with. The one native pointer that outlives a call, the cancel token, is owned by
/// <see cref="CancelHandle"/>, a `SafeHandle`, and this file only passes it. Responses are
/// length-delimited UTF-8 rather than NUL-terminated, so a response may contain any byte and the
/// caller's payload never becomes a string. The class is internal, and the test assembly is its only
/// other reader through `InternalsVisibleTo`: a raw round trip means <see cref="Extract{TResult}"/>,
/// and the raw imports stay reachable for the ABI's own edge cases.
/// </remarks>
internal static partial class Interop
{
    /// <summary>The ABI's library name; the platform supplies its own prefix and suffix.</summary>
    private const string LibraryName = "rust_trafilatura_ffi";

    private static readonly string LibraryFileName = OperatingSystem.IsWindows()
        ? LibraryName + ".dll"
        : OperatingSystem.IsMacOS()
            ? "lib" + LibraryName + ".dylib"
            : "lib" + LibraryName + ".so";

    /// <summary>Test seam, null in production: runs after `rt_extract` returns and before the cancel registration is disposed.</summary>
    internal static Action? AfterNativeCall { get; set; }

    /// <summary>Extracts from a UTF-8 JSON header and a raw payload, handing the ABI's return code and its UTF-8 JSON response to a reader.</summary>
    /// <remarks>
    /// A token that can be cancelled gets a native handle for the length of the call, and the token's
    /// registration is disposed before the handle, so a cancel that races the return never reaches a
    /// released handle. A cancelled call throws instead of handing the reader its envelope.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The token was cancelled and native work stopped at a check.</exception>
    /// <exception cref="DllNotFoundException">The native library is not where the host resolves libraries from.</exception>
    /// <exception cref="EntryPointNotFoundException">The library that loaded is not a build of this ABI.</exception>
    internal static TResult Extract<TResult>(
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> payload,
        ResponseReader<TResult> reader,
        CancellationToken cancellationToken = default)
    {
        nint outPointer = 0;
        nint outLength = 0;
        nint outCapacity = 0;
        int code;
        var cancel = CancelHandle.None;
        var registration = default(CancellationTokenRegistration);

        // Loading is diagnosed by attempting the call, not by a pre-check: the host resolves the
        // library from the application directory *and* from the package layout's
        // runtimes/<rid>/native, so a pre-check that knew only one of the two rejected deployments
        // that work. The handle and the registration sit in the same try so their diagnostics match
        // the call's, and the finally keeps their order on every path: registration first, then handle.
        try
        {
            if (cancellationToken.CanBeCanceled)
            {
                cancel = CancelHandle.Create();
                registration = cancellationToken.UnsafeRegister(static state => Interop.RtCancel((CancelHandle)state!), cancel);
            }

            unsafe
            {
                // Pinning is what keeps "nothing is copied on the managed side" true: the caller's
                // array stays where it is and only its address crosses the boundary.
                fixed (byte* headerPointer = header)
                fixed (byte* payloadPointer = payload)
                {
                    code = RtExtract(
                        headerPointer,
                        (nuint)header.Length,
                        payloadPointer,
                        (nuint)payload.Length,
                        cancel,
                        &outPointer,
                        &outLength,
                        &outCapacity);
                }
            }

            AfterNativeCall?.Invoke();
        }
        catch (DllNotFoundException exception)
        {
            throw MissingLibrary(exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw MissingSymbol(exception);
        }
        finally
        {
            registration.Dispose();
            if (!ReferenceEquals(cancel, CancelHandle.None))
            {
                cancel.Dispose();
            }
        }

        // The call is deliberately outside the ownership try: a P/Invoke that never reached the native
        // side wrote no buffer, and a release attempted from a finally there would replace the real
        // failure with a second copy of itself. The free covers the reader's call, so a response this
        // reader cannot read, or a reader that throws, is still released.
        try
        {
            // Native reports a cancelled call as code 1 with a Cancelled envelope, and only a rt_cancel
            // from the registration produces that, so a cancelled token is the only reason it can appear
            // here. A rejection that finished before the cancel keeps its own envelope.
            if (code == 1 && cancellationToken.IsCancellationRequested && Failures.IsCancelled(ResponseSpan(outPointer, outLength)))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return reader(code, ResponseSpan(outPointer, outLength));
        }
        finally
        {
            RtFree(outPointer, (nuint)outLength, (nuint)outCapacity);
        }
    }

    /// <summary>Returns the ABI's return code and the version document as UTF-8 JSON, which reads as an empty string when that code carries no buffer.</summary>
    /// <exception cref="DllNotFoundException">The native library is not where the host resolves libraries from.</exception>
    /// <exception cref="EntryPointNotFoundException">The library that loaded is not a build of this ABI.</exception>
    internal static (int Code, string Json) Version()
    {
        nint outPointer = 0;
        nint outLength = 0;
        nint outCapacity = 0;
        int code;
        try
        {
            unsafe
            {
                code = RtVersion(&outPointer, &outLength, &outCapacity);
            }
        }
        catch (DllNotFoundException exception)
        {
            throw MissingLibrary(exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw MissingSymbol(exception);
        }

        try
        {
            return (code, ReadDocument(outPointer, outLength));
        }
        finally
        {
            RtFree(outPointer, (nuint)outLength, (nuint)outCapacity);
        }
    }

    /// <summary>Returns 0 ok, 1 extraction error, 2 invalid request, 3 a caught panic. A cancelled call is 1 with a `Cancelled` envelope.</summary>
    [LibraryImport(LibraryName, EntryPoint = "rt_extract", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static unsafe partial int RtExtract(
        byte* header,
        nuint headerLength,
        byte* payload,
        nuint payloadLength,
        CancelHandle cancel,
        nint* outPointer,
        nint* outLength,
        nint* outCapacity);

    /// <summary>Releases a buffer this ABI handed out; a null pointer is a no-op on the native side.</summary>
    [LibraryImport(LibraryName, EntryPoint = "rt_free", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void RtFree(nint pointer, nuint length, nuint capacity);

    /// <summary>Returns `{"abi":2,"schemaVersion":2,"crate":"...","core":"..."}`.</summary>
    [LibraryImport(LibraryName, EntryPoint = "rt_version", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static unsafe partial int RtVersion(nint* outPointer, nint* outLength, nint* outCapacity);

    /// <summary>Creates a native cancel token, which is not cancelled; the result is invalid when the native side returns null.</summary>
    [LibraryImport(LibraryName, EntryPoint = "rt_cancel_new")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial CancelHandle RtCancelNew();

    /// <summary>Cancels the token, from any thread, while a call holds it. The call stops at its next check.</summary>
    [LibraryImport(LibraryName, EntryPoint = "rt_cancel")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void RtCancel(CancelHandle handle);

    /// <summary>Releases a token from <c>rt_cancel_new</c>; a null pointer is a no-op on the native side.</summary>
    [LibraryImport(LibraryName, EntryPoint = "rt_cancel_free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void RtCancelFree(nint handle);

    /// <summary>
    /// The library the host could not open. A source checkout stages it with the build scripts while a
    /// package delivers it as a RID asset, so the message names the paths and says which of the two is
    /// missing rather than leaving the runtime's bare library name.
    /// </summary>
    private static DllNotFoundException MissingLibrary(DllNotFoundException inner) =>
        new(
            $"The native library {LibraryFileName} could not be loaded.{Environment.NewLine}"
            + $"Searched:{Environment.NewLine}{DescribeCandidates()}{Environment.NewLine}"
            + "In a source checkout the native library is staged by scripts/build.ps1 (or scripts/build.sh) under src/RustTrafilatura/runtimes/<rid>/native, and the test build copies it beside the test assembly. "
            + "A package consumer instead needs the package's runtimes/<rid>/native asset for this platform; the package ships win-x64, linux-x64, linux-arm64, osx-x64 and osx-arm64.",
            inner);

    /// <summary>
    /// Something with the library's name opened but is not this ABI: a placeholder or a stale build
    /// lands here, and the difference from "you have not built it" is worth a sentence of its own.
    /// </summary>
    private static EntryPointNotFoundException MissingSymbol(EntryPointNotFoundException inner) =>
        new(
            $"The native library {LibraryFileName} loaded but does not export the rt_extract, rt_free, rt_version, rt_cancel_new, rt_cancel and rt_cancel_free symbols, so it is not a build of this ABI - typically a stale or placeholder artifact. Rebuild it with scripts/build.ps1 (or scripts/build.sh).{Environment.NewLine}"
            + $"Looked at:{Environment.NewLine}{DescribeCandidates()}",
            inner);

    /// <summary>
    /// Every path the library could have come from: the application directory, the host RID's package
    /// folder, and every other RID folder that is on disk.
    /// </summary>
    /// <remarks>
    /// Total by construction: it runs while the message for a failure that already happened is being
    /// built, so it must not become a second failure of its own. `Directory.Exists` and `File.Exists`
    /// cannot throw, and the one call that can (listing the RID folders of a `runtimes` directory that
    /// exists but cannot be read) reports that instead of propagating it. The listing is guarded
    /// rather than made silently partial with `IgnoreInaccessible`, because "there is a runtimes
    /// folder I could not read" is exactly what the caller needs to hear.
    /// </remarks>
    private static string DescribeCandidates()
    {
        var candidates = new SortedSet<string>(StringComparer.Ordinal)
        {
            Path.Combine(AppContext.BaseDirectory, LibraryFileName),
            Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", LibraryFileName),
        };

        string? unlistable = null;
        var runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes");
        if (Directory.Exists(runtimes))
        {
            try
            {
                foreach (var rid in Directory.EnumerateDirectories(runtimes))
                {
                    candidates.Add(Path.Combine(rid, "native", LibraryFileName));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                unlistable = $"  {runtimes} (could not be listed: {exception.GetType().Name})";
            }
        }

        var lines = candidates.Select(candidate => $"  {candidate} ({(File.Exists(candidate) ? "present" : "absent")})");
        return string.Join(Environment.NewLine, unlistable is null ? lines : lines.Append(unlistable));
    }

    /// <summary>
    /// The returned buffer as a span over the native bytes, which is what keeps a response from becoming
    /// a full-size managed string. A null pointer is the ABI's "nothing was written", which an empty span
    /// stands for.
    /// </summary>
    /// <remarks>
    /// Length-delimited, so the byte count is the whole contract; a response above the platform's int
    /// limit cannot be viewed at all, so it throws instead of truncating.
    /// </remarks>
    private static unsafe ReadOnlySpan<byte> ResponseSpan(nint pointer, nint length)
    {
        if (pointer == 0 || length == 0)
        {
            return default;
        }

        return new ReadOnlySpan<byte>((void*)pointer, checked((int)length));
    }

    /// <summary>
    /// The version document as a string. It is the text <see cref="Extractor.LibraryVersion"/> parses and
    /// hands out in <see cref="NativeLibraryVersion.Document"/> rather than a copy of a larger payload, so
    /// materializing it is what the API reads.
    /// </summary>
    private static string ReadDocument(nint pointer, nint length)
    {
        if (pointer == 0 || length == 0)
        {
            return string.Empty;
        }

        return Marshal.PtrToStringUTF8(pointer, checked((int)length)) ?? string.Empty;
    }
}
