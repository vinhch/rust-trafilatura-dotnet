# Third-party notices

This package is MIT. The full text is in [`LICENSE`](LICENSE), which is packed beside this file.

## What the package contains

The managed binding under `dotnet/` is this repository's own source.

The NuGet package also packs compiled native libraries under `runtimes/<rid>/native/`. They are built
from the released [`rust-trafilatura`](https://crates.io/crates/rust-trafilatura) crate and its
dependencies. This repository does not modify that crate's source: it depends on the published
version and builds it.

## Licence terms of those dependencies

`rust-trafilatura` is dual-licensed `Apache-2.0 OR MIT`. Both are permissive, and the MIT option is
the one this package's `MIT` licence expression takes.

The dependency graph's licence expressions, as this repository's build reads them, are the allow-list
in `rust/deny.toml`. That list is the inventory of what the graph reports. `cargo deny check licenses`
in `rust/` fails when a dependency falls outside it, so a new licence cannot enter the build without
this repository noticing. Every entry is a permissive licence that requires attribution at most.

## Credits

`rust-trafilatura` and `go-trafilatura` credit Adrien Barbaresi, who created the original Python
package [`adbar/trafilatura`](https://github.com/adbar/trafilatura) that defines the extraction this
package follows, and Markus Mobius, who maintains both ports. Radhi Fadlillah wrote the initial Go
port. The upstream crate records the fuller credit chain, including the readability-lxml ancestry.

None of these credits implies that any of these people or projects endorses this package.
