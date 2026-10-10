using System.Text.Json;

namespace RustTrafilatura;

/// <summary>
/// Reads one member out of a parsed JSON object, with the kind check the version document and the
/// error envelope both need.
/// </summary>
internal static class JsonMembers
{
    /// <summary>A string member, or null when it is absent or is not a string.</summary>
    /// <remarks>
    /// The response path keeps the "not requested" (JSON null) and "nothing found" (empty string)
    /// distinction in `Response.ReadStringValue`, which is the only reader that has to tell them apart.
    /// </remarks>
    internal static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// An integer member of a JSON object, or null when the parent is not an object, the member is
    /// absent, its value is not a number, or it does not fit an `int` this client can compare.
    /// </summary>
    internal static int? Number(JsonElement document, string name) =>
        document.ValueKind == JsonValueKind.Object
            && document.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;
}
