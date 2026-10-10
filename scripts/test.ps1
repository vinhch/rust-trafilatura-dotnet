# Run the Rust and C# suites with the same commands CI calls, so a developer and a workflow run one
# thing. Fails on the first failing command, and refuses to run at all when the staged native library
# does not correspond to the current rust/ sources.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# PowerShell 7.4+ can turn a native command's stderr into a terminating error, and cargo writes its
# progress there.
if (Test-Path Variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$ScriptName = 'test.ps1'

function Fail {
    param([Parameter(Mandatory = $true)][string]$Message)
    $Host.UI.WriteErrorLine("$ScriptName`: $Message")
    exit 1
}

function Show-Usage {
    @"
Usage: scripts/$ScriptName

Runs every command CI runs: cargo fmt --check, cargo clippy --locked --all-targets -- -D warnings,
cargo test --locked and a second cargo test --locked --features test-hooks in rust/, then
dotnet build -c Release and dotnet test -c Release in dotnet/.
The staged native library is verified against its build stamp first, so a stale or placeholder
artifact fails the run instead of passing against nothing.

Tests in the Package category are excluded: they read the packed .nupkg, which only
scripts/release.ps1 produces, and this run has to pass on a checkout that has built the library and
never packed. scripts/release.ps1 runs them after packing, where a missing entry fails the release.

  -h, --help   Show this message.
"@ | Write-Host
}

# Parsed by hand instead of through param(): both shells must accept exactly the same arguments.
$Help = $false
for ($i = 0; $i -lt $args.Count; $i++) {
    $argument = [string]$args[$i]
    if ($argument -notlike '-*') { Fail "unknown argument: $argument" }
    switch (($argument -replace '^--?', '').ToLowerInvariant()) {
        'h' { $Help = $true }
        'help' { $Help = $true }
        default { Fail "unknown argument: $argument" }
    }
}

if ($Help) {
    Show-Usage
    exit 0
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
$RustDir = Join-Path $RepoRoot 'rust'
$Manifest = Join-Path $RustDir 'Cargo.toml'
$DotnetDir = Join-Path $RepoRoot 'dotnet'
$Solution = Join-Path $DotnetDir 'RustTrafilatura.sln'
$BuildScript = Join-Path $PSScriptRoot 'build.ps1'

if (-not (Test-Path -PathType Container $RustDir)) { Fail "missing directory: $RustDir" }
if (-not (Test-Path -PathType Leaf $Manifest)) { Fail "missing manifest: $Manifest" }
if (-not (Test-Path -PathType Container $DotnetDir)) { Fail "missing directory: $DotnetDir" }
if (-not (Test-Path -PathType Leaf $Solution)) { Fail "missing solution: $Solution" }
if (-not (Test-Path -PathType Leaf $BuildScript)) { Fail "missing script: $BuildScript" }
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { Fail 'cargo not found on PATH' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'dotnet not found on PATH' }

# The guard lives in the build script, whose stamp it checks: that keeps one implementation of the
# digest per shell, so the writer and the verifier cannot drift apart. It reports a missing staged
# directory, a missing stamp or a stale artifact, and its exit code is this script's.
Write-Host "$ScriptName`: verifying the staged native library"
& $BuildScript --check
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# No command below is piped: a pipeline would report the last stage's exit code instead of cargo's or
# dotnet's, and each failure has to name the step that failed.
Write-Host "$ScriptName`: cargo fmt --check"
& cargo fmt --check --manifest-path $Manifest
if ($LASTEXITCODE -ne 0) { Fail "cargo fmt --check failed with exit code $LASTEXITCODE" }

Write-Host "$ScriptName`: cargo clippy --locked --all-targets -- -D warnings"
& cargo clippy --locked --manifest-path $Manifest --all-targets -- -D warnings
if ($LASTEXITCODE -ne 0) { Fail "cargo clippy failed with exit code $LASTEXITCODE" }

Write-Host "$ScriptName`: cargo test --locked"
& cargo test --locked --manifest-path $Manifest
if ($LASTEXITCODE -ne 0) { Fail "cargo test failed with exit code $LASTEXITCODE" }

# The test-hooks feature gates four tests, three of them allocation-counter tests
# (`free_round_trip_restores_allocation_counter`, `allocation_counter_balances_over_many_calls` and
# `panic_is_caught`; the fourth, `concurrent_extract_and_free_returns_the_counter_to_baseline`, a
# concurrency test). Without this run they are compiled out, and
# `rust/tests/allocation.rs`, gated whole, reports "running 0 tests": a green suite that executed
# nothing. The staged library stays the default build, because scripts/release.* refuses an artifact
# that exports the feature's symbols.
Write-Host "$ScriptName`: cargo test --locked --features test-hooks"
& cargo test --locked --manifest-path $Manifest --features test-hooks
if ($LASTEXITCODE -ne 0) { Fail "cargo test --features test-hooks failed with exit code $LASTEXITCODE" }

Write-Host "$ScriptName`: dotnet build -c Release"
& dotnet build $Solution -c Release
if ($LASTEXITCODE -ne 0) { Fail "dotnet build failed with exit code $LASTEXITCODE" }

Write-Host "$ScriptName`: dotnet test -c Release (Package category excluded)"
# The filter is xunit v3's own trait flag: the test project runs on Microsoft.Testing.Platform, opted
# in by global.json, and that platform does not accept the VSTest --filter expression syntax.
& dotnet test --solution $Solution -c Release --filter-not-trait "Category=Package"
if ($LASTEXITCODE -ne 0) { Fail "dotnet test failed with exit code $LASTEXITCODE" }

Write-Host "$ScriptName`: Rust and C# suites passed"
