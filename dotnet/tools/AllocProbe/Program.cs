using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RustTrafilatura.AllocProbe;

/// <summary>
/// Prints the managed bytes one extraction call allocates through the public API, on the fixture the
/// Rust round-trip bench uses, so the allocation figure in the specification has a command that
/// reproduces it.
/// </summary>
/// <remarks>
/// Two paths are measured over the same page: the shipped read, which takes the response as a
/// <see cref="Utf8JsonReader"/> over the native bytes, and the string-envelope read it replaced,
/// which materializes the whole response with <see cref="Marshal.PtrToStringUTF8"/> and parses it with
/// <see cref="JsonDocument"/>. Measuring both is what makes the saving checkable instead of asserted,
/// and it is why that read is reconstructed here rather than left to the commit that removed it.
///
/// The ABI is declared here as well as in the package because the package's interop is internal: this
/// tool has to hold the raw response bytes, which is exactly what the public API no longer exposes.
/// The declarations mirror rust/src/lib.rs.
///
/// Nothing here is a test: scripts/bench.ps1 and scripts/bench.sh run it beside the Rust bench, and a
/// regression in the read path's allocation shows up as its exit code.
/// </remarks>
internal static class Program
{
    /// <summary>The size the generator aims at, the bench's own constant.</summary>
    private const int FixtureBytes = 200 * 1024;

    /// <summary>
    /// The length the generator produces, so a drift in either generator fails here instead of showing
    /// up as two numbers nobody can compare. rust/benches/roundtrip.rs asserts the same value.
    /// </summary>
    private const int FixtureLength = 206_687;

    /// <summary>
    /// FNV-1a 64 over the fixture's UTF-8 bytes. The bench prints no hash of its own, so this pin was
    /// taken by running the bench's generator through a scratch `rustc` build of the same file: the
    /// length assertion alone would miss a changed document of unchanged length.
    /// </summary>
    private const ulong FixtureFingerprint = 0xbe525fc10a5ad3f0UL;

    private const int WarmupCalls = 5;
    private const int MeasuredCalls = 20;
    private const int Runs = 5;

    /// <summary>The bench's pinned header: the FAST path, so the measured call is the one the spec's table describes.</summary>
    private static readonly byte[] Header = "{\"options\":{\"enableFallback\":false}}"u8.ToArray();

    /// <summary>
    /// The configuration this file was compiled in, so the profile printed below is the profile
    /// measured. A field rather than a constant: a constant would make the check below a constant
    /// condition, and the compiler would warn that its failure branch is unreachable in Release.
    /// </summary>
#if DEBUG
    private static readonly string Configuration = "Debug";
#else
    private static readonly string Configuration = "Release";
#endif

    private static int Main()
    {
        // A debug-profile number would be meaningless, and printing "Release" over one would be worse:
        // scripts/bench.* always pass --configuration Release, so this only catches a hand-run.
        if (Configuration != "Release")
        {
            return Fail("these are release numbers: run this with dotnet run --configuration Release");
        }

        var payload = Encoding.UTF8.GetBytes(Fixture());

        if (payload.Length != FixtureLength)
        {
            return Fail($"the fixture generator changed: {payload.Length} bytes, expected {FixtureLength} from rust/benches/roundtrip.rs");
        }

        var fingerprint = Fnv1a(payload);
        if (fingerprint != FixtureFingerprint)
        {
            return Fail($"the fixture is not the bench's document: FNV-1a 64 0x{fingerprint:x16}, expected 0x{FixtureFingerprint:x16}");
        }

        var (responseBytes, envelopeText) = Envelope(Header, payload);
        if (envelopeText is null)
        {
            return Fail("the string-envelope read found no contentText in the response");
        }

        var options = new ExtractionOptions { EnableFallback = false };
        var shipped = Extractor.ExtractFromBytes(payload, options);
        if (!string.Equals(shipped.ContentText, envelopeText, StringComparison.Ordinal))
        {
            return Fail("the two paths read different documents, so their allocations are not comparable");
        }

        Console.WriteLine($"probe: fixture    {payload.Length} bytes, FNV-1a 64 0x{fingerprint:x16} (rust/benches/roundtrip.rs)");
        Console.WriteLine($"probe: header     {Encoding.UTF8.GetString(Header)}");
        Console.WriteLine($"probe: response   {responseBytes} bytes, contentText {envelopeText.Length} characters");
        Console.WriteLine($"probe: profile    {Configuration}, {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"probe: method     {MeasuredCalls} calls per run, {Runs} runs, after {WarmupCalls} warmup calls, GC.GetAllocatedBytesForCurrentThread");
        Console.WriteLine("probe: note       the shipped path also builds its request header per call; the string-envelope path posts a fixed header literal");

        var shippedRuns = Measure(() => Extractor.ExtractFromBytes(payload, options));
        var envelopeRuns = Measure(() => Envelope(Header, payload));

        Report("shipped", shippedRuns);
        Report("envelope", envelopeRuns);
        // Paired per run, so both paths meet the same machine state in run n.
        var savings = new long[Runs];
        for (var run = 0; run < Runs; run++)
        {
            savings[run] = envelopeRuns[run] - shippedRuns[run];
        }

        Report("saving", savings);

        if (shippedRuns.Max() >= envelopeRuns.Min())
        {
            return Fail("the shipped read path allocates as much as the string envelope it replaced");
        }

        Console.WriteLine("probe: PASS the shipped read path allocates less per call than the string envelope it replaced");
        return 0;
    }

    /// <summary>
    /// The bytes each call allocates on this thread, once per run, after the warmup calls have taken
    /// the first-call costs (JIT, the first response buffer, the source-generated header serializer's
    /// own initialization) out of the measured loop.
    /// </summary>
    private static long[] Measure(Action call)
    {
        for (var warmup = 0; warmup < WarmupCalls; warmup++)
        {
            call();
        }

        var perCall = new long[Runs];
        for (var run = 0; run < Runs; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < MeasuredCalls; iteration++)
            {
                call();
            }

            perCall[run] = (GC.GetAllocatedBytesForCurrentThread() - before) / MeasuredCalls;
        }

        return perCall;
    }

    private static void Report(string name, long[] runs) =>
        Console.WriteLine($"probe: {name,-9} {runs.Min()}-{runs.Max()} B/call (per run: {string.Join(", ", runs)})");

    /// <summary>
    /// One response read the way the package did before the read moved onto the native bytes: the whole
    /// response as a managed string, then a <see cref="JsonDocument"/> over it, then the one field this
    /// comparison needs. The free is inside the measurement, as it is for the shipped path.
    /// </summary>
    private static unsafe (int Bytes, string? Text) Envelope(byte[] header, byte[] payload)
    {
        nint pointer = 0;
        nint length = 0;
        nint capacity = 0;

        fixed (byte* headerPointer = header)
        fixed (byte* payloadPointer = payload)
        {
            var code = Native.RtExtract(
                headerPointer,
                (nuint)header.Length,
                payloadPointer,
                (nuint)payload.Length,
                &pointer,
                &length,
                &capacity);
            if (code != 0)
            {
                Native.RtFree(pointer, (nuint)length, (nuint)capacity);
                throw new InvalidOperationException($"rt_extract returned {code}");
            }
        }

        try
        {
            // What the removed path did: the response becomes a full-size UTF-16 string first, and the
            // JSON document is then parsed out of that string.
            var response = Marshal.PtrToStringUTF8(pointer, checked((int)length)) ?? string.Empty;
            using var document = JsonDocument.Parse(response);
            return (checked((int)length), document.RootElement.GetProperty("contentText").GetString());
        }
        finally
        {
            Native.RtFree(pointer, (nuint)length, (nuint)capacity);
        }
    }

    /// <summary>
    /// The round-trip bench fixture, byte for byte: rust/benches/roundtrip.rs generates the same
    /// document from the same templates. Every character is ASCII, so this string's UTF-8 bytes are the
    /// bytes that generator produces, and the length and fingerprint assertions above are what keep the
    /// two in step.
    /// </summary>
    private static string Fixture()
    {
        string[] sentences =
        [
            "The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published.",
            "Officials said the new framework will take effect next spring and that compliance will be reviewed annually.",
            "Critics argued the timeline is too tight for smaller publishers and asked for the reporting burden to scale with the size of the organisation.",
            "Supporters replied that a public report each year is the least that makes the framework checkable by anyone outside the room.",
        ];

        var html = new StringBuilder(FixtureBytes + 2048);
        html.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Round-trip fixture</title><meta name=\"author\" content=\"Ada Lovelace\"><meta name=\"description\" content=\"A deterministic fixture for the round-trip bench.\"><meta property=\"og:site_name\" content=\"Example Journal\"><meta property=\"article:published_time\" content=\"2026-10-08T00:00:00Z\"></head><body>");
        html.Append("<nav><a href=\"/\">Home</a><a href=\"/news\">News</a><a href=\"/about\">About</a></nav>");
        html.Append("<main><article><h1>Round-trip fixture</h1>");
        html.Append("<figure><img src=\"/photo.jpg\" alt=\"A council chamber\"><figcaption>The council chamber.</figcaption></figure>");

        var paragraph = 0;
        while (html.Length < FixtureBytes)
        {
            var sentence = sentences[paragraph % sentences.Length];
            var next = sentences[(paragraph + 1) % sentences.Length];
            html.Append(CultureInfo.InvariantCulture, $"<p>Paragraph {paragraph}: {sentence} {next}</p>");
            if (paragraph % 16 == 0)
            {
                html.Append("<table><tr><th>Year</th><th>Reports</th></tr><tr><td>2026</td><td>12</td></tr><tr><td>2027</td><td>14</td></tr></table>");
            }

            paragraph++;
        }

        html.Append("</article><div id=\"comments\">");
        for (var comment = 0; comment < 8; comment++)
        {
            html.Append(CultureInfo.InvariantCulture, $"<div class=\"comment\"><p>Comment {comment}: the annual review is a sensible compromise, and I would rather see the framework start next spring than wait for a version that never arrives.</p></div>");
        }

        html.Append("</div></main><footer><p>Copyright 2026 Example Journal. All rights reserved.</p></footer></body></html>");
        return html.ToString();
    }

    /// <summary>FNV-1a 64, the cheap cross-language pin that needs no dependency on either side.</summary>
    private static ulong Fnv1a(ReadOnlySpan<byte> bytes)
    {
        var hash = 14695981039346656037UL;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"probe: FAIL {message}");
        return 1;
    }
}

/// <summary>The library's C exports, declared as the package declares them.</summary>
/// <remarks>
/// A sibling type rather than a nested one: the interop source generator needs every containing type
/// to be partial, and <see cref="Program"/> is the entry point, not a generated-partial host.
/// </remarks>
internal static partial class Native
{
    private const string LibraryName = "rust_trafilatura_ffi";

    [LibraryImport(LibraryName, EntryPoint = "rt_extract")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static unsafe partial int RtExtract(
        byte* header,
        nuint headerLength,
        byte* payload,
        nuint payloadLength,
        nint* outPointer,
        nint* outLength,
        nint* outCapacity);

    [LibraryImport(LibraryName, EntryPoint = "rt_free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void RtFree(nint pointer, nuint length, nuint capacity);
}
