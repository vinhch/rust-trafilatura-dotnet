using RustTrafilatura;

// The page and the call are the README's usage example, so the AOT job publishes and runs what a
// consumer copies rather than a shape only this probe uses. Its exit code and the text it prints are
// the whole assertion: reaching the native library from a Native AOT binary is what a trimmed
// consumer cannot do if the interop or the JSON paths reflect.

const string Html = """
    <!DOCTYPE html>
    <html lang="en">
    <head>
    <title>Council approves the new library</title>
    <meta name="author" content="Ada Lovelace">
    <meta property="article:published_time" content="2026-10-08T00:00:00Z">
    </head>
    <body>
    <nav><a href="/">Home</a><a href="/news">News</a></nav>
    <main><article>
    <h1>Council approves the new library</h1>
    <p>The council approved the new central library on Tuesday, after a debate that ran past midnight and filled every seat in the chamber with residents who had waited four hours to speak.</p>
    <p>The building will open in the spring of 2028 on the site of the old bus depot, and the architects said the reading rooms will face the river so that the afternoon light reaches the desks.</p>
    <p>Critics of the plan said the cost had risen by a fifth since the first estimate, and asked the council to publish the revised budget before the contract is signed later this month.</p>
    </article></main>
    <footer><p>Copyright Example Journal. All rights reserved.</p></footer>
    </body>
    </html>
    """;

try
{
    var result = Extractor.ExtractFromHtml(Html, new ExtractionOptions
    {
        Url = "https://example.com/news/library",
        ResponseFields = ["contentText", "metadata"],
    });

    Console.WriteLine(result.ContentText);

    // Text on stdout is the signal the job reads; an empty result is the failure it must not report
    // as a pass, which is why the exit code carries the same verdict.
    if (string.IsNullOrWhiteSpace(result.ContentText))
    {
        Console.Error.WriteLine("the extraction returned no text");
        return 1;
    }

    return 0;
}
catch (Exception exception)
{
    // Printed rather than rethrown: an AOT build has no managed stack to unwind into, and the job
    // reads this line out of the log.
    Console.Error.WriteLine(exception);
    return 1;
}
