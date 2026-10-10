using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace RustTrafilatura;

/// <summary>
/// Extracts text, comments, HTML and metadata from a page. One call per page, with no state to hold
/// or dispose between calls.
/// </summary>
public static class Extractor
{
    /// <summary>The `abi` and the JSON schema this assembly was compiled for; a library that reports other numbers is read with the wrong contract.</summary>
    internal const int RequiredAbi = 1;

    /// <summary>The other half of <see cref="RequiredAbi"/>: the schema version of the response this client parses.</summary>
    internal const int RequiredSchemaVersion = 1;

    private static readonly Lazy<string> VersionDocument = new(ReadVersionDocument);

    /// <summary>
    /// The same document, parsed once: the accessor below hands the fields out, and a document that does
    /// not parse states nothing rather than failing, because nothing here is compared.
    /// </summary>
    private static readonly Lazy<NativeLibraryVersion> ParsedVersionDocument = new(ReadVersion);

    /// <summary>
    /// The one contract comparison per process: `Lazy` runs the factory at most once, so no extraction
    /// after the first calls `rt_version` at all, and null means the library passed.
    /// </summary>
    private static readonly Lazy<ContractVersionMismatchException?> ContractMismatch = new(() => CheckContract(VersionDocument.Value));

    /// <summary>
    /// The version document of the loaded native library, parsed: the ABI it implements, the JSON schema
    /// it speaks, the version of this binding's own crate and the version of the `rust-trafilatura` core
    /// it links.
    /// </summary>
    /// <remarks>
    /// The document is what the library says about itself, and this accessor does not compare it: it is
    /// the path a caller reads to see which library is loaded, including after
    /// <see cref="ContractVersionMismatchException"/> reported that `abi` or `schemaVersion` differs. The
    /// comparison runs on the first call that extracts (see
    /// <see cref="ExtractFromBytes(ReadOnlyMemory{byte}, ExtractionOptions)"/>), which can catch a
    /// library that reports different numbers and cannot catch one that reports these while implementing
    /// something else, because the document is still only the library's claim. `crate` and `core` are
    /// versioned independently of `abi`, so a different version is not a mismatch and is not compared;
    /// either is null when the document states none, and <see cref="NativeLibraryVersion.Document"/> is
    /// the library's own text.
    /// </remarks>
    /// <exception cref="ContractVersionMismatchException">
    /// The library's `rt_version` answered with a code that carried no document, so there is nothing to
    /// parse. A document that is readable is handed out as it stands, because this accessor does not
    /// compare it.
    /// </exception>
    public static NativeLibraryVersion LibraryVersion => ParsedVersionDocument.Value;

    /// <summary>
    /// Extracts from a page the caller already holds as bytes. Gzip detection, charset detection,
    /// decoding, normalization and parsing all happen inside the native library, which is given the
    /// caller's own memory: the page is never copied or decoded on the managed side, and a
    /// <see cref="byte"/> array arrives here through the implicit conversion with no allocation.
    /// </summary>
    /// <param name="html">
    /// The page's bytes: its whole length, a slice of an array, a pooled rent or any other memory
    /// view. Bytes that are not memory-backed, such as a `stackalloc` buffer or unmanaged memory, go
    /// to <see cref="ExtractFromSpan"/> instead. An empty buffer, which is what a null array becomes,
    /// is a valid request that the core itself rejects as too short.
    /// </param>
    /// <param name="options">The options for this page, or null for the core's own defaults.</param>
    /// <exception cref="ContractVersionMismatchException">
    /// The native library reports an `abi` or `schemaVersion` this assembly was not compiled for, or
    /// its `rt_version` answered with a code that carried no version document at all. The comparison is
    /// made once per process, on the first call that reaches here, and every later call throws that
    /// same failure until a usable library is loaded.
    /// </exception>
    /// <exception cref="TrafilaturaException">The request was rejected or the extraction failed.</exception>
    /// <exception cref="DllNotFoundException">The native library is missing, so the host could not load it.</exception>
    /// <exception cref="EntryPointNotFoundException">The library that loaded is stale or foreign and does not export this ABI.</exception>
    /// <exception cref="OverflowException">The response is larger than the platform's `int` limit, so it cannot be marshalled at all.</exception>
    public static ExtractionResult ExtractFromBytes(ReadOnlyMemory<byte> html, ExtractionOptions? options = null)
    {
        return ExtractFromSpan(html.Span, options);
    }

    /// <summary>
    /// Extracts from a page the caller already holds as a span, which is the shape every byte entry
    /// point funnels into. Gzip detection, charset detection, decoding, normalization and parsing all
    /// happen inside the native library, which is given the caller's own bytes: the page is never
    /// copied or decoded on the managed side, and neither an array nor a memory view costs an
    /// allocation to reach here.
    /// </summary>
    /// <param name="html">
    /// The page's bytes: an array (through the implicit conversion), a slice of one, a pooled rent, a
    /// span taken from a memory view, a `stackalloc` buffer, or unmanaged memory behind a pointer. An
    /// empty span is a valid request that the core itself rejects as too short.
    /// </param>
    /// <param name="options">The options for this page, or null for the core's own defaults.</param>
    /// <exception cref="ContractVersionMismatchException">
    /// The native library reports an `abi` or `schemaVersion` this assembly was not compiled for, or
    /// its `rt_version` answered with a code that carried no version document at all. The comparison is
    /// made once per process, on the first call that reaches here, and every later call throws that
    /// same failure until a usable library is loaded.
    /// </exception>
    /// <exception cref="TrafilaturaException">The request was rejected or the extraction failed.</exception>
    /// <exception cref="DllNotFoundException">The native library is missing, so the host could not load it.</exception>
    /// <exception cref="EntryPointNotFoundException">The library that loaded is stale or foreign and does not export this ABI.</exception>
    /// <exception cref="OverflowException">The response is larger than the platform's `int` limit, so it cannot be marshalled at all.</exception>
    public static ExtractionResult ExtractFromSpan(ReadOnlySpan<byte> html, ExtractionOptions? options = null)
    {
        EnsureContract();

        return Interop.Extract(Request.Header(options), html, Response.Read);
    }

    /// <summary>
    /// Extracts from a page the caller already holds as a string, by encoding it to UTF-8 and taking
    /// the same path as <see cref="ExtractFromSpan"/>. For the same text the two are
    /// interchangeable, because charset detection is a no-op on .NET's UTF-8 output. An unpaired
    /// surrogate in <paramref name="html"/> becomes U+FFFD before the call.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="html"/> is null.</exception>
    /// <exception cref="ContractVersionMismatchException">The same check <see cref="ExtractFromSpan(ReadOnlySpan{byte}, ExtractionOptions)"/> makes, since this overload reaches it.</exception>
    /// <exception cref="TrafilaturaException">The request was rejected or the extraction failed.</exception>
    /// <exception cref="DllNotFoundException">The native library is missing, so the host could not load it.</exception>
    /// <exception cref="EntryPointNotFoundException">The library that loaded is stale or foreign and does not export this ABI.</exception>
    /// <exception cref="OverflowException">The response is larger than the platform's `int` limit, so it cannot be marshalled at all.</exception>
    public static ExtractionResult ExtractFromHtml(string html, ExtractionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);

        return ExtractFromSpan(Encoding.UTF8.GetBytes(html), options);
    }

    /// <summary>
    /// Extracts from many pages with bounded parallelism, returning one item per input in input order.
    /// A page that fails becomes its own item's <see cref="ExtractionBatchItem.Error"/> instead of
    /// aborting the batch, so one bad page cannot discard the good ones around it. Every call is an
    /// independent one-shot extraction, so the batch is worth its cost only across pages: a single
    /// page gains nothing from it.
    /// </summary>
    /// <param name="pages">The pages to extract, one per item in the returned list.</param>
    /// <param name="options">The options for every page in the batch.</param>
    /// <param name="maxDegreeOfParallelism">
    /// How many extractions may run at once; 0 means <see cref="Environment.ProcessorCount"/>.
    /// </param>
    /// <remarks>
    /// What a batch throws when the extraction itself cannot run depends on how many failures the
    /// parallel loop collected. With exactly one it throws that failure, so a batch reports a missing
    /// or stale native library as the same exception a single-page call reports. With more than one
    /// it throws the <see cref="AggregateException"/> the loop built, because none of them can stand
    /// for the others, and a batch of two pages against one missing library is enough to reach that.
    /// A page that fails to extract is neither: it becomes its item's
    /// <see cref="ExtractionBatchItem.Error"/> and the call returns normally.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="pages"/> is null or empty, or one of its elements is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxDegreeOfParallelism"/> is negative.</exception>
    /// <exception cref="AggregateException">More than one failure was collected, so none of them can stand for the others.</exception>
    /// <exception cref="ContractVersionMismatchException">
    /// The native library reports an `abi` or `schemaVersion` this assembly was not compiled for, and
    /// it was the only failure collected. Several pages against the same library report it inside an
    /// <see cref="AggregateException"/> instead.
    /// </exception>
    /// <exception cref="DllNotFoundException">
    /// The native library is missing and it was the only failure collected, so the batch reports it
    /// as itself. Several pages against the same missing library report it inside an
    /// <see cref="AggregateException"/> instead.
    /// </exception>
    /// <exception cref="EntryPointNotFoundException">
    /// The library that loaded is stale or foreign, does not export this ABI, and was the only
    /// failure collected. As with the missing library, several pages report it aggregated.
    /// </exception>
    public static IReadOnlyList<ExtractionBatchItem> ExtractMany(
        IReadOnlyList<byte[]> pages,
        ExtractionOptions? options = null,
        int maxDegreeOfParallelism = 0)
    {
        // The array overload delegates rather than duplicating the loop: the two have to stay
        // identical in ordering, failure capture and bounds, and a second copy of that is a second
        // place for them to drift. Validation runs here so the conversion below is handed a list whose
        // elements are known to be non-null, and so a null list is not reported as an empty one.
        var validated = ValidateBatch(pages, maxDegreeOfParallelism, static page => page is null);

        var memory = new ReadOnlyMemory<byte>[validated.Count];
        for (var index = 0; index < memory.Length; index++)
        {
            memory[index] = validated[index];
        }

        return ExtractMany(memory, options, maxDegreeOfParallelism);
    }

    /// <summary>
    /// The same batch over pages held as memory, so a caller whose pages are slices of a larger buffer
    /// or rents from a pool never has to copy them into arrays first.
    /// </summary>
    /// <param name="pages">The pages to extract, one per item in the returned list.</param>
    /// <param name="options">The options for every page in the batch.</param>
    /// <param name="maxDegreeOfParallelism">
    /// How many extractions may run at once; 0 means <see cref="Environment.ProcessorCount"/>.
    /// </param>
    /// <remarks>
    /// This is one batch among a candidate set the compiler can distinguish, even though a
    /// <see cref="ReadOnlyMemory{T}"/> element is what a <see cref="byte"/> array converts to:
    /// <see cref="IReadOnlyList{T}"/> is covariant, and variance carries only reference and identity
    /// conversions, so a list of arrays is no candidate for this overload at all. Both overloads
    /// therefore resolve for the collection each caller holds, and neither call site is ambiguous.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="pages"/> is null or empty, or one of its elements is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxDegreeOfParallelism"/> is negative.</exception>
    /// <exception cref="AggregateException">More than one failure was collected, so none of them can stand for the others.</exception>
    /// <exception cref="ContractVersionMismatchException">
    /// The same check <see cref="ExtractFromBytes(ReadOnlyMemory{byte}, ExtractionOptions)"/> makes,
    /// reported the same way a missing library is: as itself when it was the only failure collected,
    /// inside an <see cref="AggregateException"/> when several pages hit it.
    /// </exception>
    /// <exception cref="DllNotFoundException">
    /// The native library is missing and it was the only failure collected, so the batch reports it
    /// as itself. Several pages against the same missing library report it inside an
    /// <see cref="AggregateException"/> instead.
    /// </exception>
    /// <exception cref="EntryPointNotFoundException">
    /// The library that loaded is stale or foreign, does not export this ABI, and was the only
    /// failure collected. As with the missing library, several pages report it aggregated.
    /// </exception>
    public static IReadOnlyList<ExtractionBatchItem> ExtractMany(
        IReadOnlyList<ReadOnlyMemory<byte>> pages,
        ExtractionOptions? options = null,
        int maxDegreeOfParallelism = 0)
    {
        // Both overloads validate through the same helper, so a rejected batch is rejected the same
        // way whichever collection the caller held. A memory element is a value, so none is ever null.
        var validated = ValidateBatch(pages, maxDegreeOfParallelism, static _ => false);

        // Written by index rather than appended: a concurrent collection would make the input order
        // depend on which page finished first, and order is part of this helper's contract.
        var items = new ExtractionBatchItem[validated.Count];
        try
        {
            Parallel.For(
                0,
                validated.Count,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxDegreeOfParallelism == 0
                        ? Environment.ProcessorCount
                        : maxDegreeOfParallelism,
                },
                index =>
                {
                    try
                    {
                        items[index] = new ExtractionBatchItem { Result = ExtractFromBytes(validated[index], options) };
                    }
                    catch (TrafilaturaException exception)
                    {
                        items[index] = new ExtractionBatchItem { Error = exception };
                    }
                });
        }
        catch (AggregateException exception)
        {
            // Unwrapped with ExceptionDispatchInfo so the caller catches the inner exception itself,
            // stack trace included, instead of the loop's wrapper.
            ExceptionDispatchInfo.Capture(NormalizeBatchFailure(exception)).Throw();
        }

        return items;
    }

    /// <summary>
    /// Rejects the batch arguments both overloads share, before either starts a loop, so a batch that
    /// cannot be extracted fails at the call rather than part-way through it. It returns the list it
    /// accepted, so a caller that has to read the elements before the loop gets them only after the
    /// elements were checked. Whether an element can be null is the one thing the overloads do not
    /// share: an array element is a reference and a <see cref="ReadOnlyMemory{T}"/> element is not.
    /// </summary>
    private static IReadOnlyList<T> ValidateBatch<T>(
        IReadOnlyList<T>? pages,
        int maxDegreeOfParallelism,
        Func<T, bool> isNullElement)
    {
        if (pages is null)
        {
            throw new ArgumentException("The pages to extract must not be null.", nameof(pages));
        }

        if (pages.Count == 0)
        {
            throw new ArgumentException("The pages to extract must not be empty.", nameof(pages));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(maxDegreeOfParallelism);

        for (var index = 0; index < pages.Count; index++)
        {
            if (isNullElement(pages[index]))
            {
                throw new ArgumentException($"The page at index {index} is null.", nameof(pages));
            }
        }

        return pages;
    }

    /// <summary>
    /// Decides what a batch throws when the parallel loop itself faulted. One collected failure is
    /// unwrapped, because the loop wrapped it and the caller would otherwise have to look inside; two
    /// or more stay wrapped, because no single one of them can stand for the others, and so does an
    /// aggregate with none, which no single failure can replace either.
    /// </summary>
    /// <remarks>
    /// Split out of the catch so the rule is assertable without a native library that fails to load:
    /// the caller throws what this returns, so returning the aggregate here is what makes a batch
    /// report an <see cref="AggregateException"/>.
    /// </remarks>
    internal static Exception NormalizeBatchFailure(AggregateException exception) =>
        exception.InnerExceptions.Count == 1 ? exception.InnerExceptions[0] : exception;

    private static string ReadVersionDocument()
    {
        var (code, document) = Interop.Version();
        return ReadVersionDocument(code, document);
    }

    /// <summary>
    /// The fields of the version document, or a member left unset where the document does not state it. A
    /// document that does not parse states nothing rather than throwing, which is the same finding as one
    /// that omits a key: this hands out the library's claim, and the comparison that can fail is
    /// <see cref="CheckContract(string)"/>.
    /// </summary>
    private static NativeLibraryVersion ReadVersion()
    {
        var document = VersionDocument.Value;

        try
        {
            using var parsed = JsonDocument.Parse(document);

            return new NativeLibraryVersion
            {
                Abi = ReadNumber(parsed.RootElement, "abi"),
                SchemaVersion = ReadNumber(parsed.RootElement, "schemaVersion"),
                Crate = JsonMembers.Text(parsed.RootElement, "crate"),
                Core = JsonMembers.Text(parsed.RootElement, "core"),
                Document = document,
            };
        }
        catch (JsonException)
        {
            return new NativeLibraryVersion { Document = document };
        }
    }

    /// <summary>
    /// The version document a `rt_version` call produced, or the mismatch of a library that produced
    /// none: a non-zero code hands out no document to compare, so there is nothing for
    /// <see cref="CheckContract(string)"/> to read and the library is one this assembly cannot use.
    /// </summary>
    /// <remarks>
    /// Internal, and taking the call's return code and document as arguments, so a version read that
    /// fails is assertable without a native library that misbehaves. The failure the read itself
    /// produced stays on the mismatch as its inner exception, because the code and the response are
    /// the only record of what the library answered.
    /// </remarks>
    internal static string ReadVersionDocument(int code, string document) =>
        code == 0
            ? document
            : throw new ContractVersionMismatchException(
                RequiredAbi,
                null,
                RequiredSchemaVersion,
                null,
                Failures.Read(code, document));

    /// <summary>
    /// Refuses to extract against a library whose contract version differs from the one this assembly
    /// was compiled for. Runs before an extraction rather than from a static constructor or from
    /// <see cref="LibraryVersion"/>: a static constructor failure reaches the caller as
    /// <see cref="TypeInitializationException"/>, which hides the finding, and the version accessor is
    /// the path a caller reads to see which library is loaded.
    /// </summary>
    /// <remarks>
    /// This is the first use of the native library, so what it finds is what a caller sees first: a
    /// missing or foreign library still reports the <see cref="DllNotFoundException"/> or
    /// <see cref="EntryPointNotFoundException"/> the extraction call used to report, with the same
    /// diagnostics, because the document is read through the same <see cref="Interop"/> calls. A
    /// `rt_version` that answers with a non-zero code reads as a mismatch with neither number
    /// reported, because it handed out no document to compare. `Lazy` caches that first outcome,
    /// mismatch or failure, so later calls report it without loading the library again, and a mismatch
    /// is the same exception instance every time.
    /// </remarks>
    private static void EnsureContract()
    {
        // The same instance every time is deliberate: the mismatch is a property of the loaded library
        // and cannot change under this process, so an extraction number one thousand reports exactly
        // what the first one did.
        if (ContractMismatch.Value is { } mismatch)
        {
            throw mismatch;
        }
    }

    /// <summary>
    /// The comparison the first extraction makes against the library's own version document, or null
    /// when that document reports the `abi` and the `schemaVersion` this assembly was compiled for.
    /// </summary>
    /// <remarks>
    /// Internal, and taking the document as text, so every outcome is assertable without a native
    /// library built for a different ABI: the first-use path throws what this returns, so what this
    /// returns is what a mismatched library produces. A document that does not parse, or that does not
    /// state both numbers, reports neither of them: the comparison passes only on a document that
    /// carries both.
    /// </remarks>
    internal static ContractVersionMismatchException? CheckContract(string versionDocument)
    {
        int? abi = null;
        int? schemaVersion = null;
        try
        {
            using var document = JsonDocument.Parse(versionDocument);
            abi = ReadNumber(document.RootElement, "abi");
            schemaVersion = ReadNumber(document.RootElement, "schemaVersion");
        }
        catch (JsonException)
        {
            // A document that does not parse states no contract version, which is the same finding as
            // one that omits a key, and neither may pass the comparison by default.
        }

        return abi == RequiredAbi && schemaVersion == RequiredSchemaVersion
            ? null
            : new ContractVersionMismatchException(RequiredAbi, abi, RequiredSchemaVersion, schemaVersion);
    }

    /// <summary>
    /// An integer member of the version document, or null when the document is not an object, the
    /// member is absent, its value is not a number, or it does not fit an `int` this client can compare.
    /// </summary>
    private static int? ReadNumber(JsonElement document, string name) =>
        document.ValueKind == JsonValueKind.Object
            && document.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;

}
