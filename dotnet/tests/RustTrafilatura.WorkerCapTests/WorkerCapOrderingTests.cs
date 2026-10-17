using System.Collections.Concurrent;
using System.Text;
using Xunit;

namespace RustTrafilatura.WorkerCapTests;

/// <summary>
/// The runaway-worker cap's process-wide lifecycle, in a test project of its own so the main
/// assembly's budget tests cannot fix the cap first in this process.
/// </summary>
/// <remarks>
/// One ordered test for every case: the cap is process-global and xunit runs a project's tests
/// in parallel, so this file's assertions are the only ordering the cap admits.
/// </remarks>
public sealed class WorkerCapOrderingTests
{
    /// <summary>
    /// A page long enough for the core to keep: this project has no fixtures file of the main
    /// assembly's (its fixtures are internal there), and one page is all the lifecycle below
    /// needs.
    /// </summary>
    private static byte[] Article()
    {
        const string article = """
            <html lang="en"><head><title>Inline article</title></head><body><article><h1>Inline article</h1>
            <p>This inline article carries enough text for the extractor to keep it, because the core discards documents shorter than its minimum output size. The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published.</p>
            <p>A second paragraph adds enough body text for the extractor to keep the article instead of discarding it as too short.</p>
            </article></body></html>
            """;
        return Encoding.UTF8.GetBytes(article);
    }

    [Fact]
    public void Worker_Cap_Lifecycle_In_Order()
    {
        // A read before anything else reports the would-be default and fixes nothing, so the
        // configure below is still free to apply.
        Assert.Equal(16, Extractor.WorkerCap);

        // The first configure applies; the cap is read once, so every later one reports
        // already-fixed rather than silently running on a value it never set.
        Assert.Equal(WorkerCapOutcome.Applied, Extractor.ConfigureWorkerCap(16));
        Assert.Equal(WorkerCapOutcome.AlreadyFixed, Extractor.ConfigureWorkerCap(32));

        // Configures racing a fixed cap all report already-fixed and none throws: OnceLock
        // semantics on the native side, observed here through concurrent managed calls.
        var outcomes = new ConcurrentQueue<WorkerCapOutcome>();
        Parallel.Invoke(
            () => outcomes.Enqueue(Extractor.ConfigureWorkerCap(48)),
            () => outcomes.Enqueue(Extractor.ConfigureWorkerCap(48)),
            () => outcomes.Enqueue(Extractor.ConfigureWorkerCap(48)),
            () => outcomes.Enqueue(Extractor.ConfigureWorkerCap(48)),
            () => outcomes.Enqueue(Extractor.ConfigureWorkerCap(48)),
            () => outcomes.Enqueue(Extractor.ConfigureWorkerCap(48)),
            () => outcomes.Enqueue(Extractor.ConfigureWorkerCap(48)),
            () => outcomes.Enqueue(Extractor.ConfigureWorkerCap(48)));
        Assert.All(outcomes, outcome => Assert.Equal(WorkerCapOutcome.AlreadyFixed, outcome));

        Assert.Equal(16, Extractor.WorkerCap);

        // The live count is advisory and read-only: it observes one budgeted call finish on a
        // generous budget — a call that fixes nothing the cap has not fixed already — and drains
        // back to zero once the worker is done. A failing extraction would have thrown, so
        // reaching the drain assertion is itself the success of the call.
        var before = Extractor.LiveRunawayWorkers;
        var result = Extractor.ExtractFromBytes(Article(), new ExtractionOptions { TimeBudgetMs = 60_000 });
        Assert.NotNull(result.ContentText);
        Assert.True(before >= 0);
        Assert.Equal(0, Extractor.LiveRunawayWorkers);
    }
}
