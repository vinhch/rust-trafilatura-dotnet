using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace CompileProbe;

// One case per -p:DefineConstants=CASE_..., never two at once. A case that is expected to fail fails
// with the diagnostic the documents quote; a case that compiles prints the overload it selected or
// the bytes it produced. With no define, Run() has no implementation, the call below is removed and
// the build is green.
internal static partial class Cases
{
    static partial void Run();

    private static void Main() => Run();

    private static void Report(string label, string resolved) =>
        Console.WriteLine($"{label} -> {resolved}");

    private static byte[] Value() => new byte[1];

    private static byte[]? NullArray() => null;

#if CASE_LANGVER
#error version
#endif

#if CASE_SHAPE_SPAN_ONLY

    private static string Shape(ReadOnlySpan<byte> payload) => "span";

    static partial void Run()
    {
        Report("byte[] value", Shape(Value()));
        Report("byte[]? holding null", Shape(NullArray()));
        Report("null literal", Shape(null));
    }

#endif

#if CASE_SHAPE_SPAN_NAMED_MEMORY

    private static string Shape(ReadOnlySpan<byte> payload) => "span";

    private static string ShapeMemory(ReadOnlyMemory<byte> payload) => "memory";

    static partial void Run()
    {
        Report("byte[] value", Shape(Value()));
        Report("byte[]? holding null", Shape(NullArray()));
        Report("null literal", Shape(null));
    }

#endif

#if CASE_SHAPE_ARRAY_SPAN

    private static string Shape(byte[] payload) => "array";

    private static string Shape(ReadOnlySpan<byte> payload) => "span";

    static partial void Run()
    {
        Report("byte[] value", Shape(Value()));
        Report("byte[]? holding null", Shape(NullArray()));
        Report("null literal", Shape(null));
    }

#endif

#if CASE_SHAPE_SPAN_MEMORY

    private static string Shape(ReadOnlySpan<byte> payload) => "span";

    private static string Shape(ReadOnlyMemory<byte> payload) => "memory";

    static partial void Run()
    {
        Report("byte[] value", Shape(Value()));
        Report("byte[]? holding null", Shape(NullArray()));
        Report("null literal", Shape(null));
    }

#endif

#if CASE_SHAPE_SPAN_MEMORY_NO_NULL

    // CASE_SHAPE_SPAN_MEMORY's pair with its null-literal call site left out: that call site is the
    // one that stays ambiguous at C# 14, so the typed call sites are where the matrix's first two
    // cells come from at those configurations.
    private static string Shape(ReadOnlySpan<byte> payload) => "span";

    private static string Shape(ReadOnlyMemory<byte> payload) => "memory";

    static partial void Run()
    {
        Report("byte[] value", Shape(Value()));
        Report("byte[]? holding null", Shape(NullArray()));
    }

#endif

#if CASE_SHAPE_ARRAY_SPAN_MEMORY

    private static string Shape(byte[] payload) => "array";

    private static string Shape(ReadOnlySpan<byte> payload) => "span";

    private static string Shape(ReadOnlyMemory<byte> payload) => "memory";

    static partial void Run()
    {
        Report("byte[] value", Shape(Value()));
        Report("byte[]? holding null", Shape(NullArray()));
        Report("null literal", Shape(null));
    }

#endif

#if CASE_SHAPE_MEMORY_ONLY

    private static string Shape(ReadOnlyMemory<byte> payload) => "memory";

    static partial void Run()
    {
        Report("byte[] value", Shape(Value()));
        Report("byte[]? holding null", Shape(NullArray()));
        Report("null literal", Shape(null));
    }

#endif

#if CASE_BATCH_PAIR

    private static string Batch(IReadOnlyList<byte[]> pages) => "array";

    private static string Batch(IReadOnlyList<ReadOnlyMemory<byte>> pages) => "memory";

    static partial void Run()
    {
        List<byte[]> list = new() { Value() };
        byte[][] jagged = { Value() };
        List<ReadOnlyMemory<byte>> memory = new() { Value() };
        Report("List<byte[]>", Batch(list));
        Report("byte[][]", Batch(jagged));
        Report("List<ReadOnlyMemory<byte>>", Batch(memory));
    }

#endif

#if CASE_BATCH_MEMORY_ONLY

    private static string Batch(IReadOnlyList<ReadOnlyMemory<byte>> pages) => "memory";

    static partial void Run()
    {
        List<byte[]> list = new() { Value() };
        byte[][] jagged = { Value() };
        Report("List<byte[]>", Batch(list));
        Report("byte[][]", Batch(jagged));
    }

#endif

#if CASE_ASYNC_SPAN_BEFORE

    private static async Task AsyncSpanBefore()
    {
        byte[] value = Value();
        ReadOnlySpan<byte> span = value;
        Report("span declared and read before the await", span.Length.ToString());
        await Task.Yield();
    }

    static partial void Run() => AsyncSpanBefore().GetAwaiter().GetResult();

#endif

#if CASE_ASYNC_SPAN_AFTER

    private static async Task AsyncSpanAfter()
    {
        await Task.Yield();
        byte[] value = Value();
        ReadOnlySpan<byte> span = value;
        Report("span declared and read after the await", span.Length.ToString());
    }

    static partial void Run() => AsyncSpanAfter().GetAwaiter().GetResult();

#endif

#if CASE_ASYNC_SPAN_ACROSS

    private static async Task AsyncSpanAcross()
    {
        byte[] value = Value();
        ReadOnlySpan<byte> span = value;
        await Task.Yield();
        Report("span declared before the await and read after it", span.Length.ToString());
    }

    static partial void Run() => AsyncSpanAcross().GetAwaiter().GetResult();

#endif

#if CASE_ASYNC_STACKALLOC

    private static async Task AsyncStackAlloc()
    {
        Span<byte> span = stackalloc byte[8];
        Report("stackalloc held in a span local", span.Length.ToString());
        await Task.Yield();
    }

    static partial void Run() => AsyncStackAlloc().GetAwaiter().GetResult();

#endif

#if CASE_ASYNC_MEMORY_ACROSS

    private static async Task AsyncMemoryAcross()
    {
        ReadOnlyMemory<byte> memory = Value();
        await Task.Yield();
        Report("memory held across the await, .Span taken after it", memory.Span.Length.ToString());
    }

    static partial void Run() => AsyncMemoryAcross().GetAwaiter().GetResult();

#endif

#if CASE_ASYNC_ARRAY_AFTER

    private static async Task AsyncArrayAfter()
    {
        byte[] value = Value();
        await Task.Yield();
        Report("byte[] read after the await", value.Length.ToString());
    }

    static partial void Run() => AsyncArrayAfter().GetAwaiter().GetResult();

#endif

#if CASE_FUNC_SPAN

    static partial void Run()
    {
        Func<ReadOnlySpan<byte>, string> reader = payload => payload.Length.ToString();
        Report("Func<ReadOnlySpan<byte>, string>", reader(Value()));
    }

#endif

#if CASE_CUSTOM_DELEGATE

    internal delegate string Reader(ReadOnlySpan<byte> payload);

    static partial void Run()
    {
        Reader reader = payload => payload.Length.ToString();
        Report("custom delegate with a span parameter", reader(Value()));
    }

#endif

#if CASE_VARIANCE

    static partial void Run()
    {
        IReadOnlyList<ReadOnlyMemory<byte>> pages = new List<byte[]>();
        Report("List<byte[]> to IReadOnlyList<ReadOnlyMemory<byte>>", pages.Count.ToString());
    }

#endif

#if CASE_STACKALLOC_MEMORY

#if STACKALLOC_DIRECT
    static partial void Run()
    {
        ReadOnlyMemory<byte> memory = stackalloc byte[8];
        Report("stackalloc to ReadOnlyMemory<byte>", memory.Length.ToString());
    }
#else
    static partial void Run()
    {
        Span<byte> span = stackalloc byte[8];
        ReadOnlyMemory<byte> memory = span;
        Report("stackalloc to ReadOnlyMemory<byte>", memory.Length.ToString());
    }
#endif

#endif

#if CASE_GENERIC_CONVERTER

    internal enum WireFocus
    {
        Balanced,
        FavorRecall,
        FavorPrecision,
    }

    static partial void Run()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter<WireFocus>(JsonNamingPolicy.CamelCase) },
        };
        Console.WriteLine(JsonSerializer.Serialize(new { focus = WireFocus.FavorPrecision }, options));
    }

#endif

#if CASE_HEADER

    internal sealed class HeaderOptions
    {
        public Focus? Focus { get; init; }

        public HtmlDateMode? HtmlDateMode { get; init; }
    }

    internal sealed class RequestHeader
    {
        public int SchemaVersion { get; init; } = 1;

        public HeaderOptions? Options { get; init; }
    }

    private static RequestHeader HeaderValue() => new()
    {
        Options = new HeaderOptions
        {
            Focus = Focus.FavorPrecision,
            HtmlDateMode = HtmlDateMode.Extensive,
        },
    };

#if NET9_0_OR_GREATER

    internal enum Focus
    {
        [JsonStringEnumMemberName("balanced")] Balanced,

        [JsonStringEnumMemberName("favorRecall")] FavorRecall,

        [JsonStringEnumMemberName("favorPrecision")] FavorPrecision,
    }

    internal enum HtmlDateMode
    {
        [JsonStringEnumMemberName("default")] Default,

        [JsonStringEnumMemberName("fast")] Fast,

        [JsonStringEnumMemberName("extensive")] Extensive,

        [JsonStringEnumMemberName("disabled")] Disabled,
    }

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UseStringEnumConverter = true)]
    [JsonSerializable(typeof(RequestHeader))]
    internal sealed partial class HeaderContext : JsonSerializerContext
    {
    }

    static partial void Run() =>
        Console.WriteLine(Encoding.UTF8.GetString(
            JsonSerializer.SerializeToUtf8Bytes(HeaderValue(), HeaderContext.Default.RequestHeader)));

#else

    internal enum Focus
    {
        Balanced,
        FavorRecall,
        FavorPrecision,
    }

    internal enum HtmlDateMode
    {
        Default,
        Fast,
        Extensive,
        Disabled,
    }

    internal sealed class FocusConverter : JsonConverter<Focus>
    {
        public override Focus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString() switch
            {
                "favorRecall" => Focus.FavorRecall,
                "favorPrecision" => Focus.FavorPrecision,
                _ => Focus.Balanced,
            };

        public override void Write(Utf8JsonWriter writer, Focus value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value switch
            {
                Focus.FavorRecall => "favorRecall",
                Focus.FavorPrecision => "favorPrecision",
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

    static partial void Run()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new FocusConverter(), new HtmlDateModeConverter() },
        };
        Console.WriteLine(Encoding.UTF8.GetString(
            JsonSerializer.SerializeToUtf8Bytes(HeaderValue(), options)));
    }

#endif

#endif
}
