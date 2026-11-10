# Architecture

Reference for the wire contract between the native library (`rust/`) and the managed client
(`dotnet/src/RustTrafilatura/`). Each section names the type or constant that owns the fact; values
live there and are not repeated here. Building and staging the library is in
[development.md](development.md); which files change together when one of these facts changes is
the "Change together" table in [AGENTS.md](../AGENTS.md).

## Call path

1. A public entry point on `dotnet/src/RustTrafilatura/Extractor.cs::Extractor` takes the page as
   bytes (`ExtractFromHtml` encodes a string to UTF-8 first).
2. `dotnet/src/RustTrafilatura/LibraryContract.cs::Ensure` checks the loaded library's version
   document once per process and throws `ContractVersionMismatchException` on every call after a
   mismatch.
3. `dotnet/src/RustTrafilatura/Request.cs::Header` serializes the options into the request header.
4. `dotnet/src/RustTrafilatura/Interop.cs::Extract` calls `rust/src/lib.rs::rt_extract` with the
   header and the page as raw buffers; the page is never base64-encoded or copied into a string.
5. `rust/src/lib.rs::rt_extract` checks both buffers against the input caps, parses the header
   (`rust/src/contract.rs::parse_request`) and runs the extraction on a scoped worker thread
   (`rust/src/lib.rs::on_worker_thread`) behind a panic boundary.
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
(`rust/src/contract.rs::OptionsJson`, with `rust/src/contract.rs::ConfigJson` for size thresholds)
and the response fields to return (`rust/src/contract.rs::ResponseField`). Unknown keys are rejected
at every level. The managed mirror is `dotnet/src/RustTrafilatura/HeaderContext.cs::RequestHeader`,
serialized by source generation: `Url` and `ResponseFields` go to the top level, every other option
under `options`.

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

`rust/src/lib.rs` holds the header and payload caps, the worker stack size and the environment
variables that override them, each read once per process. Both buffers are checked before either is
read, so an oversized input is rejected before anything is allocated in proportion to it.
