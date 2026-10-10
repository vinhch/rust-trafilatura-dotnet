// The core version this binding links, compiled into the ABI document so a consumer can read it back.
//
// Nothing in `rust-trafilatura` reports its own version, and cargo exposes no macro for a dependency's,
// so the resolved version is read out of the lockfile here. The lockfile is also what pins the
// dependency exactly, which makes this the version the linked code was compiled from rather than the
// version the manifest asks for.
//
// No dependency is added for the parse: the lockfile is read as text and the entry is located by name,
// the same way scripts/version.sh reads it.
//
// `rerun-if-changed` is what keeps the injected value honest: without it cargo would re-run this script
// for any change in the package, and with only this line it re-runs exactly when the resolved
// dependency set changes, which is the only thing the emitted value depends on.

use std::env;
use std::fs;
use std::path::PathBuf;

fn main() {
    println!("cargo:rerun-if-changed=Cargo.lock");

    let root = PathBuf::from(
        env::var("CARGO_MANIFEST_DIR").expect("cargo sets CARGO_MANIFEST_DIR for a build script"),
    );
    let lockfile = root.join("Cargo.lock");
    let text = fs::read_to_string(&lockfile)
        .unwrap_or_else(|error| panic!("cannot read {}: {error}", lockfile.display()));

    let version = core_version(&text).unwrap_or_else(|| {
        panic!(
            "no `rust-trafilatura` [[package]] block in {}, so the core version this binding links cannot be reported",
            lockfile.display()
        )
    });

    println!("cargo:rustc-env=RT_CORE_VERSION={version}");
}

/// The version of the `rust-trafilatura` entry in a lockfile: the entry is located by its `name = `
/// line, and the version is the `version = ` line that follows it, which is the order cargo writes.
fn core_version(lockfile: &str) -> Option<String> {
    let mut inside = false;

    for line in lockfile.lines() {
        if let Some(name) = line.strip_prefix("name = \"") {
            inside = name.trim_end_matches('"') == "rust-trafilatura";
            continue;
        }

        if inside {
            if let Some(version) = line.strip_prefix("version = \"") {
                return Some(version.trim_end_matches('"').to_owned());
            }
        }
    }

    None
}
