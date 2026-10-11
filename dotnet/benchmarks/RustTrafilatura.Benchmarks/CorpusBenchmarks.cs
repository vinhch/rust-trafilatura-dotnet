using BenchmarkDotNet.Attributes;

namespace RustTrafilatura.Benchmarks;

/// <summary>
/// Corpus throughput: one measured invocation walks every page the corpus holds, so the reported mean is
/// one full pass over the pinned 1,057 pages and the per-page cost is that mean divided by the page
/// count the setup prints.
/// </summary>
/// <remarks>
/// The default job's iteration count would spend minutes on a case here, so this class runs a short one.
/// TRAFILATURA_CORPUS_PAGES cuts the pass down when a full one is not worth the wait.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 5)]
public class CorpusBenchmarks
{
    private ReadOnlyMemory<byte>[] _pages = [];
    private ExtractionOptions _options = new();

    [GlobalSetup]
    public void Setup()
    {
        _pages = [.. Corpus.Load().Select(static page => page.Memory)];
        Console.WriteLine($"corpus: {_pages.Length} pages, {_pages.Sum(static page => (long)page.Length):N0} bytes");
    }

    /// <summary>Every page through the single-page entry point, one after another.</summary>
    [Benchmark]
    public long ExtractEveryPage()
    {
        long total = 0;
        foreach (var page in _pages)
        {
            try
            {
                total += Extractor.ExtractFromBytes(page, _options).ContentText?.Length ?? 0;
            }
            catch (TrafilaturaException)
            {
                // Pages too short to extract are part of the corpus and contribute nothing here.
            }
        }

        return total;
    }

    /// <summary>The same pages through the batch entry point, at the parallelism the case names.</summary>
    /// <param name="maxDegreeOfParallelism">0 is every core, 1 is one extraction at a time.</param>
    [Benchmark]
    [Arguments(1)]
    [Arguments(0)]
    public long ExtractMany(int maxDegreeOfParallelism)
    {
        long total = 0;
        foreach (var item in Extractor.ExtractMany(_pages, _options, maxDegreeOfParallelism))
        {
            total += item.Result?.ContentText?.Length ?? 0;
        }

        return total;
    }
}
