using Xunit;

namespace RustTrafilatura.Tests;

/// <summary>
/// The cap argument's own validity, which is settled before any native call: the rejects below
/// throw without touching the library, so they cannot fix the cap and are safe among the
/// assembly's parallel tests. The cap's process-wide lifecycle lives in the WorkerCapTests
/// project, whose own process cannot be raced by this assembly.
/// </summary>
public sealed class WorkerCapValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConfigureWorkerCap_Rejects_NonPositive_Caps(int cap)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => Extractor.ConfigureWorkerCap(cap));

        Assert.Equal("cap", exception.ParamName);
    }
}
