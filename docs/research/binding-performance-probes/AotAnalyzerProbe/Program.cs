using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AotAnalyzerProbe;

#if CASE_LIBRARYIMPORT

// The package's own import shape, under the same analyzers: must produce no warning.
internal static partial class Native
{
    [LibraryImport("rust_trafilatura_ffi", EntryPoint = "rt_extract", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int RtExtract(
        byte* header,
        nuint headerLength,
        byte* payload,
        nuint payloadLength,
        nint* outPointer,
        nint* outLength,
        nint* outCapacity);
}

#endif

public enum ExtractionFocus
{
    Balanced,
    FavorRecall,
    FavorPrecision,
}

public enum HtmlDateMode
{
    Default,
    Fast,
    Extensive,
    Disabled,
}

internal sealed class RequestOptions
{
    public ExtractionFocus? Focus { get; init; }

    public HtmlDateMode? HtmlDateMode { get; init; }
}

internal sealed class RequestHeader
{
    public int SchemaVersion { get; init; } = 1;

    public string? Url { get; init; }

    public List<string>? ResponseFields { get; init; }

    public RequestOptions? Options { get; init; }
}

#if CASE_CONTEXT

// The shape section 8 proposes: one context covering the header, per-enum converters that write the
// core's spellings by hand, and the JsonTypeInfo overload. Must produce neither IL2026 nor IL3050.
internal sealed class FocusConverter : JsonConverter<ExtractionFocus>
{
    public override ExtractionFocus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "favorRecall" => ExtractionFocus.FavorRecall,
            "favorPrecision" => ExtractionFocus.FavorPrecision,
            _ => ExtractionFocus.Balanced,
        };

    public override void Write(Utf8JsonWriter writer, ExtractionFocus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            ExtractionFocus.FavorRecall => "favorRecall",
            ExtractionFocus.FavorPrecision => "favorPrecision",
            _ => "balanced",
        });
}

internal sealed class HtmlDateModeConverter : JsonConverter<HtmlDateMode>
{
    public override HtmlDateMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "fast" => HtmlDateMode.Fast,
            "extensive" => HtmlDateMode.Extensive,
            "disabled" => HtmlDateMode.Disabled,
            _ => HtmlDateMode.Default,
        };

    public override void Write(Utf8JsonWriter writer, HtmlDateMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            HtmlDateMode.Fast => "fast",
            HtmlDateMode.Extensive => "extensive",
            HtmlDateMode.Disabled => "disabled",
            _ => "default",
        });
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = new[] { typeof(FocusConverter), typeof(HtmlDateModeConverter) })]
[JsonSerializable(typeof(RequestHeader))]
internal sealed partial class HeaderContext : JsonSerializerContext
{
}

internal static class Build
{
    internal static byte[] Header(RequestHeader header) =>
        JsonSerializer.SerializeToUtf8Bytes(header, HeaderContext.Default.RequestHeader);
}

#elif CASE_SERIALIZATION_MODE

// The fast-path generation mode, reached through a resolver-based options object. Builds clean and
// throws at run time: the generated JsonTypeInfo carries no property metadata.
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Serialization,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RequestHeader))]
internal sealed partial class HeaderContext : JsonSerializerContext
{
}

internal static class Build
{
    internal static byte[] Header(RequestHeader header) =>
        JsonSerializer.SerializeToUtf8Bytes(
            header,
            new JsonSerializerOptions { TypeInfoResolver = HeaderContext.Default });
}

#elif CASE_LIBRARYIMPORT

// No serializer at all: the only thing under the analyzers is the pointer-parameter import above.
internal static class Build
{
    internal static byte[] Header(RequestHeader header) => Array.Empty<byte>();
}

#else

// The shape the package ships today: reflection-based serialization plus the non-generic enum
// converter. Must report IL2026 once and IL3050 twice.
internal static class Build
{
    private static readonly JsonSerializerOptions HeaderJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static byte[] Header(RequestHeader header) =>
        JsonSerializer.SerializeToUtf8Bytes(header, HeaderJsonOptions);
}

#endif

internal static class Program
{
    private static void Main()
    {
        byte[] bytes = Build.Header(new RequestHeader
        {
            Url = "https://example.test/page",
            ResponseFields = new List<string> { "contentText" },
            Options = new RequestOptions
            {
                Focus = ExtractionFocus.FavorPrecision,
                HtmlDateMode = HtmlDateMode.Extensive,
            },
        });

        Console.WriteLine(System.Text.Encoding.UTF8.GetString(bytes));
    }
}
