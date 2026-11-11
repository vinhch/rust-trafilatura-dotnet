using System.Text;
using Xunit;

namespace RustTrafilatura.Tests;

public sealed class StackTests
{
    private const int OneMiB = 1024 * 1024;

    [Fact]
    public void Extraction_Fits_A_One_MiB_Thread()
    {
        var page = DeepPage();
        var options = new ExtractionOptions { ResponseFields = ["contentText"] };
        ExtractionResult? result = null;
        Exception? failure = null;

        // The thread body records its outcome instead of asserting: a failed assertion on another
        // thread would end the test process rather than fail this test.
        var thread = new Thread(() =>
        {
            try
            {
                result = Extractor.ExtractFromBytes(page, options);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }, OneMiB);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.False(string.IsNullOrEmpty(result?.ContentText));
    }

    [Fact]
    public async Task Default_And_Thread_Pool_Stacks_Are_At_Least_One_MiB()
    {
        // On macOS, .NET 8 gives a thread 512 KiB, below what the extraction needs. The native library
        // runs the extraction on its own 1 MiB worker there, so the managed default is not the bound
        // that matters on that OS. .NET 9 and later give 1.5 MiB on macOS (dotnet/runtime PR #98007),
        // so the bound is enforced there like on the other hosts.
        if (OperatingSystem.IsMacOS() && Environment.Version.Major < 9)
        {
            Assert.Skip(".NET 8 defaults to 512 KiB on macOS; the native library runs extraction on a 1 MiB worker there");
        }

        if (!ThreadStack.IsSupported)
        {
            Assert.Skip("the thread stack size is read on Windows, Linux and macOS only");
        }

        long threadSize = 0;
        Exception? threadFailure = null;
        var thread = new Thread(() =>
        {
            try
            {
                threadSize = ThreadStack.CurrentSizeBytes();
            }
            catch (Exception exception)
            {
                threadFailure = exception;
            }
        });
        thread.Start();
        thread.Join();

        Assert.Null(threadFailure);
        var poolSize = await Task.Run(() => ThreadStack.CurrentSizeBytes());

        Assert.True(threadSize >= OneMiB, $"a new thread has {threadSize} bytes of stack");
        Assert.True(poolSize >= OneMiB, $"a thread-pool thread has {poolSize} bytes of stack");
    }

    /// <summary>
    /// The page the Rust stack test extracts: a paragraph before 1000 nested divs, because parsing stops
    /// at depth 256 and a page that nested first would leave no text to extract.
    /// </summary>
    private static byte[] DeepPage() =>
        Encoding.UTF8.GetBytes(
            "<html><body><article><p>"
            + string.Concat(Enumerable.Repeat("word ", 60))
            + "</p></article>"
            + string.Concat(Enumerable.Repeat("<div>", 1000))
            + "text"
            + string.Concat(Enumerable.Repeat("</div>", 1000))
            + "</body></html>");
}
