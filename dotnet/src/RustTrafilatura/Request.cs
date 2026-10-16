using System.Text.Json;

namespace RustTrafilatura;

/// <summary>
/// The header of one request, in the two shapes this layer needs: the bytes that cross the ABI and
/// the JSON a caller reads.
/// </summary>
internal static class Request
{
    /// <summary>
    /// Assembles the request header as the bytes that cross the ABI. `responseFields`, `url` and
    /// `inputEncoding` belong at its top level while every core option belongs inside `options`, and a
    /// member that was not set is left out so the native side applies its own default instead of a
    /// value this layer invented.
    /// </summary>
    /// <remarks>Internal so the header's bytes, not only its parsed shape, are assertable.</remarks>
    internal static byte[] Header(ExtractionOptions? options) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new RequestHeader
            {
                ResponseFields = options?.ResponseFields,
                Url = options?.Url,
                InputEncoding = options?.InputEncoding,
                TimeBudgetMs = options?.TimeBudgetMs,
                Options = options,
            },
            HeaderContext.Default.RequestHeader);

    /// <summary>
    /// The request header as JSON, for a caller that needs to see the wire shape itself. The element is
    /// cloned and the document disposed: the element's own buffers come from a pool and are returned
    /// when the document is, so keeping the element without the clone would hand back a value that
    /// dies with it.
    /// </summary>
    internal static JsonElement HeaderJson(ExtractionOptions? options)
    {
        using var document = JsonDocument.Parse(Header(options));
        return document.RootElement.Clone();
    }
}
