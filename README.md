# RustTrafilatura DotNet ![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/vinhch/rust-trafilatura-dotnet/ci.yml?branch=main) [![Nuget](https://img.shields.io/nuget/v/RustTrafilatura?maxAge=3600)](https://www.nuget.org/packages/RustTrafilatura/)

.NET binding for [`rust-trafilatura`](https://github.com/markusmobius/rust-trafilatura), the crate linked into a native library per RID and called in
process over a length-delimited C ABI. Extraction is the crate's; this package supplies the ABI, the
JSON contract and the managed surface. The payload crosses as a raw byte buffer and the response is
parsed from the native bytes, with no base64, no UTF-16 copy and no intermediate `string`;
`ExtractMany` batches under bounded parallelism.

## Installation

```console
dotnet add package RustTrafilatura
```

## Quick Start

```csharp
using RustTrafilatura;

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

var result = Extractor.ExtractFromHtml(Html, new ExtractionOptions
{
    Url = "https://example.com/news/library",
    ResponseFields = ["contentText", "metadata"],
});

Console.WriteLine("Title: " + result.Metadata!.Title); // Council approves the new library
Console.WriteLine("Author: " + result.Metadata.Author); // Ada Lovelace
Console.WriteLine("Date: " + result.Metadata.Date?.ToString("yyyy-MM-dd")); // 2026-10-08
Console.WriteLine("Text: " + result.ContentText); // the article text, no nav or footer
```

## API Reference

### Entry points

| Function | Description |
| --- | --- |
| `Extractor.ExtractFromHtml(string html, ExtractionOptions? options = null)` | Extracts a page held as a string, encoding it as UTF-8 before the path below |
| `Extractor.ExtractFromBytes(ReadOnlyMemory<byte> html, ExtractionOptions? options = null)` | Extracts bytes that are memory-backed: a `byte[]`, a slice, a pooled rent or a memory view, none of them copied |
| `Extractor.ExtractFromSpan(ReadOnlySpan<byte> html, ExtractionOptions? options = null)` | The same pipeline one step earlier, for a buffer no memory view can express: a `stackalloc` one or unmanaged memory behind a pointer |
| `Extractor.ExtractMany(IReadOnlyList<byte[]> pages, ExtractionOptions? options = null, int maxDegreeOfParallelism = 0)` | Extracts a batch under bounded parallelism |
| `Extractor.ExtractMany(IReadOnlyList<ReadOnlyMemory<byte>> pages, ExtractionOptions? options = null, int maxDegreeOfParallelism = 0)` | The same batch over memory pages |
| `Extractor.ConfigureWorkerCap(int cap)` | Sets the runaway-worker cap once per process; returns `WorkerCapOutcome.Applied` or `AlreadyFixed`, and throws for a cap of 0 or below |
| `Extractor.WorkerCap` | The cap that applies right now, read without fixing it |
| `Extractor.LiveRunawayWorkers` | An advisory snapshot of how many detached budgeted workers are live |
| `Extractor.LibraryVersion` | What the loaded native library says about itself: the `abi` and `schemaVersion` it implements, the version of this binding's crate and the version of the `rust-trafilatura` core it links |

### Options

Every member of `ExtractionOptions` is optional. Leaving one unset passes nothing for it, and the
native library applies its own default rather than a value this package invented.

| Option | Default | Effect |
| --- | --- | --- |
| `ResponseFields` | text, comments and metadata | Which of `contentText`, `commentsText`, `contentHtml`, `commentsHtml` and `metadata` to return. An unnamed field is never computed, so omitting HTML skips serializing it |
| `Url` | null | The absolute `http`/`https` URL the page was fetched from. Used for metadata the document cannot carry; any other value is rejected |
| `InputEncoding` | null | An `encoding_rs` label to decode the payload with, instead of detecting the charset from it |
| `TimeBudgetMs` | null | The wall-clock budget for the whole call, in milliseconds. A call with a budget returns by the deadline plus scheduler slack with a `TimeBudgetExceeded` failure, while the underlying extraction finishes on a detached worker afterwards. Valid values are 1..=4294967296; 0, negative and out-of-range values are rejected as `InvalidRequest`. The pool of budgeted workers is capped by `Extractor.ConfigureWorkerCap` or `RUSTTRAFILATURA_MAX_RUNAWAY_WORKERS` (default 16), and a call that finds it full fails with `WorkerCapExceeded` |
| `TargetLanguage` | null | The language to expect. Null expects nothing |
| `EnableFallback` | false | Whether to compare against the bundled readability-lxml candidate as well. The default is the FAST path: native recall and baseline recovery only |
| `Focus` | `Balanced` | How much of the page to keep, out of `Balanced`, `FavorRecall` and `FavorPrecision` |
| `ExcludeComments` | false | Whether to drop the page's comments |
| `ExcludeTables` | false | Whether to drop tables |
| `IncludeImages` | false | Whether to keep image references |
| `IncludeLinks` | false | Whether to keep links |
| `BlacklistedAuthors` | none | Authors whose documents are rejected |
| `Deduplicate` | false | Whether to remove repeated content |
| `HasEssentialMetadata` | false | Whether a document without essential metadata is rejected |
| `MaxTreeSize` | 0 | An upper bound on the extracted tree's direct children; 0 means no limit |
| `HtmlDateMode` | `Default` | How hard to look for a date, out of `Default`, `Fast`, `Extensive` and `Disabled` |
| `PruneSelector` | none | A CSS selector whose matching nodes are removed before extraction |
| `Config` | core defaults | The size thresholds: `CacheSize` 4096, `MaxDuplicateCount` 2, `MinDuplicateCheckSize` 100, `MinExtractedSize` 250, `MinExtractedCommentSize` 1, `MinOutputSize` 1, `MinOutputCommentSize` 1 |

### Results and failures

```csharp
ExtractionResult result;

try
{
    result = Extractor.ExtractFromBytes(page, options);
}
catch (ContractVersionMismatchException)
{
    // The native library that loaded is not the one this assembly was compiled for.
    return;
}
catch (TrafilaturaException exception) when (exception.Kind == TrafilaturaErrorKind.OutputTooShort)
{
    // The page extracted too little text. ContextJson is {"content": 12, "comments": 0}, and the
    // Config thresholds behind it are the caller's to lower.
    return;
}
catch (TrafilaturaException)
{
    // Every other kind. Kind and ContextJson are on the exception either way.
    return;
}

// Content
result.ContentText    // the article's text
result.CommentsText   // the comments' text
result.ContentHtml    // the article as cleaned HTML
result.CommentsHtml   // the comments as cleaned HTML

// Metadata, as the core reports it
result.Metadata!.Title         // page title
result.Metadata!.Author        // author name
result.Metadata!.Date          // publication date, or null
result.Metadata!.Url           // canonical URL
result.Metadata!.Hostname      // domain name
result.Metadata!.Description   // meta description
result.Metadata!.Sitename      // site name
result.Metadata!.Categories    // IReadOnlyList<string>, never null
result.Metadata!.Tags          // IReadOnlyList<string>, never null
result.Metadata!.Id            // the core's document id
result.Metadata!.Fingerprint   // the core's fingerprint of the document
result.Metadata!.License       // content licence
result.Metadata!.Language      // detected language
result.Metadata!.Image         // featured image URL
result.Metadata!.PageType      // page type, such as "article"
```

Note: a member the request did not name is null; one it named that the page has nothing for is empty.

| `TrafilaturaException.Kind` | Raised when | Context |
| --- | --- | --- |
| `Io` | a compressed payload cannot be read | |
| `UnsupportedCharset` | `InputEncoding` names a charset the decoder does not have | |
| `CharsetNotDetected` | the payload's charset cannot be detected and `InputEncoding` is unset | |
| `MissingDocument` | the parser found no document | |
| `HtmlLanguage` | the page's own declared language does not match `TargetLanguage` | |
| `WrongLanguage` | the language of the extracted text does not match `TargetLanguage` | |
| `RequiredTitle`, `RequiredUrl`, `RequiredDate` | `HasEssentialMetadata` is set and that piece is missing | |
| `OutputTreeTooLong` | the extracted tree exceeds `MaxTreeSize` | `count` |
| `OutputTooShort` | the extracted text is under the `Config` thresholds | `content`, `comments` |
| `DuplicateBody` | `Deduplicate` is set and the content repeats | |
| `TimeBudgetExceeded` | the call's `TimeBudgetMs` deadline expired | `budgetMs` |
| `WorkerCapExceeded` | the budget path's detached-worker pool is full | `limit` |
| `InvalidUrl` | `Url` is not an absolute `http`/`https` URL | |
| `InvalidRequest` | the request header is malformed: an unknown key, an unknown or empty `ResponseFields`, an unknown enum spelling | |
| `Panic` | a Rust panic was caught at the ABI boundary | |
| `Unknown` | the library reported a kind this client version does not recognise | |

## Advanced Usage

### Getting the page

The package never fetches: you hand it a page you already have, and the URL it came from is a
separate argument.

```csharp
using var http = new HttpClient();
var url = "https://example.com/news/library";
var page = await http.GetByteArrayAsync(url);

var result = Extractor.ExtractFromBytes(page, new ExtractionOptions { Url = url });
```

`Url` is metadata the document cannot carry about itself, and a page without it extracts anyway. A
gzip response is accepted as it is, because the library detects and decodes a compressed payload
itself; a page you already hold as a string goes to `ExtractFromHtml` instead.

### Slices and pooled buffers

Anything memory-backed goes to `ExtractFromBytes` without a copy: a `byte[]`, a slice of one, a
pooled rent or a `Memory<byte>`.

```csharp
// One page inside a longer response: extract the slice, not the envelope around it.
var result = Extractor.ExtractFromBytes(buffer.AsMemory(offset, pageLength), options);
```

### Reading from a stream

The entry points take bytes, not streams, and they read nothing: you read the page into a buffer, and
the extractor reads the buffer. With a stream whose length is known, one rent and one read are enough,
and nothing copies the page out of that buffer:

```csharp
using var http = new HttpClient();
using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
using var stream = await response.Content.ReadAsStreamAsync();

// ResponseHeadersRead is what keeps HttpClient from buffering the body first.
var length = (int)response.Content.Headers.ContentLength!;
var buffer = ArrayPool<byte>.Shared.Rent(length);
try
{
    await stream.ReadExactlyAsync(buffer.AsMemory(0, length));

    var result = Extractor.ExtractFromBytes(buffer.AsMemory(0, length), new ExtractionOptions { Url = url });
}
finally
{
    ArrayPool<byte>.Shared.Return(buffer);
}
```

A local file is the same shape with `File.OpenRead("page.html")` and `file.Length` for the length.

A chunked response carries no `Content-Length`, and `HttpClientHandler` drops it when the handler
decompresses the body itself. The library decodes a compressed payload on its own, so a handler left
at `DecompressionMethods.None` both keeps the length and hands the extractor the bytes as they
arrived. A stream whose length is not known has to be assembled first, by `MemoryStream` or
`ReadAsByteArrayAsync`, and the array that comes out reaches `ExtractFromBytes` without a copy.

Extraction is not incremental: the page is still held whole, once, in the rent, and the library
builds its own tree from it. What this shape saves is a second page-sized copy and a page-sized
allocation per call, not the page.

### Batches

`ExtractMany` runs pages in parallel with `maxDegreeOfParallelism` extractions at once, defaulting to
`Environment.ProcessorCount`. Items come back in input order, and one page's failure stays in its own
item rather than aborting the batch:

```csharp
var batch = Extractor.ExtractMany(pages, maxDegreeOfParallelism: 4);

for (var index = 0; index < batch.Count; index++)
{
    if (!batch[index].Succeeded)
    {
        Console.WriteLine($"{index}: {batch[index].Error!.Kind}");
        continue;
    }

    Console.WriteLine(batch[index].Result!.ContentText);
}
```

A failure to run the extraction at all, a missing native library for one, is thrown instead: as
itself when it was the only failure the loop collected, inside an `AggregateException` when several
pages hit it.

### Only the fields you need

`ResponseFields` shapes the response rather than the extraction, so it is the cheapest option to use:
asking for text and metadata alone avoids the tree walk and the article-sized buffer that serializing
HTML costs.

```csharp
var options = new ExtractionOptions { ResponseFields = ["contentText", "metadata"] };
```

### A wall-clock budget

`TimeBudgetMs` bounds the wait, not the work: a call with a budget returns by the deadline plus
scheduler slack with a `TimeBudgetExceeded` failure, while the extraction finishes on a detached
worker afterwards. That is the shape for untrusted pages, where a pathological document must not pin
its caller, and the two kinds stay separate because they call for different responses — the budget
expired means this page was slow, the pool is full means the process is saturated.

```csharp
try
{
    var result = Extractor.ExtractFromBytes(page, new ExtractionOptions { TimeBudgetMs = 500 });
}
catch (TrafilaturaException exception) when (exception.Kind == TrafilaturaErrorKind.TimeBudgetExceeded)
{
    // The page outlasted its budget. The worker keeps running to completion in the background,
    // so a retry re-enters a process that is still paying for this page.
    return;
}
catch (TrafilaturaException exception) when (exception.Kind == TrafilaturaErrorKind.WorkerCapExceeded)
{
    // Every detached-worker slot is taken: the right response is backoff, not another page.
    return;
}
```

### Bounding the worker pool

The pool those workers run on is capped, once per process, by whichever of two setters fixes the
cap first: `Extractor.ConfigureWorkerCap(cap)` in code, or the `RUSTTRAFILATURA_MAX_RUNAWAY_WORKERS`
variable in the process environment (default 16). Calling the configure at host start, before the
first budgeted call, is the way to make the cap certain:

```csharp
Extractor.ConfigureWorkerCap(32);  // WorkerCapOutcome.Applied
```

The outcome says which writer won: `Applied` when this call fixed the cap, `AlreadyFixed` when an
earlier configure or the first budgeted call had already fixed it, in which case nothing changes and
the process keeps the earlier value — a late configure cannot silently run on the default. On Unix,
setting the variable through `Environment.SetEnvironmentVariable` updates a managed copy that the
native side never sees, so the in-code route there is the configure, not the variable; on the
Windows path the variable is read at the first budgeted call, so it too has to be in place before
that call — a container's environment block, a systemd unit, `launchSettings`.

For a positive integer the variable names the cap; an absent, blank, zero or unparsable value keeps
the default. `Extractor.WorkerCap` reports the cap that applies right now without fixing it —
before anything else it is the value that would apply, so a host may log its intent before
configuring — and `Extractor.LiveRunawayWorkers` is an advisory snapshot of how many detached
workers are live, for saturation logging behind a `WorkerCapExceeded` burst. The cap counts every
in-flight budgeted call — a worker holds its slot from its call's start, not from its deadline —
and each one carries its own payload copy, its working heap and a 16 MiB stack, so the cap is the
worst-case memory bound of the budget path; raise it only with that budget in mind. A call
without a budget takes the same one-shot path as before, so the option costs nothing until it is set.
In `ExtractMany` the budget applies to each page's own call and every page holds a slot while it
runs, so a batch whose parallelism exceeds the cap has its extra pages fail with `WorkerCapExceeded`
right away: keep `maxDegreeOfParallelism` at or below the cap. A batch of N pages at degree of
parallelism D can take about N/D times the budget in total.

## Supported Platforms

The package ships a `net8.0` and a `net10.0` asset: a `net9.0` project resolves the `net8.0` one,
and `net461` and `netstandard2.0` are out of scope, because the source-generated interop needs .NET
7 or later. Each native library is packed under `runtimes/<rid>/native/`; a host RID the package does
not carry fails with `DllNotFoundException` at the first extraction.

| RID | Platform | Native library |
| --- | --- | --- |
| `win-x64` | Windows x64 | `rust_trafilatura_ffi.dll` |
| `linux-x64` | Linux x64 | `librust_trafilatura_ffi.so` |
| `linux-arm64` | Linux arm64 | `librust_trafilatura_ffi.so` |
| `osx-x64` | macOS x64 | `librust_trafilatura_ffi.dylib` |
| `osx-arm64` | macOS arm64 | `librust_trafilatura_ffi.dylib` |

Both assets compile with `IsAotCompatible`, so the trim and AOT analyzers run on every build and a
reflection-based path fails the build rather than a consumer's publish; no `PublishTrimmed`
application is published anywhere. The `Aot_Publish_Extracts_A_Page` job publishes and runs the
committed consumer with `-p:PublishAot=true` for `win-x64` and `linux-x64` only, through a
`ProjectReference` rather than NuGet's `runtimes/<rid>/native/` resolution.

## Compatibility and Limitations

- **The contract is checked once per process.** The first extracting call compares the loaded
  library's `abi` and `schemaVersion` against the `1` and `1` this assembly was compiled for, and
  throws `ContractVersionMismatchException`, carrying both sides, when they differ. Later calls
  report that same outcome rather than re-reading the library; `crate` and `core` are versioned
  independently of `abi` and are not compared.
- **Nothing is fetched and nothing is written.** The library opens no socket and reads no file. It
  writes nothing to stderr, and extraction never echoes the input HTML back in an error.
- **Untrusted input can still end the process.** A Rust stack overflow aborts rather than throwing,
  so a caller cannot catch it. A payload above 64 MiB and a header above 1 MiB are rejected before
  parsing; `MaxTreeSize` bounds the extracted tree, not the input, and is not a denial-of-service
  bound.
- **One-shot, synchronous, no cancellation.** There is no handle to hold, no async entry point and
  no cancellation token. `ExtractMany` is the only concurrency the package offers. The bounded
  alternative to cancelling is `TimeBudgetMs`: a call with a budget returns by the deadline plus
  scheduler slack with a `TimeBudgetExceeded` failure, while the extraction finishes on a detached
  worker afterwards — the core has no cancellation checkpoints, so the budget bounds the caller's
  wait, not the worker's work. Those workers are capped by `Extractor.ConfigureWorkerCap` or
  `RUSTTRAFILATURA_MAX_RUNAWAY_WORKERS`, whichever fixes the cap first (default 16, read once); a
  call that finds the pool full fails with `WorkerCapExceeded`, which a caller answers with backoff
  rather than a per-page timeout.
- **No declared dependencies.** Both assets use the `System.Text.Json` in box with their target
  framework; a consumer that wants a particular patch level references it itself.

## Development

Prerequisites, with the minimum version of each:

| Tool | Minimum | Needed by |
| --- | --- | --- |
| .NET SDK | 10.0.100 | everything under `dotnet/` |
| .NET 8 runtime | 8.0 | the `net8.0` tests |
| Rust toolchain | 1.98.1 | everything under `rust/` |
| `cargo-nextest` | 0.9.148 | `test.sh` |
| `cargo-mutants` | latest | the mutation runs in `quality.yml` |
| git | any | `corpus.sh`, which fetches the benchmark pages once |
| clang with the zlib headers, or the MSVC build tools | any | publishing the AOT consumer |

```console
bash scripts/version.sh    # report the version the crate, the lockfile and the package carry
bash scripts/build.sh      # stage the host RID's native library
bash scripts/test.sh       # verify the staged library, then run both suites; --report adds the reports
bash scripts/release.sh    # pack artifacts/RustTrafilatura.<version>.nupkg, then check it
bash scripts/bench.sh      # the round-trip ratio under its budget
```

The same five scripts exist as `scripts/*.ps1`, and each one's `--help` lists its flags. `test.sh --report`
puts one JUnit file per test command into `artifacts/test-results/`, the directory the CI report step
publishes from. `version.sh --set` and `--check` are what keep the crate version, the lockfile entry and the
package `<Version>` one number.

Cutting a release is in [`docs/releasing.md`](docs/releasing.md): the version, the tag, the checks and the
GitHub Release that tag produces, and the manual publish to NuGet through trusted publishing.

`release.sh` requires all five RIDs to be staged unless it is given `--allow-partial`, and refuses a
staged library that exports a `test-hooks` symbol. Its `Category=Package` check reads the packed `.nupkg`,
so `test.sh` leaves it out and a bare `dotnet test` includes it: skip it with xunit v3's own
`--filter-not-trait "Category=Package"`. The test project is an executable on Microsoft.Testing.Platform,
opted in by the `test` section of `global.json`, which is why a trait flag replaces the VSTest `--filter`
expression. A locally packed package whose version is already installed needs a fresh `NUGET_PACKAGES`
folder or the cached entry cleared, because NuGet resolves by version.

`bench.sh` compares one `rt_extract` call against a direct call into the `rust-trafilatura` core, and fails
when the ratio exceeds its budget; it is a ratio, not a throughput figure. The allocation and speed analysis
is in [`docs/research/binding-performance.md`](docs/research/binding-performance.md).

`dotnet/benchmarks/RustTrafilatura.Benchmarks` is the BenchmarkDotNet project for the managed side:
the three single-page entry points over pages spread across the corpus's size range, plus two
whole-corpus benchmarks, each case reporting its time and the managed bytes it allocates.
`bash scripts/corpus.sh` fetches the pages it reads, the pinned commit of
`content-extractor-benchmark`, into the gitignored `dotnet/benchmarks/corpus/`, and then
`dotnet run --project dotnet/benchmarks/RustTrafilatura.Benchmarks -c Release -- --job short`
runs it against the staged native library. A run takes minutes, so `test.sh` and CI leave it out.

`dotnet/tests/RustTrafilatura.AotConsumer` is not in `dotnet/RustTrafilatura.sln`: it is published and run,
never tested and never packed.

## Credits

- [adbar/trafilatura](https://github.com/adbar/trafilatura) defines the extraction this package follows.
- [rust-trafilatura](https://github.com/markusmobius/rust-trafilatura) is the crate this package binds.

## License

MIT
