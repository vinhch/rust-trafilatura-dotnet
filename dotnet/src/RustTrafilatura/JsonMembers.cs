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
}
