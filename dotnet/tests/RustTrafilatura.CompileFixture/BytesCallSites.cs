using RustTrafilatura;

namespace RustTrafilatura.CompileFixture;

/// <summary>
/// Every byte-surface call site a consumer has, written the way that consumer writes it. Nothing here
/// runs: the fixture exists so that the compiler rejects a change to the surface, and its build is the
/// test. The call sites are deliberately not wrapped in their own methods with inferred types, because
/// what has to be proved is that each expression is unambiguous as written at the call site.
/// </summary>
internal static class BytesCallSites
{
    private static void Main()
    {
        // A byte array the caller already holds: it converts to ReadOnlyMemory<byte> implicitly, so
        // the call site that compiled against the array overload still compiles and still costs no
        // allocation for the conversion.
        byte[] page = "Content"u8.ToArray();
        ExtractionResult fromArray = Extractor.ExtractFromBytes(page);

        // A null array is not an argument error any more: it converts to an empty view and reaches
        // the core, which reports its own error. The type has to be written out because null has none.
        byte[]? absent = null;
        ExtractionResult fromNullArray = Extractor.ExtractFromBytes(absent!);

        // The null literal itself, which binds to the memory parameter because it is the only one.
        ExtractionResult fromNullLiteral = Extractor.ExtractFromBytes(null!);

        // Memory of the caller's own, which is the shape the memory entry point is declared with.
        ReadOnlyMemory<byte> view = page.AsMemory(0, page.Length);
        ExtractionResult fromMemory = Extractor.ExtractFromBytes(view);
        ExtractionResult fromSliceWithOptions = Extractor.ExtractFromBytes(
            view,
            new ExtractionOptions { ResponseFields = ["contentText"] });

        // The span entry point, which is how a buffer no memory view can express arrives: a
        // `stackalloc` one here, and unmanaged memory in the block below.
        ExtractionResult fromSpan = Extractor.ExtractFromSpan(page);
        ExtractionResult fromStackAlloc = Extractor.ExtractFromSpan(stackalloc byte[8]);
        ExtractionResult fromMemorySpan = Extractor.ExtractFromSpan(view.Span);

        // A string stays a string call site.
        ExtractionResult fromString = Extractor.ExtractFromHtml("<p>hi</p>");

        // The batch keeps both overloads, so each collection a caller holds binds to the one for it.
        IReadOnlyList<ExtractionBatchItem> fromListOfArrays = Extractor.ExtractMany(new List<byte[]> { page });
        IReadOnlyList<ExtractionBatchItem> fromArrayOfArrays = Extractor.ExtractMany(new[] { page, page });
        IReadOnlyList<ExtractionBatchItem> fromMemoryList = Extractor.ExtractMany(
            new List<ReadOnlyMemory<byte>> { view });

        // Unmanaged memory reaches the same entry point as a span rather than through an adapter type,
        // and the caller keeps ownership of the buffer. Forming the span needs unsafe at the call site.
        unsafe
        {
            byte* pointer = null;
            ExtractionResult fromUnmanaged = Extractor.ExtractFromSpan(new ReadOnlySpan<byte>(pointer, 0));
            _ = fromUnmanaged;
        }

        _ = (fromArray, fromNullArray, fromNullLiteral, fromMemory, fromSliceWithOptions, fromSpan, fromStackAlloc, fromMemorySpan, fromString);
        _ = (fromListOfArrays, fromArrayOfArrays, fromMemoryList);
    }
}
