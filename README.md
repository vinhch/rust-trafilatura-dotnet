# RustTrafilatura DotNet ![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/vinhch/rust-trafilatura-dotnet/ci.yml?branch=main) [![Nuget](https://img.shields.io/nuget/v/RustTrafilatura?maxAge=3600)](https://www.nuget.org/packages/RustTrafilatura/)

.NET binding for [`trafilatura-rust`](https://crates.io/crates/trafilatura-rust), the crate linked into a native library per RID and called in
process over a length-delimited C ABI. Extraction is the crate's; this package supplies the ABI, the
JSON contract and the managed surface. The payload crosses as a raw byte buffer and the response is
parsed from the native bytes, with no base64, no UTF-16 copy and no intermediate `string`.

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
Console.WriteLine("Date: " + result.Metadata.Date); // 2026-10-08
Console.WriteLine("Text: " + result.ContentText); // the article text, no nav or footer
```

## API Reference

### Entry points

| Function | Description |
| --- | --- |
| `Extractor.ExtractFromHtml(string html, ExtractionOptions? options = null, CancellationToken cancellationToken = default)` | Extracts a page held as a string, encoding it as UTF-8 before the path below |
| `Extractor.ExtractFromBytes(ReadOnlyMemory<byte> html, ExtractionOptions? options = null, CancellationToken cancellationToken = default)` | Extracts bytes that are memory-backed: a `byte[]`, a slice, a pooled rent or a memory view, none of them copied |
| `Extractor.ExtractFromSpan(ReadOnlySpan<byte> html, ExtractionOptions? options = null, CancellationToken cancellationToken = default)` | The same pipeline one step earlier, for a buffer no memory view can express: a `stackalloc` one or unmanaged memory behind a pointer |
| `Extractor.LibraryVersion` | What the loaded native library says about itself: the `abi` and `schemaVersion` it implements, the version of this binding's crate and the version of the `trafilatura-rust` core it links |

### Options

Every member of `ExtractionOptions` is optional. Leaving one unset passes nothing for it, and the
native library applies its own default rather than a value this package invented.

| Option | Default | Effect |
| --- | --- | --- |
| `ResponseFields` | text, comments and metadata | Which of `contentText`, `commentsText` and `metadata` to return. A field that is not named is null in the response and in the result |
| `Url` | null | The URL the page was fetched from. Used for metadata the document cannot carry. Any string is accepted; one that is not an absolute URL is ignored, and an empty string means none. With `IncludeImages`, a value the core cannot join gives `InvalidValue` |
| `TargetLanguage` | null | The language to expect. Null expects nothing |
| `Focus` | `Balanced` | How much of the page to keep, out of `Balanced`, `FavorRecall` and `FavorPrecision` |
| `ExcludeComments` | false | Whether to drop the page's comments |
| `ExcludeTables` | false | Whether to drop tables |
| `IncludeImages` | false | Whether to keep image references |
| `IncludeLinks` | false | Whether to keep links |
| `BlacklistedAuthors` | none | Authors dropped from the metadata when they match; the document is not rejected |
| `HasEssentialMetadata` | false | Whether a document without essential metadata is rejected |
| `MaxTreeSize` | 0 | An upper bound on the extracted tree's direct children; 0 means no limit |
| `HtmlDateMode` | `Default` | How hard to look for a date, out of `Default`, `Fast` and `Extensive` |
| `Config` | core defaults | The size thresholds: `MinExtractedSize` 250, `MinOutputSize` 1, `MinOutputCommentSize` 1 |

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
    // The page extracted too little text. ContextJson is {"textChars": 12, "commentsChars": 0}, and the
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
result.Metadata!.License       // content licence
result.Metadata!.Language      // always empty until the core reports a language
result.Metadata!.Image         // featured image URL
result.Metadata!.PageType      // page type, such as "article"
```

Note: a member the request did not name is null; one it named that the page has nothing for is empty.

| `TrafilaturaException.Kind` | Raised when | Context |
| --- | --- | --- |
| `MissingDocument` | the parser found no document, including empty input | |
| `HtmlLanguage` | the page's own declared language does not match `TargetLanguage` | |
| `WrongLanguage` | not raised until the core reports a language: only the `<html lang>` check applies | |
| `MissingMetadata` | `HasEssentialMetadata` is set and a piece is missing | `missing`: the names of the missing pieces |
| `OutputTreeTooLong` | the extracted tree exceeds a limit | `limit`: `maxTreeSize`, `maxNodes`, `tagAttributes`, `tableCells` or `unknown`; `children` as well for `maxTreeSize` |
| `OutputTooShort` | the extracted text is under the `Config` thresholds | `textChars`, `commentsChars` |
| `InputTooLarge` | the payload or a document limit is exceeded | `limit`: `maxInputBytes`, `maxFileSize`, `jsonSize` or `unknown` |
| `DuplicateBody` | not raised: deduplication is off and no option turns it on | |
| `BlacklistedUrl` | not raised: no option sets a URL blacklist | |
| `InvalidValue` | the core rejected a value, such as an image URL it cannot join | |
| `InvalidRequest` | the request header is malformed: an unknown key, an unknown or empty `ResponseFields`, an unknown enum spelling | |
| `Panic` | a Rust panic was caught at the ABI boundary | |
| `Unknown` | the library reported a kind this client version does not recognise | |

A cancelled call throws `OperationCanceledException`, not `TrafilaturaException`, so it has no `Kind`.

### Cancellation

Every entry point takes a `CancellationToken` as its last argument, and the default is a token that cannot be cancelled.

- A token that is already cancelled throws `OperationCanceledException` before the native library is called, so no extraction runs.
- A token cancelled during the call stops the native extraction at its next check, which the core makes during the parse and every 256 nodes. The call then throws `OperationCanceledException` carrying the caller's token.
- A token cancelled after the native call returned has no effect: the call returns its result, or throws the `TrafilaturaException` the extraction produced.
- A token that cannot be cancelled, such as `default` or `CancellationToken.None`, allocates no native handle.

```csharp
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

try
{
    result = Extractor.ExtractFromBytes(page, options, cancellation.Token);
}
catch (OperationCanceledException)
{
    // The two seconds ran out. The native work stopped at its next check.
    return;
}
```

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

### Only the fields you need

`ResponseFields` shapes the response rather than the extraction: a field that is not named is null in
the response and in the result, so it is not serialized and not read back.

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
  library's `abi` and `schemaVersion` against the `2` and `2` this assembly was compiled for, and
  throws `ContractVersionMismatchException`, carrying both sides, when they differ. Later calls
  report that same outcome rather than re-reading the library; `crate` and `core` are versioned
  independently of `abi` and are not compared.
- **Nothing is fetched and nothing is written.** The library opens no socket and reads no file. It
  writes nothing to stderr, and extraction never echoes the input HTML back in an error.
- **Untrusted input can still end the process.** A Rust stack overflow aborts rather than throwing,
  so a caller cannot catch it. A payload above 64 MiB and a header above 1 MiB are rejected before
  parsing; `MaxTreeSize` bounds the extracted tree, not the input, and is not a denial-of-service
  bound.
- One-shot and synchronous: a call blocks its thread until it returns or throws. There is no async entry point; cancellation is the `CancellationToken` parameter, described under Cancellation.
- **Extraction needs a 1 MiB stack.** On Windows and Linux it runs on the caller's thread. `dotnet.exe`, an apphost and the thread pool provide that much; on Linux with glibc that holds while the `RLIMIT_STACK` soft limit is at least 1 MiB. A host other than these has to call from a thread with a 1 MiB stack. Hosting under IIS (`w3wp.exe`) is unchecked. On macOS extraction runs on a fixed 1 MiB native worker thread, whatever the caller's stack.
- **No declared dependencies.** Both assets use the `System.Text.Json` in box with their target
  framework; a consumer that wants a particular patch level references it itself.

## Development

Building, testing, benchmarking and what CI runs are in [`docs/development.md`](https://github.com/vinhch/rust-trafilatura-dotnet/blob/main/docs/development.md);
cutting a release, from the version bump to the NuGet publish, is in
[`docs/releasing.md`](https://github.com/vinhch/rust-trafilatura-dotnet/blob/main/docs/releasing.md).
What changed between releases is in [`CHANGELOG.md`](https://github.com/vinhch/rust-trafilatura-dotnet/blob/main/CHANGELOG.md).

## Credits

- [adbar/trafilatura](https://github.com/adbar/trafilatura) defines the extraction this package follows.
- [trafilatura-rust](https://crates.io/crates/trafilatura-rust) is the crate this package binds.

## License

`MIT AND Apache-2.0`: MIT for this repository's source, Apache-2.0 for the bundled native core. See [THIRD-PARTY-NOTICES.md](https://github.com/vinhch/rust-trafilatura-dotnet/blob/main/THIRD-PARTY-NOTICES.md) for the core's notice and licence text.
