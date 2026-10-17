using System.Text;

namespace RustTrafilatura;

/// <summary>
/// Extracts text, comments, HTML and metadata from a page. One call per page, with no state to hold
/// or dispose between calls.
/// </summary>
public static class Extractor
{
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
    public static NativeLibraryVersion LibraryVersion => LibraryContract.Version;

    /// <summary>
    /// Sets the runaway-worker cap for every later budgeted extraction, once per process: it
    /// returns <see cref="WorkerCapOutcome.Applied"/> when this call fixed the cap, and
    /// <see cref="WorkerCapOutcome.AlreadyFixed"/> when an earlier call of it or the first
    /// budgeted extraction had already fixed it, in which case nothing changes and the process
    /// keeps the value that was fixed.
    /// </summary>
    /// <remarks>
    /// The cap is read once, native side, and this and the environment variable
    /// `RUSTTRAFILATURA_MAX_RUNAWAY_WORKERS` are two writers of one value: whoever fixes it
    /// first wins. Calling this at host start, before the first budgeted extraction, is the way
    /// to make the cap certain; on Unix, setting the variable through
    /// `Environment.SetEnvironmentVariable` never reaches the native side, so this call is also
    /// the only in-code way there. A cap of 0 or below throws here, before any native call; the
    /// native export rejects 0 on its own for callers that are not this binding.
    /// </remarks>
    /// <param name="cap">The maximum number of detached workers; 1 or more.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cap"/> is 0 or below.</exception>
    /// <exception cref="DllNotFoundException">The native library is missing, so the host could not load it.</exception>
    /// <exception cref="EntryPointNotFoundException">
    /// The library that loaded is a stale build that predates this API and does not export the
    /// worker-cap symbols.
    /// </exception>
    public static WorkerCapOutcome ConfigureWorkerCap(int cap)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cap);

        // Code 2 cannot reach here (the guard above rejects 0), and a code this binding does
        // not know is a mismatched build, reported rather than mistaken for already-fixed.
        return Interop.SetWorkerCap(cap) switch
        {
            0 => WorkerCapOutcome.Applied,
            1 => WorkerCapOutcome.AlreadyFixed,
            _ => throw new InvalidOperationException(
                $"The native library returned an unknown worker-cap code for cap {cap}."),
        };
    }

    /// <summary>
    /// The runaway-worker cap that applies right now, read without fixing it: before any
    /// <see cref="ConfigureWorkerCap"/> call and before the first budgeted extraction this is
    /// the value that would apply — the environment variable's when it names a positive
    /// number, otherwise the default 16 — and reading it never locks the process to that
    /// value.
    /// </summary>
    /// <exception cref="DllNotFoundException">The native library is missing, so the host could not load it.</exception>
    /// <exception cref="EntryPointNotFoundException">
    /// The library that loaded is a stale build that predates this API and does not export the
    /// worker-cap symbols.
    /// </exception>
    public static int WorkerCap => Interop.WorkerCap();

    /// <summary>
    /// How many detached budgeted workers are live right now. The value is advisory: a worker
    /// reserves its slot before it starts and releases it when it finishes, so the count can
    /// change as soon as it is read — it exists for saturation logging behind
    /// <see cref="TrafilaturaErrorKind.WorkerCapExceeded"/>, not for control flow.
    /// </summary>
    /// <exception cref="DllNotFoundException">The native library is missing, so the host could not load it.</exception>
    /// <exception cref="EntryPointNotFoundException">
    /// The library that loaded is a stale build that predates this API and does not export the
    /// worker-cap symbols.
    /// </exception>
    public static int LiveRunawayWorkers => Interop.LiveRunawayWorkers();

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
        LibraryContract.Ensure();

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
        var validated = ExtractionBatch.Validate(pages, maxDegreeOfParallelism, static page => page is null);

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
        return ExtractionBatch.Run(pages, options, maxDegreeOfParallelism);
    }
}
