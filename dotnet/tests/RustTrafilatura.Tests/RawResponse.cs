using System.Text;

namespace RustTrafilatura.Tests;

/// <summary>
/// Readers for the tests that assert on the ABI's own response rather than on a mapped result. Both
/// forms exist because the response is a span over the buffer the ABI handed out: a test that keeps it
/// has to copy it, and a test that reads it as text is only valid inside the call.
/// </summary>
internal static class RawResponse
{
    /// <summary>The whole response as text, for a test that asserts on the envelope itself.</summary>
    internal static (int Code, string Json) Read(int code, ReadOnlySpan<byte> response) =>
        (code, Encoding.UTF8.GetString(response));

    /// <summary>Copies the response, because the span it arrives as dies when the call returns.</summary>
    internal sealed class Capture
    {
        internal byte[] Bytes { get; private set; } = [];

        internal int Read(int code, ReadOnlySpan<byte> response)
        {
            Bytes = response.ToArray();
            return code;
        }
    }
}
