using System.IO.Compression;
using System.Text;

namespace RustTrafilatura.Tests;

/// <summary>
/// The inline pages the tests extract. The core repository ships no HTML corpus, so the fixtures
/// live in code, and the member names below are the ones the option, metadata and error tests
/// consume. Static initializers run in declaration order, so every source page is declared before
/// the byte arrays built from it.
/// </summary>
internal static class Fixtures
{
    /// <summary>U+00AD SOFT HYPHEN, which the core drops while normalizing the decoded text.</summary>
    private static readonly string SoftHyphen = char.ConvertFromUtf32(0x00AD);

    /// <summary>U+0308 COMBINING DIAERESIS, so the soft-hyphen page carries NFD text.</summary>
    private static readonly string CombiningDiaeresis = char.ConvertFromUtf32(0x0308);

    /// <summary>U+00E9 LATIN SMALL LETTER E WITH ACUTE: 0xE9 in windows-1252, invalid as UTF-8.</summary>
    private static readonly char EAcute = (char)0xE9;

    /// <summary>U+00EF LATIN SMALL LETTER I WITH DIAERESIS: 0xEF in windows-1252, invalid as UTF-8.</summary>
    private static readonly char IDiaeresis = (char)0xEF;

    /// <summary>U+00FC LATIN SMALL LETTER U WITH DIAERESIS: 0xFC in windows-1252, invalid as UTF-8.</summary>
    private static readonly char UDiaeresis = (char)0xFC;

    /// <summary>The whole date signal in <see cref="ArticleHtml"/>, so <see cref="NoDateHtml"/> is the same page without it.</summary>
    private const string DateMeta = "<meta property=\"article:published_time\" content=\"2026-10-08T00:00:00Z\">";

    /// <summary>An article whose title, author and publication date are all present.</summary>
    internal static string ArticleHtml { get; } = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>Research results</title>
        <meta name="author" content="Ada Lovelace">
        <meta name="description" content="The research team reports its findings.">
        <meta property="og:site_name" content="Example Journal">
        <meta property="article:published_time" content="2026-10-08T00:00:00Z">
        </head>
        <body>
        <nav><a href="/">Home</a><a href="/news">News</a><a href="/about">About</a></nav>
        <main><article>
        <h1>Research results</h1>
        <p>The research team published its findings after a two-year study of how public data is collected, checked and released to the people who paid for it.</p>
        <p>Officials said the new framework will take effect next spring, and that compliance will be reviewed every year by an independent panel of reviewers.</p>
        <p>Critics argued the timeline is too tight for smaller publishers in Zurich and asked for the reporting burden to scale with the size of the organisation.</p>
        <p>Supporters replied that one public report each year is the least that makes the framework checkable by anyone outside the room.</p>
        </article></main>
        <footer><p>Copyright Example Journal. All rights reserved.</p></footer>
        </body>
        </html>
        """;

    /// <summary>
    /// The same page with U+00AD inside two words and an NFD umlaut. The core composes NFD and drops
    /// soft hyphens while decoding, so this reaches the extractor as the same document, one umlaut
    /// apart from <see cref="ArticleHtml"/>.
    /// </summary>
    internal static string ArticleWithSoftHyphensHtml { get; } = ArticleHtml
        .Replace("research", "re" + SoftHyphen + "search", StringComparison.Ordinal)
        .Replace("published", "pub" + SoftHyphen + "lished", StringComparison.Ordinal)
        .Replace("Zurich", "Zu" + CombiningDiaeresis + "rich", StringComparison.Ordinal);

    /// <summary>The same article with no date signal anywhere in the document.</summary>
    internal static string NoDateHtml { get; } =
        ArticleHtml.Replace(DateMeta, string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// A page whose extracted text carries a quotation mark and a non-ASCII letter, both written as
    /// character references so the page's own bytes stay ASCII: a response built from it carries that
    /// quote escaped and that letter as more than one byte, which are the two encodings a reader that
    /// only worked on plain text would get wrong.
    /// </summary>
    internal static string QuotedHtml { get; } = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>The office said no</title>
        <meta name="author" content="Grace Hopper">
        <meta property="article:published_time" content="2026-10-08T00:00:00Z">
        </head>
        <body>
        <main><article>
        <h1>The office said no</h1>
        <p>The office said &quot;the caf&eacute; stays open&quot; and then closed the file without a second hearing for the tenants who had waited since the spring for one.</p>
        <p>Nobody in the room asked the question again, and the minutes record only that the matter was noted and that the file would be kept for the usual period before it was archived.</p>
        <p>Two of the tenants sent a letter afterwards, and the letter is quoted in full in the appendix, quotation marks included, because the department chose not to paraphrase it.</p>
        </article></main>
        </body>
        </html>
        """;

    /// <summary>
    /// A windows-1252 page, written as its own text: the accented letters are single bytes there
    /// (0xE9, 0xEF and 0xFC) and none of them is valid UTF-8, so a UTF-8 decode would not produce the
    /// text this page carries and the charset detector has to choose a single-byte encoding. Those
    /// three bytes also decode the same way in every Latin-1 and Latin-2 candidate, so the text below
    /// does not depend on which one the detector picks.
    /// </summary>
    private static string Windows1252Html { get; } =
        "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"windows-1252\">"
        + "<title>Caf" + EAcute + " review</title>"
        + "<meta name=\"description\" content=\"A caf" + EAcute + " in Z" + UDiaeresis + "rich, reviewed.\">"
        + "</head><body><main><article>"
        + "<h1>Caf" + EAcute + " review</h1>"
        + "<p>The caf" + EAcute + " in Z" + UDiaeresis + "rich serves the best coffee in the city, and the r"
        + EAcute + "sum" + EAcute + " of its founder mentions another caf" + EAcute
        + " on the same street.</p>"
        + "<p>A na" + IDiaeresis + "ve reader may think the caf" + EAcute + " is ordinary, but the r"
        + EAcute + "sum" + EAcute + " says the caf" + EAcute + " in Z" + UDiaeresis
        + "rich opened in 1902 and has served the same guests ever since.</p>"
        + "</article></main></body></html>";

    /// <summary>The article as UTF-8 bytes: what the byte overload takes.</summary>
    internal static byte[] Article { get; } = Encoding.UTF8.GetBytes(ArticleHtml);

    /// <summary>UTF-8 bytes of the U+00AD and NFD variant.</summary>
    internal static byte[] ArticleWithSoftHyphens { get; } = Encoding.UTF8.GetBytes(ArticleWithSoftHyphensHtml);

    /// <summary>UTF-8 bytes of the article without a date.</summary>
    internal static byte[] NoDate { get; } = Encoding.UTF8.GetBytes(NoDateHtml);

    /// <summary>The windows-1252 page as raw bytes, never as a UTF-8 string.</summary>
    internal static byte[] Windows1252 { get; } = Latin1(Windows1252Html);

    /// <summary>UTF-8 bytes of the page whose text carries a quote and an accented letter.</summary>
    internal static byte[] Quoted { get; } = Encoding.UTF8.GetBytes(QuotedHtml);

    /// <summary>A gzip stream carrying <see cref="Article"/>.</summary>
    internal static byte[] Gzip { get; } = GzipBytes(Article);

    /// <summary>
    /// Maps code points to bytes one for one. windows-1252 agrees with Latin-1 above U+009F, which is
    /// the whole range this fixture uses, so the page can be written in C# as its own text.
    /// </summary>
    private static byte[] Latin1(string html) => Encoding.Latin1.GetBytes(html);

    private static byte[] GzipBytes(byte[] payload)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(payload);
        }

        return compressed.ToArray();
    }
}
