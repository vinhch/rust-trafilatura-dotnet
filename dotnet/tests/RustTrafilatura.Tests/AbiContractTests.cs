using Xunit;

namespace RustTrafilatura.Tests;

/// <summary>
/// The contract check the first extraction makes against the library's own version document: which
/// documents pass, which are reported as a mismatch, and what a caller reads off the exception.
/// </summary>
/// <remarks>
/// The document cases drive <see cref="Extractor.CheckContract(string)"/>, which takes the document as
/// text; the failing-read case drives <see cref="Extractor.ReadVersionDocument(int, string)"/>, which
/// is where a `rt_version` call's code becomes a document, because a read that fails never reaches the
/// comparison. The alternative - a native library built for a different ABI, or one whose `rt_version`
/// fails - is not something a test can load, and the first-use path throws exactly what these two
/// methods return or throw.
/// </remarks>
public sealed class AbiContractTests
{
    /// <summary>The shipped library's own document is the one passing case only a run can produce.</summary>
    [Fact]
    public void RealLibrary_Passes_The_Contract_Check() =>
        Assert.Null(Extractor.CheckContract(Extractor.AbiVersion));

    /// <summary>The `abi` side alone, with the schema matching, so only that number can be the finding.</summary>
    [Fact]
    public void Abi_Mismatch_Is_Reported_With_Both_Sides()
    {
        var mismatch = Assert.IsType<ContractVersionMismatchException>(
            Extractor.CheckContract("""{"abi":2,"schemaVersion":1,"crate":"2.2.8"}"""));

        Assert.Equal(1, mismatch.ExpectedAbi);
        Assert.Equal(2, mismatch.ActualAbi);
        Assert.Equal(1, mismatch.ExpectedSchemaVersion);
        Assert.Equal(1, mismatch.ActualSchemaVersion);
        Assert.Contains("reports abi 2 and schemaVersion 1", mismatch.Message, StringComparison.Ordinal);
        Assert.Contains("requires abi 1 and schemaVersion 1", mismatch.Message, StringComparison.Ordinal);
        AssertNamesTheFix(mismatch);
    }

    /// <summary>The `schemaVersion` side alone, with the ABI matching.</summary>
    [Fact]
    public void Schema_Version_Mismatch_Is_Reported_With_Both_Sides()
    {
        var mismatch = Assert.IsType<ContractVersionMismatchException>(
            Extractor.CheckContract("""{"abi":1,"schemaVersion":2,"crate":"2.2.8"}"""));

        Assert.Equal(1, mismatch.ExpectedAbi);
        Assert.Equal(1, mismatch.ActualAbi);
        Assert.Equal(1, mismatch.ExpectedSchemaVersion);
        Assert.Equal(2, mismatch.ActualSchemaVersion);
        Assert.Contains("reports abi 1 and schemaVersion 2", mismatch.Message, StringComparison.Ordinal);
        Assert.Contains("requires abi 1 and schemaVersion 1", mismatch.Message, StringComparison.Ordinal);
        AssertNamesTheFix(mismatch);
    }

    /// <summary>Both sides at once, so neither the exception nor its message may report only the first.</summary>
    [Fact]
    public void Both_Values_Mismatch_Is_Reported_With_Both_Sides()
    {
        var mismatch = Assert.IsType<ContractVersionMismatchException>(
            Extractor.CheckContract("""{"abi":2,"schemaVersion":3,"crate":"2.2.8"}"""));

        Assert.Equal(1, mismatch.ExpectedAbi);
        Assert.Equal(2, mismatch.ActualAbi);
        Assert.Equal(1, mismatch.ExpectedSchemaVersion);
        Assert.Equal(3, mismatch.ActualSchemaVersion);
        Assert.Contains("reports abi 2 and schemaVersion 3", mismatch.Message, StringComparison.Ordinal);
        Assert.Contains("requires abi 1 and schemaVersion 1", mismatch.Message, StringComparison.Ordinal);
        AssertNamesTheFix(mismatch);
    }

    /// <summary>
    /// `crate` is versioned independently of `abi` (R6), so a document whose crate version differs in
    /// every digit still passes when both numbers match.
    /// </summary>
    [Fact]
    public void Crate_Version_Is_Not_Compared() =>
        Assert.Null(Extractor.CheckContract("""{"abi":1,"schemaVersion":1,"crate":"9.9.9"}"""));

    /// <summary>
    /// A document that states no contract version cannot pass: text that is not JSON, a document with
    /// neither key, and keys whose values are not numbers all report both sides as absent rather than
    /// guessing a number the library never sent.
    /// </summary>
    [Fact]
    public void Unreadable_Document_Reports_Neither_Number()
    {
        string[] documents =
        [
            "not json",
            """{"crate":"2.2.8"}""",
            """{"abi":"1","schemaVersion":"1"}""",
        ];

        foreach (var document in documents)
        {
            var mismatch = Assert.IsType<ContractVersionMismatchException>(Extractor.CheckContract(document));

            Assert.Equal(1, mismatch.ExpectedAbi);
            Assert.Null(mismatch.ActualAbi);
            Assert.Equal(1, mismatch.ExpectedSchemaVersion);
            Assert.Null(mismatch.ActualSchemaVersion);
            Assert.Contains(
                "reports abi (not reported) and schemaVersion (not reported)",
                mismatch.Message,
                StringComparison.Ordinal);
            AssertNamesTheFix(mismatch);
        }
    }

    /// <summary>
    /// A `rt_version` call that answers with a non-zero code hands out no document, so there is no
    /// number to compare and the library is reported as one this assembly cannot use rather than as an
    /// extraction failure.
    /// </summary>
    /// <remarks>
    /// The read's own failure stays reachable through the inner exception, which is what still names
    /// the code the library answered with; the empty document is the shape a foreign library that
    /// writes no buffer produces.
    /// </remarks>
    [Fact]
    public void Version_Read_That_Fails_Reports_Neither_Number()
    {
        var mismatch = Assert.Throws<ContractVersionMismatchException>(
            () => Extractor.ReadVersionDocument(2, string.Empty));

        Assert.Equal(1, mismatch.ExpectedAbi);
        Assert.Null(mismatch.ActualAbi);
        Assert.Equal(1, mismatch.ExpectedSchemaVersion);
        Assert.Null(mismatch.ActualSchemaVersion);
        Assert.Contains(
            "reports abi (not reported) and schemaVersion (not reported)",
            mismatch.Message,
            StringComparison.Ordinal);
        AssertNamesTheFix(mismatch);
        Assert.Contains(
            "code 2",
            Assert.IsType<TrafilaturaException>(mismatch.InnerException).Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The fix, because the message is the only part of the failure a caller may log: it has to name
    /// what to install, not only what differed.
    /// </summary>
    private static void AssertNamesTheFix(ContractVersionMismatchException mismatch) =>
        Assert.Contains("Install the RustTrafilatura package version", mismatch.Message, StringComparison.Ordinal);
}
