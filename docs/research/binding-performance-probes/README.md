# Binding-performance probes

The committed harness behind the `compiled` rows of `../binding-performance.md` and
`../binding-performance-recommendations.md`. Each project is a scratch-grade probe kept in the
repository so the record is reproducible: a statement labelled `compiled` in those documents names a
case here, and `transcripts.md` in this directory carries that case's verbatim build output.

Nothing here is product code. No project is a member of `dotnet/RustTrafilatura.sln`, no workflow or
script under `scripts/` refers to them, and none of them is referenced by the package. A later CI task
may add them to a job; until then they are run by hand from this directory. No `Directory.Build.props`
sits above this directory, so each project builds under the SDK's own defaults.

Environment these transcripts were taken on: .NET SDK 10.0.401, Windows 11, `net8.0` and `net10.0`
targeting packs, no NuGet packages. `net8.0` builds use the SDK's default language version (C# 12)
unless `-p:LangVersion=latest` is passed; `net10.0` builds use C# 14 by default.

## MarshallingProbe

Which parameter types `[LibraryImport]` accepts, and the marshalling code the generator emits for
each. One `[LibraryImport]` declaration per case, selected with `-p:DefineConstants`; the default,
no-define build compiles no declaration and stays green. `EmitCompilerGeneratedFiles` is on and writes
`obj/generated/Microsoft.Interop.LibraryImportGenerator/.../LibraryImports.g.cs`, which is the evidence
for the array and span rows.

| Case | Command | Expected |
| --- | --- | --- |
| default | `dotnet build MarshallingProbe/MarshallingProbe.csproj` | builds |
| `CASE_POINTER` | `... -p:DefineConstants=CASE_POINTER` | builds; the import passes `byte*` through unchanged |
| `CASE_ARRAY` | `... -p:DefineConstants=CASE_ARRAY` | builds; `fixed` over `ArrayMarshaller<byte, byte>` |
| `CASE_SPAN` | `... -p:DefineConstants=CASE_SPAN` | builds; `fixed` over `ReadOnlySpanMarshaller<byte, byte>` |
| `CASE_MUTABLE_SPAN` | `... -p:DefineConstants=CASE_MUTABLE_SPAN` | builds; `fixed` over `SpanMarshaller<byte, byte>` |
| `CASE_MEMORY` | `... -p:DefineConstants=CASE_MEMORY` | fails, one `SYSLIB1051` per parameter |
| span without unsafe | `... -p:DefineConstants=CASE_SPAN -p:AllowUnsafeBlocks=false` | fails with `SYSLIB1062` and `CS0227` |

Running the bare build also executes the runtime checks the documents label `observed`:
`dotnet run --project MarshallingProbe/MarshallingProbe.csproj` prints the length a null `byte[]`
becomes as a span and as a memory, whether the empty collection expression is the shared
`Array.Empty<string>()` at the `IReadOnlyList<string>` target the package returns it as, and
the allocated bytes for `new byte[1024]`, `new List<string>()`, the implicit `byte[]` to
`ReadOnlyMemory<byte>` conversion, `array.AsMemory`, and `Span<byte>.ToArray()`.

## AotAnalyzerProbe

`net8.0` with `IsAotCompatible=true`, which turns on the trim, single-file and AOT analyzers. One case
per serialization shape, selected the same way.

| Case | Command | Expected |
| --- | --- | --- |
| default | `dotnet build AotAnalyzerProbe/AotAnalyzerProbe.csproj` | builds with `IL2026` once and `IL3050` twice, all from the reflection path |
| `CASE_CONTEXT` | `... -p:DefineConstants=CASE_CONTEXT` | builds with no warning: source-generated context plus per-enum converters |
| `CASE_LIBRARYIMPORT` | `... -p:DefineConstants=CASE_LIBRARYIMPORT` | builds with no warning: the pointer-parameter `[LibraryImport]` declaration and no serializer |
| `CASE_SERIALIZATION_MODE` | `... -p:DefineConstants=CASE_SERIALIZATION_MODE` | builds; running it throws `InvalidOperationException: TypeInfoResolver ... did not provide property metadata` |

`AllowUnsafeBlocks` is on because `CASE_LIBRARYIMPORT` declares a pointer signature; the other cases do
not depend on it.

`dotnet run` on the default case and on `CASE_CONTEXT` prints the same header JSON, which is the
byte-identity observation the main document cites. The Native AOT publish is also taken from this
project: `dotnet publish AotAnalyzerProbe/AotAnalyzerProbe.csproj -c Release -r win-x64
-p:PublishAot=true`, which needs the platform toolchain (MSVC on Windows) and produces an app that
throws at the header call.

## CompileProbe

`net8.0;net10.0`. The compile-checkable rows: language versions, the six overload shapes and their
typed and `null` call sites, the batch pair, the async and `ref struct` constructs, `Func<,>` and a custom
delegate with a span parameter, the variance conversion, `stackalloc` to memory, and the enum wire
spelling on both frameworks. Cases that compile are also run, and print the overload they selected or
the bytes they wrote.

Three build configurations, named in the transcripts: `net8-default` is
`dotnet build CompileProbe/CompileProbe.csproj -f net8.0`, `net8-latest` adds
`-p:LangVersion=latest`, and `net10-default` is `-f net10.0`.

| Case | Configurations | Expected |
| --- | --- | --- |
| `CASE_LANGVER` | all three | `CS8304` reporting the language version |
| `CASE_SHAPE_SPAN_ONLY` | all three | builds; every call site binds to the span method |
| `CASE_SHAPE_SPAN_NAMED_MEMORY` | all three | builds; every call site binds to the span method |
| `CASE_SHAPE_ARRAY_SPAN` | all three | builds; every call site binds to the array method |
| `CASE_SHAPE_SPAN_MEMORY` | all three | `CS0121` on all three call sites at `net8-default`, on the `null` literal only at the other two |
| `CASE_SHAPE_SPAN_MEMORY_NO_NULL` | all three | `CS0121` on both typed call sites at `net8-default`, builds and binds to the span method at the other two |
| `CASE_SHAPE_ARRAY_SPAN_MEMORY` | all three | builds; every call site binds to the array method |
| `CASE_SHAPE_MEMORY_ONLY` | all three | builds; every call site binds to the memory method |
| `CASE_BATCH_PAIR` | `net8-default`, `net10-default` | builds; `List<byte[]>` and `byte[][]` bind to the array overload, `List<ReadOnlyMemory<byte>>` to the memory one |
| `CASE_BATCH_MEMORY_ONLY` | `net8-default`, `net10-default` | `CS1503` for `List<byte[]>` and for `byte[][]` |
| `CASE_ASYNC_SPAN_BEFORE` | all three | `CS9202` at `net8-default`, builds otherwise |
| `CASE_ASYNC_SPAN_AFTER` | all three | `CS9202` at `net8-default`, builds otherwise |
| `CASE_ASYNC_SPAN_ACROSS` | all three | `CS9202` at `net8-default`, `CS4007` at the other two |
| `CASE_ASYNC_STACKALLOC` | all three | `CS9202` at `net8-default`, builds otherwise |
| `CASE_ASYNC_MEMORY_ACROSS` | all three | builds |
| `CASE_ASYNC_ARRAY_AFTER` | all three | builds |
| `CASE_FUNC_SPAN` | all three | `CS9244` on `net8.0` at both language versions, builds on `net10.0` |
| `CASE_CUSTOM_DELEGATE` | `net8-default`, `net10-default` | builds; a delegate may take a span parameter |
| `CASE_VARIANCE` | `net8-default` | `CS0266` assigning `List<byte[]>` to `IReadOnlyList<ReadOnlyMemory<byte>>` |
| `CASE_STACKALLOC_MEMORY` | `net8-default` | `CS0029` for a span local, `CS8346` with `-p:DefineConstants=CASE_STACKALLOC_MEMORY%3BSTACKALLOC_DIRECT` for the direct conversion |
| `CASE_GENERIC_CONVERTER` | `net8-default`, `net10-default` | builds; prints `{"focus":"favorPrecision"}` |
| `CASE_HEADER` | `net8-default`, `net10-default` | builds; both print `{"schemaVersion":1,"options":{"focus":"favorPrecision","htmlDateMode":"extensive"}}` |

`net8-default` for `CASE_HEADER` exercises reflection-based serialization with two hand-written
`JsonConverter<T>` types; `net10-default` exercises `[JsonStringEnumMemberName]` with
`UseStringEnumConverter = true` and no converter code. Both assets use the in-box `System.Text.Json`:
no project here has a package reference.

## Build output

`bin/` and `obj/` are ignored by the repository's `.gitignore`. Only the `.csproj` and `.cs` files are
tracked.
