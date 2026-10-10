using System.Runtime.ExceptionServices;

namespace RustTrafilatura;

/// <summary>
/// Runs a batch of pages: the argument check both public overloads share, the bounded parallel loop, and
/// the rule for what the loop reports when the extraction itself cannot run.
/// </summary>
internal static class ExtractionBatch
{
    /// <summary>
    /// Extracts every page, one item per input in input order, under the bound the caller set: a page
    /// that fails becomes its own item's error instead of aborting the batch.
    /// </summary>
    internal static IReadOnlyList<ExtractionBatchItem> Run(
        IReadOnlyList<ReadOnlyMemory<byte>> pages,
        ExtractionOptions? options,
        int maxDegreeOfParallelism)
    {
        // Both overloads validate through the same helper, so a rejected batch is rejected the same
        // way whichever collection the caller held. A memory element is a value, so none is ever null.
        var validated = Validate(pages, maxDegreeOfParallelism, static _ => false);

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
                        items[index] = new ExtractionBatchItem { Result = Extractor.ExtractFromBytes(validated[index], options) };
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
            ExceptionDispatchInfo.Capture(NormalizeFailure(exception)).Throw();
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
    internal static IReadOnlyList<T> Validate<T>(
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
    internal static Exception NormalizeFailure(AggregateException exception) =>
        exception.InnerExceptions.Count == 1 ? exception.InnerExceptions[0] : exception;
}
