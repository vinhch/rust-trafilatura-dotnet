using System.Text;
using BenchmarkDotNet.Attributes;

namespace RustTrafilatura.Benchmarks;

/// <summary>Which response fields a case asks for.</summary>
public enum ResponseFields
{
    /// <summary>The default: content text, comments text and metadata.</summary>
    Default,

    /// <summary>Every field, including the two HTML serializations.</summary>
    Everything,
}

/// <summary>
/// One measured call per entry point, over pages spread across the corpus's size range. The reported
/// mean is one extraction of one page; the allocations column is what one call allocates on the managed
/// side, which is the cost the native measurement in docs/research/binding-performance.md cannot see.
/// </summary>
/// <remarks>
/// A page the core rejects is measured as a failure rather than aborting the run: the corpus holds
/// pages with nothing to extract, and a case that throws would be reported as an error and dropped.
/// </remarks>
[MemoryDiagnoser]
public class ExtractionBenchmarks
{
    private const int SampleSize = 12;

    private ExtractionOptions _options = new();
    private string _text = string.Empty;

    /// <summary>The page this case extracts; one case per sampled page.</summary>
    [ParamsSource(nameof(Pages))]
    public Page Page { get; set; } = null!;

    /// <summary>The response fields this case asks for.</summary>
    [Params(ResponseFields.Default, ResponseFields.Everything)]
    public ResponseFields Fields { get; set; }

    /// <summary>The sampled pages, smallest first.</summary>
    public static IReadOnlyList<Page> Pages => Corpus.Sample(SampleSize);

    [GlobalSetup]
    public void Setup()
    {
        _text = Encoding.UTF8.GetString(Page.Html);
        _options = Fields == ResponseFields.Everything
            ? new ExtractionOptions
            {
                ResponseFields = ["contentText", "commentsText", "contentHtml", "commentsHtml", "metadata"],
            }
            : new ExtractionOptions();
    }

    /// <summary>The bytes entry point, which passes the caller's own memory to the core.</summary>
    [Benchmark]
    public int ExtractFromBytes()
    {
        try
        {
            return Extractor.ExtractFromBytes(Page.Memory, _options).ContentText?.Length ?? 0;
        }
        catch (TrafilaturaException)
        {
            return 0;
        }
    }

    /// <summary>The span entry point, over the same bytes.</summary>
    [Benchmark]
    public int ExtractFromSpan()
    {
        try
        {
            return Extractor.ExtractFromSpan(Page.Html, _options).ContentText?.Length ?? 0;
        }
        catch (TrafilaturaException)
        {
            return 0;
        }
    }

    /// <summary>The string entry point, which encodes the page to UTF-8 before it reaches the core.</summary>
    [Benchmark]
    public int ExtractFromHtml()
    {
        try
        {
            return Extractor.ExtractFromHtml(_text, _options).ContentText?.Length ?? 0;
        }
        catch (TrafilaturaException)
        {
            return 0;
        }
    }
}
