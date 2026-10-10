# RustTrafilatura DotNet

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
| `Extractor.LibraryVersion` | What the loaded native library says about itself: the `abi` and `schemaVersion` it implements, the version of this binding's crate and the version of the `rust-trafilatura` core it links |

### Options

Every member of `ExtractionOptions` is optional. Leaving one unset passes nothing for it, and the
native library applies its own default rather than a value this package invented.

| Option | Default | Effect |
| --- | --- | --- |
| `ResponseFields` | text, comments and metadata | Which of `contentText`, `commentsText`, `contentHtml`, `commentsHtml` and `metadata` to return. An unnamed field is never computed, so omitting HTML skips serializing it |
| `Url` | null | The absolute `http`/`https` URL the page was fetched from. Used for metadata the document cannot carry; any other value is rejected |
| `InputEncoding` | null | An `encoding_rs` label to decode the payload with, instead of detecting the charset from it |
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
  no cancellation token. `ExtractMany` is the only concurrency the package offers.
- **No declared dependencies.** Both assets use the `System.Text.Json` in box with their target
  framework; a consumer that wants a particular patch level references it itself.

## Development

Prerequisites: the .NET SDK, a Rust toolchain, `cargo-nextest` for `test.sh`, and a checkout of this
repository.

```console
bash scripts/version.sh    # report the version the crate, the lockfile and the package carry
bash scripts/build.sh      # stage the host RID's native library
bash scripts/test.sh       # verify the staged library, then run both suites and write the reports
bash scripts/release.sh    # pack artifacts/RustTrafilatura.<version>.nupkg, then check it
bash scripts/bench.sh      # the round-trip ratio under its budget
```

The same five scripts exist as `scripts/*.ps1`, and each one's `--help` lists its flags.
`test.sh` needs `cargo-nextest` because `cargo test` cannot write a report on stable Rust: libtest's
machine-readable output is unstable, so the JUnit report comes from nextest's own reporter. Every test
command writes one into `artifacts/test-results/`, which is the gitignored directory the CI workflow's
report step publishes from.
`version.sh --check` fails when the crate version in `rust/Cargo.toml`, the entry cargo writes into
`rust/Cargo.lock` and `<Version>` in the package project disagree; `--check --tag vX.Y.Z` also fails
when that tag does not name the version the sources carry, and `--set X.Y.Z` writes one version into
all three without committing or tagging.

A release is that one version and one tag: `scripts/version.sh --set X.Y.Z` writes the version into both
manifests and lets cargo write it into the lockfile, those three files are committed, and then
`git tag vX.Y.Z` is pushed. Only a tag push starts `.github/workflows/release.yml`, which builds all
five RIDs, packs the package and publishes it through the `nuget` environment. Every other change is
verified by `.github/workflows/ci.yml` on push and pull request. Starting the release workflow by hand
builds and checks a package without publishing it, which is how `main` is checked before a tag exists.

`release.sh` requires all five RIDs to be staged unless it is given `--allow-partial`, and refuses a
staged library that exports a `test-hooks` symbol. Its `Category=Package` check reads the packed
`.nupkg`, so `test.sh` leaves it out and a bare `dotnet test` includes it: skip it with xunit v3's own
`--filter-not-trait "Category=Package"`. The test project runs on Microsoft.Testing.Platform, opted in
by the `test` section of `global.json`, which is why a trait flag replaces the VSTest `--filter`
expression and why the project is an executable.

`bench.sh` compares one `rt_extract` call against a direct call into the `rust-trafilatura` core on
the same fixture and machine, and fails when the ratio exceeds its budget; it is a ratio, not a
throughput figure. The allocation and speed analysis is in
[`docs/research/binding-performance.md`](docs/research/binding-performance.md).

A locally packed package whose version is already installed needs a fresh `NUGET_PACKAGES` folder or
the cached entry cleared, because NuGet resolves by version.

`dotnet/tests/RustTrafilatura.AotConsumer` is not in `dotnet/RustTrafilatura.sln`: it is published
and run, never tested and never packed.

## Credits

[adbar/trafilatura](https://github.com/adbar/trafilatura) defines the extraction this package follows.
[rust-trafilatura](https://github.com/markusmobius/rust-trafilatura) is the crate this package binds.

## License

MIT
