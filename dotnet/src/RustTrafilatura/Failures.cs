using System.Text.Json;

namespace RustTrafilatura;

/// <summary>
/// Turns a non-zero return code and the envelope beside it into the exception a caller catches.
/// </summary>
internal static class Failures
{
    /// <summary>
    /// Turns a non-zero return code into an exception. The code names the class and the response
    /// carries the kind, the message and any context that kind has; a response that cannot be read
    /// still produces an exception rather than a JSON error nobody asked for.
    /// </summary>
    /// <remarks>
    /// Internal so the mapping is assertable for a kind no build of this ABI emits yet; the caller
    /// receives the exception this returns, so its fallbacks matter as much as its kind switch.
    /// </remarks>
    internal static TrafilaturaException Read(int code, string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                return new TrafilaturaException(
                    TrafilaturaErrorKind.Unknown,
                    $"The native library returned code {code} without an error object.");
            }

            return new TrafilaturaException(
                ReadKind(JsonMembers.Text(error, "kind")),
                JsonMembers.Text(error, "message") ?? string.Empty,
                ReadContext(error));
        }
        catch (JsonException exception)
        {
            return new TrafilaturaException(
                TrafilaturaErrorKind.Unknown,
                $"The native library returned code {code} with a response that is not JSON.",
                contextJson: null,
                exception);
        }
    }

    /// <summary>The error object's `context` as JSON text; the kinds without a payload leave the key out.</summary>
    private static string? ReadContext(JsonElement error) =>
        error.TryGetProperty("context", out var context) && context.ValueKind != JsonValueKind.Null
            ? context.GetRawText()
            : null;

    /// <summary>Maps the wire kind onto the enum, and anything this client does not know onto <see cref="TrafilaturaErrorKind.Unknown"/>.</summary>
    private static TrafilaturaErrorKind ReadKind(string? kind) => kind switch
    {
        "Io" => TrafilaturaErrorKind.Io,
        "UnsupportedCharset" => TrafilaturaErrorKind.UnsupportedCharset,
        "CharsetNotDetected" => TrafilaturaErrorKind.CharsetNotDetected,
        "MissingDocument" => TrafilaturaErrorKind.MissingDocument,
        "HtmlLanguage" => TrafilaturaErrorKind.HtmlLanguage,
        "RequiredTitle" => TrafilaturaErrorKind.RequiredTitle,
        "RequiredUrl" => TrafilaturaErrorKind.RequiredUrl,
        "RequiredDate" => TrafilaturaErrorKind.RequiredDate,
        "OutputTreeTooLong" => TrafilaturaErrorKind.OutputTreeTooLong,
        "OutputTooShort" => TrafilaturaErrorKind.OutputTooShort,
        "DuplicateBody" => TrafilaturaErrorKind.DuplicateBody,
        "WrongLanguage" => TrafilaturaErrorKind.WrongLanguage,
        "InvalidUrl" => TrafilaturaErrorKind.InvalidUrl,
        "InvalidRequest" => TrafilaturaErrorKind.InvalidRequest,
        "Panic" => TrafilaturaErrorKind.Panic,
        _ => TrafilaturaErrorKind.Unknown,
    };
}
