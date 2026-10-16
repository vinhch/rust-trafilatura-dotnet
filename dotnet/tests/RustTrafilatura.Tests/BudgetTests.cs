using System.Diagnostics;
using Xunit;

namespace RustTrafilatura.Tests;

/// <summary>
/// The time budget end to end, over the real ABI. The staged library is the default build, so the
/// timeout must come from real work rather than a test hook: the payload copy is inside the
/// budget, which is what makes a payload big enough to cross a 1 ms budget deterministic.
/// </summary>
public sealed class BudgetTests
{
    /// <summary>
    /// A payload whose copy alone outlasts a 1 ms budget, padded past the point the whole
    /// extraction takes orders of magnitude longer than the budget. The nesting is the pattern the
    /// native suite's stack tests use; the padding keeps the document parseable while the copy and
    /// the parse both stay inside the worker the budget bounds.
    /// </summary>
    private static byte[] OversizedPayload()
    {
        const int nesting = 1000;
        var payload = new List<byte>(32 * 1024 * 1024);
        payload.AddRange("<html lang=\"en\"><head><title>Deep</title></head><body><article><p>"u8);
        payload.AddRange("<div>"u8);
        foreach (var _ in Enumerable.Range(0, nesting))
        {
            payload.AddRange("<div>"u8);
        }
        payload.AddRange("Text long enough for the extractor to keep the document."u8);
        payload.AddRange("</div>"u8);
        foreach (var _ in Enumerable.Range(0, nesting))
        {
            payload.AddRange("</div>"u8);
        }
        payload.AddRange("</p></article></body></html>"u8);
        // Trailing spaces after `</html>`: the core trims them, so they pad the copy without
        // changing what the worker extracts.
        payload.AddRange(new byte[32 * 1024 * 1024 - payload.Count].Select(_ => (byte)' '));
        return payload.ToArray();
    }

    [Fact]
    public void Large_Payload_With_Tiny_Budget_Throws_TimeBudgetExceeded()
    {
        var payload = OversizedPayload();
        var stopwatch = Stopwatch.StartNew();

        var exception = Assert.Throws<TrafilaturaException>(
            () => Extractor.ExtractFromBytes(payload, new ExtractionOptions { TimeBudgetMs = 1 }));

        stopwatch.Stop();
        Assert.Equal(TrafilaturaErrorKind.TimeBudgetExceeded, exception.Kind);
        using var context = System.Text.Json.JsonDocument.Parse(exception.ContextJson!);
        Assert.Equal(1, context.RootElement.GetProperty("budgetMs").GetInt32());
        // The deadline is honoured plus scheduler slack: the call must return well before the
        // worker finishes the extraction of a payload this size.
        Assert.True(
            stopwatch.ElapsedMilliseconds < 5_000,
            $"the call must return by the deadline plus scheduler slack, took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Generous_Budget_Extracts_Normally()
    {
        var withoutBudget = Extractor.ExtractFromBytes(Fixtures.Article);
        var withBudget = Extractor.ExtractFromBytes(
            Fixtures.Article, new ExtractionOptions { TimeBudgetMs = 60_000 });

        Assert.Equal(withoutBudget.ContentText, withBudget.ContentText);
        Assert.Equal(withoutBudget.Metadata!.Title, withBudget.Metadata!.Title);
    }
}
