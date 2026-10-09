# Measure the rt_extract round trip against a direct core call on the host RID, in the bench
# profile, and exit non-zero when the ratio exceeds the budget in
# docs/specs/2026-10-08-dotnet-bindings-design.md (section 9).

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# PowerShell 7.4+ can turn a native command's stderr into a terminating error, and cargo writes its
# progress there.
if (Test-Path Variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$ScriptName = 'bench.ps1'
$Rids = @('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')
$Triples = @{
    'win-x64'     = 'x86_64-pc-windows-msvc'
    'linux-x64'   = 'x86_64-unknown-linux-gnu'
    'linux-arm64' = 'aarch64-unknown-linux-gnu'
    'osx-x64'     = 'x86_64-apple-darwin'
    'osx-arm64'   = 'aarch64-apple-darwin'
}

function Fail {
    param([Parameter(Mandatory = $true)][string]$Message)
    $Host.UI.WriteErrorLine("$ScriptName`: $Message")
    exit 1
}

function Show-Usage {
    @"
Usage: scripts/$ScriptName

Runs rust/benches/roundtrip.rs for the host RID in the bench profile, which inherits
release, prints milliseconds per call for the direct core call and the two rt_extract
round trips, and exits non-zero when the round-trip ratio exceeds the 1.5x budget in
docs/specs/2026-10-08-dotnet-bindings-design.md (section 9).

  -h, --help   Show this message.
"@ | Write-Host
}

function Get-HostRid {
    if ($env:OS -eq 'Windows_NT') {
        # MSYS-style shells report the shell's own architecture, which stays x86_64 under
        # emulation, so prefer the native architecture when Windows supplies it.
        $arch = $env:PROCESSOR_ARCHITECTURE
        if ($env:PROCESSOR_ARCHITEW6432) { $arch = $env:PROCESSOR_ARCHITEW6432 }
        if ($arch -eq 'ARM64') { return 'win-arm64' }
        return 'win-x64'
    }

    $osName = (& uname -s).Trim()
    if ($LASTEXITCODE -ne 0) { Fail "uname failed with exit code $LASTEXITCODE" }
    if ($osName -eq 'Darwin') { $os = 'osx' }
    elseif ($osName -eq 'Linux') { $os = 'linux' }
    else { Fail "unsupported host OS: $osName" }

    $machine = (& uname -m).Trim()
    if ($LASTEXITCODE -ne 0) { Fail "uname failed with exit code $LASTEXITCODE" }
    if ($machine -in @('x86_64', 'amd64')) { $arch = 'x64' }
    elseif ($machine -in @('arm64', 'aarch64')) { $arch = 'arm64' }
    else { Fail "unsupported host architecture: $machine" }

    return "$os-$arch"
}

# Parsed by hand instead of through param(): a param() block rejects `-h` in the
# `& .\scripts\bench.ps1 -h` form, and both shells must accept exactly the same arguments.
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

$Rid = Get-HostRid
# Case-sensitive, unlike PowerShell's default comparison: the two shells must accept exactly the
# same five strings.
if ($Rids -cnotcontains $Rid) {
    Fail "unsupported host RID: $Rid (expected one of: $($Rids -join ', '))"
}
$Triple = $Triples[$Rid]

$RepoRoot = Split-Path -Parent $PSScriptRoot
$RustDir = Join-Path $RepoRoot 'rust'
$Manifest = Join-Path $RustDir 'Cargo.toml'

if (-not (Test-Path -PathType Container $RustDir)) { Fail "missing directory: $RustDir" }
if (-not (Test-Path -PathType Leaf $Manifest)) { Fail "missing manifest: $Manifest" }
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { Fail 'cargo not found on PATH' }

# Cargo resolves a relative CARGO_TARGET_DIR against the working directory, which this script never
# changes, so the same value locates the tree scripts/build.* filled. --target is what makes the
# bench reuse that tree instead of building a second set of dependencies for the host triple.
$TargetDir = if ($env:CARGO_TARGET_DIR) { $env:CARGO_TARGET_DIR } else { Join-Path $RustDir 'target' }

Write-Host "$ScriptName`: benching $Rid ($Triple)"
Write-Host "$ScriptName`: target directory $TargetDir"

# The bench profile inherits release, so the ratio is measured on optimized code: a debug-profile
# number would be meaningless. Nothing here is piped, and a non-zero exit means the round trip is
# over budget or the build failed.
& cargo bench --locked --manifest-path $Manifest --target $Triple --bench roundtrip
if ($LASTEXITCODE -ne 0) {
    $Host.UI.WriteErrorLine("$ScriptName`: the bench failed with exit code $LASTEXITCODE")
    exit $LASTEXITCODE
}
