# Binding performance: ranked recommendations

Companion to `binding-performance.md`, which holds the evaluation these recommendations come from.
The split exists because the evaluation plus a sketch per recommendation passes what one page holds;
the reasoning, measurements and citations stay in the main file, and only the actionable list is here.
Ranking rule: by measured effect, not by compatibility. This repository is experimental and a
breaking change to the public surface is acceptable, so a right-but-breaking item is recommended as
breaking rather than softened into an additive overload.

| # | Change | Impact | Effort | Risk | Touches | Kind |
| --- | --- | --- | --- | --- | --- | --- |
| R1 | Read the response with `Utf8JsonReader` over the returned bytes instead of materialising a `string` | Removes the 397,250-byte envelope string and one full-size UTF-8 decode per call; measured on a synthetic 198,625-byte response, read path C 396,613-396,627 B at 0.23-0.48 ms against path A 794,019-794,099 B at 0.72-1.37 ms | Medium | Medium: buffer lifetime moves, and every field mapping moves with it | Managed surface only; ABI unchanged | Additive to the public API (the changed methods are internal) |
| R2 | Multi-target `net8.0;net10.0`, one source-generated header on both, `[JsonStringEnumMemberName]` under `#if NET9_0_OR_GREATER` | Clears `IL2026`/`IL3050` (compiled: the harness's context case builds with no warning under `IsAotCompatible=true`); byte-identical wire output observed on both assets (transcripts); drops the enum converters on that asset | Medium | Low: the byte-identity test pins it | Managed surface, package layout, CI, docs | Additive to the API; the package gains a second asset |
| R3 | Set `IsAotCompatible=true` and add an AOT publish job | Makes the package's trimming and AOT compatibility a promise to consumers, proven by a publish-and-run job rather than asserted | Medium-high: needs the native toolchain in CI | Medium: an unexercised claim is worse than none | Managed surface, CI | Additive to the package; costs CI time |
| R4 | The analysis recommends `ExtractFromBytes(ReadOnlySpan<byte>)` plus `ExtractFromMemory(ReadOnlyMemory<byte>)` with `byte[]` dropped, and an `ExtractMany` memory overload beside its array one; the adopted surface is `ExtractFromBytes(ReadOnlyMemory<byte>)` plus `ExtractFromSpan(ReadOnlySpan<byte>)` (see "Adopted surface" and its "Later revised" note below) | Removes a 206,712-byte allocation per call for slice and pooled-buffer callers (measured 0.08-0.26 ms on a zero-filled 206,687-byte array, roughly 1% of the 23.25 ms round trip) | Low: all entry points funnel into one span pipeline | Low: the shape is chosen from the compile matrix in section 5 of the main file, reproduced by the committed harness | Managed surface only | Breaking: the `ExtractFromBytes(byte[])` symbol is gone, a null array stops throwing `ArgumentNullException`, and callers rebuild. `ExtractMany` callers change nothing |
| R5 | Return the shared empty array for a present-but-empty `categories` or `tags` array | Removes two 32-byte `List<string>` allocations per call on that path; a key that is absent or not an array already returns `Array.Empty<string>()` and allocates nothing | Low | Low | Managed surface only | Additive |
| R6 | Document that `AbiVersion` is not compared against a required ABI, or close the gap; the maintainer chose to close it in D14, which compares `abi` and `schemaVersion` before the first extraction | Closes the one drift-detection gap UniFFI covers with a runtime version check | Low | Low | Docs, tests and one public exception type | Additive |
| R7 | Say in the README that the package is not AOT-verified, until R3 lands | Stops a consumer assuming it is | Low | Low | Docs | Additive |

## R1, read the response from the native bytes

Files: `dotnet/src/RustTrafilatura/Interop.cs`, `Extractor.cs`.

Add `internal delegate TResult ResponseReader<TResult>(ReadOnlySpan<byte> response);` and change
`Extract` to `Extract<TResult>(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload, ResponseReader<TResult> reader)`.
The reader runs inside the ownership `try` and `RtFree` moves into the `finally` after it, so the span
never outlives the buffer and the pointer still never leaves `Interop`. `Utf8JsonReader` is a
`ref struct`, so the callback cannot be a `Func<...>` on the `net8.0` asset: compiled by the harness's
`CASE_FUNC_SPAN` and `CASE_CUSTOM_DELEGATE` cases (transcripts), `Func<T, TResult>` there has no
`allows ref struct` constraint and the build fails with `error CS9244: The type
'ReadOnlySpan<byte>' may not be a ref struct or a type parameter allowing ref structs in order to use it
as parameter 'T' in the generic type or method 'Func<T, TResult>'` at `net8.0` both at its default
language version and at C# 14, while `net10.0` compiles the same code and a custom delegate compiles and
runs on both. One shared implementation therefore needs the custom delegate.

`Extractor.ReadResult` becomes a `ResponseReader<ExtractionResult>` built from `Utf8JsonReader`,
keeping the existing field mapping and the `JsonValueKind` distinction between "not requested" (JSON
null) and "nothing found" (empty string). `ReadFailure` parses only the small error envelope, where
the current string-based parse is not worth changing.

A `JsonDocument` over the same memory is the alternative and is ruled out: its documented contract is
that "the `ReadOnlyMemory<T>` value will be used for the entire lifetime of the JsonDocument object,
and the caller must ensure that the data therein does not change during the object lifetime", which
puts the free after the document's disposal for no gain over the reader.

Test: `Response_Is_Read_From_The_Native_Buffer`, asserting for the existing fixtures that every
`ExtractionResult` and `Metadata` member equals the result of parsing the same response through the
current string path, and that a response carrying a multi-byte character and an escaped quote decodes
identically.

## R2, the source-generated header

Files: new `dotnet/src/RustTrafilatura/HeaderContext.cs`, `Extractor.cs`, `Models.cs`,
`RustTrafilatura.csproj`, `dotnet/Directory.Build.props`, the test project, CI.

Add a `JsonSerializerContext` covering `RequestHeader`, `ExtractionOptions` and `ExtractionConfig`
with `PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase`,
`DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull`,
`Converters = new[] { typeof(FocusConverter), typeof(HtmlDateModeConverter) }` and the default
(metadata) generation mode; under `#if NET9_0_OR_GREATER` add `UseStringEnumConverter = true` and
`[JsonStringEnumMemberName(...)]` on the two public enums instead of the converters. Serialize with
`JsonSerializer.SerializeToUtf8Bytes(header, HeaderContext.Default.RequestHeader)`: `IL2026` names that
overload ("Use the overload that takes a `JsonTypeInfo` or `JsonSerializerContext`"), while `IL3050`
asks for `System.Text.Json` source generation generally ("Use System.Text.Json source generation for
native AOT applications"). Both messages are quoted in full in section 8 of the main file; their text is
the build output of the committed `AotAnalyzerProbe`. Set
`<TargetFrameworks>net8.0;net10.0</TargetFrameworks>`. The recommendation to keep the
`System.Text.Json` reference on `net8.0` is superseded by measurement: NuGet pruning drops that dependency
from the packed nuspec once the project multi-targets, and deleting the reference leaves both assets
building and testing clean, so the shipped package declares none and a consumer that wants a particular
patch level references it itself.

Do not use `JsonSourceGenerationMode.Serialization` (the fast-path mode): its `JsonTypeInfo` carries
no property metadata, so reaching it through a resolver-based options object throws (observed: the
harness's `CASE_SERIALIZATION_MODE` run fails with `System.InvalidOperationException: TypeInfoResolver
'AotAnalyzerProbe.HeaderContext' did not provide property metadata for type
'AotAnalyzerProbe.RequestHeader'.`; transcripts).

Tests: `Header_Json_Is_Identical_Across_TargetFrameworks`, comparing the header bytes for a fixed
option set, including `ExtractionFocus.FavorPrecision` and `HtmlDateMode.Extensive`, against a literal
recorded in the test, run for both frameworks; plus the existing `Header_Json_Has_The_Wire_Shape`,
`Enum_Options_Use_The_Core_Spellings` and `DefaultOptions_Equal_CoreDefaults` for both.

## R3, AOT compatibility

Files: `dotnet/Directory.Build.props` (`<IsAotCompatible>true</IsAotCompatible>`), a scratch console
project under the test tree, and the two workflow files, `.github/workflows/bindings.yml` and
`.github/workflows/ci.yml`, plus their package README. Neither workflow exists in the repository yet:
`bindings.yml` belongs to the packaging task and `ci.yml` to the CI task, so this sketch adds jobs to
files those tasks create, and the README sentence lands after they have. If this
task runs first, it creates the workflow files with only the AOT job in them and the later tasks add
theirs.

The job publishes the scratch app with `-p:PublishAot=true -r win-x64` and `-r linux-x64`, runs it,
and asserts it prints a non-empty extracted text for an inline page; the CI workflow promotes `IL2026`
and `IL3050` to errors so the claim cannot regress silently. Test name:
`Aot_Publish_Extracts_A_Page`, whose assertion is the published app's exit code plus the non-empty
`ContentText` it prints from the package's own output. Do not set the property before this job exists:
without it the property is a claim, not evidence.

## R4, the byte surface

Files: `dotnet/src/RustTrafilatura/Extractor.cs`, the test project, the XML docs on the new members,
and a compile-only fixture for the net8.0 default language version.

Rank 1's signatures, the analysis's recommendation:

```csharp
public static ExtractionResult ExtractFromBytes(ReadOnlySpan<byte> html, ExtractionOptions? options = null);
public static ExtractionResult ExtractFromMemory(ReadOnlyMemory<byte> html, ExtractionOptions? options = null);
public static IReadOnlyList<ExtractionBatchItem> ExtractMany(
    IReadOnlyList<ReadOnlyMemory<byte>> pages, ExtractionOptions? options = null, int maxDegreeOfParallelism = 0);
public static IReadOnlyList<ExtractionBatchItem> ExtractMany(
    IReadOnlyList<byte[]> pages, ExtractionOptions? options = null, int maxDegreeOfParallelism = 0);
```

Ranked shapes. The span and memory entry points reach the same internal span and cost the same, so the
differences are caller reach, overload resolution, what the caller has to spell and surface size. The
basis of each row is named in it, because they are not all the same kind of evidence: rows 1, 2, 4, 5
and 6 rest on the compiled matrix in section 5 of the main file, row 3 on that matrix plus a
measurement, and the two inferences below — the pre-C# 14 conversion mechanism and the variance
mechanism — are labelled as such where they appear. The matrix is reproduced by
`docs/research/binding-performance-probes/CompileProbe`, whose output is in
`binding-performance-probes/transcripts.md`.

| Rank | Shape | Why | Basis |
| --- | --- | --- | --- |
| 1 | `ExtractFromBytes(ReadOnlySpan<byte>)` plus `ExtractFromMemory(ReadOnlyMemory<byte>)` | Every probed call site compiles in every probed configuration, including a consumer at the net8.0 default language version; no caller loses reach, because every `Memory<byte>` has a `.Span` and every `byte[]` converts to one | compiled (the `CASE_SHAPE_*` cases, transcripts); the reach claim is that matrix, and its measured part is 0 B/call for the `byte[]` to `ReadOnlyMemory<byte>` conversion and for `array.AsMemory` |
| 2 | Span only | The same reach as rank 1, with one method fewer: a caller holding a `ReadOnlyMemory<byte>` across an `await` passes `memory.Span` at the call site, which the harness compiled at the net8.0 default, at C# 14 and on net10.0 (`CASE_ASYNC_MEMORY_ACROSS`). The only cost is that the caller spells `.Span` | compiled |
| 3 | Memory only | The same reach as rank 1 except for one caller: a span whose bytes do not live in a managed array, such as a `stackalloc` buffer or one wrapping unmanaged memory, cannot become a `ReadOnlyMemory<byte>` without a copy or a custom `MemoryManager<byte>` (compiled: `CS8346`). A `byte[]` caller is unaffected, because the implicit `byte[]` to `ReadOnlyMemory<byte>` conversion and `array.AsMemory(start, length)` both allocate nothing | compiled (`CS8346`) and measured (0 B/call) |
| 4 | `byte[]` plus `ReadOnlySpan<byte>` | Compiles everywhere, but the array overload is redundant: every `byte[]` caller is already served by the span overload, while a caller holding a slice, a pooled rent or a memory view still needs the span one | compiled |
| 5 | `byte[]` plus span plus `ReadOnlyMemory<byte>` | Rank 4's redundancy plus a third member, so three public methods where rank 1 needs two | compiled |
| 6 | Span plus `ReadOnlyMemory<byte>` under one name | Rejected: it fails to compile for every existing `byte[]` call site below C# 14 and on the `null` literal at every language version | compiled (`CS0121`) |

Ranks 1 and 3 are the real choice, and they differ in two ways only: memory only cannot accept a span
that is not backed by memory, which is the reach difference above, and memory only has the smaller
surface, with no `.Span` for the caller to spell and no second name to document. Rank 1 is the
analysis's recommendation because the non-memory-backed span is exactly the caller that would otherwise
copy, which is the cost this change exists to remove.

Adopted surface. The maintainer took rank 3's shape instead: one single-page entry point, named
`ExtractFromBytes` for the callers who have it today and taking `ReadOnlyMemory<byte>`, plus a
non-owning `UnmanagedMemory.Wrap(byte* pointer, int length)` returning `Memory<byte>` for a caller whose
bytes sit behind a pointer. Both `ExtractMany` overloads stay, and no span entry point is added,
because a `Memory<byte>` can outlive the frame a span cannot. Rank 1's extra reach is the property this
trades away. The signatures and tests below are rank 1's, and the adopted surface's own signatures,
XML docs and tests belong to the byte-surface task.

Later revised. The shipped surface is rank 1: `ExtractFromBytes(ReadOnlyMemory<byte>)` plus
`ExtractFromSpan(ReadOnlySpan<byte>)` into the same private `Extract(ReadOnlySpan<byte>,
ExtractionOptions?)`, with `UnmanagedMemory` removed and its reach covered by the span entry point.

Both single-page methods funnel into one private `Extract(ReadOnlySpan<byte>, ExtractionOptions?)`, so
`html.Span` is the only difference and there is one pipeline. The memory method carries its own name
rather than a second `ExtractFromBytes` overload: with both names on `ExtractFromBytes`, a `byte[]`
argument, a null `byte[]` and the `null` literal are all `CS0121` for a consumer compiling at the net8.0
default language version (C# 12), and the `null` literal stays `CS0121` at every language version.
Reasoning for the mechanism, the cells themselves being compiled: before C# 14 the array-to-span and
array-to-memory conversions are both user-defined, so neither overload is better.

`ExtractMany` is different and keeps both overloads. The harness found the pair unambiguous at both
frameworks — a `List<byte[]>`, a `List<ReadOnlyMemory<byte>>` and a `byte[][]` each bind to the matching
one — and it also built the conversion that variance would have to supply, which the compiler rejects
(`error CS0266: Cannot implicitly convert type 'System.Collections.Generic.List<byte[]>' to
'System.Collections.Generic.IReadOnlyList<System.ReadOnlyMemory<byte>>'`). Reasoning for why, that
diagnostic being the evidence: `IReadOnlyList<T>`'s covariance carries only reference and identity
conversions, so a user-defined element conversion cannot make the memory overload a candidate for a
`List<byte[]>`. A memory-only batch instead rejects a `List<byte[]>` and a `byte[][]` with `CS1503`, so
the retained overload is what a caller with a `List<byte[]>` already has.

`Interop.Extract` keeps its span parameters, and its `fixed` stays a choice rather than a requirement:
the generator marshals a span parameter and pins it itself, and rejects only a memory parameter,
`ReadOnlyMemory<byte>`, with `SYSLIB1051` (compiled; the marshalling table and the generated code are in
section 5 of the main file and the transcripts).

What a caller must change, exactly: the `ExtractFromBytes(byte[])` symbol is gone, so the call site
keeps compiling (a `byte[]` argument binds through the implicit conversion) but the caller rebuilds
against the new assembly; and a null `byte[]` becomes empty bytes (observed: the harness's runtime check
prints `null byte[] to ReadOnlyMemory<byte>: Length = 0, IsEmpty = True`) instead of throwing
`ArgumentNullException`, so that argument check goes with the array overload. No `ExtractMany` caller
changes anything.

Tests: `Bytes_Entry_Points_Agree`, asserting field-for-field equality of `ExtractionResult` and every
`Metadata` member across the span method, the memory method, the `string` overload and the `byte[]`
call site for the existing fixtures, plus `OutputTooShort` rather than `ArgumentNullException` for a
null array; `Bytes_Call_Sites_Compile_At_The_Net8_Default`, a compile-only fixture built at `net8.0`
with the default language version that calls the span method with a `byte[]`, a null `byte[]` and
`null`; `ExtractMany_Array_And_Memory_Overloads_Are_Both_Reachable`, a compile-only fixture that passes
a `List<byte[]>`, a `byte[][]` and a `List<ReadOnlyMemory<byte>>` and asserts the resolved overload by
result; and `ExtractMany_MemoryOverload_Matches_Sequential`, asserting one item per input in input
order with the same per-page error kinds as the sequential call.

## R5, empty metadata arrays

File: `Extractor.cs`, in `ReadStringArray`. The absent or non-array path already returns `[]`, which the
compiler lowers to the shared `Array.Empty<string>()` at the `IReadOnlyList<string>` target the method
returns (observed: the harness's runtime check prints `collection expression [] with an
IReadOnlyList<string> target is Array.Empty<string>(): True`), so nothing changes there. The path that
allocates is the key present as an empty JSON array, which builds a `List<string>` and returns it
(measured: `new List<string>()` is 32 bytes, not the shared array).
Return the shared empty array when no element was added, keeping the `IReadOnlyList<string>` member
type. Test: `Empty_Categories_And_Tags_Are_The_Shared_Empty_Array`, asserting
`Assert.Same(Array.Empty<string>(), result.Metadata!.Categories)` and the same for `Tags` for a page
whose JSON carries `"categories":[]`, plus the existing absent-key assertions unchanged.

## R6, ABI drift

Superseded by D14. The maintainer chose to compare rather than only to document: the client now reads
`abi` and `schemaVersion` once per process before the first extraction, caches the verdict, and throws
`ContractVersionMismatchException` when either number differs from the one this assembly was compiled for.
`AbiVersion` still returns the library's own document without comparing it, which is how a caller sees what
actually loaded after a refusal. The comparison does not cover `crate`, which is versioned independently of
the ABI, and it cannot catch a library that reports these numbers while implementing something else,
because the document is still the library's own claim.

The remainder of this item is the reasoning as it stood when the choice was still open:

Files: `Extractor.cs`, README, test project. Keep `AbiVersion` returning the library's document, and
add to its remarks that the client does not compare it and what a comparison would do now that the ABI
is versioned independently of the crate. Test:
`AbiVersion_Reports_The_Shipped_Abi`, asserting the parsed `abi` is `1` and `schemaVersion` is `1`.

## R7, README wording

File: the package README, which the packaging task creates; if this lands first, the sentence goes
into whichever README the package task produces, and the packaging task owns the file afterwards. One
sentence naming the supported frameworks and, until R3's job exists, stating that Native AOT and
trimming are not verified. No test.

## Not to be done

Each negation is tied to a decision or a measurement, not to taste.

- `[SuppressGCTransition]` on `rt_extract` or `rt_free`. The attribute's documentation requires that
  the native function "always executes for a trivial amount of time (less than 1 microsecond)", "does
  not perform a blocking syscall", "does not call back into the runtime", "does not throw exceptions"
  and "does not manipulate locks or other concurrency primitives", and lists GC starvation,
  immediate runtime termination and data corruption as the consequences of invalid use.
  `rt_extract` measures 23.25 ms per call on the bench fixture of 206,687 bytes on an Intel Core
  i7-9850H, Windows 11 Pro build 26300, four orders of magnitude above the clause, and it spawns and
  joins a worker thread inside the call, which is the concurrency primitive the last clause names.
  That is enough on its own; reasoning, not measurement, for the second export: `rt_free` deallocates a
  buffer the size of a response, so it offers no better guarantee of the first clause than `rt_extract`
  does, and the attribute may be applied to the pair or not at all in this ABI's client.
- A UniFFI-style object handle or session API (design decision D5). It would introduce the
  `Arc`/`Dispose` leak class into a design that has no handles to leak, and the extraction is
  stateless.
- Base64 anywhere in the request or response (D10). It costs a 1.33x inflation, a UTF-16 string and a
  decode pass, and the payload split exists to avoid exactly that.
- Caching the request header for speed: worth about 5 us and 728 B (today's reflection path) of a
  23.25 ms call on the benchmark host, about 0.02%, against an invalidation risk. Reuse it only if the
  caller owns a constant option set.
- An `async`/`Task` surface (D5). Documented [TASKRUN]: `Task.Run` "queues the specified work to run on
  the thread pool". Reasoning from that: the work is CPU-bound, so moving it to a pool thread changes no
  throughput; `ExtractMany` already bounds parallelism across pages; and a `Task`-returning method would
  imply a cancellation the ABI cannot honour, because `rt_extract` has no cancellation path.
- Streaming the response to the caller instead of returning it whole. The ABI hands out one buffer
  freed as a unit, and the target consumer is a server-side workload that wants the typed result, not
  the JSON.
- A hand-rolled JSON scanner in place of `Utf8JsonReader`. Reasoning: the reader is a `ref struct` over
  the span whose read path materialises nothing but the strings it returns (the measured allocation
  inventory is section 6 of the main file), so a hand-rolled scanner can only match it while adding
  escaping and surrogate bugs.

## Sources

- The C ABI and its return codes: `rust/src/contract.rs`, `rust/src/engine.rs` and
  `dotnet/src/RustTrafilatura/Interop.cs`.
- `rust/benches/roundtrip.rs`, `scripts/bench.ps1`, and the run recorded in the main document.
- The probes under `docs/research/binding-performance-probes/`; their output is
  `binding-performance-probes/transcripts.md`.
- `Task.Run` remarks and summary:
  https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.run
- `SuppressGCTransitionAttribute` remarks:
  https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.suppressgctransitionattribute
- `JsonDocument.Parse(ReadOnlyMemory<byte>, JsonDocumentOptions)` remarks:
  https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsondocument.parse
- Source generation in `System.Text.Json`:
  https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation
- Native AOT overview: https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/
- Published-trimmed projects fail reflection-based serialization:
  https://learn.microsoft.com/en-us/dotnet/core/compatibility/serialization/8.0/publishtrimmed
- `mozilla/uniffi-rs` user guide, 0.32 series, and `mozilla/uniffi-rs` `CHANGELOG.md`.
