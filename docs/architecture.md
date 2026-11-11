# Architecture

Reference for the wire contract between the native library (`rust/`) and the managed client
(`dotnet/src/RustTrafilatura/`). Each section names the type or constant that owns the fact; values
live there and are not repeated here. Building and staging the library is in
[development.md](development.md); which files change together when one of these facts changes is
the "Change together" table in [AGENTS.md](../AGENTS.md).

## Call path

1. A public entry point on `dotnet/src/RustTrafilatura/Extractor.cs::Extractor` takes the page as
   bytes (`ExtractFromHtml` encodes a string to UTF-8 and sends it as text, see "Request header").
2. `dotnet/src/RustTrafilatura/LibraryContract.cs::Ensure` checks the loaded library's version
   document once per process and throws `ContractVersionMismatchException` on every call after a
   mismatch.
3. `dotnet/src/RustTrafilatura/Request.cs::Header` serializes the options into the request header.
4. `dotnet/src/RustTrafilatura/Interop.cs::Extract` calls `rust/src/lib.rs::rt_extract` with the
   header and the page as raw buffers; the page is never base64-encoded or copied into a string.
5. `rust/src/lib.rs::rt_extract` checks the null pointers and the header size, parses the header
   (`rust/src/contract.rs::parse_request`) and runs the extraction through
   `rust/src/lib.rs::on_worker_thread`, on the caller's thread or on a worker on macOS, behind a panic
   boundary. `rt_extract` checks the payload against its cap after the header is parsed and before any payload
   byte is read, and the core checks it again.
6. `rust/src/engine.rs::build` calls the core and assembles the response JSON.
7. The managed side reads the response straight from the native buffer
   (`dotnet/src/RustTrafilatura/Response.cs::Read`, or `dotnet/src/RustTrafilatura/Failures.cs::Read`
   on an error code), then releases the buffer with `rust/src/lib.rs::rt_free` in a `finally` block.

## Return codes

The `CODE_*` constants in `rust/src/lib.rs`: success, extraction error, invalid request, caught
panic. Every code that writes a buffer hands the caller a UTF-8 JSON buffer it owns; a null
out-parameter writes nothing. The `rust/src/lib.rs::rt_extract` doc comment is the precise statement.

## Request header

`rust/src/contract.rs::Header`: the schema version, the page URL, the options
(`rust/src/contract.rs::OptionsJson`, with `rust/src/contract.rs::ConfigJson` for size thresholds),
the response fields to return (`rust/src/contract.rs::ResponseField`) and `payloadText`. Unknown keys
are rejected at every level. `payloadText: true` says the payload is UTF-8 text, which skips the byte
path's decompression, charset detection and binary check; invalid UTF-8 under it is an
`InvalidRequest`, and an absent or false key keeps the payload as bytes. `ExtractFromHtml` sends the
key and the byte entry points never do. The managed mirror is
`dotnet/src/RustTrafilatura/HeaderContext.cs::RequestHeader`, serialized by source generation: `Url`,
`ResponseFields` and `PayloadText` go to the top level, every other option under `options`.

## Response

`rust/src/engine.rs::Response`: the schema version, the requested content fields, the metadata
(`rust/src/engine.rs::MetadataJson`) and the error. Every key is always serialized; a field the
request did not name is null. The managed reader is `dotnet/src/RustTrafilatura/Response.cs::Read`,
which fills `dotnet/src/RustTrafilatura/ExtractionResult.cs::ExtractionResult`.

## Errors

`rust/src/engine.rs::ErrorEnvelope`: a kind, a message and, for kinds that carry one, a context
object. Core errors map to kinds in `rust/src/contract.rs::error_kind`; the invalid-request and panic
envelopes come from `rust/src/lib.rs::request_error` and `rust/src/lib.rs::error_response`. The
managed side maps kind names to `dotnet/src/RustTrafilatura/TrafilaturaException.cs::TrafilaturaErrorKind`
in `dotnet/src/RustTrafilatura/Failures.cs::ReadKind`; a name it does not know becomes `Unknown`. The
README error table is the user-facing list.

## Version document

`rust/src/lib.rs::rt_version` returns `rust/src/lib.rs::VersionJson`: the ABI version
(`rust/src/lib.rs::ABI_VERSION`), the schema version (`rust/src/contract.rs::SCHEMA_VERSION`), the
crate version from the manifest and the linked core version, which `rust/build.rs` reads from the
lockfile. `dotnet/src/RustTrafilatura/LibraryContract.cs::Check` compares the first two against
`RequiredAbi` and `RequiredSchemaVersion`; the crate and core versions are reported, not compared.

## Memory ownership

The library allocates every output buffer and hands back its pointer, length and capacity. The
caller passes that exact triple to `rust/src/lib.rs::rt_free` once; a null pointer is a no-op, and
a second release of a live pointer is undefined behaviour. The managed side never keeps a reference
to the native buffer past the read.

## Input caps

`rust/src/lib.rs` holds the header and payload caps and the environment variables that override them,
each read once per process. The header is checked against its cap before it is parsed, so an
oversized header is rejected before anything is parsed. The payload cap reaches the core as its input
cap (`rust/src/engine.rs::options_from`), so an oversized payload is rejected before the core reads it,
as an `InputTooLarge` error. Both caps are compared with `>`, and `0` disables the cap it names.

## Stack

The extraction needs 1 MiB of stack (`rust/src/lib.rs::WORKER_STACK_BYTES`). On macOS it runs on a
worker thread with that stack (`rust/src/lib.rs::extraction_stack`); elsewhere it runs on the caller's
thread, so the host's threads have to provide it. Rust's own test runs the extraction on 768 KiB
(`rust/tests/abi_hardening.rs::extraction_fits_a_768_kib_stack`). The test that enforces the bound
on the host is `dotnet/tests/RustTrafilatura.Tests/StackTests.cs::Default_And_Thread_Pool_Stacks_Are_At_Least_One_MiB`,
which runs on every host CI runs (Linux, Windows) and reads the stack with
`dotnet/tests/RustTrafilatura.Tests/ThreadStack.cs::CurrentSizeBytes`. The table records no CI number.

| Host | .NET | Default stack of a new or thread-pool thread | Source |
| --- | --- | --- | --- |
| Windows x64 | 8, 10 | 1572864 (1.5 MiB) | OS-inherited: the `dotnet.exe` PE stack reserve; the runtime passes 0 to `CreateThread` (vm/threads.cpp in dotnet/runtime) |
| Linux glibc x64, arm64 | 8, 10 | 8388608 (8 MiB) at `ulimit -s 8192` | glibc follows `RLIMIT_STACK`, and the runtime sets nothing. The bound holds while the soft limit is at least 1 MiB. When `RLIMIT_STACK` is unlimited, glibc uses 2 MiB on most architectures (man7 `pthread_create(3)`; not checked in glibc source for arm64) |
| macOS x64, arm64 | 8 | 524288 (512 KiB) | OS-inherited: the libpthread default. Below 1 MiB, so the macOS worker covers it |
| macOS x64, arm64 | 10 | 1572864 (1.5 MiB) | set by the runtime (`pal.cpp`, `HOST_APPLE`) |

Sources are the dotnet/runtime release tags v8.0.33 and v10.0.14. Overrides: `DOTNET_DefaultStackSize`
(.NET 8); `DOTNET_Thread_DefaultStackSize` and the runtimeconfig `System.Threading.DefaultStackSize`
(.NET 10). Gap: the macOS value comes from the libpthread main branch, not a release build.
