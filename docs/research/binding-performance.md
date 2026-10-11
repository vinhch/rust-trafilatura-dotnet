# Binding performance: UniFFI techniques and the managed byte surface

This evaluates the binding layer of this repository against what UniFFI does per call, against
UniFFI's documented practice, and against the maintainer's hypothesis that `ReadOnlyMemory<byte>` or
`ReadOnlySpan<byte>` beats `byte[]` on the public surface. It changes no product code, manifest or
workflow; every recommendation becomes its own task once approved. This file is the evaluation, and it
carries the evidence the recommendations rest on, including the compile-probe matrices and the trimming
proposal; the ranked list with a sketch per item lives in `binding-performance-recommendations.md`,
because that list does not fit the same page.

Statuses are per statement. `compiled` means `binding-performance-probes/transcripts.md` holds the
declaration and the verbatim build output for that statement; `observed` means a first-hand run, given
with its command and its verbatim output, number or exception (`measured` in the tables is that form);
`documented` means the rule is quoted with its URL; `reasoning` means an inference that names its basis.

## 1. The measured bound

The native wrapper is gated at 1.5x a direct core call, and the envelope this document measures records
`roundtrip/core` at 0.95-1.10. Re-run for this evaluation with `pwsh -File scripts/bench.ps1` on an
Intel Core i7-9850H, Windows 11 Pro build 26300, with rustc 1.98.1 and the `bench` profile
(`x86_64-pc-windows-msvc`):

| Path | Mean ms/call | Ratio | Note |
| --- | --- | --- | --- |
| `core` | 23.532 | 1.000 | `rust_trafilatura::extract`, what `rt_extract` calls internally |
| `roundtrip` | 23.250 | 0.988 | `rt_extract` + `rt_free`, default `responseFields` |
| `roundtripHtml` | 24.281 | 1.032 | same plus `contentHtml` (informational) |

Fixture 206,687 bytes; response 198,625 bytes by default and 403,131 bytes with `contentHtml`
[bench]. The host is the one the bench above records, so the shares below are
comparable with the table above. Every managed figure in this document was measured on that same
host, in the release configuration of the harness named beside it. Two consequences bound everything
below.

- The native wrapper's whole added cost, including the header parse, the worker thread, the response
  serialization and both crossings, is inside this machine's run-to-run spread. There is no room for
  a binding-layer change to be measured through that gate.
- That ratio is measured in Rust only: `rust/benches/roundtrip.rs` calls `rt_extract` directly and
  never enters the managed side. The C# layer's cost is therefore unmeasured anywhere in this
  repository, and the figures in sections 5 and 6 come from a separate harness whose numbers are
  comparable to the table above only as a share of a 23.25 ms call.

Which cost belongs to whom: extraction, decoding, DOM construction and HTML serialization are the
core's and are identical under any binding technique; the crossings, the buffer copies that are not
the core's own, the string decode, the JSON parse and the object graph are the binding layer's.

## 2. What UniFFI does per call

Version context: the guide's `latest` at the time of writing is the 0.32 series; the newest release is
0.32.2 (2026-09-23) and the newest tagged changelog entry is 0.32.1 (2026-09-08) [CHANGELOG]
[crates]. Each fact below names the release it belongs to.

| Step | What happens | Cost | Source |
| --- | --- | --- | --- |
| Lower each argument | Integers are cast; `string`, `Option`, `sequence`, `record`, `map`, `enum` and `bytes` (`Vec<u8>`/`sequence<u8>`) are serialized into a `RustBuffer` in UniFFI's own fixed-width big-endian format | One foreign-side allocation and one copy per non-primitive argument | [UG-lift] |
| Allocate that buffer | The foreign side calls `uniffi_rustbuffer_alloc`/`uniffi_rustbuffer_from_bytes` through the FFI, so one crossing precedes the real call | One extra FFI crossing per owned-buffer argument | [RUSTBUF] [CS-RUSTBUF] |
| Call the scaffolding function | `extern "C"`, arguments lowered, plus a `RustCallStatus` out-pointer | One crossing | [UG-rustcalls] |
| Lift in Rust | `RustBuffer::destroy_into_vec` takes ownership of the `Vec`; no second copy | Free | [RUSTBUF] |
| Run the function | `catch_unwind` wraps every export | Small | [UG-rustcalls] |
| Lower the return value | Non-primitive returns are serialized into a fresh `RustBuffer` | One Rust allocation and one copy | [UG-lift] |
| Lift in the foreign binding | Read out of the buffer field by field, then `uniffi_rustbuffer_free` | One crossing plus the per-field copies | [UG-lift] [CS-STRING] [RUSTBUF] |
| Object handle, if used | Lowered as a `u64`; each method call clones the handle (`Arc::increment_strong_count`) and the callee consumes it (`Arc::from_raw`) | Two refcount operations and one crossing per call, plus the foreign wrapper object | [UG-objects] |
| Callback interface, if used | Rust calls a vtable entry; the foreign side resolves the handle through its handle map; the callee writes its return through an out-pointer | One reverse crossing plus a handle-map lookup | [UG-foreign] (vtable, out-pointer) [UG-objects] (handle map) |
| Async, if used | A future handle is returned; the foreign runtime polls, completes and frees it | Three extra scaffolding calls per async call plus the foreign runtime's scheduling | [UG-async] |

The zero-copy escape hatch is narrow and new: `&[u8]` / `[ByRef] bytes` borrows the foreign buffer for
the duration of the call with no copy, but only foreign to Rust, only in argument position, and never
nested in a record, sequence, map, `Option` or `Result`. It arrived in 0.32.0 (2026-06-30), and that
release states it is "not yet supported in async functions on any language" [UG-bytes] [CHANGELOG].
Every other structured type still crosses as a serialized `RustBuffer`.

The C# generator is third-party and targets an older UniFFI than the zero-copy work. Its newest
release is `v0.11.1+v0.31.0` (2026-10-09) and its README states it "is young, and it's unclear how
stable the generated bindings are going to be between versions", which is why the major version stays
0 [CS-README]. For a `bytes` argument the generated code allocates a Rust buffer through the FFI and
writes the payload into it, because its `ForeignBytes` struct is present but unused: the template says
"It's not actually used at the moment ... if we're going to copy something then we might as well copy
it into a `RustBuffer`" [CS-RUSTBUF]. A `string` argument is lowered the same way, `RustBuffer.Alloc`
then a UTF-8 encode into it [CS-STRING]. So on today's C# generator a bytes-in, text-out call costs at
least three crossings (alloc, call, free) and one full copy of the payload into Rust-owned memory,
against two crossings and no copy here.

UniFFI also carries things this design does not need: a runtime contract-version check and per-API
checksums at library initialization, both visible in the C# generator's library template
[CS-NAMESPACE], and a leak class that exists only because handles exist at all (section 4).

## 3. UniFFI's documented best practices

| Practice | What the source says | Version | Source |
| --- | --- | --- | --- |
| Cross the boundary as little as possible | The guide's own framing is lowering and lifting per call; an async call needs a poll, a complete and a free call on top of the one that starts it, and a callback costs one reverse crossing per method. The design principles put performance second by policy: "Given the choice we will pick simple but slow over fast but complicated" | 0.32 | [UG-lift] [UG-async] [UG-principles] |
| Do not expect the structured-data path to be fast | Design principles list the `RustBuffer` serialization as a standing "Violation": "we have not done any work to measure the performance impact or iterate towards something with lower overhead (such as using `repr(C)` structs)" | 0.32 | [UG-principles] |
| Byte payloads: borrow if you can, and only in argument position | `&[u8]` borrows with no copy; `Vec<u8>`/`bytes` still copies into a `RustBuffer`. Constraints: direction, argument position, call lifetime | 0.32, added 0.32.0 | [UG-bytes] [CHANGELOG] |
| Records copy every time, including on a method call | A record is serialized by value across the FFI on every call: "every time one of the methods is called, the entire record will be copied by-value across the FFI" | 0.32 | [UG-records] |
| `Option`, `Vec`, `HashMap`, `enum` | All lower to a serialized `RustBuffer`; `Option` is a boolean tag plus the value, `HashMap` a count plus key/value pairs, and an `enum` a variant index plus that variant's fields | 0.32 | [UG-lift] |
| Errors | A status code plus a serialized error `RustBuffer`, which the foreign bindings "are responsible for deserializing ... into an exception value and throwing" | 0.32 | [UG-rustcalls] |
| Object lifetime | Instances are `Arc`-backed; the foreign side holds a leaked reference and must free it. Kotlin exposes an explicit `close()` and "these wrappers all implement a `close()` method, which must be explicitly called to ensure the associated Rust resources are reclaimed"; records holding objects need the same care | 0.32 | [UG-objects] [UG-kotlin] [UG-records] |
| What a foreign caller can leak | Forgetting the free keeps the `Arc` alive; callback interface handles live in a foreign handle map that retains an entry until it is removed; a cycle between a Rust object and a foreign implementation leaks by construction, and "UniFFI doesn't try to help here and there's no universal advice" | 0.32 | [UG-objects] (free, handle map) [UG-ftraits] (cycles) |
| Callback interfaces | Re-entrancy and retention are the caller's problem: calls go through a vtable, the handle map entry behind each live callback is freed only when Rust drops it, and an unexpected foreign error makes the Rust side panic unless `From<UnexpectedUniFFICallbackError>` is implemented | 0.32 | [UG-foreign] (vtable, panic) [UG-objects] (handle map) [UG-ftraits] (`From<...>`) |
| Async | The foreign runtime drives the future; `uniffi_set_event_loop` exists for Python because a thread without a loop cannot run an async callback; cancellation is not supported and must be built by the library | 0.32 | [UG-async] [UG-futures] |
| Leak-shaped defects do occur | A `RustBuffer` leak fixed in 0.27.1; a callback-interface leak fixed in 0.27.0; `rust_future_free` leaking one `RustFuture` per async call in 0.31.0, fixed; an open report from 2026-09-24 that freeing a `WrappedFuture` without completing it leaks the lowered result; an open request from 2020 to guard lowering against leaks | 0.27.0-0.32 | [CHANGELOG] [#2056] [#2858] [#3009] [#195] |

## 4. This design, point by point

| Behaviour | UniFFI, C# generator today | This repository | Verdict |
| --- | --- | --- | --- |
| Crossings per call | Three or more (buffer alloc, call, free) | Two (`rt_extract`, `rt_free`) | Already better |
| Payload transfer | Copied into a Rust-owned `RustBuffer`; zero-copy `&[u8]` needs 0.32 and a generator built against it | The caller's buffer is pinned and passed as `(ptr, len)`; the core then copies once through `read_to_end`, which is the core's own price for reusing its public entry point [S9] | Already better; the remaining copy is the core's |
| Structured response | Custom fixed-width big-endian format, lifted field by field in C# | One UTF-8 JSON buffer, read once | Comparable; neither is free |
| Response as text | Strings lifted with `Encoding.UTF8.GetString(ptr, len)` per field, with no envelope string [CS-STRING] | The whole response is materialised as one `string`, then parsed | This design pays one extra full-size allocation (section 6) |
| Buffer ownership | A `RustBuffer` free call, plus `Dispose` on object wrappers | `(ptr, len, cap)` handed back to `rt_free` in a `finally`; no public pointer | Equivalent, and the `cap` component is the extra guarantee |
| Base64 | Not used | Not used (D10) | Equivalent; both avoid the 1.33x inflation |
| Handles, callbacks, async, cancellation | Available and costly | Absent (D5) | This design pays nothing because it has nothing to pay for |
| Runtime contract check | A version check plus per-API checksums at library initialization [CS-NAMESPACE] | `rt_version` reports `abi` and `schemaVersion`, and the client exposes that document but never compares it | This design is weaker on drift detection, deliberately |
| Leak surface | `Arc` handles, callback handle maps, future handles | None: no handle outlives a call | Already better |

Marginal difference, on the measured bound. The native side is already inside the 0.95-1.10 spread,
so a different binding technique can win at most the managed-side share. Measured in section 6, that
share is the response read at roughly 1-6% of a 23.25 ms call, dominated by one full-size managed
string allocation. Eliminating that is worth more than eliminating a crossing would be, which is why
the recommendation list puts it first.

## 5. The managed byte surface

### What `byte[]` costs a caller

Fixture for this figure: a zero-filled 206,687-byte array, the bench fixture's size, converted with
`Span<byte>.ToArray()`. Measured on net8.0 in Release with SDK 10.0.401 on the section 1 host (harness
`Eval`, 20,000 iterations): 206,712 bytes allocated per call, and 0.08-0.26 ms per call across
repeated runs, dominated by the allocation and the resulting gen2 collections rather than by the copy.
Against the measured 23.25 ms round trip that is roughly 1%. A caller that already holds a
slice, a `Memory<byte>` view or a rented pooled buffer pays that once per page to use the current
surface, and a pooled-buffer caller cannot avoid the copy at all.

The view is free; the buffer is not. `ReadOnlyMemory<byte>` is a three-field struct — `_object`,
`_index` and `_length` [ROMMEM] — over a buffer somebody else allocated, so "zero allocation" is true
only of the view. A caller holding a `byte[]`
from `HttpClient.GetByteArrayAsync` gains nothing from a memory entry point. The callers that gain are
the ones holding a slice, a pooled rent, or a `PipeReader`/`ReadOnlySequence<byte>` segment.

### How each shape marshals

Compiled by building one `[LibraryImport]` declaration per parameter type on net8.0 with SDK 10.0.401 on
the section 1 host and reading the generated source
(`docs/research/binding-performance-probes/MarshallingProbe`; transcripts in
`binding-performance-probes/transcripts.md`):

| Parameter type | Result |
| --- | --- |
| `byte*` plus a length | Supported; passed through unchanged, which is the current declaration |
| `byte[]` | Supported; the generator emits `fixed` over `ArrayMarshaller<byte, byte>` and passes a pointer, so pinning with no copy |
| `ReadOnlySpan<byte>` | Supported; the generator emits the `fixed` over `ReadOnlySpanMarshaller<byte, byte>` and the `(byte*, byte*)` call, so pinning with no copy |
| `Span<byte>` | Supported the same way, through `SpanMarshaller<byte, byte>` |
| `ReadOnlyMemory<byte>` | `error SYSLIB1051: The type 'System.ReadOnlyMemory<byte>' is not supported by source-generated P/Invokes. The generated source will not handle marshalling of parameter 'header'.` |

That span case's generated method, verbatim from `binding-performance-probes/transcripts.md`:

```csharp
internal static partial int SpanArgs(global::System.ReadOnlySpan<byte> header, global::System.ReadOnlySpan<byte> payload)
{
    int __retVal;
    // Pin - Pin data in preparation for calling the P/Invoke.
    fixed (void* __payload_native = &global::System.Runtime.InteropServices.Marshalling.ReadOnlySpanMarshaller<byte, byte>.ManagedToUnmanagedIn.GetPinnableReference(payload))
    fixed (void* __header_native = &global::System.Runtime.InteropServices.Marshalling.ReadOnlySpanMarshaller<byte, byte>.ManagedToUnmanagedIn.GetPinnableReference(header))
    {
        __retVal = __PInvoke((byte*)__header_native, (byte*)__payload_native);
    }

    return __retVal;
    // Local P/Invoke
    [global::System.Runtime.InteropServices.DllImportAttribute("rt", EntryPoint = "rt_extract", ExactSpelling = true)]
    static extern unsafe int __PInvoke(byte* __header_native, byte* __payload_native);
}
```

So the generator marshals array and span parameters and pins them itself, and only memory types are
rejected. The explicit `fixed (byte* headerPointer = header)` in
`dotnet/src/RustTrafilatura/Interop.cs` is a choice, not a requirement, and the pointer-parameter
declaration stays valid. `AllowUnsafeBlocks=true` is still required, because the generated marshalling
code is `unsafe`: with the property off the span case reports `SYSLIB1062` ("LibraryImportAttribute
requires unsafe code. Project must be updated with '<AllowUnsafeBlocks>true</AllowUnsafeBlocks>'.") and
`CS0227`, never `SYSLIB1051`. For the byte-surface task that means `RtExtract`'s two input parameters
may become `ReadOnlySpan<byte>` to drop the manual pinning, at no functional cost, while
`Interop.Extract` keeps an `unsafe` context for its `nint*` out-parameters unless those become
`out nint`; both forms are zero-copy for the input. The internal
`Interop.Extract(ReadOnlySpan<byte>, ReadOnlySpan<byte>)` shape is therefore correct and stays; the
public span and memory entry points differ only in how they reach that span.

### Proposed signatures

The table below is compiled by the overload cases of
`docs/research/binding-performance-probes/CompileProbe` (transcripts), one shape per build, over the
call sites a `byte[]` value, a `byte[]?` holding null and the `null` literal, built at three
configurations on the section 1 host with SDK 10.0.401. The configurations and their language versions
are the compiler's own report, from a build of `#error version` (`CS8304`): `net8.0` default is `12.0`,
`net8.0` with `LangVersion=latest` is `latest (14.0)`, and `net10.0` default is `14.0`. Each cell names
the overload the compiler selects, or the diagnostic.

| Shape | `byte[]` value | `byte[]?` holding null | `null` literal |
| --- | --- | --- | --- |
| `ReadOnlySpan<byte>` only | compiles, span | compiles, span | compiles, span |
| `ReadOnlySpan<byte>` plus a separately named `ReadOnlyMemory<byte>` method | compiles, span | compiles, span | compiles, span |
| `byte[]` plus `ReadOnlySpan<byte>` | compiles, array | compiles, array | compiles, array |
| `ReadOnlySpan<byte>` plus `ReadOnlyMemory<byte>` | net8.0 default: `CS0121`; otherwise compiles, span | net8.0 default: `CS0121`; otherwise compiles, span | `CS0121` at all three configurations |
| `byte[]` plus span plus `ReadOnlyMemory<byte>` | compiles, array | compiles, array | compiles, array |
| `ReadOnlyMemory<byte>` only | compiles, memory | compiles, memory | compiles, memory |

Two cases carry the span-plus-memory shape: `CASE_SHAPE_SPAN_MEMORY_NO_NULL` holds its typed call sites
alone, which is where the `otherwise compiles, span` cells come from, and `CASE_SHAPE_SPAN_MEMORY` adds
the `null` literal, whose call site is the `CS0121` at all three configurations. The `byte[]`-plus-span
rows are one case each, `CASE_SHAPE_ARRAY_SPAN` and `CASE_SHAPE_ARRAY_SPAN_MEMORY`, whose methods are
overloads of one name, so every call site resolves to the array method rather than to a separately
named one.

The mechanism (reasoning from the compiled matrix above, which is the evidence): before C# 14 both
`byte[]` to `ReadOnlySpan<byte>` and `byte[]` to `ReadOnlyMemory<byte>` are user-defined conversions, so
neither overload is better and the call is ambiguous. C# 14 (documented, quoted) "introduces first-class
support for `Span<T>` and `ReadOnlySpan<T>` in the language", with implicit conversions between `T[]` and
the span types [CS14]; on that reading those built-in conversions beat the user-defined conversion to
`ReadOnlyMemory<byte>`, which leaves the untyped `null` literal, which has no array type to convert
from, as the only `CS0121` case.

The batch argument behaves differently, because it is a list whose element type fixes the interface
rather than a byte-like value. Compiled by the batch cases of the same harness (transcripts):
`ExtractMany(IReadOnlyList<byte[]>)` and `ExtractMany(IReadOnlyList<ReadOnlyMemory<byte>>)` coexist
without ambiguity at both frameworks: a `List<byte[]>`, a `List<ReadOnlyMemory<byte>>` and a
`byte[][]` each bind to the matching overload, and a memory-only batch rejects the `List<byte[]>` and
the `byte[][]` with `CS1503`. The same harness builds the conversion a variance rule would have to
supply, and the compiler rejects it: `IReadOnlyList<ReadOnlyMemory<byte>> pages = new List<byte[]>();`
gives `error CS0266: Cannot implicitly convert type 'System.Collections.Generic.List<byte[]>' to
'System.Collections.Generic.IReadOnlyList<System.ReadOnlyMemory<byte>>'`. Reasoning for why, that
diagnostic being the evidence: `IReadOnlyList<T>` is covariant, but variance carries only reference and
identity conversions, so a user-defined element conversion to `ReadOnlyMemory<byte>` cannot make the
memory overload a candidate for a `List<byte[]>`.

Ranked alternatives. The span and memory entry points reach the same internal span and cost the same, so
the differences between the shapes are caller reach, overload resolution, what the caller has to spell
at the call site, and how many public members the surface carries. Caller reach is the difference that
decides between the top three: a memory-only entry point cannot accept a span whose bytes do not live
in a managed array, which the harness rejects with `CS8346` ("Conversion of a stackalloc expression of
type 'byte' to type 'ReadOnlyMemory<byte>' is not possible.", transcripts), while a span entry point
accepts every `ReadOnlyMemory<byte>` through `.Span`. The ranking of all six shapes is in R4 of the
companion recommendations file; the one the analysis selects is the pair of signatures below.

Adopted surface. The maintainer decided against that pair's reach: one single-page entry point,
`ExtractFromBytes(ReadOnlyMemory<byte>)`, which is rank 3's shape under the name callers already have,
plus a non-owning `UnmanagedMemory.Wrap(byte* pointer, int length)` returning `Memory<byte>` for a
caller whose bytes sit behind a pointer. Both `ExtractMany` overloads are kept and no span entry point
is added, because a `Memory<byte>` can outlive the frame a span cannot. Rank 1's extra reach is what
this trades away, and the smaller byte surface is what it buys.

Later revised. The surface shipped as rank 1 after all: `ExtractFromBytes(ReadOnlyMemory<byte>)` for
the memory-backed shapes and `ExtractFromSpan(ReadOnlySpan<byte>)` for the rest, into one private
`Extract(ReadOnlySpan<byte>, ExtractionOptions?)`, and `UnmanagedMemory` is gone. The adapter was
buying back the reach rank 3 gives up, at the cost of a public `unsafe` signature and a view that does
not keep its buffer alive; a span entry point covers the same bytes with neither.

Rank 1, as signatures (not the adopted shape):

```csharp
public static ExtractionResult ExtractFromBytes(ReadOnlySpan<byte> html, ExtractionOptions? options = null);
public static ExtractionResult ExtractFromMemory(ReadOnlyMemory<byte> html, ExtractionOptions? options = null);
public static IReadOnlyList<ExtractionBatchItem> ExtractMany(
    IReadOnlyList<ReadOnlyMemory<byte>> pages, ExtractionOptions? options = null, int maxDegreeOfParallelism = 0);
public static IReadOnlyList<ExtractionBatchItem> ExtractMany(
    IReadOnlyList<byte[]> pages, ExtractionOptions? options = null, int maxDegreeOfParallelism = 0);
```

Either shape leaves one pipeline: the public entry points reach the same private
`Extract(ReadOnlySpan<byte>, ExtractionOptions?)`, so a `ReadOnlyMemory<byte>` only has to produce
`memory.Span`. What a caller must change: the `ExtractFromBytes(byte[])` symbol is gone, so a `byte[]`
call site keeps compiling through the implicit conversion to whichever byte parameter type the shape
takes (a compiled cell of the matrix above, and one that allocates nothing) but the caller rebuilds
against the new assembly, and a null `byte[]` becomes an empty memory (observed: the harness's runtime
check prints `null byte[] to ReadOnlyMemory<byte>: Length = 0, IsEmpty = True`), so the core sees a
zero-length payload and answers with its own too-short output error rather than the caller seeing
`ArgumentNullException` (the core's test `empty_payload_reaches_the_core`, `rust/src/engine.rs:412-413`,
asserts kind `OutputTooShort`), and that check goes with the array overload. `ExtractMany` keeps its
`IReadOnlyList<byte[]>` overload beside the new `IReadOnlyList<ReadOnlyMemory<byte>>` one, because the
batch probe shows the pair is unambiguous and a `List<byte[]>` caller would otherwise have to build a
second list.

What an `async` caller can and cannot do. The table is compiled by the async cases of the same harness
(transcripts), one construct per build, at the three configurations the overload matrix above uses.

| Construct inside an `async` method | C# 12 (`net8.0` default) | C# 14 (`net8.0` latest) | C# 14 (`net10.0` default) |
| --- | --- | --- | --- |
| `ReadOnlySpan<byte>` local declared and read before the `await` | `CS9202` | compiles | compiles |
| `ReadOnlySpan<byte>` local declared and read after the `await` | `CS9202` | compiles | compiles |
| `ReadOnlySpan<byte>` local declared before an `await` and read after it | `CS9202` | `CS4007` | `CS4007` |
| `stackalloc byte[8]` held in a span local | `CS9202` | compiles | compiles |
| `ReadOnlyMemory<byte>` held across an `await`, `.Span` taken after it | compiles | compiles | compiles |
| `byte[]` read after an `await` | compiles | compiles | compiles |

The diagnostics, verbatim: `error CS9202: Feature 'ref and unsafe in async and iterator methods' is not
available in C# 12.0. Please use language version 13.0 or greater.` and `error CS4007: Instance of type
'System.ReadOnlySpan<byte>' cannot be preserved across 'await' or 'yield' boundary.` Rows 1, 2 and 4 are
the first of the two rules the reference states: before C# 13 a `ref struct` variable cannot be used in
an `async` method at all, whatever it is used for. Row 3 is the second rule: from C# 13 a `ref struct`
variable cannot be used in the same block as an `await` [refstruct]. Only row 3 is a crossing failure,
and in the `CS9202` column it is reported as the same missing feature as the others rather than as a
crossing.

The practical conclusion is rows 5 and 6, and it is unchanged by the correction: both entry points are
callable from an `async` method, because a caller that must hold the buffer across an `await` holds the
`ReadOnlyMemory<byte>` across it and takes `.Span` after the `await`. What a memory entry point saves is
spelling `.Span`, not a capability. Nothing in this table is a property of the entry points; it is a
property of a `ref struct` local in the caller's own method.

The tests that pin rank 1 (the adopted surface's own tests belong to the byte-surface task):
`Bytes_Entry_Points_Agree`, asserting field-for-field equality of
`ExtractionResult` and of every `Metadata` member across the span method, the memory method, the
`string` overload and the `byte[]` call site for the existing fixtures, plus `OutputTooShort` rather
than `ArgumentNullException` for a null array; `Bytes_Call_Sites_Compile_At_The_Net8_Default`, a
compile-only fixture built at `net8.0` with the default language version that calls the span method
with a `byte[]`, a null `byte[]` and `null` (the matrix above is the manual version of it, and this
makes it a gate); `ExtractMany_Array_And_Memory_Overloads_Are_Both_Reachable`, cross-referenced from R4,
which pins the batch coexistence this section measures; and `ExtractMany_MemoryOverload_Matches_
Sequential`, asserting one item per input in input order with the same per-page error kinds as the
sequential call.

## 6. Managed allocations per call

Fixture: a synthetic response of exactly 198,625 bytes, the size the bench transcript reports for the
default `responseFields`, whose `contentText` holds 198,180 characters (396,360 bytes of UTF-16). It
is synthetic because `rust/benches/roundtrip.rs` never enters the managed side, so there is no
recorded managed response to replay. Method: `GC.GetAllocatedBytesForCurrentThread` around a loop,
five runs (harness `AllocProbe`) and 500-iteration loops for the times (harness `Eval`), on the
section 1 host, net8.0 in Release with SDK 10.0.401. Path A is today's `Marshal.PtrToStringUTF8` plus
`JsonDocument.Parse(string)`; path B is a pooled `ArrayPool` copy plus
`JsonDocument.Parse(ReadOnlyMemory<byte>)`; path C is `Utf8JsonReader` over a `ReadOnlySpan<byte>`
built from the returned pointer.

| Allocation, of a 198,625-byte response | Paths | Measured per call | Can it go |
| --- | --- | --- | --- |
| Response envelope `string` (198,625 chars) | A | 397,250 B of text plus one object header | Yes: read the native bytes (path C) |
| `contentText` string (198,180 chars) | A, B, C | 396,360 B plus one object header | No: the value the caller asked for |
| `JsonDocument` metadata, other field strings, the probe's own return value | A | about 420 B (A's total minus the two rows above) | Yes, with path C |
| `JsonDocument` metadata over the rented copy | B | about 315 B | Yes, with path C |
| Small field strings and the probe's own return value | C | about 257 B | Partly: the field strings are the floor |
| Whole path | A | 794,019-794,099 B, 0.72-1.37 ms | |
| Whole path | B | 396,661-396,683 B, 0.27-0.53 ms | |
| Whole path | C | 396,613-396,627 B, 0.23-0.48 ms | C wins on both |
| Request header bytes, `JsonSerializer.SerializeToUtf8Bytes` over the harness's fixed option set | reflection | 728 B, 4.6-12.3 us | Yes: 680 B, 4.7-11.3 us source-generated, or 24 B, 2.6-8.1 us hand-written |
| `RequestHeader` DTO instance | all | no probe took a number | Yes, only by hand-writing the header |
| `ExtractionResult` and `Metadata` | all | a small object graph; no probe took a number | Convertible to structs; not worth it |
| `List<string>` for `categories` and `tags`, key present as an empty array | all | 32 B per list (measured) | Yes: return the shared empty array (R5) |
| `List<string>` for `categories` and `tags`, key absent or not an array | all | 0 B (measured: the shared empty array) | Already gone |
| `TrafilaturaException` | failure path only | one exception | Floor |

The arithmetic, which the rows above must satisfy: path A is the envelope string (397,250 B) plus the
`contentText` string it is then parsed into (396,360 B) plus about 420 B of document metadata and
small strings, which is 794,030 B against the measured 794,019-794,099 B; paths B and C pay only the
`contentText` string plus their own overhead, which is 396,675 B and 396,617 B against the measured
396,661-396,683 B and 396,613-396,627 B. The saving is therefore one full-size managed string and one
UTF-8-to-UTF-16 decode before any JSON work, not the elimination of string handling: the field strings
are still materialised, and they have to be, because `ExtractionResult` exposes `string`.
`Encoding.UTF8.GetString(pointer, length)` produces the same full-size string and is therefore not the
fix; it is what today's code already does through `Marshal.PtrToStringUTF8`.

A `JsonDocument` over the native buffer is possible but constrained: "The `ReadOnlyMemory<T>` value
will be used for the entire lifetime of the JsonDocument object, and the caller must ensure that the
data therein does not change during the object lifetime" [jsondoc]. That moves `rt_free` to after the
document is disposed, so the reader form is the simpler of the two, and adding a rented copy buys
nothing over it.

The header can be built once and reused for a fixed option set, but the measured win is about 5 us and
728 B (today's reflection path; the generated context is 680 B) of a 23.25 ms call on the section 1
host, about 0.02%, and reuse needs a cache whose invalidation a caller can get wrong. Do not cache it
for speed; change it only for AOT compatibility (section 8).

## 7. Target frameworks: `net8.0;net10.0`

The maintainer decided the package multi-targets `net8.0;net10.0`, with `net9.0` out. That is
recorded here as a decision; what follows is what the second asset buys and costs. The spec's reach
argument is unaffected and is not an argument against it: a `net10.0` consumer resolving the
`net8.0` asset is why a second asset is not needed for reach, and also why dropping the `net8.0` asset
would strand every consumer below .NET 10.

| Difference | `net8.0` | `net10.0` | Measured here |
| --- | --- | --- | --- |
| Enum member names on the wire | No `[JsonStringEnumMemberName]`, which arrived in .NET 9 [NET9], so the wire spellings need a hand-written `JsonConverter<T>` or a `#if` | `[JsonStringEnumMemberName("favorPrecision")]` plus `[JsonSourceGenerationOptions(UseStringEnumConverter = true)]` produces the exact wire bytes | Observed: the harness's net8.0 case (reflection plus hand-written converters) and its net10.0 case (attributes, no converter code) both print `{"schemaVersion":1,"options":{"focus":"favorPrecision","htmlDateMode":"extensive"}}` (transcripts) |
| `JsonStringEnumConverter<TEnum>` | Present, assembly version 8.0.0.0 [enumdoc] | Present | Observed: the harness's generic-converter case builds and runs on both, printing `{"focus":"favorPrecision"}` |
| `System.Text.Json` origin | In-box 8.0.x plus an explicit `System.Text.Json 8.0.6` reference | In-box 10.x; the reference can be dropped from that asset | Compiled: the harness project that produces the byte-identical header has no `System.Text.Json` package reference at all and its `net10.0` asset builds and runs (transcripts) |
| Trim and AOT analyzers | Present; the SDK guidance says targeting the latest supported TFM "helps you benefit from the latest analyzer improvements" [AOT] | Newer analyzer set | No delta measured: the guidance names the benefit without quantifying it, and no analyzer release note quantifying it for .NET 9 or 10 was found |
| `JsonSerializerOptions.Strict`, `AllowDuplicateProperties` (.NET 10 [NET10]) | Absent | Present | No use here: the header is written by this package and the response is read by hand |
| `SearchValues<T>` | Introduced in .NET 8 [NET9] | Extended to substrings in .NET 9 | No use here: no hot set or substring search exists in this package |

Consequences to accept: a second asset in the `.nupkg`; the test matrix running twice, or a
deliberate decision to test only one asset, which is how an asset rots; a CI job that provides a .NET
10 runtime for the second asset (`.github/workflows/` does not exist at this commit; the CI task
creates it, and the SDK version it installs decides whether a runner change is needed); a sentence in
the README and the package metadata naming the supported frameworks; and any `#if` divergence becoming
a second implementation to keep in step.

Should `net8.0` keep the fuller feature set or fall back under `#if`? One implementation on both, and
that implementation is the source-generated context of section 8. The two frameworks differ today in
exactly one place, how the enum spellings reach the wire, and the net10.0 way is shorter rather than
better: both produce identical bytes (transcripts) and the header costs 5 us of a 23.25 ms call on the
section 1 host. A
second header implementation would buy nothing measurable and cost a divergence to maintain. The one
place a `#if` earns its keep is the enum attributes themselves (`#if NET9_0_OR_GREATER` around
`[JsonStringEnumMemberName]` on the two public enums), which lets the net10.0 asset drop the
converters without forking the writer; adopt it only with the byte-identity test below, because it
puts the wire contract in two places.

What proves the second asset: `Header_Json_Is_Identical_Across_TargetFrameworks`, which serializes the
header for a fixed option set including `ExtractionFocus.FavorPrecision` and
`HtmlDateMode.Extensive` and compares the bytes to a literal recorded in the repository, run in the
test matrix for both frameworks. That is the assertion `Header_Json_Has_The_Wire_Shape` and
`Enum_Options_Use_The_Core_Spellings` already make for one.

## 8. Trimming and Native AOT

What trimming does to a library that uses reflection. Documented [TRIM]: a project that sets
`PublishTrimmed` has `JsonSerializerIsReflectionEnabledByDefault` set to `false` automatically, and
reflection-based serialization then "fail[s] serialization outright" with `InvalidOperationException:
Reflection-based serialization has been disabled for this application`. Reasoning from that consequence
(the page states the outcome, not the mechanism): trimming removes code the compiler believes is
unreachable, and reflection reaches code in ways the compiler cannot see, so the reflection-based
serialization path is the one trimming breaks. Native AOT adds to that rather than relaxing it.
Documented [AOT], which lists under "Native AOT apps have the following limitations": "Requires
trimming, which has limitations", "Implies compilation into a single file, which has known
incompatibilities" and "No runtime code generation, for example, System.Reflection.Emit".

What Native AOT requires of a package. No runtime-generated marshalling stubs, which is why the SDK
says `DllImport` "isn't an option for platforms that require full Native AOT scenarios" and
`[LibraryImport]` is the replacement [PINVOKE]; no reflection-based serialization; and the native
library resolving as a file beside the published executable rather than through a `runtimes/<rid>`
probe. Documented [SINGLEFILE]: "Only managed DLLs are bundled with the app into a single executable."
Reasoning from that: the native library stays a separate file, and the directory an application loads
it from is the application's own, which is where `AppContext.BaseDirectory` points, so
`Interop.DescribeCandidates`, which lists that path first, still resolves it and only the message a
failure prints would change.

What `IsAotCompatible` promises. Setting it to `true` "assigns a default of `true`" to `IsTrimmable`,
`EnableTrimAnalyzer`, `EnableSingleFileAnalyzer` and `EnableAotAnalyzer` [AOT]. In plain terms
(reasoning over that quote): the package declares to consumers that it is safe under trimming,
single-file and Native AOT, and the analyzers enforce that claim in this repository's build rather than
at a consumer's runtime. It is one switch for three claims, and the [AOT] page states their direction:
"Requires trimming, which has limitations" and "Implies compilation into a single file, which has known
incompatibilities" are limitations of Native AOT apps, so a Native AOT library must also be trim-clean
and single-file-clean. The converse does not follow, and two more entries in the same list are why:
"No runtime code generation" and "No dynamic loading" are AOT-only limitations, so a library that is
trim-clean and single-file-clean can still fail Native AOT (documented: the quotes are the page's own
list of AOT limitations).

What the analyzers report today. The harness (`AotAnalyzerProbe`, transcripts) builds the package's call
shapes under `IsAotCompatible=true` with SDK 10.0.401, which is where these come from in the package:

| Location in the package | Warning |
| --- | --- |
| `Extractor.cs:19`, `HeaderJsonOptions` | `IL3050` on `JsonStringEnumConverter(JsonNamingPolicy, bool)`: "use the generic `JsonStringEnumConverter<TEnum>` instead" |
| `Extractor.cs:182`, `BuildHeader` | `IL2026` (trim) and `IL3050` (AOT) on `JsonSerializer.SerializeToUtf8Bytes<TValue>(TValue, JsonSerializerOptions)` |
| `Interop.cs` | Clean: the harness's `[LibraryImport]` declaration takes pointer parameters and produces no warning under the same analyzers |

The three warnings, verbatim from that build:

```text
warning IL2026: Using member 'System.Text.Json.JsonSerializer.SerializeToUtf8Bytes<TValue>(TValue, JsonSerializerOptions)' which has 'RequiresUnreferencedCodeAttribute' can break functionality when trimming application code. JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes a JsonTypeInfo or JsonSerializerContext, or make sure all of the required types are preserved.
warning IL3050: Using member 'System.Text.Json.JsonSerializer.SerializeToUtf8Bytes<TValue>(TValue, JsonSerializerOptions)' which has 'RequiresDynamicCodeAttribute' can break functionality when AOT compiling. JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use System.Text.Json source generation for native AOT applications.
warning IL3050: Using member 'System.Text.Json.Serialization.JsonStringEnumConverter.JsonStringEnumConverter(JsonNamingPolicy, Boolean)' which has 'RequiresDynamicCodeAttribute' can break functionality when AOT compiling. JsonStringEnumConverter cannot be statically analyzed and requires runtime code generation. Applications should use the generic JsonStringEnumConverter<TEnum> instead.
```

`IL2026` names the overload that takes a `JsonTypeInfo` or `JsonSerializerContext`, which is step 3
below; `IL3050` names `System.Text.Json` source generation generally, which is steps 1 and 2.

The non-generic `JsonStringEnumConverter` is not AOT-safe and the generic form is the documented
replacement: "The non-generic `JsonStringEnumConverter` type is not supported by the Native AOT
runtime" [S2J-enum], with `JsonStringEnumConverter<TEnum>` in the assembly since 8.0.0.0 [enumdoc].
This is not a theory about a future publish. Observed:
`dotnet publish AotAnalyzerProbe/AotAnalyzerProbe.csproj -c Release -r win-x64 -p:PublishAot=true`
(MSVC present, exit 0) produces an app that throws at the header call --
`Unhandled Exception: System.InvalidOperationException: Reflection-based serialization has been disabled
for this application. Either use the source generator APIs or explicitly configure the
'JsonSerializerOptions.TypeInfoResolver' property.`
-- with the stack through `System.Text.Json.JsonSerializerOptions.ConfigureForJsonSerializer()`
(the transcripts also carry the three analyzer warnings that publish repeats).

The proposal, all of it in the managed package and none of it in the ABI:

1. Add one `JsonSerializerContext` in `dotnet/src/RustTrafilatura/` covering `RequestHeader`,
   `ExtractionOptions` and `ExtractionConfig`, with `PropertyNamingPolicy = CamelCase`,
   `DefaultIgnoreCondition = WhenWritingNull` and the two enum converters [S2J-src]. Use the default
   metadata generation mode: in the fast-path mode `JsonSourceGenerationMode.Serialization` the
   generated `JsonTypeInfo` carries no property metadata, and reaching it through a resolver-based
   options object throws (observed: the harness's `CASE_SERIALIZATION_MODE` build succeeds and its run
   fails with `System.InvalidOperationException: TypeInfoResolver 'AotAnalyzerProbe.HeaderContext' did
   not provide property metadata for type 'AotAnalyzerProbe.RequestHeader'.`, transcripts).
2. Replace `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` with per-enum converters. Observed:
   in `AotAnalyzerProbe`, `CASE_CONTEXT` (per-enum `JsonConverter<T>` types registered through
   `[JsonSourceGenerationOptions(Converters = ...)]`) prints the same header bytes as that probe's
   reflection case on net8.0, `url` and `responseFields` included; and in `CompileProbe`, `CASE_HEADER`
   built at net10.0 prints the same shorter
   `{"schemaVersion":1,"options":{"focus":"favorPrecision","htmlDateMode":"extensive"}}` as the same case
   at net8.0, from `[JsonStringEnumMemberName]` plus `UseStringEnumConverter = true` and no converter
   code at all (transcripts).
3. Serialize through the generated metadata with
   `JsonSerializer.SerializeToUtf8Bytes(header, HeaderContext.Default.RequestHeader)`, the overload the
   `IL2026`/`IL3050` messages ask for. Compiled: that context and its converters build with
   `0 Warning(s)` under `IsAotCompatible=true`, while the reflection shape in the same project reports
   all three warnings (transcripts).
4. Set `<IsAotCompatible>true</IsAotCompatible>` in `dotnet/Directory.Build.props` for both target
   frameworks. No `[RequiresUnreferencedCode]` annotation is needed anywhere (compiled: the harness's
   `CASE_LIBRARYIMPORT` build, which contains the pointer-parameter `[LibraryImport]` declaration and
   nothing else, reports `0 Warning(s) 0 Error(s)` under the same analyzers), because
   `Marshal.PtrToStringUTF8`, `Utf8JsonReader` and the `[LibraryImport]` declarations are all trim- and
   AOT-safe. `System.Text.Json` stays an explicit reference for
   `net8.0`.
5. Do not add a hand-written `Utf8JsonWriter` header at the same time. It is faster and allocates less
   (measured over the harness's fixed option set on the section 1 host: 24 B and 2.6-8.1 us against
   680 B and 4.7-11.3 us for the generated context) and is equally AOT-safe, but it
   duplicates the wire shape that the DTO and the existing header tests already define, and it leaves no
   DTO for `[JsonStringEnumMemberName]` to annotate on the net10.0 asset. If that duplication is
   acceptable it is the better end state; rank it after the context.

What proving it costs in CI: an AOT publish needs the platform's native toolchain (MSVC on Windows,
clang on Linux), it is slower than a normal test run, and the native asset has to resolve inside a
RID-specific AOT app. A property set without that job is a claim, not evidence. The check that would
prove it: a job that publishes a scratch console app with `PublishAot` for `win-x64` and `linux-x64`,
runs one extraction through the package's own output, and asserts a non-empty `ContentText`; plus
`-warnaserror` on the analyzers in `ci.yml`, so `IL2026` and `IL3050` fail the build instead of
scrolling past. Recommendation: set `IsAotCompatible=true` on both frameworks once the context and the
converters land, and add the publish job in the same change; without the job, leave the property unset
and state in the README that the package is not AOT-verified.

## 9. Verdict on D3

D3 rejected UniFFI for the hand-written C ABI plus JSON. Nothing found or measured here meets the bar
for revisiting it. What would have to be true, and where each condition stands:

1. The C# generator would have to reach the zero-copy `&[u8]` path, which needs uniffi-rs 0.32.0 or
   later [CHANGELOG] and a generator built against it. Today's newest generator targets 0.31.0
   [CS-README], so its `bytes` arguments still copy through a `RustBuffer` [CS-RUSTBUF]. Not met.
2. The surface would have to be wide enough for UniFFI's per-call and per-type costs to amortise: many
   small calls, stateful objects, callbacks or async. This component is one call with no state (D5),
   where UniFFI's minimum is three or more crossings against this design's two. Not met, and not
   planned.
3. UniFFI would have to be the faster path on this workload. Its own design principles call the
   structured-data serialization a standing, unmeasured "Violation" and state that performance loses
   to simplicity by policy [UG-principles], and no page found claims a win here. Not met.
4. The measured binding budget would have to be at risk. It is not: the native wrapper measures 0.988
   against a 1.5x budget, and the managed layer's own measurable cost (section 6) is addressable
   without changing the ABI at all. Not met.

Adopting UniFFI would add a generated binding surface, a runtime contract check, an `Arc`/`Dispose`
leak class this design does not have, and a third-party generator whose own README warns it is not
stable between versions — in exchange for a payload copy and a serialization format that are not
faster here. D3 stands. It should be revisited only if the surface grows objects, callbacks or async,
or if a measurement of the managed layer shows the hand-written ABI over its budget.

## 10. The scratch note, item by item

`.work/RustTrafilaturaOptimizeResearch.md` is a Vietnamese research note without citations, written as
if this project used UniFFI. It is treated here as a checklist, never as a source. The R-ids below are
defined in the companion recommendations file.

| Note item | Verdict |
| --- | --- |
| RustBuffer serialization of complex types is a bottleneck; use raw byte sequences or `sequence<u8>` | Already true of the applicable half: the payload is raw bytes, never base64 (D10), and the response is one buffer. The premise is UniFFI's problem, not this design's |
| Object wrapping causes GC pressure; batch instead of returning objects | Not applicable: there are no handle objects, and `ExtractMany` already batches across pages |
| Missing `Dispose` leaks native objects | Not applicable: no handle outlives a call and nothing here is disposable |
| Add `[SuppressGCTransition]` for short calls | Wrong for this design; rejected with the attribute's own rule in the recommendations file |
| A sync call blocks the caller's thread; use UniFFI async | Accurate about UniFFI, which does emit `Task`-returning bindings and an async runtime helper [CS-ASYNC]. Not applicable here: no async surface (D5), and the work is CPU-bound, so `ExtractMany` is the throughput answer |
| Thread-pool starvation between two runtimes | Not applicable: there is one runtime, one worker thread per call (D6), and no Rust async runtime |
| Data-oriented design, flat arrays instead of per-entity objects | Already true: one flat call per page, and no per-node object crosses the boundary |
| Push large loops into Rust for cache locality | Already true: all looping over the document happens inside the core; `ExtractMany` parallelises across pages rather than looping in C# |
| Avoid exceptions for ordinary failures; return a status instead | Reasoning, not measured: failures are the cold path, and the allocation table in section 6 charges one exception to the failure path only. `ExtractMany` already captures a per-page failure as a value rather than throwing it. Converting the single-page API to a status struct would be a breaking surface change for a path no measurement shows to be hot, so it is not recommended |
| Zero-copy with a raw pointer and `Span<byte>` on the C# side | Already true for the input: it is passed as `(ptr, len)` with no managed copy, whether the generator pins a `byte[]`/span itself or the explicit `fixed` takes the pointer (section 5). The response is the part that still copies, and that is R1 |
| Wrap the returned pointer with `new ReadOnlySpan<byte>((void*)rustPtr, length)` | Correct, and already how the ABI is shaped; it is the mechanism R1 uses for the response |
| Use `fixed` or `unsafe` to take the pointer for the P/Invoke | Correct, and optional: `[LibraryImport]` accepts a `byte[]` or a span parameter and pins it itself, and rejects only a memory parameter, `ReadOnlyMemory<byte>`, with `SYSLIB1051` (section 5); the explicit `fixed` in `Interop.cs` is a choice, not the only way |
| Pool or scope the extractor object across calls | Not applicable: the API is static and stateless, so there is nothing to pool. The per-call thread is the core's stack policy (D6), not an object |
| Compile Rust regexes once with `lazy_static`/`OnceLock` | Core-crate concern, outside this repository and outside the binding layer. The core already holds its per-call caches in `LazyLock` statics and has no shared mutable state in the extraction path |
| Reuse `Cow<'a, str>` and buffer text in Rust | Core-crate concern, outside this repository and outside the binding layer; no measurement here supports a change |
| Whitespace cleanup by character iteration beats regex "5 to 10 times" | No source and no measurement: the note states the figure without one, nothing in this repository checks it, and the core's whitespace handling is outside this repository |
| Estimate the Rust output buffer at "20 to 30% of the HTML size" | No source and no measurement either. This repository's own checkable number is different in kind: the bench fixture's 206,687 bytes of HTML produce a 198,625-byte default JSON response [bench] |
| `ReadOnlyMemory<byte>`/`Span<byte>` is "zero allocation" | Partly wrong: the view is free and the buffer is not (measured: the allocation table in section 6 charges the buffer to whichever path allocates it), and a caller holding `byte[]` from `GetByteArrayAsync` gains nothing (section 5) |
| Decode the result bytes once in C# with `Encoding.UTF8.GetString` and skip UniFFI's intermediate | Already true here, and it is what the current code does: `Marshal.PtrToStringUTF8` over the returned pointer produces the string in one pass. What it does not avoid is the full-size string itself, which is why section 6 measures the envelope at 397,250 B and recommends the reader instead. The note's suggestion is correct advice for a UniFFI binding, not a change for this one |
| The C# generator emits async `Task` bindings | True, and read from the generator's templates: `Task<T>` return types and an async runtime helper [CS-ASYNC] |

## Sources

The UniFFI guide is cited at the 0.32 series; each fact names the release it belongs to. `uniffi` on
crates.io: 0.32.2 (2026-09-23), 0.32.1 (2026-09-09), 0.32.0 (2026-06-30).

- [bench]: `rust/benches/roundtrip.rs`, `scripts/bench.ps1`, and the run recorded in section 1.
- [CHANGELOG]: https://github.com/mozilla/uniffi-rs/blob/main/CHANGELOG.md
- [crates]: https://crates.io/api/v1/crates/uniffi
- [UG-lift]: https://mozilla.github.io/uniffi-rs/0.32/internals/lifting_and_lowering.html
- [UG-rustcalls]: https://mozilla.github.io/uniffi-rs/0.32/internals/rust_calls.html
- [UG-foreign]: https://mozilla.github.io/uniffi-rs/0.32/internals/foreign_calls.html
- [UG-ftraits]: https://mozilla.github.io/uniffi-rs/0.32/foreign_traits.html
- [UG-objects]: https://mozilla.github.io/uniffi-rs/0.32/internals/object_references.html
- [UG-async]: https://mozilla.github.io/uniffi-rs/0.32/internals/async-overview.html
- [UG-bytes]: https://mozilla.github.io/uniffi-rs/0.32/types/bytes.html
- [UG-records]: https://mozilla.github.io/uniffi-rs/0.32/types/records.html
- [UG-kotlin]: https://mozilla.github.io/uniffi-rs/0.32/kotlin/lifetimes.html
- [UG-futures]: https://mozilla.github.io/uniffi-rs/0.32/futures.html
- [UG-principles]: https://mozilla.github.io/uniffi-rs/0.32/internals/design_principles.html
- [RUSTBUF]: https://github.com/mozilla/uniffi-rs/blob/v0.32.1/uniffi_core/src/ffi/rustbuffer.rs
- [#195]: https://github.com/mozilla/uniffi-rs/issues/195
- [#2056]: https://github.com/mozilla/uniffi-rs/issues/2056
- [#2858]: https://github.com/mozilla/uniffi-rs/issues/2858
- [#3009]: https://github.com/mozilla/uniffi-rs/issues/3009
- [CS-README]: https://github.com/NordSecurity/uniffi-bindgen-cs, README at `v0.11.1+v0.31.0`
- [CS-RUSTBUF], [CS-STRING], [CS-NAMESPACE], [CS-ASYNC]: `RustBufferTemplate.cs`, `StringHelper.cs`,
  `NamespaceLibraryTemplate.cs`, `Async.cs` and `macros.cs` at `v0.11.1+v0.31.0`,
  https://github.com/NordSecurity/uniffi-bindgen-cs/tree/v0.11.1+v0.31.0/bindgen/templates
- [AOT]: https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/
- [TRIM]: https://learn.microsoft.com/en-us/dotnet/core/compatibility/serialization/8.0/publishtrimmed
- [PINVOKE]: https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation
- [SINGLEFILE]: https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview
- [ROMMEM]: https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/ReadOnlyMemory.cs
- [S2J-src]: https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation
- [S2J-enum]: the same page, section "Serialize enum fields as strings"
- [jsondoc]: https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsondocument.parse
- [enumdoc]: https://learn.microsoft.com/en-us/dotnet/api/system.text.json.serialization.jsonstringenumconverter-1
- [refstruct]: https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/ref-struct
- [CS14]: https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14, section "Implicit span conversions"
- [NET9]: https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-9/libraries
- [NET10]: https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/libraries

Measurements in sections 1, 5, 6 and 8 that are not cited to a file above were taken for this
evaluation with the bench and the harnesses described inline; `binding-performance-probes/transcripts.md`
holds the transcripts of the probes committed under `docs/research/binding-performance-probes/`. Where
the scratch note's claims have no source, the row says so instead of carrying a status.

## Probe transcripts

The verbatim build and run output behind every `compiled` row lives in
`binding-performance-probes/transcripts.md`, beside the harness that produces it; a row labelled
`compiled` above points there. That file's own intro records what each transcript strips, and the
harness's README names every case and its exact command.
