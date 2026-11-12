# Changelog

All notable changes to this package are recorded here, following [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The package follows [Semantic Versioning](https://semver.org/) loosely: a release that changes behavior a caller can see says so under Breaking.

## [Unreleased]

### Changed

The native library is built with fat LTO and one codegen unit: about 6.7 percent smaller on win-x64, round-trip bench within noise (medians in the commit).

## [0.3.0] - 2026-11-12

The package binds the `trafilatura-rust` 0.2.0 core instead of `rust-trafilatura` 2.2.8. Extraction results follow the new core, so a page can extract differently from 0.2.x even where the API is unchanged.

### Breaking

Core and contract:

- The native core is `trafilatura-rust` 0.2.0 (`Apache-2.0`).
- The ABI and schema versions are 2 and 2, where 0.2.0 shipped 1 and 1. A 0.2.0 native library is rejected by the contract check with `ContractVersionMismatchException`.
- The package licence expression is `MIT AND Apache-2.0`: MIT for this repository's source, Apache-2.0 for the bundled native core. `THIRD-PARTY-NOTICES.md` carries the core's NOTICE and licence text.

Removed members that break the build:

- `Extractor.ConfigureWorkerCap`, `Extractor.WorkerCap`, `Extractor.LiveRunawayWorkers` and the type `WorkerCapOutcome`: there is no detached worker, so there is no cap.
- `Extractor.ExtractMany` and the types `ExtractionBatch` and `ExtractionBatchItem`.
- `ExtractionOptions.TimeBudgetMs`: use the `CancellationToken` parameter.
- `ExtractionOptions.InputEncoding`, `ExtractionOptions.EnableFallback`, `ExtractionOptions.Deduplicate` and `ExtractionOptions.PruneSelector`.
- `ExtractionConfig.CacheSize`, `ExtractionConfig.MaxDuplicateCount`, `ExtractionConfig.MinDuplicateCheckSize` and `ExtractionConfig.MinExtractedCommentSize`.
- `HtmlDateMode.Disabled`.
- `ExtractionResult.ContentHtml` and `ExtractionResult.CommentsHtml`.
- `Metadata.Id` and `Metadata.Fingerprint`.
- `Metadata.Date` is a `string?` (`YYYY-MM-DD`), not a `DateTimeOffset?`. Code that calls `Date?.ToString("yyyy-MM-dd")` no longer compiles; print the value as is.
- `TrafilaturaErrorKind` loses `Io`, `UnsupportedCharset`, `CharsetNotDetected`, `TimeBudgetExceeded`, `WorkerCapExceeded` and `InvalidUrl`. `RequiredTitle`, `RequiredUrl` and `RequiredDate` merge into `MissingMetadata`, whose context key `missing` names the absent piece.

Changes that compile and then behave differently:

- `ExtractionOptions.BlacklistedAuthors` drops a matching author from the metadata; it no longer rejects the document.
- `ExtractionOptions.TargetLanguage` no longer raises `WrongLanguage` on a text-language mismatch until the core reports a language; only the `<html lang>` check (`HtmlLanguage`) remains.
- `ExtractionOptions.ResponseFields` naming `contentHtml` or `commentsHtml` fails with `InvalidRequest`.
- `ExtractionOptions.Url` is no longer validated by the binding: a value that is not an absolute `http` or `https` URL no longer fails with `InvalidUrl`. With `IncludeImages`, a value the core cannot join gives `InvalidValue`.
- `TrafilaturaException.ContextJson` keys changed: `count` is `limit` (plus `children` for `maxTreeSize`), and `content` and `comments` are `textChars` and `commentsChars`. Code that reads the old keys compiles and then finds them missing.
- The `RUSTTRAFILATURA_STACK_BYTES` environment variable is ignored: the 16 MiB worker is gone.
- `HtmlDateMode.Default` runs the extensive date search.
- A tree over `maxNodes` is rejected with `OutputTreeTooLong`; the other tree limits (`tagAttributes`, `tableCells`) use the same kind.
- Empty input fails with `MissingDocument`.
- `Metadata.Language` is always empty until the core reports a language.
- A payload above the input cap fails with `InputTooLarge` (native code 1) instead of `InvalidRequest` (native code 2).
- `ExtractFromHtml` skips the binary check, decompression and charset detection, so a string that starts with `%PDF-` extracts instead of failing.
- On macOS, extraction runs on a fixed 1 MiB native worker thread; on Windows and Linux it runs on the caller's thread, which needs a 1 MiB stack.

Recompile required:

- `TrafilaturaErrorKind` numeric values changed, so a stored `(int)Kind` changes meaning.
- Each extract method gained an optional `CancellationToken` parameter. An assembly built against 0.2.0 throws `MissingMethodException`; recompile it against 0.3.0.

### Added

- A `CancellationToken` on `ExtractFromHtml`, `ExtractFromBytes` and `ExtractFromSpan`. A cancelled call throws `OperationCanceledException`, which is not a `TrafilaturaException` and has no `Kind`.
- Error kinds `MissingMetadata`, `InputTooLarge` (context key `limit`: `maxInputBytes`, `maxFileSize`, `jsonSize` or `unknown`), `InvalidValue` and `BlacklistedUrl` (declared; no option sets a URL blacklist, so it is not raised).
- macOS runs in CI, and the release builds `osx-x64` on `macos-15-intel` and `osx-arm64` on `macos-15`.

## [0.2.0]

See the [v0.2.0 release](https://github.com/vinhch/rust-trafilatura-dotnet/releases/tag/v0.2.0).

## [0.1.0]

See the [v0.1.0 release](https://github.com/vinhch/rust-trafilatura-dotnet/releases/tag/v0.1.0).

## [0.0.1]

See the [v0.0.1 release](https://github.com/vinhch/rust-trafilatura-dotnet/releases/tag/v0.0.1).
