using System.Text.Json;
using System.Text.Json.Serialization;

namespace RustTrafilatura;

#if !NET9_0_OR_GREATER

/// <summary>Writes <see cref="ExtractionFocus"/> with the spellings the core accepts.</summary>
/// <remarks>
/// `net9.0` and later spell the two enums from `[JsonStringEnumMemberName]`, which removes this pair
/// there. On `net8.0` the hand-written converters are the shape the AOT probe compiled with zero
/// `IL2026`/`IL3050`. `JsonStringEnumConverter&lt;TEnum&gt;` is analyzer-clean there as well, but the
/// `Converters` array below can only name a type, and its parameterless construction writes
/// `FavorPrecision` where the wire contract wants `favorPrecision`.
/// Each `Read` accepts the two forms its `Write` produces and throws `JsonException` on anything else.
/// Nothing in this package deserializes a header today, so the throw exists to keep a later read path
/// from answering with a member the caller never sent.
/// </remarks>
internal sealed class FocusConverter : JsonConverter<ExtractionFocus>
{
    public override ExtractionFocus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => (ExtractionFocus)reader.GetInt32(),
            JsonTokenType.String => reader.GetString() switch
            {
                "balanced" => ExtractionFocus.Balanced,
                "favorRecall" => ExtractionFocus.FavorRecall,
                "favorPrecision" => ExtractionFocus.FavorPrecision,
                _ => throw new JsonException("Unknown ExtractionFocus spelling."),
            },
            _ => throw new JsonException("ExtractionFocus must be a spelling or a number."),
        };

    public override void Write(Utf8JsonWriter writer, ExtractionFocus value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case ExtractionFocus.Balanced:
                writer.WriteStringValue("balanced");
                break;
            case ExtractionFocus.FavorRecall:
                writer.WriteStringValue("favorRecall");
                break;
            case ExtractionFocus.FavorPrecision:
                writer.WriteStringValue("favorPrecision");
                break;
            default:
                // A value outside the declared members has no spelling the core accepts, so it crosses as
                // the number the caller cast: the core rejects that request, while a substituted name
                // would turn it into a different request the core answers.
                writer.WriteNumberValue((int)value);
                break;
        }
    }
}

/// <summary>Writes <see cref="HtmlDateMode"/> with the spellings the core accepts.</summary>
/// <remarks>
/// The same contract as <see cref="FocusConverter"/>: a declared member crosses under the core's
/// spelling, and a value outside the members crosses as its number.
/// </remarks>
internal sealed class HtmlDateModeConverter : JsonConverter<HtmlDateMode>
{
    public override HtmlDateMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => (HtmlDateMode)reader.GetInt32(),
            JsonTokenType.String => reader.GetString() switch
            {
                "default" => HtmlDateMode.Default,
                "fast" => HtmlDateMode.Fast,
                "extensive" => HtmlDateMode.Extensive,
                "disabled" => HtmlDateMode.Disabled,
                _ => throw new JsonException("Unknown HtmlDateMode spelling."),
            },
            _ => throw new JsonException("HtmlDateMode must be a spelling or a number."),
        };

    public override void Write(Utf8JsonWriter writer, HtmlDateMode value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case HtmlDateMode.Default:
                writer.WriteStringValue("default");
                break;
            case HtmlDateMode.Fast:
                writer.WriteStringValue("fast");
                break;
            case HtmlDateMode.Extensive:
                writer.WriteStringValue("extensive");
                break;
            case HtmlDateMode.Disabled:
                writer.WriteStringValue("disabled");
                break;
            default:
                writer.WriteNumberValue((int)value);
                break;
        }
    }
}

#endif

/// <summary>
/// The wire header. The context below gives it camelCase names and drops unset members, so an option
/// the caller left alone cannot shadow the native library's default.
/// </summary>
internal sealed class RequestHeader
{
    public int SchemaVersion { get; init; } = 1;

    public string? Url { get; init; }

    public string? InputEncoding { get; init; }

    public List<string>? ResponseFields { get; init; }

    public long? TimeBudgetMs { get; init; }

    public ExtractionOptions? Options { get; init; }
}

/// <summary>
/// Generates the header's serialization at build time, so the request path reflects over nothing and
/// the package keeps working when a consumer trims it or publishes it with Native AOT.
/// </summary>
/// <remarks>
/// The generation mode is the default metadata mode on purpose: `JsonSourceGenerationMode.Serialization`
/// generates a `JsonTypeInfo` with no property metadata, so reaching it through a resolver throws at run
/// time. The metadata the default mode emits is what the `JsonTypeInfo` overload of
/// `JsonSerializer.SerializeToUtf8Bytes` needs.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
#if NET9_0_OR_GREATER
    UseStringEnumConverter = true
#else
    Converters = new[] { typeof(FocusConverter), typeof(HtmlDateModeConverter) }
#endif
)]
[JsonSerializable(typeof(RequestHeader))]
internal sealed partial class HeaderContext : JsonSerializerContext
{
}
