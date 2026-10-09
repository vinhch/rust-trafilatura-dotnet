# Build the native library for one RID and copy it to
# dotnet/src/RustTrafilatura/runtimes/<rid>/native/.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# PowerShell 7.4+ can turn a native command's stderr into a terminating error, and
# cargo writes its progress there.
if (Test-Path Variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$ScriptName = 'build.ps1'
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
    $ridList = $Rids -join ', '
    @"
Usage: scripts/$ScriptName [--rid <rid>]

Builds rust-trafilatura-ffi for one RID and copies the shared library to
dotnet/src/RustTrafilatura/runtimes/<rid>/native/.

  --rid <rid>  Target RID ($ridList). Defaults to the host RID.
  -h, --help   Show this message.
"@ | Write-Host
}

function Get-HostRid {
    if ($env:OS -eq 'Windows_NT') {
        # MSYS-style shells report the shell's own architecture, which stays x86_64
        # under emulation, so prefer the native architecture when Windows supplies it.
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

# Parsed by hand instead of through param(): a param() block rejects `--rid` in the
# `& .\scripts\build.ps1 --rid <rid>` form, which is the spelling build.sh documents.
# Accepts --rid and the PowerShell-native -Rid, plus -h/--help; anything else is an error.
$Rid = ''
$Help = $false
for ($i = 0; $i -lt $args.Count; $i++) {
    $argument = [string]$args[$i]
    if ($argument -notlike '-*') { Fail "unknown argument: $argument" }
    switch (($argument -replace '^--?', '').ToLowerInvariant()) {
        'rid' {
            if ($i + 1 -ge $args.Count) { Fail '--rid requires a value' }
            if ($Rid) { Fail '--rid given more than once' }
            $Rid = [string]$args[$i + 1]
            if (-not $Rid) { Fail '--rid requires a non-empty value' }
            $i++
        }
        'h' { $Help = $true }
        'help' { $Help = $true }
        default { Fail "unknown argument: $argument" }
    }
}

if ($Help) {
    Show-Usage
    exit 0
}

if (-not $Rid) { $Rid = Get-HostRid }
# Case-sensitive, unlike PowerShell's default comparison: the RID becomes a directory
# name under runtimes/, and the two shells must accept exactly the same five strings.
if ($Rids -cnotcontains $Rid) {
    Fail "unsupported RID: $Rid (expected one of: $($Rids -join ', '))"
}
$Triple = $Triples[$Rid]
$Library = 'librust_trafilatura_ffi.so'
if ($Rid -like 'win-*') { $Library = 'rust_trafilatura_ffi.dll' }
elseif ($Rid -like 'osx-*') { $Library = 'librust_trafilatura_ffi.dylib' }

$RepoRoot = Split-Path -Parent $PSScriptRoot
$RustDir = Join-Path $RepoRoot 'rust'
$Manifest = Join-Path $RustDir 'Cargo.toml'

if (-not (Test-Path -PathType Container $RustDir)) { Fail "missing directory: $RustDir" }
if (-not (Test-Path -PathType Leaf $Manifest)) { Fail "missing manifest: $Manifest" }
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { Fail 'cargo not found on PATH' }

# Cargo resolves a relative CARGO_TARGET_DIR against the working directory, which
# this script never changes, so the same value locates the artifact here.
$TargetDir = if ($env:CARGO_TARGET_DIR) { $env:CARGO_TARGET_DIR } else { Join-Path $RustDir 'target' }
$Artifact = Join-Path (Join-Path (Join-Path $TargetDir $Triple) 'release') $Library
$DestDir = Join-Path $RepoRoot "dotnet/src/RustTrafilatura/runtimes/$Rid/native"

Write-Host "$ScriptName`: building $Rid ($Triple)"
& cargo build --locked --release --manifest-path $Manifest --target $Triple
if ($LASTEXITCODE -ne 0) { Fail "cargo build failed with exit code $LASTEXITCODE" }

if (-not (Test-Path -PathType Leaf $Artifact)) { Fail "expected artifact not found: $Artifact" }

New-Item -ItemType Directory -Force -Path $DestDir | Out-Null
Copy-Item -Path $Artifact -Destination $DestDir -Force

Write-Host "$ScriptName`: source      $Artifact"
Write-Host "$ScriptName`: destination $(Join-Path $DestDir $Library)"
