using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RustTrafilatura;

/// <summary>
/// The native library's C ABI, spelled exactly as `rust/src/lib.rs` exports it.
/// </summary>
/// <remarks>
/// No pointer leaves this file: the callers pass spans and get text back, and every buffer the native
/// side hands out is released here, in a `finally`, with the same `(ptr, len, cap)` it was written
/// with. Responses are length-delimited UTF-8 rather than NUL-terminated, so a response may contain
/// any byte and the caller's payload never becomes a string. The class is internal, and the test
/// assembly is its only other reader through `InternalsVisibleTo`: a raw round trip means
/// <see cref="Extract"/>, and the three imports stay reachable for the ABI's own edge cases.
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

    /// <summary>Extracts from a UTF-8 JSON header and a raw payload, returning the ABI's return code with its UTF-8 JSON response.</summary>
    /// <exception cref="DllNotFoundException">The native library is not where the host resolves libraries from.</exception>
    /// <exception cref="EntryPointNotFoundException">The library that loaded is not a build of this ABI.</exception>
    internal static (int Code, string Json) Extract(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
    {
        nint outPointer = 0;
        nint outLength = 0;
        nint outCapacity = 0;
        int code;

        // Loading is diagnosed by attempting the call, not by a pre-check: the host resolves the
        // library from the application directory *and* from the package layout's
        // runtimes/<rid>/native, so a pre-check that knew only one of the two rejected deployments
        // that work.
        try
        {
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
                        &outPointer,
                        &outLength,
                        &outCapacity);
                }
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

        // The call is deliberately outside the ownership try: a P/Invoke that never reached the native
        // side wrote no buffer, and a release attempted from a finally there would replace the real
        // failure with a second copy of itself. The free covers the read as well, so a response this
        // marshaller cannot read is still released.
        try
        {
            return (code, ReadBuffer(outPointer, outLength));
        }
        finally
        {
            RtFree(outPointer, (nuint)outLength, (nuint)outCapacity);
        }
    }

    /// <summary>Returns the version document as UTF-8 JSON, or throws for a code that carries no buffer.</summary>
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
            return (code, ReadBuffer(outPointer, outLength));
        }
        finally
        {
            RtFree(outPointer, (nuint)outLength, (nuint)outCapacity);
        }
    }

    /// <summary>Returns 0 ok, 1 extraction error, 2 invalid request, 3 a caught panic.</summary>
    [LibraryImport(LibraryName, EntryPoint = "rt_extract", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static unsafe partial int RtExtract(
        byte* header,
        nuint headerLength,
        byte* payload,
        nuint payloadLength,
        nint* outPointer,
        nint* outLength,
        nint* outCapacity);

    /// <summary>Releases a buffer this ABI handed out; a null pointer is a no-op on the native side.</summary>
    [LibraryImport(LibraryName, EntryPoint = "rt_free", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void RtFree(nint pointer, nuint length, nuint capacity);

    /// <summary>Returns `{"abi":1,"schemaVersion":1,"crate":"..."}`.</summary>
    [LibraryImport(LibraryName, EntryPoint = "rt_version", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static unsafe partial int RtVersion(nint* outPointer, nint* outLength, nint* outCapacity);

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
            $"The native library {LibraryFileName} loaded but does not export the rt_extract, rt_free and rt_version symbols, so it is not a build of this ABI - typically a stale or placeholder artifact. Rebuild it with scripts/build.ps1 (or scripts/build.sh).{Environment.NewLine}"
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
    /// Copies the returned buffer into a string; the release stays with the caller's `finally`. A null
    /// pointer is the ABI's "nothing was written", which the marshaller must not be asked to read.
    /// </summary>
    private static string ReadBuffer(nint pointer, nint length)
    {
        if (pointer == 0 || length == 0)
        {
            return string.Empty;
        }

        // Length-delimited, so the byte count is the whole contract; a response above the platform's
        // int limit cannot be read by this marshaller at all, so it throws instead of truncating.
        return Marshal.PtrToStringUTF8(pointer, checked((int)length)) ?? string.Empty;
    }
}
