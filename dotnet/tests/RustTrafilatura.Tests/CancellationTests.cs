using System.Diagnostics;
using System.Text;
using Xunit;

namespace RustTrafilatura.Tests;

/// <summary>
/// The cancellation path from the caller's token to the native handle and back: what a token does
/// before the call, during it and after native returned, and what the handle leaves behind.
/// </summary>
/// <remarks>
/// The seams and the handle counts are process-wide, so the class runs alone in its collection.
/// </remarks>
[Collection(ProcessWideCancelState.Name)]
public sealed class CancellationTests
{
    private const int ElementPairCount = 60_000;

    private static readonly ExtractionOptions ContentAndMetadata = new()
    {
        ResponseFields = ["contentText", "metadata"],
    };

    private static readonly byte[] DensePage = BuildDensePage();

    /// <summary>
    /// A page with many elements per byte, because the core checks the token every 256 nodes and a page
    /// of long text would reach the check only a few times.
    /// </summary>
    private static byte[] BuildDensePage()
    {
        var builder = new StringBuilder("<!DOCTYPE html><html lang=\"en\"><head><title>Dense page</title></head><body><article>");
        for (var index = 0; index < ElementPairCount; index++)
        {
            builder.Append("<div><p>Sentence ").Append(index).Append(" carries a few ordinary words for the extractor to keep in the text.</p></div>");
        }

        builder.Append("</article></body></html>");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    [Fact]
    public void Cancel_Mid_Extraction_Throws_In_Under_A_Quarter_Of_The_Uncancelled_Run()
    {
        var uncancelled = Stopwatch.StartNew();
        Extractor.ExtractFromBytes(DensePage, null, TestContext.Current.CancellationToken);
        uncancelled.Stop();

        // The floor comes first: a page too small to measure fails here instead of passing by luck.
        Assert.True(
            uncancelled.Elapsed >= TimeSpan.FromMilliseconds(200),
            $"The uncancelled run took {uncancelled.Elapsed.TotalMilliseconds} ms, under the 200 ms floor.");

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(5);

        var cancelled = Stopwatch.StartNew();
        var exception = Assert.Throws<OperationCanceledException>(() => Extractor.ExtractFromBytes(DensePage, null, cts.Token));
        cancelled.Stop();

        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.True(
            cancelled.Elapsed < uncancelled.Elapsed / 4,
            $"The cancelled call took {cancelled.Elapsed.TotalMilliseconds} ms against {uncancelled.Elapsed.TotalMilliseconds} ms uncancelled.");
    }

    [Fact]
    public void Pre_Cancelled_Token_Throws_Without_A_Native_Call()
    {
        // Over the 64 MiB cap, so native would answer InputTooLarge: a thrown OperationCanceledException
        // instead shows that the token was checked before the call.
        var oversizedBytes = new byte[(64 * 1024 * 1024) + 1];
        var oversizedText = new string('a', (64 * 1024 * 1024) + 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var createdBefore = CancelHandle.CreatedCount;

        Assert.Throws<OperationCanceledException>(() => Extractor.ExtractFromBytes(oversizedBytes, null, cts.Token));
        Assert.Throws<OperationCanceledException>(() => Extractor.ExtractFromSpan(oversizedBytes, null, cts.Token));
        Assert.Throws<OperationCanceledException>(() => Extractor.ExtractFromHtml(oversizedText, null, cts.Token));

        Assert.Equal(createdBefore, CancelHandle.CreatedCount);
    }

    [Fact]
    public void ExtractFromHtml_Null_Html_Is_An_Argument_Error_Even_When_Cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<ArgumentNullException>(() => Extractor.ExtractFromHtml(null!, null, cts.Token));
    }

    [Fact]
    public void Cancel_After_Native_Returns_Gives_The_Normal_Result()
    {
        var expected = Extractor.ExtractFromBytes(Fixtures.Article, ContentAndMetadata, TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();

        try
        {
            Interop.AfterNativeCall = () => cts.Cancel();

            var actual = Extractor.ExtractFromBytes(Fixtures.Article, ContentAndMetadata, cts.Token);

            Assert.True(cts.IsCancellationRequested);
            Assert.Equal(expected.ContentText, actual.ContentText);
            Assert.Equal(expected.Metadata!.Title, actual.Metadata!.Title);
        }
        finally
        {
            Interop.AfterNativeCall = null;
        }
    }

    [Fact]
    public void Cancel_After_Handle_Dispose_Does_Not_Throw()
    {
        using var cts = new CancellationTokenSource();
        var fired = false;

        try
        {
            // Runs inside the handle's dispose, after the registration is gone. A cancel that reached
            // the released handle would throw here, and the throw would surface from the call.
            CancelHandle.AfterRelease = () =>
            {
                fired = true;
                cts.Cancel();
            };

            var result = Extractor.ExtractFromBytes(Fixtures.Article, ContentAndMetadata, cts.Token);

            Assert.True(fired);
            Assert.True(cts.IsCancellationRequested);
            Assert.False(string.IsNullOrEmpty(result.ContentText));
        }
        finally
        {
            CancelHandle.AfterRelease = null;
        }
    }

    [Fact]
    public void Cancel_After_A_Rejection_Keeps_The_Rejection()
    {
        using var cts = new CancellationTokenSource();

        try
        {
            Interop.AfterNativeCall = () => cts.Cancel();

            var exception = Assert.Throws<TrafilaturaException>(() => Extractor.ExtractFromBytes(
                Fixtures.Article,
                new ExtractionOptions { Config = new ExtractionConfig { MinOutputSize = 10000 } },
                cts.Token));

            Assert.Equal(TrafilaturaErrorKind.OutputTooShort, exception.Kind);
        }
        finally
        {
            Interop.AfterNativeCall = null;
        }
    }

    [Fact]
    public void Is_Cancelled_Reads_Only_The_Cancelled_Kind()
    {
        Assert.True(Failures.IsCancelled("""{"error":{"kind":"Cancelled","message":"extraction cancelled"}}"""u8));
        Assert.False(Failures.IsCancelled("""{"error":{"kind":"OutputTooShort","message":"too short"}}"""u8));
        Assert.False(Failures.IsCancelled("not json"u8));
        Assert.False(Failures.IsCancelled("[]"u8));
    }

    [Fact]
    public void Uncancellable_Token_Creates_No_Handle()
    {
        var createdBefore = CancelHandle.CreatedCount;

        // A variable rather than a literal default, which the xunit analyzer reads as a call that ignores the test's token.
        CancellationToken uncancellable = default;
        Extractor.ExtractFromBytes(Fixtures.Article, null, uncancellable);
        Extractor.ExtractFromBytes(Fixtures.Article, null, CancellationToken.None);

        Assert.Equal(createdBefore, CancelHandle.CreatedCount);
    }

    [Fact]
    public void Live_Handle_Count_Is_Zero_After_Single_And_Concurrent_Runs()
    {
        for (var index = 0; index < 20; index++)
        {
            RunMixedCall(index);
        }

        Parallel.For(0, 200, RunMixedCall);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Equal(0L, CancelHandle.LiveCount);
    }

    /// <summary>
    /// One call of each kind the leak test mixes: cancelled mid-run, pre-cancelled, and a cancellable token
    /// that never fires. The mid-run cancel uses the dense page; the other two use the small fixture.
    /// </summary>
    /// <remarks>
    /// The mid-run cancel runs on its own thread: a timer callback would wait for a thread-pool thread, and
    /// under this much parallel blocking in native calls the pool does not supply one in time, so the
    /// call would finish before its cancel arrived.
    /// </remarks>
    private static void RunMixedCall(int index)
    {
        switch (index % 3)
        {
            case 0:
                using (var cts = new CancellationTokenSource())
                {
                    var canceller = CancelAfterOnOwnThread(cts, TimeSpan.FromMilliseconds(5));
                    try
                    {
                        Assert.Throws<OperationCanceledException>(() => Extractor.ExtractFromBytes(DensePage, null, cts.Token));
                    }
                    finally
                    {
                        canceller.Join();
                    }
                }

                break;
            case 1:
                using (var cts = new CancellationTokenSource())
                {
                    cts.Cancel();
                    Assert.Throws<OperationCanceledException>(() => Extractor.ExtractFromBytes(Fixtures.Article, ContentAndMetadata, cts.Token));
                }

                break;
            default:
                using (var cts = new CancellationTokenSource())
                {
                    var result = Extractor.ExtractFromBytes(Fixtures.Article, ContentAndMetadata, cts.Token);
                    Assert.False(string.IsNullOrEmpty(result.ContentText));
                }

                break;
        }
    }

    /// <summary>Cancels the source after the delay from a thread of its own. The caller joins it, so the source is never used after a test returns.</summary>
    private static Thread CancelAfterOnOwnThread(CancellationTokenSource cts, TimeSpan delay)
    {
        var thread = new Thread(() =>
        {
            Thread.Sleep(delay);
            cts.Cancel();
        });
        thread.Start();
        return thread;
    }
}

/// <summary>
/// The collection the cancellation tests run in, with parallelization disabled, so no other test creates
/// a handle while a count is being read.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public static class ProcessWideCancelState
{
    /// <summary>The collection's name, as the tests that belong to it name it.</summary>
    public const string Name = "ProcessWideCancelState";
}
